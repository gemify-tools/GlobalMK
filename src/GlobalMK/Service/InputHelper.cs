using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GlobalMK
{
    /// <summary>
    /// The SYSTEM input helper (<c>GlobalMK.exe --input-helper &lt;service pid&gt;</c>), started by the helper
    /// service inside the signed-in user's session. It owns no windows and no network connections: it only
    /// replays validated input events from the client app's pipe on whichever desktop currently receives input —
    /// the normal desktop, the lock screen, or a UAC prompt.
    /// </summary>
    internal static class InputHelper
    {
        public static int Run(string[] args)
        {
            Log.Init(Path.Combine(ServiceSetup.ExeDirectory, "helper.log"));
            int session = Process.GetCurrentProcess().SessionId;
            Log.Info($"Input helper started in session {session}.");

            if (args.Length > 1 && int.TryParse(args[1], out int parentPid))
            {
                // Exit together with the service that started us.
                new Thread(() =>
                {
                    try { Process.GetProcessById(parentPid).WaitForExit(); } catch { }
                    Log.Info("Helper service stopped; input helper exiting.");
                    Environment.Exit(0);
                }) { IsBackground = true, Name = "GMK parent watch" }.Start();
            }

            var desktop = new DesktopSwitcher();
            var injector = new InputInjector(desktop.Send);
            var server = new HelperPipeServer(HelperProtocol.PipeName(session), HelperPipeServer.AdministratorsOnly, injector.Handle, injector.ReleaseAll);
            server.Run();
            return 0;
        }
    }

    /// <summary>
    /// SendInput only reaches the desktop the calling thread is attached to. This keeps the (window-less)
    /// injecting thread attached to the current input desktop, switching when Windows changes it.
    /// </summary>
    internal sealed class DesktopSwitcher
    {
        private IntPtr _desktop;
        private string _name = "";
        private string _lastFailure;
        private int _lastCheck = Environment.TickCount - 10000;

        public bool Send(Native.INPUT[] inputs)
        {
            if (unchecked(Environment.TickCount - _lastCheck) >= 100) Sync();
            if (Native.SendInput((uint)inputs.Length, inputs, Native.InputSize) == inputs.Length) return true;
            Sync();
            return Native.SendInput((uint)inputs.Length, inputs, Native.InputSize) == inputs.Length;
        }

        private void Sync()
        {
            _lastCheck = Environment.TickCount;
            IntPtr h = Native.OpenInputDesktop(0, false, Native.GENERIC_ALL);
            if (h == IntPtr.Zero) return;
            string name = NameOf(h);
            if (_desktop != IntPtr.Zero && name == _name)
            {
                Native.CloseDesktop(h);
                return;
            }
            if (Native.SetThreadDesktop(h))
            {
                if (_desktop != IntPtr.Zero) Native.CloseDesktop(_desktop);
                _desktop = h;
                _name = name;
                _lastFailure = null;
                Log.Info("Input desktop: " + name);
            }
            else
            {
                if (_lastFailure != name) Log.Warn($"Could not switch to desktop {name} (error {Marshal.GetLastWin32Error()}).");
                _lastFailure = name;
                Native.CloseDesktop(h);
            }
        }

        private static string NameOf(IntPtr desktop)
        {
            var sb = new StringBuilder(256);
            Native.GetUserObjectInformation(desktop, Native.UOI_NAME, sb, sb.Capacity * 2, out _);
            return sb.ToString();
        }
    }
}
