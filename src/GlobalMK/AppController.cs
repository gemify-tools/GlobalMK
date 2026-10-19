using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GlobalMK
{
    /// <summary>
    /// Wires networking, input capture/injection, clipboard and layout together. All config and layout
    /// changes happen on the UI thread; network and hook threads hand work over with BeginInvoke.
    /// </summary>
    internal sealed class AppController : IDisposable
    {
        public static readonly string AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private static string _cachedPassphrase;
        private static byte[] _cachedKey;

        private readonly Control _ui;
        private readonly ConcurrentDictionary<Connection, ClipboardAssembler> _assemblers = new ConcurrentDictionary<Connection, ClipboardAssembler>();
        private ClipboardSync _clipboard;
        private CursorHider _hider;
        private ScreenInfo[] _localScreens = new ScreenInfo[0];
        private bool _running;

        public AppConfig Config { get; private set; }
        public Node Node { get; private set; }
        public InputCapture Capture { get; private set; }
        public InputInjector Injector { get; private set; }
        public HelperLink Helper { get; private set; }
        public string StartError { get; private set; }
        public bool IsServer => Config.RoleValue == Role.Server;

        /// <summary>Raised on the UI thread when anything shown in the UI may have changed.</summary>
        public event Action StatusChanged;

        public AppController(AppConfig config, Control uiInvoker)
        {
            Config = config;
            _ui = uiInvoker;
        }

        public void Start()
        {
            StartError = null;
            _localScreens = Screens.Enumerate();
            UpsertLayout(Config.MachineName, _localScreens, isLocal: true);
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _running = true;

            string pass = Config.Passphrase;
            if (string.IsNullOrEmpty(pass))
            {
                StartError = "Choose a passphrase on the Setup tab — use the same one on every computer.";
                Log.Warn(StartError);
                RaiseStatus();
                return;
            }
            if (pass != _cachedPassphrase)
            {
                _cachedKey = SecureChannel.DerivePassphraseKey(pass);
                _cachedPassphrase = pass;
            }

            Node = new Node(new NodeOptions
            {
                Role = Config.RoleValue,
                Name = Config.MachineName,
                Port = Config.Port,
                PassphraseKey = _cachedKey,
                Peers = Config.Peers.ToList(),
                AutoConnectDiscovered = Config.AutoConnectDiscovered,
                OnlyPrivateNetworks = Config.OnlyPrivateNetworks,
                DiscoverOtherNetworks = Config.DiscoverOtherNetworks,
                KnownPeers = Config.KnownPeers.Select(ParseEndpoint).Where(e => e != null).ToList(),
                AppVersion = AppVersion,
                GetScreens = () => _localScreens,
            });
            Node.PeerConnected += c => Post(() => OnPeerConnected(c));
            Node.PeerDisconnected += c => Post(() => OnPeerDisconnected(c));
            Node.MessageReceived += OnMessage;

            if (IsServer)
            {
                _hider = new CursorHider();
                Capture = new InputCapture { SwitchDelayMs = Config.SwitchDelayMs, CornerSize = Config.CornerDeadZone };
                Capture.StateChanged += () => Post(OnCaptureStateChanged);
                Capture.SetLayout(BuildSnapshot());
                Capture.Start();
            }
            else
            {
                Injector = new InputInjector();
                Injector.ActiveChanged += () => Post(RaiseStatus);
                // If the helper service is installed, input goes through it so it also reaches the lock screen and UAC prompts.
                Helper = new HelperLink(HelperProtocol.PipeName(System.Diagnostics.Process.GetCurrentProcess().SessionId));
                Helper.StateChanged += () => Post(RaiseStatus);
                Injector.Forward = Helper;
                Helper.Start();
            }

            _clipboard = new ClipboardSync(Config.MachineName, OnLocalClipboard) { Enabled = Config.ClipboardSync };
            Node.Start();
            Log.Info($"Started as {(IsServer ? "server" : "client")} \"{Config.MachineName}\" (v{AppVersion}{(SystemIntegration.IsElevated ? ", administrator" : "")}).");
            RaiseStatus();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            Capture?.Dispose();
            Capture = null;
            Injector?.ReleaseAll();
            Injector = null;
            Helper?.Dispose();
            Helper = null;
            Node?.Stop();
            Node = null;
            _clipboard?.Dispose();
            _clipboard = null;
            if (_hider != null)
            {
                _hider.Close();
                _hider.Dispose();
                _hider = null;
            }
            _assemblers.Clear();
        }

        public void Restart(AppConfig newConfig)
        {
            Stop();
            Config = newConfig;
            Config.Save();
            Start();
        }

        public void Dispose() => Stop();

        // ------------------------------------------------------------------ layout

        public LayoutSnapshot BuildSnapshot()
        {
            var machines = new List<MachineGeom>();
            var local = Config.FindLayout(Config.MachineName);
            machines.Add(new MachineGeom(Config.MachineName, true, _localScreens, new Point(local?.X ?? 0, local?.Y ?? 0), true, null));
            if (IsServer)
            {
                foreach (var entry in Config.Layout)
                {
                    if (string.Equals(entry.Name, Config.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                    var conn = Node?.Get(entry.Name);
                    var screens = conn?.PeerScreens ?? ToScreens(entry.Screens);
                    machines.Add(new MachineGeom(entry.Name, false, screens, new Point(entry.X, entry.Y), conn != null, conn));
                }
            }
            return new LayoutSnapshot(machines);
        }

        public void MoveMachine(string name, Point position)
        {
            var entry = Config.FindLayout(name);
            if (entry == null) return;
            entry.X = position.X;
            entry.Y = position.Y;
            SaveQuietly();
            RefreshLayout();
        }

        public void ForgetMachine(string name)
        {
            if (string.Equals(name, Config.MachineName, StringComparison.OrdinalIgnoreCase)) return;
            Config.Layout.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            SaveQuietly();
            RefreshLayout();
        }

        private void RefreshLayout()
        {
            Capture?.SetLayout(BuildSnapshot());
            RaiseStatus();
        }

        private void UpsertLayout(string name, ScreenInfo[] screens, bool isLocal)
        {
            var entry = Config.FindLayout(name);
            if (entry == null)
            {
                entry = new MachineLayout { Name = name };
                if (!isLocal)
                {
                    // New computers start to the right of everything already placed, top-aligned with this one.
                    var snap = BuildSnapshot();
                    int right = snap.Machines.Max(m => m.GlobalBounds.Right);
                    entry.X = right;
                    entry.Y = snap.Local.Position.Y;
                }
                Config.Layout.Add(entry);
            }
            entry.Screens = screens.Select(s => new ScreenRect { X = s.Bounds.X, Y = s.Bounds.Y, W = s.Bounds.Width, H = s.Bounds.Height, Primary = s.Primary }).ToList();
            SaveQuietly();
        }

        private static ScreenInfo[] ToScreens(List<ScreenRect> rects) =>
            (rects ?? new List<ScreenRect>()).Select(r => new ScreenInfo(new Rectangle(r.X, r.Y, r.W, r.H), r.Primary)).ToArray();

        private void SaveQuietly()
        {
            try { Config.Save(); }
            catch (Exception ex) { Log.Warn("Could not save settings: " + ex.Message); }
        }

        // ------------------------------------------------------------------ events

        private void OnPeerConnected(Connection c)
        {
            if (!_running || Node == null) return;
            if (IsServer) UpsertLayout(c.PeerName, c.PeerScreens, isLocal: false);
            // An address we reached ourselves works from here; remember it for a direct reconnect next time.
            if (c.Initiator && Config.RememberPeer(c.Remote.ToString())) SaveQuietly();
            RefreshLayout();
        }

        private static System.Net.IPEndPoint ParseEndpoint(string s)
        {
            int colon = s.LastIndexOf(':');
            return colon > 0 && System.Net.IPAddress.TryParse(s.Substring(0, colon), out var ip) && int.TryParse(s.Substring(colon + 1), out int port)
                ? new System.Net.IPEndPoint(ip, port)
                : null;
        }

        private void OnPeerDisconnected(Connection c)
        {
            if (!_running) return;
            _assemblers.TryRemove(c, out _);
            if (!IsServer) Injector?.ReleaseAll();
            RefreshLayout();
        }

        /// <summary>Called on a connection's reader thread.</summary>
        private void OnMessage(Connection c, byte[] p)
        {
            var type = Proto.TypeOf(p);
            switch (type)
            {
                case MsgType.Enter:
                case MsgType.Leave:
                case MsgType.MouseMove:
                case MsgType.MouseButton:
                case MsgType.MouseWheel:
                case MsgType.Key:
                    if (c.PeerRole == Role.Server) Injector?.Handle(p);
                    break;
                case MsgType.Screens:
                    c.PeerScreens = Proto.ReadScreens(p);
                    Post(() =>
                    {
                        if (!_running) return;
                        if (IsServer) UpsertLayout(c.PeerName, c.PeerScreens, isLocal: false);
                        RefreshLayout();
                    });
                    break;
                case MsgType.Clipboard:
                    var clip = _assemblers.GetOrAdd(c, _ => new ClipboardAssembler()).Add(p);
                    if (clip != null) Post(() => OnRemoteClipboard(c, clip));
                    break;
            }
        }

        private void OnRemoteClipboard(Connection from, ClipData clip)
        {
            if (!_running || _clipboard == null || !Config.ClipboardSync) return;
            _clipboard.Apply(clip);
            if (IsServer)
            {
                // Relay to the other clients so every computer shares one clipboard.
                foreach (var c in Node.Connections)
                    if (c != from) SendClipboard(c, clip);
            }
        }

        private void OnLocalClipboard(ClipData clip)
        {
            if (Node == null || !Config.ClipboardSync) return;
            foreach (var c in Node.Connections) SendClipboard(c, clip);
        }

        private static void SendClipboard(Connection c, ClipData clip)
        {
            try
            {
                foreach (var chunk in ClipboardTransfer.Encode(clip)) c.SendBulk(chunk);
            }
            catch (Exception ex)
            {
                Log.Warn("Clipboard not shared: " + ex.Message);
            }
        }

        private void OnCaptureStateChanged()
        {
            if (!_running || Capture == null) return;
            if (Capture.ActiveName != null && Config.HideCursorWhenRemote)
            {
                _hider?.ShowAt(Capture.ParkPoint);
                Capture.Repark();
            }
            else _hider?.Hide();
            RaiseStatus();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e) => Post(() =>
        {
            if (!_running) return;
            _localScreens = Screens.Enumerate();
            UpsertLayout(Config.MachineName, _localScreens, isLocal: true);
            Log.Info("Display settings changed: " + string.Join(", ", _localScreens.Select(s => s.ToString())));
            if (Node != null)
                foreach (var c in Node.Connections) c.Send(Proto.Screens(_localScreens));
            RefreshLayout();
        });

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // Locking this computer (Win+L) or switching users: bring the cursor home so that after
            // unlocking, the mouse and keyboard work here as expected.
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteConnect)
                Capture?.ReturnToLocal();
            if (e.Reason == SessionSwitchReason.SessionLock) Injector?.ReleaseAll();
        }

        private void RaiseStatus() => StatusChanged?.Invoke();

        private void Post(Action a)
        {
            try
            {
                if (_ui.IsHandleCreated && !_ui.IsDisposed) _ui.BeginInvoke(a);
            }
            catch (InvalidOperationException) { /* shutting down */ }
        }
    }
}
