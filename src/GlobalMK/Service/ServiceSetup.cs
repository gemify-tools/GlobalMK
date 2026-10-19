using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace GlobalMK
{
    internal enum HelperServiceState { NotInstalled, Stopped, Starting, Running }

    /// <summary>
    /// Installs, updates and removes the helper service. The service always runs a copy of this exe in
    /// Program Files: a SYSTEM service must never run a file that a normal user could replace.
    /// </summary>
    internal static class ServiceSetup
    {
        public const string InstallArg = "--install-service";
        public const string UninstallArg = "--uninstall-service";

        public static string ExePath => Assembly.GetExecutingAssembly().Location;
        public static string ExeDirectory => Path.GetDirectoryName(ExePath);
        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GlobalMK");
        public static string InstalledExe => Path.Combine(InstallDir, "GlobalMK.exe");

        public static HelperServiceState Query()
        {
            try
            {
                using (var sc = new ServiceController(HelperService.Name))
                {
                    switch (sc.Status)
                    {
                        case ServiceControllerStatus.Running: return HelperServiceState.Running;
                        case ServiceControllerStatus.Stopped: return HelperServiceState.Stopped;
                        default: return HelperServiceState.Starting;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return HelperServiceState.NotInstalled;
            }
        }

        /// <summary>True when the installed copy is not the same build as this app (copies keep their file time).</summary>
        public static bool IsOutdated()
        {
            try
            {
                if (string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return false;
                var installed = new FileInfo(InstalledExe);
                var mine = new FileInfo(ExePath);
                return !installed.Exists || installed.Length != mine.Length || installed.LastWriteTimeUtc != mine.LastWriteTimeUtc;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Run <c>--install-service</c> or <c>--uninstall-service</c> in an elevated copy (UAC prompt).</summary>
        public static bool RunElevated(string arg, out string error)
        {
            error = null;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(ExePath, arg) { Verb = "runas", UseShellExecute = true }))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) error = "it reported a problem — see the log on the Connections & log tab";
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception)
            {
                error = "administrator permission was not granted";
                return false;
            }
        }

        // ------------------------------------------------------------------ elevated entry points

        public static int Install()
        {
            try
            {
                StopService();
                Directory.CreateDirectory(InstallDir);
                if (!string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                    CopyWithRetry(ExePath, InstalledExe);

                string bin = $"\\\"{InstalledExe}\\\" --service";
                if (Query() == HelperServiceState.NotInstalled)
                    Sc($"create {HelperService.Name} binPath= \"{bin}\" start= auto DisplayName= \"Global Mouse & Keyboard helper\"");
                else
                    Sc($"config {HelperService.Name} binPath= \"{bin}\" start= auto");
                Sc($"description {HelperService.Name} \"Lets Global Mouse & Keyboard type into the lock screen and UAC prompts of the signed-in user.\"", true);
                Sc($"failure {HelperService.Name} reset= 86400 actions= restart/5000/restart/5000/restart/60000", true);

                using (var sc = new ServiceController(HelperService.Name))
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
                Log.Info($"Installed the helper service ({InstalledExe}).");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Installing the helper service failed: " + ex.Message);
                MessageBox.Show("Installing the helper service failed:\n\n" + ex.Message, "Global Mouse & Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        public static int Uninstall()
        {
            try
            {
                if (Query() != HelperServiceState.NotInstalled)
                {
                    StopService();
                    Sc($"delete {HelperService.Name}");
                }
                for (int i = 0; i < 20 && Directory.Exists(InstallDir); i++)
                {
                    try { Directory.Delete(InstallDir, true); }
                    catch (IOException) { Thread.Sleep(250); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(250); }
                }
                Log.Info("Removed the helper service.");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Removing the helper service failed: " + ex.Message);
                MessageBox.Show("Removing the helper service failed:\n\n" + ex.Message, "Global Mouse & Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void StopService()
        {
            if (Query() == HelperServiceState.NotInstalled) return;
            using (var sc = new ServiceController(HelperService.Name))
            {
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
            }
        }

        private static void CopyWithRetry(string from, string to)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(from, to, true);
                    return;
                }
                catch (IOException) when (attempt < 40)
                {
                    Thread.Sleep(250); // the helper process may still be exiting
                }
            }
        }

        private static void Sc(string args, bool ignoreErrors = false)
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && !ignoreErrors)
                    throw new InvalidOperationException($"sc {args.Split(' ')[0]} failed: {output.Trim()}");
            }
        }
    }
}
