using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace GlobalMK
{
    /// <summary>
    /// Windows service (<c>GlobalMK.exe --service</c>, LocalSystem). Services run in session 0 and can't reach the
    /// user's screens, so this service's only job is to keep one input helper running as SYSTEM inside the
    /// active console session, and to move it when the console session changes (sign-in, user switch).
    /// </summary>
    internal sealed class HelperService : ServiceBase
    {
        public const string Name = "GlobalMKHelper";

        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _stopping;
        private Thread _thread;
        private IntPtr _helper;
        private uint _helperSession = uint.MaxValue;
        private DateTime _launchedAt;

        public HelperService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            CanHandleSessionChangeEvent = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            Log.Init(Path.Combine(ServiceSetup.ExeDirectory, "service.log"));
            Log.Info("Helper service started.");
            _thread = new Thread(Supervise) { IsBackground = true, Name = "GMK helper supervisor" };
            _thread.Start();
        }

        protected override void OnStop()
        {
            _stopping = true;
            _wake.Set();
            _thread?.Join(5000);
            StopHelper();
            Log.Info("Helper service stopped.");
        }

        protected override void OnShutdown() => OnStop();

        protected override void OnSessionChange(SessionChangeDescription change)
        {
            Log.Info($"Session change: {change.Reason} (session {change.SessionId}).");
            _wake.Set();
        }

        private void Supervise()
        {
            int failures = 0;
            while (!_stopping)
            {
                try
                {
                    uint session = Native.WTSGetActiveConsoleSessionId();
                    bool alive = _helper != IntPtr.Zero && Native.WaitForSingleObject(_helper, 0) == Native.WAIT_TIMEOUT;
                    if (alive && session != _helperSession)
                    {
                        Log.Info($"Console moved to session {session}; restarting the input helper there.");
                        StopHelper();
                        alive = false;
                    }
                    if (!alive)
                    {
                        if (_helper != IntPtr.Zero)
                        {
                            // It exited on its own; back off if it keeps dying right after starting.
                            failures = (DateTime.Now - _launchedAt).TotalSeconds < 10 ? failures + 1 : 0;
                            Native.CloseHandle(_helper);
                            _helper = IntPtr.Zero;
                        }
                        if (session != 0 && session != 0xFFFFFFFF && failures < 20)
                        {
                            if (!Launch(session)) failures++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Supervisor: " + ex.Message);
                }
                _wake.WaitOne(failures == 0 ? 1000 : Math.Min(60000, 1000 * (1 << Math.Min(failures, 6))));
            }
        }

        private bool Launch(uint session)
        {
            IntPtr token = IntPtr.Zero, dup = IntPtr.Zero;
            try
            {
                if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.MAXIMUM_ALLOWED, out token))
                    throw new Win32Exception();
                if (!Native.DuplicateTokenEx(token, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out dup))
                    throw new Win32Exception();
                uint s = session;
                if (!Native.SetTokenInformation(dup, Native.TokenSessionId, ref s, sizeof(uint)))
                    throw new Win32Exception();

                string exe = ServiceSetup.ExePath;
                var cmd = new StringBuilder($"\"{exe}\" --input-helper {Process.GetCurrentProcess().Id}");
                var si = new Native.STARTUPINFO { cb = Marshal.SizeOf(typeof(Native.STARTUPINFO)), lpDesktop = @"winsta0\default" };
                if (!Native.CreateProcessAsUser(dup, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, Native.CREATE_NO_WINDOW,
                        IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
                    throw new Win32Exception();

                Native.CloseHandle(pi.hThread);
                _helper = pi.hProcess;
                _helperSession = session;
                _launchedAt = DateTime.Now;
                Log.Info($"Started the input helper (pid {pi.dwProcessId}) in session {session}.");
                return true;
            }
            catch (Win32Exception ex)
            {
                Log.Error($"Could not start the input helper in session {session}: {ex.Message}");
                return false;
            }
            finally
            {
                if (dup != IntPtr.Zero) Native.CloseHandle(dup);
                if (token != IntPtr.Zero) Native.CloseHandle(token);
            }
        }

        private void StopHelper()
        {
            if (_helper == IntPtr.Zero) return;
            Native.TerminateProcess(_helper, 0);
            Native.CloseHandle(_helper);
            _helper = IntPtr.Zero;
            _helperSession = uint.MaxValue;
        }
    }
}
