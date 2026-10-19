using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GlobalMK
{
    internal sealed partial class MainForm
    {
        private bool _exiting;
        private bool _shownTrayTip;
        private Color _trayDot = Color.Empty;
        private int _ticks;
        private bool _helperBusy;

        public IEnumerable<EditorMachine> PreviewMachines { get; set; }

        private void WireEvents()
        {
            _chkShowPass.CheckedChanged += (s, e) => _txtPass.UseSystemPasswordChar = !_chkShowPass.Checked;
            _btnApply.Click += (s, e) => ApplySettings();
            _btnFirewall.Click += (s, e) => OpenFirewall();
            _btnAdmin.Click += (s, e) => RestartAsAdmin();
            _btnHelperInstall.Click += (s, e) => RunHelperSetup(ServiceSetup.InstallArg);
            _btnHelperRemove.Click += (s, e) => RunHelperSetup(ServiceSetup.UninstallArg);
            _editor.MachineMoved += (name, pos) => _app.MoveMachine(name, pos);
            _editor.ForgetRequested += name => _app.ForgetMachine(name);
            _app.StatusChanged += RefreshAll;

            if (_tray != null)
            {
                _tray.DoubleClick += (s, e) => ShowMain();
                _miOpen.Click += (s, e) => ShowMain();
                _miLock.Click += (s, e) => _app.Capture?.SetLocked(!_app.Capture.Locked);
                _miHome.Click += (s, e) => _app.Capture?.ReturnToLocal();
                _miExit.Click += (s, e) => ExitApp();
                _tray.ContextMenuStrip.Opening += (s, e) =>
                {
                    _miLock.Visible = _miHome.Visible = _app.Capture != null;
                    _miLock.Checked = _app.Capture?.Locked == true;
                    _miHome.Enabled = _app.Capture?.ActiveName != null;
                };
                _timer.Tick += (s, e) =>
                {
                    RefreshHeader();
                    if (_tabs.SelectedTab == _tabStatus) RefreshStatusTab();
                    if (_tabs.SelectedTab == _tabSetup && ++_ticks % 3 == 0) RefreshHelperRow();
                };
                _timer.Start();
            }

            foreach (var line in Log.Recent()) _txtLog.AppendText(line + Environment.NewLine);
            Log.Line += OnLogLine;
            LoadSettingsIntoUi(_app.Config);
        }

        private void OnLogLine(string line)
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (_txtLog.TextLength > 200000) _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 100000);
                _txtLog.AppendText(line + Environment.NewLine);
            }));
        }

        // ------------------------------------------------------------------ settings

        private void LoadSettingsIntoUi(AppConfig c)
        {
            _rbServer.Checked = c.RoleValue == Role.Server;
            _rbClient.Checked = c.RoleValue == Role.Client;
            _txtName.Text = c.MachineName;
            _txtPass.Text = c.Passphrase;
            _txtPeers.Text = string.Join(", ", c.Peers);
            _numPort.Value = Math.Max(_numPort.Minimum, Math.Min(_numPort.Maximum, c.Port));
            _chkClipboard.Checked = c.ClipboardSync;
            _chkHideCursor.Checked = c.HideCursorWhenRemote;
            _chkAutoConnect.Checked = c.AutoConnectDiscovered;
            _chkOtherNetworks.Checked = c.DiscoverOtherNetworks;
            _chkPrivateOnly.Checked = c.OnlyPrivateNetworks;
            _chkStartMin.Checked = c.StartMinimized;
            _numDelay.Value = c.SwitchDelayMs;
            _numCorner.Value = c.CornerDeadZone;
            try { _chkStartup.Checked = !_preview && SystemIntegration.IsStartupEnabled; } catch { }

            var ips = Node.LocalIPv4Addresses();
            _txtMyIps.Text = ips.Count == 0 ? "(no network connection)" : string.Join(",  ", ips);

            bool admin = !_preview && SystemIntegration.IsElevated;
            _btnAdmin.Visible = !admin;
            if (!admin) SetShield(_btnAdmin);
            SetShield(_btnFirewall);
            SetShield(_btnHelperInstall);
            SetShield(_btnHelperRemove);
            _lblAdmin.Text = admin
                ? "Running as administrator: the shared mouse and keyboard also work in administrator windows."
                : "Windows blocks input into administrator windows (Task Manager, installers) unless this app also runs as administrator.";
        }

        private void ApplySettings()
        {
            string name = _txtName.Text.Trim();
            string pass = _txtPass.Text;
            if (name.Length == 0 || name.IndexOf('|') >= 0)
            {
                Warn("Enter a computer name (without the | character).");
                return;
            }
            if (pass.Length < 8)
            {
                Warn("Choose a passphrase of at least 8 characters, and enter the same one on every computer.");
                return;
            }

            var cfg = _app.Config.Clone();
            string oldName = cfg.MachineName;
            cfg.RoleValue = _rbClient.Checked ? Role.Client : Role.Server;
            cfg.MachineName = name;
            cfg.Passphrase = pass;
            cfg.Peers = _txtPeers.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            cfg.Port = (int)_numPort.Value;
            cfg.ClipboardSync = _chkClipboard.Checked;
            cfg.HideCursorWhenRemote = _chkHideCursor.Checked;
            cfg.AutoConnectDiscovered = _chkAutoConnect.Checked;
            cfg.DiscoverOtherNetworks = _chkOtherNetworks.Checked;
            cfg.OnlyPrivateNetworks = _chkPrivateOnly.Checked;
            cfg.StartMinimized = _chkStartMin.Checked;
            cfg.SwitchDelayMs = (int)_numDelay.Value;
            cfg.CornerDeadZone = (int)_numCorner.Value;
            if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
            {
                var mine = cfg.FindLayout(oldName);
                if (mine != null && cfg.FindLayout(name) == null) mine.Name = name;
            }

            try
            {
                if (_chkStartup.Checked != SystemIntegration.IsStartupEnabled) SystemIntegration.SetStartup(_chkStartup.Checked);
            }
            catch (Exception ex)
            {
                Warn("Could not change the start-with-Windows setting: " + ex.Message);
            }

            Cursor = Cursors.WaitCursor;
            try { _app.Restart(cfg); }
            catch (Exception ex) { Warn("Could not apply settings: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
            LoadSettingsIntoUi(_app.Config);
            RefreshAll();
            if (_app.IsServer && _app.StartError == null && (_app.Node?.Connections.Length ?? 0) == 0)
                _tabs.SelectedTab = _tabLayout;
        }

        private void OpenFirewall()
        {
            if (SystemIntegration.AllowThroughFirewall(_app.Config.Port, out string error))
                MessageBox.Show(this, $"Windows Firewall now allows connections to TCP port {_app.Config.Port} from home and private network addresses.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                Warn("The firewall rule was not added: " + error);
        }

        private void RestartAsAdmin()
        {
            if (SystemIntegration.StartElevatedCopy("--wait-for-exit")) ExitApp();
        }

        private void Warn(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private void RunHelperSetup(string arg)
        {
            _helperBusy = true;
            _btnHelperInstall.Enabled = _btnHelperRemove.Enabled = false;
            _lblHelper.Text = arg == ServiceSetup.InstallArg ? "Installing the helper service…" : "Removing the helper service…";
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = ServiceSetup.RunElevated(arg, out string error);
                BeginInvoke(new Action(() =>
                {
                    _helperBusy = false;
                    _btnHelperInstall.Enabled = _btnHelperRemove.Enabled = true;
                    if (!ok) Warn((arg == ServiceSetup.InstallArg ? "The helper service was not installed: " : "The helper service was not removed: ") + error);
                    RefreshHelperRow();
                }));
            });
        }

        private void RefreshHelperRow()
        {
            if (_helperBusy) return;
            if (_preview)
            {
                _lblHelper.Text = "Ready: the shared keyboard and mouse also work on this computer's lock screen and in UAC prompts.";
                _btnHelperInstall.Visible = false;
                return;
            }

            var service = ServiceSetup.Query();
            bool client = !_app.IsServer;
            string text, install = null;
            if (service == HelperServiceState.NotInstalled)
            {
                text = client
                    ? "Not set up. Without the helper service, the shared keyboard and mouse can't type into this computer's lock screen or UAC prompts."
                    : "Only needed on client computers, so they can be unlocked with the shared keyboard.";
                install = "Install helper service…";
            }
            else if (ServiceSetup.IsOutdated())
            {
                text = "The helper service is from a different version of this app.";
                install = "Update helper service…";
            }
            else if (service != HelperServiceState.Running)
            {
                text = service == HelperServiceState.Starting ? "The helper service is starting…" : "The helper service is installed but not running.";
                install = "Repair helper service…";
            }
            else if (!client)
            {
                text = "The helper service is running. It is used when this computer is a client.";
            }
            else
            {
                switch (_app.Helper?.State)
                {
                    case HelperLinkState.Connected:
                        text = "Ready: the shared keyboard and mouse also work on this computer's lock screen and in UAC prompts.";
                        break;
                    case HelperLinkState.NeedsAdministrator:
                        text = "The helper service is running, but only an app running as administrator may use it. Choose Restart as administrator.";
                        break;
                    case HelperLinkState.Untrusted:
                        text = "Another program is pretending to be the helper, so it isn't used. Restart the computer; if this message stays, scan it for malware.";
                        break;
                    default:
                        text = SystemIntegration.IsElevated
                            ? "The helper service is running; connecting to it…"
                            : "The helper service is running, but only an app running as administrator may use it. Choose Restart as administrator.";
                        break;
                }
            }
            _lblHelper.Text = text;
            _btnHelperInstall.Visible = install != null;
            if (install != null) _btnHelperInstall.Text = install;
            _btnHelperRemove.Visible = service != HelperServiceState.NotInstalled;
        }

        // ------------------------------------------------------------------ status

        private void RefreshAll()
        {
            if (IsDisposed) return;
            RefreshHeader();
            RefreshLayoutTab();
            RefreshStatusTab();
            RefreshHelperRow();
        }

        private void RefreshHeader()
        {
            string title, sub;
            Color dot;
            if (_preview)
            {
                title = "2 computers connected";
                sub = "Mouse and keyboard are on this computer";
                dot = Color.FromArgb(22, 163, 74);
            }
            else if (_app.StartError != null)
            {
                title = "Not running yet";
                sub = _app.StartError;
                dot = Color.FromArgb(217, 119, 6);
            }
            else
            {
                var conns = _app.Node?.Connections ?? new Connection[0];
                dot = conns.Length > 0 ? Color.FromArgb(22, 163, 74) : Color.FromArgb(156, 163, 175);
                if (_app.IsServer)
                {
                    title = conns.Length == 0 ? "Waiting for other computers" : conns.Length == 1 ? "1 computer connected" : $"{conns.Length} computers connected";
                    string active = _app.Capture?.ActiveName;
                    sub = active == null ? "Mouse and keyboard are on this computer" : $"Mouse and keyboard are on {active}";
                    if (_app.Capture?.Locked == true) sub += " · locked (Scroll Lock to unlock)";
                }
                else
                {
                    var server = conns.FirstOrDefault();
                    title = server == null ? "Waiting for the server" : $"Controlled by {server.PeerName}";
                    sub = server == null
                        ? (_app.Config.Peers.Count > 0 ? "Trying " + string.Join(", ", _app.Config.Peers) : "Looking for a server on this network…")
                        : _app.Injector?.Active == true ? "The shared mouse and keyboard are on this computer" : "The shared mouse and keyboard are on another computer";
                }
                if (_app.Node?.ListenError != null) sub += " · port " + _app.Config.Port + " unavailable";
            }

            _statusTitle.Text = title;
            _statusSub.Text = sub;
            _statusDot.ForeColor = dot;
            if (_tray != null)
            {
                string tip = "Global Mouse & Keyboard — " + title;
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                if (dot != _trayDot)
                {
                    _trayDot = dot;
                    var old = _tray.Icon;
                    _tray.Icon = AppIcon.Small(dot);
                    old?.Dispose();
                }
            }
        }

        private void RefreshLayoutTab()
        {
            bool server = _preview || _app.IsServer;
            _editor.Visible = server;
            _lblLayoutHelp.Visible = server;
            _lblClientLayout.Visible = !server;
            if (!server) return;
            if (_preview && PreviewMachines != null)
            {
                _editor.SetMachines(PreviewMachines);
                return;
            }
            string active = _app.Capture?.ActiveName;
            _editor.SetMachines(_app.BuildSnapshot().Machines.Select(m => EditorMachine.From(m, active)));
        }

        private void RefreshStatusTab()
        {
            var conns = _app.Node?.Connections ?? new Connection[0];
            _lvPeers.BeginUpdate();
            _lvPeers.Items.Clear();
            foreach (var c in conns)
            {
                var screens = string.Join(", ", (c.PeerScreens ?? new ScreenInfo[0]).Select(s => $"{s.Bounds.Width}×{s.Bounds.Height}"));
                _lvPeers.Items.Add(new ListViewItem(new[]
                {
                    c.PeerName,
                    c.PeerRole == Role.Server ? "server" : "client",
                    c.Remote.Address.ToString(),
                    screens,
                    c.RttMs < 0 ? "–" : c.RttMs + " ms",
                    c.ConnectedAt.ToShortTimeString(),
                }));
            }
            _lvPeers.EndUpdate();

            var found = _app.Node?.Discovered ?? new DiscoveredPeer[0];
            _lvDiscovered.BeginUpdate();
            _lvDiscovered.Items.Clear();
            foreach (var d in found)
                _lvDiscovered.Items.Add(new ListViewItem(new[]
                {
                    d.Name,
                    d.Role == Role.Server ? "server" : "client",
                    $"{d.Address}:{d.Port}",
                    d.OtherNetwork ? "other home network" : "this network",
                }));
            _lvDiscovered.EndUpdate();
        }

        // ------------------------------------------------------------------ window & tray

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RefreshAll();
            if (!_preview && _app.StartError != null) _tabs.SelectedTab = _tabSetup;
        }

        public void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing && _tray != null)
            {
                e.Cancel = true;
                Hide();
                if (!_shownTrayTip)
                {
                    _shownTrayTip = true;
                    _tray.ShowBalloonTip(4000, "Still running", "Global Mouse & Keyboard keeps sharing your mouse and keyboard. Right-click this icon to exit.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        public void ExitApp()
        {
            _exiting = true;
            Log.Line -= OnLogLine;
            _timer?.Stop();
            _app.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            Close();
            Application.ExitThread();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void SetShield(Button b)
        {
            if (b.FlatStyle != FlatStyle.System) return;
            SendMessage(b.Handle, 0x160C /* BCM_SETSHIELD */, IntPtr.Zero, (IntPtr)1);
        }
    }
}
