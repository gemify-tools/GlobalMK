using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GlobalMK
{
    /// <summary>Elevation, Windows Firewall and start-with-Windows helpers. Each runs only when the user asks.</summary>
    internal static class SystemIntegration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "GlobalMK";
        private const string TaskName = "GlobalMK";
        private const string FirewallRuleName = "Global Mouse and Keyboard";

        public static string ExePath => Application.ExecutablePath;

        public static bool IsElevated
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>Start an elevated copy (UAC prompt). Returns false if the user declined.</summary>
        public static bool StartElevatedCopy(string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ExePath, args) { Verb = "runas", UseShellExecute = true });
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Replace any firewall rules for this program (including "block" rules Windows creates when its
        /// first-run prompt is dismissed) with allow rules for private-range addresses only.
        /// </summary>
        public static bool AllowThroughFirewall(int port, out string error)
        {
            error = null;
            const string ranges = "LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10";
            string exe = ExePath;
            string cmd =
                $"/c netsh advfirewall firewall delete rule name=all program=\"{exe}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall add rule name=\"{FirewallRuleName} (TCP)\" dir=in action=allow program=\"{exe}\" protocol=TCP localport={port} profile=any remoteip={ranges} >nul && " +
                $"netsh advfirewall firewall add rule name=\"{FirewallRuleName} (discovery)\" dir=in action=allow program=\"{exe}\" protocol=UDP localport={port + 1} profile=any remoteip={ranges} >nul";
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0) error = "netsh reported error " + p.ExitCode;
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception)
            {
                error = "administrator permission was not granted";
                return false;
            }
        }

        public static bool IsStartupEnabled => RunValueExists() || TaskExists();

        /// <summary>
        /// When running as administrator a logon task with highest privileges is used, so the shared mouse and
        /// keyboard also work in elevated windows after a reboot. Otherwise a per-user Run entry is used.
        /// </summary>
        public static void SetStartup(bool enable)
        {
            if (enable)
            {
                if (IsElevated)
                {
                    CreateLogonTask();
                    SetRunValue(false);
                }
                else
                {
                    SetRunValue(true);
                }
            }
            else
            {
                SetRunValue(false);
                if (TaskExists() && RunSchtasks($"/Delete /TN \"{TaskName}\" /F") != 0)
                    throw new InvalidOperationException("The startup task was created as administrator; restart as administrator to remove it.");
            }
        }

        private static bool RunValueExists()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                return key?.GetValue(RunValueName) != null;
        }

        private static void SetRunValue(bool enable)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enable) key.SetValue(RunValueName, $"\"{ExePath}\" --minimized");
                else if (key.GetValue(RunValueName) != null) key.DeleteValue(RunValueName);
            }
        }

        private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

        private static void CreateLogonTask()
        {
            string user;
            using (var id = WindowsIdentity.GetCurrent()) user = id.Name;
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Global Mouse &amp; Keyboard</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(ExePath)}</Command><Arguments>--minimized</Arguments></Exec></Actions>
</Task>";
            string path = Path.Combine(Path.GetTempPath(), "GlobalMK-task.xml");
            File.WriteAllText(path, xml, Encoding.Unicode);
            try
            {
                if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F") != 0)
                    throw new InvalidOperationException("Windows Task Scheduler refused to create the startup task.");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static int RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode;
            }
        }
    }
}
