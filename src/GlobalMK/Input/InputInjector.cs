using System;
using System.Collections.Generic;
using System.Linq;

namespace GlobalMK
{
    /// <summary>
    /// Client side. Replays mouse and keyboard events from the server with SendInput. Keys are sent by
    /// hardware scan code so this computer's own keyboard layout decides which character is typed.
    /// Remembers what it pressed so everything can be released when the cursor leaves or the link drops.
    ///
    /// When <see cref="Forward"/> is connected, events go to the SYSTEM input helper instead, which can also
    /// type into the lock screen and UAC prompts. Without it, events are injected here.
    /// </summary>
    internal sealed class InputInjector
    {
        private readonly object _sync = new object();
        private readonly HashSet<int> _keysDown = new HashSet<int>();
        private readonly HashSet<MouseButton> _buttonsDown = new HashSet<MouseButton>();
        private readonly Func<Native.INPUT[], bool> _send;

        public bool Active { get; private set; }
        public event Action ActiveChanged;

        /// <summary>Optional link to the input helper; used whenever it is connected.</summary>
        public HelperLink Forward { get; set; }

        /// <param name="send">How to inject a batch of inputs. Defaults to SendInput on the current desktop.</param>
        public InputInjector(Func<Native.INPUT[], bool> send = null)
        {
            _send = send ?? (inputs => Native.SendInput((uint)inputs.Length, inputs, Native.InputSize) == inputs.Length);
        }

        public void Handle(byte[] p)
        {
            lock (_sync)
            {
                var type = Proto.TypeOf(p);
                if (type == MsgType.Enter) SetActive(true);
                if (type == MsgType.Leave) SetActive(false);

                if (Forward != null && Forward.TrySend(p))
                {
                    // Anything still held from before the helper connected is released here.
                    if (type == MsgType.Leave) ReleaseAllLocked();
                    return;
                }

                switch (type)
                {
                    case MsgType.Enter:
                        Proto.ReadPoint(p, out int ex, out int ey);
                        MoveTo(ex, ey);
                        break;
                    case MsgType.Leave:
                        ReleaseAllLocked();
                        break;
                    case MsgType.MouseMove:
                        Proto.ReadPoint(p, out int x, out int y);
                        MoveTo(x, y);
                        break;
                    case MsgType.MouseButton:
                        Button((MouseButton)p[1], p[2] != 0);
                        break;
                    case MsgType.MouseWheel:
                        Wheel(Proto.ReadInt(p, 1), p[5] != 0);
                        break;
                    case MsgType.Key:
                        Proto.ReadKey(p, out ushort vk, out ushort scan, out KeyFlags flags);
                        Key(vk, scan, flags);
                        break;
                }
            }
        }

        public void ReleaseAll()
        {
            lock (_sync)
            {
                ReleaseAllLocked();
                Forward?.TrySend(Proto.Simple(MsgType.Leave));
                SetActive(false);
            }
        }

        private void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            ActiveChanged?.Invoke();
        }

        private void ReleaseAllLocked()
        {
            foreach (var k in _keysDown.ToArray())
                Key((ushort)(k & 0xFF), (ushort)((k >> 8) & 0xFFFF), ((k & (1 << 24)) != 0 ? KeyFlags.Extended : KeyFlags.None) | KeyFlags.Up);
            foreach (var b in _buttonsDown.ToArray())
                Button(b, false);
            _keysDown.Clear();
            _buttonsDown.Clear();
        }

        private void MoveTo(int x, int y)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            int vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            // Normalized 0..65535 coordinates over the whole virtual desktop; round up so the
            // system's conversion back to pixels lands exactly on (x, y).
            long nx = ((long)(x - vx) * 65536 + vw - 1) / vw;
            long ny = ((long)(y - vy) * 65536 + vh - 1) / vh;
            SendMouse((int)Math.Max(0, Math.Min(65535, nx)), (int)Math.Max(0, Math.Min(65535, ny)), 0,
                Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK);
        }

        private void Button(MouseButton b, bool down)
        {
            uint flags;
            uint data = 0;
            switch (b)
            {
                case MouseButton.Left: flags = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
                case MouseButton.Right: flags = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
                case MouseButton.Middle: flags = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
                case MouseButton.X1: flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = Native.XBUTTON1; break;
                case MouseButton.X2: flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = Native.XBUTTON2; break;
                default: return;
            }
            if (down) _buttonsDown.Add(b); else _buttonsDown.Remove(b);
            SendMouse(0, 0, data, flags);
        }

        private void Wheel(int delta, bool horizontal) =>
            SendMouse(0, 0, unchecked((uint)delta), horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL);

        private void Key(ushort vk, ushort scan, KeyFlags flags)
        {
            bool up = (flags & KeyFlags.Up) != 0;
            bool ext = (flags & KeyFlags.Extended) != 0;
            int id = (vk & 0xFF) | (scan << 8) | (ext ? 1 << 24 : 0);
            if (up) _keysDown.Remove(id); else _keysDown.Add(id);

            var ki = new Native.KEYBDINPUT { dwExtraInfo = Native.InjectedMarker };
            if (scan != 0 && !VirtualKeyOnly(vk))
            {
                ki.wScan = scan;
                ki.dwFlags = Native.KEYEVENTF_SCANCODE;
            }
            else
            {
                ki.wVk = vk;
                ki.wScan = scan;
            }
            if (ext) ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (up) ki.dwFlags |= Native.KEYEVENTF_KEYUP;
            _send(new[] { new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.InputUnion { ki = ki } } });
        }

        /// <summary>Keys whose scan code is ambiguous or missing; these are replayed by virtual-key code.</summary>
        private static bool VirtualKeyOnly(ushort vk) =>
            vk == 0x03     // Ctrl+Break
            || vk == 0x13  // Pause
            || vk == 0x2C  // Print Screen
            || vk == 0x90  // Num Lock
            || (vk >= 0xA6 && vk <= 0xB7); // browser, volume, media and launch keys

        private void SendMouse(int dx, int dy, uint data, uint flags)
        {
            var mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Native.InjectedMarker };
            _send(new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.InputUnion { mi = mi } } });
        }
    }
}
