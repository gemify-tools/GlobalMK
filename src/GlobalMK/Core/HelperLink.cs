using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace GlobalMK
{
    /// <summary>
    /// Local protocol between the client app and the SYSTEM input helper. The helper only ever accepts the
    /// six input message types the server can send, each with its exact length; nothing else gets through.
    /// Frames are [u8 length][payload].
    /// </summary>
    public static class HelperProtocol
    {
        public const byte Version = 1;
        public const int MaxPayload = 16;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GMKP");

        public static string PipeName(int sessionId) => "GlobalMK-input-" + sessionId;

        public static byte[] Hello()
        {
            var p = new byte[5];
            Buffer.BlockCopy(Magic, 0, p, 0, 4);
            p[4] = Version;
            return p;
        }

        public static bool IsHello(byte[] p) =>
            p != null && p.Length == 5 && p[0] == Magic[0] && p[1] == Magic[1] && p[2] == Magic[2] && p[3] == Magic[3] && p[4] == Version;

        public static byte[] Frame(byte[] payload)
        {
            if (payload.Length == 0 || payload.Length > MaxPayload) throw new ArgumentException("payload size");
            var f = new byte[payload.Length + 1];
            f[0] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, f, 1, payload.Length);
            return f;
        }

        /// <summary>True only for well-formed input messages (and the keep-alive ping).</summary>
        public static bool IsValidInput(byte[] p)
        {
            if (p == null || p.Length == 0) return false;
            switch ((MsgType)p[0])
            {
                case MsgType.Enter:
                case MsgType.MouseMove:
                case MsgType.Ping:
                    return p.Length == 9;
                case MsgType.Leave:
                    return p.Length == 1;
                case MsgType.MouseButton:
                    return p.Length == 3 && p[1] >= (byte)MouseButton.Left && p[1] <= (byte)MouseButton.X2 && p[2] <= 1;
                case MsgType.MouseWheel:
                    return p.Length == 6 && p[5] <= 1;
                case MsgType.Key:
                    return p.Length == 6 && p[5] <= (byte)(KeyFlags.Extended | KeyFlags.Up);
                default:
                    return false;
            }
        }

        internal static byte[] ReadFrame(Stream s)
        {
            int len = s.ReadByte();
            if (len < 0) throw new EndOfStreamException();
            if (len == 0 || len > MaxPayload) throw new ProtocolException("Bad helper frame length.");
            var p = new byte[len];
            SecureChannel.ReadExactly(s, p, 0, len);
            return p;
        }
    }

    /// <summary>Runs inside the input helper: accepts one client at a time and hands it validated input messages.</summary>
    public sealed class HelperPipeServer : IDisposable
    {
        private readonly string _name;
        private readonly Func<PipeSecurity> _security;
        private readonly Action<byte[]> _onInput;
        private readonly Action _onDisconnect;
        private volatile bool _stopped;
        private NamedPipeServerStream _current;

        public HelperPipeServer(string name, Func<PipeSecurity> security, Action<byte[]> onInput, Action onDisconnect)
        {
            _name = name;
            _security = security;
            _onInput = onInput;
            _onDisconnect = onDisconnect;
        }

        /// <summary>
        /// Only LocalSystem and elevated administrators may connect, and never over the network. A normal
        /// (non-elevated) process can't open the pipe, so it can't use the helper to click through a UAC prompt.
        /// </summary>
        public static PipeSecurity AdministratorsOnly()
        {
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return ps;
        }

        /// <summary>For tests: the current user may connect.</summary>
        public static PipeSecurity CurrentUserOnly()
        {
            var ps = new PipeSecurity();
            using (var me = WindowsIdentity.GetCurrent())
                ps.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return ps;
        }

        /// <summary>Blocking accept/serve loop. Returns after <see cref="Dispose"/>.</summary>
        public void Run()
        {
            while (!_stopped)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 1024, 1024, _security());
                }
                catch (Exception ex)
                {
                    Log.Warn($"Input pipe {_name} unavailable: {ex.Message}");
                    Thread.Sleep(2000);
                    continue;
                }

                _current = pipe;
                bool served = false;
                try
                {
                    pipe.WaitForConnection();
                    if (_stopped) break;
                    served = true;
                    Serve(pipe);
                }
                catch (Exception ex)
                {
                    if (!_stopped && !(ex is EndOfStreamException)) Log.Info("Input helper client disconnected: " + ex.Message);
                }
                finally
                {
                    _current = null;
                    pipe.Dispose();
                }
                if (served)
                {
                    try { _onDisconnect(); } catch (Exception ex) { Log.Error("Release after disconnect: " + ex.Message); }
                }
            }
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            if (!HelperProtocol.IsHello(HelperProtocol.ReadFrame(pipe)))
                throw new ProtocolException("unexpected hello");
            Log.Info("Input helper: client app connected.");
            while (!_stopped)
            {
                var p = HelperProtocol.ReadFrame(pipe);
                if (!HelperProtocol.IsValidInput(p)) throw new ProtocolException("invalid input message");
                if (Proto.TypeOf(p) != MsgType.Ping) _onInput(p);
            }
        }

        public void Dispose()
        {
            _stopped = true;
            // Wake a WaitForConnection that is blocked, then close whatever is open.
            try
            {
                using (var wake = new NamedPipeClientStream(".", _name, PipeDirection.Out)) wake.Connect(200);
            }
            catch { }
            try { _current?.Dispose(); } catch { }
        }
    }

    public enum HelperLinkState { NotInstalled, NeedsAdministrator, Untrusted, Connected }

    /// <summary>
    /// Client-app side of the input helper link. Reconnects in the background; <see cref="TrySend"/> returns
    /// false whenever the helper isn't available so the caller can inject locally instead.
    /// </summary>
    public sealed class HelperLink : IDisposable
    {
        private const int RetryMs = 2000;
        private readonly string _name;
        private readonly Func<NamedPipeClientStream, bool> _verifyServer;
        private readonly object _lock = new object();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private NamedPipeClientStream _pipe;
        private Thread _thread;
        private HelperLinkState _state = HelperLinkState.NotInstalled;

        public event Action StateChanged;

        public HelperLink(string pipeName, Func<NamedPipeClientStream, bool> verifyServer = null)
        {
            _name = pipeName;
            _verifyServer = verifyServer ?? IsSystemServer;
        }

        public HelperLinkState State => _state;
        public bool Connected => _state == HelperLinkState.Connected;

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "GMK helper link" };
            _thread.Start();
        }

        private void Loop()
        {
            do
            {
                if (!Connected) TryConnect();
                else if (!TrySend(Proto.Ping(MsgType.Ping, 0))) TryConnect(); // detect a helper that went away
            } while (!_stop.WaitOne(RetryMs));
        }

        /// <summary>One connection attempt. Public for tests.</summary>
        public void TryConnect()
        {
            // NamedPipeClientStream.Connect busy-waits while a pipe doesn't exist, so check first.
            if (!Native.WaitNamedPipe(@"\\.\pipe\" + _name, 1) && Marshal.GetLastWin32Error() == Native.ERROR_FILE_NOT_FOUND)
            {
                SetState(HelperLinkState.NotInstalled);
                return;
            }

            NamedPipeClientStream p = null;
            try
            {
                p = new NamedPipeClientStream(".", _name, PipeDirection.Out);
                p.Connect(250);
                if (!_verifyServer(p))
                {
                    p.Dispose();
                    SetState(HelperLinkState.Untrusted);
                    return;
                }
                var hello = HelperProtocol.Frame(HelperProtocol.Hello());
                p.Write(hello, 0, hello.Length);
                lock (_lock)
                {
                    _pipe?.Dispose();
                    _pipe = p;
                }
                SetState(HelperLinkState.Connected);
            }
            catch (UnauthorizedAccessException)
            {
                p?.Dispose();
                SetState(HelperLinkState.NeedsAdministrator);
            }
            catch (Exception)
            {
                p?.Dispose();
                SetState(HelperLinkState.NotInstalled);
            }
        }

        public bool TrySend(byte[] payload)
        {
            lock (_lock)
            {
                if (_pipe == null) return false;
                try
                {
                    var f = HelperProtocol.Frame(payload);
                    _pipe.Write(f, 0, f.Length);
                    return true;
                }
                catch (Exception)
                {
                    _pipe.Dispose();
                    _pipe = null;
                }
            }
            SetState(HelperLinkState.NotInstalled);
            return false;
        }

        private void SetState(HelperLinkState s)
        {
            if (_state == s) return;
            var old = _state;
            _state = s;
            if (s == HelperLinkState.Connected) Log.Info("Connected to the input helper: the lock screen and UAC prompts on this computer can be used.");
            else if (old == HelperLinkState.Connected) Log.Warn("Lost the input helper; typing on the lock screen and UAC prompts is unavailable.");
            if (s == HelperLinkState.Untrusted) Log.Warn("Ignored an input pipe that isn't run by the helper service (possible impersonation).");
            try { StateChanged?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            _stop.Set();
            _thread?.Join(1000);
            lock (_lock)
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }

        /// <summary>
        /// Anti-impersonation check: the pipe must be served by a LocalSystem process. A normal program could
        /// otherwise create the pipe first and receive what you type on the lock screen.
        /// </summary>
        public static bool IsSystemServer(NamedPipeClientStream pipe)
        {
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return false;
            IntPtr proc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return false;
            try
            {
                if (!Native.OpenProcessToken(proc, Native.TOKEN_QUERY, out IntPtr token)) return false;
                try
                {
                    Native.GetTokenInformation(token, Native.TokenUser, IntPtr.Zero, 0, out int needed);
                    if (needed <= 0) return false;
                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (!Native.GetTokenInformation(token, Native.TokenUser, buf, needed, out needed)) return false;
                        var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buf));
                        return sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                finally
                {
                    Native.CloseHandle(token);
                }
            }
            finally
            {
                Native.CloseHandle(proc);
            }
        }
    }
}
