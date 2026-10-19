using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace GlobalMK
{
    internal static class Program
    {
        private const string MutexName = @"Local\GlobalMK.SingleInstance";
        private const string ShowEventName = @"Local\GlobalMK.Show";

        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int render = Array.IndexOf(args, "--render-ui");
            if (render >= 0) return UiPreview.Render(args.Length > render + 1 ? args[render + 1] : ".");

            // Helper-service modes run before the single-instance check: they live alongside the app.
            switch (args.FirstOrDefault())
            {
                case "--service":
                    System.ServiceProcess.ServiceBase.Run(new HelperService());
                    return 0;
                case "--input-helper":
                    return InputHelper.Run(args);
                case ServiceSetup.InstallArg:
                case ServiceSetup.UninstallArg:
                    Directory.CreateDirectory(AppConfig.Directory);
                    Log.Init(Path.Combine(AppConfig.Directory, "log.txt"));
                    return args[0] == ServiceSetup.InstallArg ? ServiceSetup.Install() : ServiceSetup.Uninstall();
            }

            Mutex instance;
            bool owned;
            try
            {
                instance = new Mutex(true, MutexName, out owned);
            }
            catch (UnauthorizedAccessException)
            {
                // The running copy is elevated, so this non-elevated copy can't even open its mutex.
                MessageBox.Show("Global Mouse & Keyboard is already running as administrator.\nOpen it from its icon in the notification area.",
                    "Global Mouse & Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            using (var mutex = instance)
            {
                if (!owned)
                {
                    if (!args.Contains("--wait-for-exit"))
                    {
                        // Already running: bring the existing window forward instead.
                        try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                        return 0;
                    }
                    try { owned = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) return 1;
                }
                try
                {
                    return Run(args);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static int Run(string[] args)
        {
            Directory.CreateDirectory(AppConfig.Directory);
            Log.Init(Path.Combine(AppConfig.Directory, "log.txt"));
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Error("Unexpected error: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Fatal error: " + e.ExceptionObject);

            bool firstRun = !File.Exists(AppConfig.FilePath);
            var config = AppConfig.Load();

            var invoker = new Control();
            _ = invoker.Handle; // a window handle on the UI thread, used to marshal work back from other threads
            var app = new AppController(config, invoker);
            var form = new MainForm(app);

            try { app.Start(); }
            catch (Exception ex) { Log.Error("Start failed: " + ex.Message); }

            bool hidden = (args.Contains("--minimized") || config.StartMinimized) && !firstRun && app.StartError == null;
            if (!hidden) form.Show();

            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            {
                var watcher = new Thread(() =>
                {
                    while (showEvent.WaitOne())
                    {
                        try { invoker.BeginInvoke(new Action(form.ShowMain)); } catch { return; }
                    }
                }) { IsBackground = true, Name = "GMK show watcher" };
                watcher.Start();

                Application.Run();
            }

            app.Stop();
            return 0;
        }
    }

    /// <summary>Renders the main window with sample data to PNG files (used to check the UI during development).</summary>
    internal static class UiPreview
    {
        public static int Render(string dir)
        {
            Directory.CreateDirectory(dir);
            var cfg = new AppConfig { MachineName = "OFFICE-PC", Peers = { "192.168.0.20" } };
            cfg.Passphrase = "correct horse battery";
            var invoker = new Control();
            invoker.CreateControl();
            var app = new AppController(cfg, invoker);
            using (var form = new MainForm(app, preview: true)
            {
                PreviewMachines = new[]
                {
                    new EditorMachine
                    {
                        Name = "OFFICE-PC", IsLocal = true, Online = true, Active = true,
                        Monitors = new[] { new Rectangle(0, 0, 2560, 1440), new Rectangle(-1920, 360, 1920, 1080) },
                        PrimaryIndex = 0, Position = new Point(0, 0),
                    },
                    new EditorMachine
                    {
                        Name = "LAPTOP", Online = true,
                        Monitors = new[] { new Rectangle(0, 0, 1920, 1200) }, Position = new Point(4480, 240),
                    },
                    new EditorMachine
                    {
                        Name = "MEDIA-PC", Online = false,
                        Monitors = new[] { new Rectangle(0, 0, 3840, 2160) }, Position = new Point(1920, 1440),
                    },
                },
            })
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000);
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(800, 1060); // tall enough to show the whole Setup tab
                form.Show();
                foreach (TabPage page in form.Controls.OfType<TabControl>().First().TabPages)
                {
                    ((TabControl)page.Parent).SelectedTab = page;
                    Application.DoEvents();
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                        string file = Path.Combine(dir, "ui-" + page.Text.Split(' ')[0].ToLowerInvariant() + ".png");
                        bmp.Save(file, ImageFormat.Png);
                    }
                }
                form.ExitApp();
            }
            return 0;
        }
    }
}
