using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace GlobalMK
{
    /// <summary>
    /// Server side. Installs low-level mouse and keyboard hooks on a dedicated thread and decides, for every
    /// physical input event, whether it stays on this computer or goes to the computer the cursor is on.
    ///
    /// While another computer is active, the real cursor is parked in the middle of this computer's primary
    /// screen; each mouse event is turned into a delta from the park point, applied to a virtual cursor in the
    /// remote computer's coordinates, and the event is swallowed. Keyboard follows the mouse.
    /// </summary>
    public sealed class InputCapture : IDisposable
    {
        private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        private const int VK_SCROLL = 0x91;
        private static readonly int[] Modifiers = { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, 0x5B, 0x5C };

        private readonly object _sync = new object();
        private readonly Native.HookProc _mouseProc;
        private readonly Native.HookProc _keyboardProc;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly HashSet<uint> _localKeysDown = new HashSet<uint>();
        private readonly bool[] _phys = new bool[256];

        private Thread _thread;
        private uint _threadId;
        private IntPtr _mouseHook, _keyboardHook;

        private LayoutSnapshot _layout;
        private MachineGeom _active; // null = this computer
        private Point _v;            // virtual cursor in the active remote computer's coordinates
        private Point _park;
        private bool _locked;
        private int _localButtons, _remoteButtons;
        private string _edgeKey;
        private int _edgeSince;
        private int _generation;

        public int SwitchDelayMs { get; set; }
        public int CornerSize { get; set; }

        /// <summary>Raised (on a worker thread) whenever the active computer or lock state changes.</summary>
        public event Action StateChanged;

        public InputCapture()
        {
            _mouseProc = MouseHook;
            _keyboardProc = KeyboardHook;
        }

        public string ActiveName { get { lock (_sync) return _active?.Name; } }
        public bool Locked { get { lock (_sync) return _locked; } }
        public Point ParkPoint { get { lock (_sync) return _park; } }
        public int Generation { get { lock (_sync) return _generation; } }

        public void Start()
        {
            _thread = new Thread(HookThread) { IsBackground = true, Name = "GMK input hooks", Priority = ThreadPriority.Highest };
            _thread.Start();
            _ready.WaitOne(5000);
        }

        public void Dispose()
        {
            if (_threadId != 0 && _thread.IsAlive)
            {
                Command(CmdHome);
                Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(2000);
            }
        }

        // Anything that may move the real cursor runs on the hook thread, never while another thread holds
        // the lock, so a cursor move can never wait on a hook callback that is waiting on that lock.
        private const uint CmdLayout = 0x8001, CmdHome = 0x8002, CmdRepark = 0x8003;
        private volatile LayoutSnapshot _pendingLayout;

        private void Command(uint cmd) => Native.PostThreadMessage(_threadId, cmd, IntPtr.Zero, IntPtr.Zero);

        public void SetLayout(LayoutSnapshot layout)
        {
            if (_threadId == 0)
            {
                lock (_sync) _layout = layout; // not started yet
                return;
            }
            _pendingLayout = layout;
            Command(CmdLayout);
        }

        public void SetLocked(bool locked)
        {
            lock (_sync)
            {
                if (_locked == locked) return;
                _locked = locked;
                Log.Info(locked ? "Cursor locked to the current computer." : "Cursor unlocked.");
                RaiseStateChanged();
            }
        }

        /// <summary>Re-warp the parked cursor (so Windows re-evaluates the cursor shape), only if still remote.</summary>
        public void Repark() => Command(CmdRepark);

        public void ReturnToLocal() => Command(CmdHome);

        private void OnCommand(uint cmd)
        {
            lock (_sync)
            {
                switch (cmd)
                {
                    case CmdLayout:
                        var layout = _pendingLayout;
                        if (layout == null) return;
                        _layout = layout;
                        if (_active == null) return;
                        var m = layout.Find(_active.Name);
                        if (m == null || !m.Online || m.Conn == null)
                        {
                            GoLocal(_park);
                            return;
                        }
                        _active = m;
                        if (!m.Contains(_v)) _v = MachineGeom.Clamp(_v, m.NearestMonitor(_v));
                        var park = layout.Local.PrimaryCenter;
                        if (park != _park)
                        {
                            // This computer's screens changed while parked; re-park on the new primary screen.
                            _park = park;
                            Native.SetCursorPos(park.X, park.Y);
                            RaiseStateChanged();
                        }
                        break;
                    case CmdHome:
                        if (_active != null) GoLocal(_park);
                        break;
                    case CmdRepark:
                        if (_active != null) Native.SetCursorPos(_park.X, _park.Y);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ hook thread

        private void HookThread()
        {
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // make sure this thread has a message queue
            _threadId = Native.GetCurrentThreadId();
            IntPtr hMod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_mouseHook == IntPtr.Zero) Log.Error("Could not install the mouse hook (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").");
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            if (_keyboardHook == IntPtr.Zero) Log.Error("Could not install the keyboard hook (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").");
            _ready.Set();

            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message >= 0x8000 && msg.message < 0xC000)
                {
                    try { OnCommand(msg.message); }
                    catch (Exception ex) { Log.Error("Input command: " + ex.Message); }
                    continue;
                }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        }

        private unsafe IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = (Native.MSLLHOOKSTRUCT*)lParam;
                if ((info->flags & Native.LLMHF_INJECTED) == 0)
                {
                    bool eat;
                    try
                    {
                        lock (_sync) eat = OnMouse((int)wParam, info->pt.X, info->pt.Y, info->mouseData);
                    }
                    catch (Exception ex)
                    {
                        eat = false;
                        Log.Error("Mouse hook: " + ex.Message);
                    }
                    if (eat) return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private unsafe IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = (Native.KBDLLHOOKSTRUCT*)lParam;
                if ((info->flags & Native.LLKHF_INJECTED) == 0)
                {
                    bool eat;
                    try
                    {
                        lock (_sync) eat = OnKey(info->vkCode, info->scanCode, info->flags);
                    }
                    catch (Exception ex)
                    {
                        eat = false;
                        Log.Error("Keyboard hook: " + ex.Message);
                    }
                    if (eat) return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // ------------------------------------------------------------------ mouse

        private bool OnMouse(int msg, int x, int y, uint data)
        {
            var layout = _layout;
            if (layout?.Local == null) return false;

            if (_active == null)
            {
                switch (msg)
                {
                    case Native.WM_MOUSEMOVE:
                        if (_locked) return false;
                        if (_localButtons != 0)
                        {
                            _localButtons = PhysicalButtons();
                            if (_localButtons != 0) return false; // never switch in the middle of a drag
                        }
                        var hit = layout.FindEdgeExit(layout.Local, new Point(x, y), CornerSize);
                        if (hit == null || !hit.Machine.Online || hit.Machine.Conn == null)
                        {
                            _edgeKey = null;
                            return false;
                        }
                        if (!EdgeDelayPassed(hit)) return false;
                        SwitchTo(hit.Machine, hit.EntryLocal, hit.Direction);
                        return true;
                    case Native.WM_LBUTTONDOWN: _localButtons |= 1; break;
                    case Native.WM_LBUTTONUP: _localButtons &= ~1; break;
                    case Native.WM_RBUTTONDOWN: _localButtons |= 2; break;
                    case Native.WM_RBUTTONUP: _localButtons &= ~2; break;
                    case Native.WM_MBUTTONDOWN: _localButtons |= 4; break;
                    case Native.WM_MBUTTONUP: _localButtons &= ~4; break;
                    case Native.WM_XBUTTONDOWN: _localButtons |= 8; break;
                    case Native.WM_XBUTTONUP: _localButtons &= ~8; break;
                }
                return false;
            }

            var conn = _active.Conn;
            switch (msg)
            {
                case Native.WM_MOUSEMOVE:
                    int dx = x - _park.X, dy = y - _park.Y;
                    if (dx != 0 || dy != 0) RemoteMove(layout, dx, dy);
                    break;
                case Native.WM_LBUTTONDOWN: RemoteButton(conn, MouseButton.Left, true); break;
                case Native.WM_LBUTTONUP: RemoteButton(conn, MouseButton.Left, false); break;
                case Native.WM_RBUTTONDOWN: RemoteButton(conn, MouseButton.Right, true); break;
                case Native.WM_RBUTTONUP: RemoteButton(conn, MouseButton.Right, false); break;
                case Native.WM_MBUTTONDOWN: RemoteButton(conn, MouseButton.Middle, true); break;
                case Native.WM_MBUTTONUP: RemoteButton(conn, MouseButton.Middle, false); break;
                case Native.WM_XBUTTONDOWN:
                case Native.WM_XBUTTONUP:
                    RemoteButton(conn, (data >> 16) == Native.XBUTTON1 ? MouseButton.X1 : MouseButton.X2, msg == Native.WM_XBUTTONDOWN);
                    break;
                case Native.WM_MOUSEWHEEL:
                    conn?.Send(Proto.Wheel((short)(data >> 16), false));
                    break;
                case Native.WM_MOUSEHWHEEL:
                    conn?.Send(Proto.Wheel((short)(data >> 16), true));
                    break;
            }
            return true;
        }

        private void RemoteButton(Connection conn, MouseButton b, bool down)
        {
            int bit = 1 << (int)b;
            if (down) _remoteButtons |= bit; else _remoteButtons &= ~bit;
            conn?.Send(Proto.Button(b, down));
        }

        private void RemoteMove(LayoutSnapshot layout, int dx, int dy)
        {
            var m = _active;
            var nv = new Point(_v.X + dx, _v.Y + dy);
            if (!m.Contains(nv))
            {
                if (!_locked && _remoteButtons == 0)
                {
                    var hit = layout.FindOvershootExit(m, _v, nv, CornerSize);
                    if (hit != null && hit.Machine.Online && (hit.Machine.IsLocal || hit.Machine.Conn != null))
                    {
                        if (EdgeDelayPassed(hit))
                        {
                            SwitchTo(hit.Machine, hit.EntryLocal, hit.Direction);
                            return;
                        }
                    }
                    else _edgeKey = null;
                }
                nv = LayoutSnapshot.ClampMove(m, _v, nv);
            }
            else _edgeKey = null;

            if (nv != _v)
            {
                _v = nv;
                m.Conn?.Send(Proto.Point(MsgType.MouseMove, nv.X, nv.Y));
            }

            // Keep the real cursor parked, in case something else moved it.
            if (Native.GetCursorPos(out var cur) && (cur.X != _park.X || cur.Y != _park.Y))
                Native.SetCursorPos(_park.X, _park.Y);
        }

        private bool EdgeDelayPassed(RayHit hit)
        {
            if (SwitchDelayMs <= 0) return true;
            string key = hit.Machine.Name + "/" + hit.Direction;
            int now = Environment.TickCount;
            if (_edgeKey != key)
            {
                _edgeKey = key;
                _edgeSince = now;
                return false;
            }
            return now - _edgeSince >= SwitchDelayMs;
        }

        private static int PhysicalButtons()
        {
            int b = 0;
            if (Native.GetAsyncKeyState(0x01) < 0) b |= 1;
            if (Native.GetAsyncKeyState(0x02) < 0) b |= 2;
            if (Native.GetAsyncKeyState(0x04) < 0) b |= 4;
            if (Native.GetAsyncKeyState(0x05) < 0 || Native.GetAsyncKeyState(0x06) < 0) b |= 8;
            return b;
        }

        // ------------------------------------------------------------------ switching

        private void SwitchTo(MachineGeom target, Point entry, Direction dir)
        {
            _edgeKey = null;
            if (target.IsLocal)
            {
                GoLocal(LayoutSnapshot.StepInside(target, entry, dir));
                return;
            }

            var from = _active;
            if (from != null)
            {
                from.Conn?.Send(Proto.Simple(MsgType.Leave));
            }
            else
            {
                _park = _layout.Local.PrimaryCenter;
                Native.SetCursorPos(_park.X, _park.Y);
                foreach (int vk in Modifiers) _phys[vk] = Native.GetAsyncKeyState(vk) < 0;
            }
            _active = target;
            _v = entry;
            _remoteButtons = 0;
            target.Conn.Send(Proto.Point(MsgType.Enter, entry.X, entry.Y));
            _generation++;
            RaiseStateChanged();
        }

        private void GoLocal(Point p)
        {
            if (_active == null) return;
            _active.Conn?.Send(Proto.Simple(MsgType.Leave));
            _active = null;
            _remoteButtons = 0;
            _localButtons = 0;
            Native.SetCursorPos(p.X, p.Y);
            _generation++;
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null) ThreadPool.QueueUserWorkItem(_ => handler());
        }

        // ------------------------------------------------------------------ keyboard

        private bool OnKey(uint vk, uint scan, uint flags)
        {
            bool up = (flags & Native.LLKHF_UP) != 0;
            if (_active == null)
            {
                // While local, Windows' own key state is authoritative (it can't go stale after Ctrl+Alt+Del).
                foreach (int m in Modifiers)
                    if (m != vk) _phys[m] = Native.GetAsyncKeyState(m) < 0;
            }
            if (vk < 256) _phys[vk] = !up;
            bool ctrl = _phys[VK_LCONTROL] || _phys[VK_RCONTROL];
            bool alt = _phys[VK_LMENU] || _phys[VK_RMENU];
            bool shift = _phys[VK_LSHIFT] || _phys[VK_RSHIFT];

            // Hotkeys (handled on this computer, never forwarded).
            if (vk == VK_SCROLL)
            {
                if (!up) ToggleLock();
                return true;
            }
            if (ctrl && alt && shift && (vk == 'L' || vk == 'H'))
            {
                if (!up)
                {
                    if (vk == 'L') ToggleLock();
                    else if (_active != null) GoLocal(_park);
                }
                return true;
            }

            if (_active == null)
            {
                if (up) _localKeysDown.Remove(vk); else _localKeysDown.Add(vk);
                return false;
            }

            // A key that was already held here when the cursor left stays here until it is released.
            if (_localKeysDown.Contains(vk))
            {
                if (up) _localKeysDown.Remove(vk);
                return false;
            }

            var kf = KeyFlags.None;
            if ((flags & Native.LLKHF_EXTENDED) != 0) kf |= KeyFlags.Extended;
            if (up) kf |= KeyFlags.Up;
            _active.Conn?.Send(Proto.Key((ushort)vk, (ushort)scan, kf));
            return true;
        }

        private void ToggleLock()
        {
            _locked = !_locked;
            Log.Info(_locked ? "Cursor locked to the current computer (Scroll Lock to unlock)." : "Cursor unlocked.");
            RaiseStateChanged();
        }
    }
}
