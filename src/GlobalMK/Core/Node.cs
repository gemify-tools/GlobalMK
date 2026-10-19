using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GlobalMK
{
    public sealed class NodeOptions
    {
        public Role Role;
        public string Name;
        public int Port = AppConfig.DefaultPort;
        public byte[] PassphraseKey;
        public List<string> Peers = new List<string>();
        public bool AutoConnectDiscovered = true;
        public bool OnlyPrivateNetworks = true;
        public bool EnableDiscovery = true;
        public bool DiscoverOtherNetworks = true;
        /// <summary>TCP endpoints this computer connected to before; re-dialed directly.</summary>
        public List<IPEndPoint> KnownPeers = new List<IPEndPoint>();
        /// <summary>Additional UDP endpoints to probe (diagnostics and tests).</summary>
        public Func<IEnumerable<IPEndPoint>> ExtraProbeTargets = () => Enumerable.Empty<IPEndPoint>();
        public IPAddress ListenAddress = IPAddress.Any;
        public string AppVersion = "1.0.0";
        public Func<ScreenInfo[]> GetScreens = () => new ScreenInfo[0];
    }

    /// <summary>
    /// Networking for one computer. Every instance listens for TCP connections AND dials the computers it
    /// finds or was given, so only one side of a pair needs to be reachable — which is what makes computers on
    /// different home networks (192.168.0.x and 192.168.1.x) behind different routers work. See <see cref="Discovery"/>.
    /// </summary>
    public sealed class Node : IDisposable
    {
        private const int RetryMs = 3000;
        private const int ConnectTimeoutMs = 4000;

        private readonly NodeOptions _o;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Connection> _conns = new Dictionary<string, Connection>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _autoDialing = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, (DateTime until, int failures)> _backoff = new ConcurrentDictionary<string, (DateTime, int)>();
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private TcpListener _listener;
        private Discovery _discovery;
        private volatile bool _stopped;

        public event Action<Connection> PeerConnected;
        public event Action<Connection> PeerDisconnected;
        public event Action<Connection, byte[]> MessageReceived;

        public string ListenError { get; private set; }
        public int DiscoveryPort => _o.Port + 1;

        public Node(NodeOptions options) => _o = options;

        public Connection[] Connections
        {
            get { lock (_lock) return _conns.Values.Where(c => c.IsAlive).ToArray(); }
        }

        public Connection Get(string name)
        {
            lock (_lock) return _conns.TryGetValue(name, out var c) && c.IsAlive ? c : null;
        }

        public DiscoveredPeer[] Discovered => _discovery?.Peers ?? new DiscoveredPeer[0];

        public void Start()
        {
            try
            {
                _listener = new TcpListener(_o.ListenAddress, _o.Port);
                _listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "GMK listener" }.Start();
                Log.Info($"Listening on TCP port {_o.Port}");
            }
            catch (Exception ex)
            {
                ListenError = ex.Message;
                Log.Error($"Cannot listen on port {_o.Port}: {ex.Message}. Another program may be using it — choose a different port on all computers.");
            }

            foreach (var peer in _o.Peers)
            {
                string target = peer;
                new Thread(() => DialLoop(target)) { IsBackground = true, Name = "GMK dial " + target }.Start();
            }

            if (_o.EnableDiscovery) StartDiscovery();
            if (_o.KnownPeers.Count > 0) new Thread(KnownPeersLoop) { IsBackground = true, Name = "GMK known peers" }.Start();
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _stopEvent.Set();
            try { _listener?.Stop(); } catch { }
            _discovery?.Dispose();
            Connection[] all;
            lock (_lock) all = _conns.Values.ToArray();
            foreach (var c in all) c.Close("shutting down");
        }

        public void Dispose() => Stop();

        // ---------------------------------------------------------------- incoming

        private void AcceptLoop()
        {
            while (!_stopped)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_stopped) return; Thread.Sleep(200); continue; }

                var ep = (IPEndPoint)tcp.Client.RemoteEndPoint;
                if (_o.OnlyPrivateNetworks && !IsPrivate(ep.Address))
                {
                    Log.Warn($"Refused connection from {ep.Address}: not a private network address.");
                    tcp.Close();
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Establish(tcp, false, ep.ToString()));
            }
        }

        // ---------------------------------------------------------------- outgoing

        private void DialLoop(string target)
        {
            string host = target;
            int port = _o.Port;
            int colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon && int.TryParse(target.Substring(colon + 1), out int p))
            {
                host = target.Substring(0, colon);
                port = p;
            }

            string learnedName = null;
            string lastError = null;
            while (!_stopped)
            {
                if ((learnedName != null && Get(learnedName) != null) || (_o.Role == Role.Client && HasServer()))
                {
                    if (_stopEvent.WaitOne(1000)) return;
                    continue;
                }
                try
                {
                    var conn = Dial(host, port);
                    if (conn != null)
                    {
                        lastError = null;
                        learnedName = conn.PeerName;
                        while (conn.IsAlive && !_stopped) _stopEvent.WaitOne(500);
                    }
                }
                catch (Exception ex)
                {
                    string msg = ex is SocketException se ? se.Message : ex.Message;
                    if (msg != lastError) Log.Warn($"Can't reach {host}:{port} — {msg} (will keep retrying)");
                    lastError = msg;
                }
                if (_stopEvent.WaitOne(RetryMs)) return;
            }
        }

        private Connection Dial(string host, int port)
        {
            IPAddress[] addrs = IPAddress.TryParse(host, out var ip) ? new[] { ip } : Dns.GetHostAddresses(host);
            var addr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            if (addr == null) throw new Exception("host name not found");

            var tcp = new TcpClient(addr.AddressFamily);
            var ar = tcp.BeginConnect(addr, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
            {
                tcp.Close();
                throw new Exception("timed out (is the other computer on, and the firewall open?)");
            }
            try { tcp.EndConnect(ar); }
            catch { tcp.Close(); throw; }
            return Establish(tcp, true, $"{host}:{port}");
        }

        // ---------------------------------------------------------------- handshake

        private Connection Establish(TcpClient tcp, bool initiator, string label)
        {
            try
            {
                tcp.NoDelay = true;
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var stream = tcp.GetStream();
                stream.ReadTimeout = Connection.TimeoutMs;
                stream.WriteTimeout = Connection.TimeoutMs;

                var channel = SecureChannel.Handshake(stream, _o.PassphraseKey, initiator);
                channel.WriteFrame(Proto.Hello(new HelloMsg
                {
                    ProtocolVersion = Proto.ProtocolVersion,
                    Role = _o.Role,
                    Name = _o.Name,
                    AppVersion = _o.AppVersion,
                    Screens = _o.GetScreens(),
                }));
                var hello = Proto.ReadHello(channel.ReadFrame());

                string reject = null;
                if (hello.ProtocolVersion != Proto.ProtocolVersion)
                    reject = $"incompatible app versions ({hello.AppVersion} vs {_o.AppVersion})";
                else if (hello.Role == _o.Role)
                    reject = _o.Role == Role.Server
                        ? "both computers are set to Server — set the one being controlled to Client"
                        : "both computers are set to Client — set the one with the mouse & keyboard to Server";
                else if (string.Equals(hello.Name, _o.Name, StringComparison.OrdinalIgnoreCase))
                    reject = $"both computers are named \"{_o.Name}\" — give each a unique name";
                else if (string.IsNullOrWhiteSpace(hello.Name))
                    reject = "the other computer has no name";

                if (reject != null)
                {
                    Log.Warn($"Rejected {label}: {reject}");
                    try { channel.WriteFrame(Proto.Notice("connection rejected: " + reject)); } catch { }
                    tcp.Close();
                    return null;
                }

                var conn = new Connection(tcp, stream, channel, hello, initiator, _o.Role);
                return Register(conn) ? conn : null;
            }
            catch (PeerAuthenticationException)
            {
                Log.Warn($"Connection with {label} failed: the passphrases don't match. Enter the same passphrase on both computers.");
            }
            catch (Exception ex)
            {
                if (!_stopped) Log.Warn($"Connection with {label} failed: {ex.Message}");
            }
            try { tcp.Close(); } catch { }
            return null;
        }

        private bool Register(Connection conn)
        {
            Connection replaced = null;
            string reject = null;
            lock (_lock)
            {
                if (_stopped) reject = "shutting down";
                else if (_o.Role == Role.Client && _conns.Values.Any(c => c.IsAlive && !string.Equals(c.PeerName, conn.PeerName, StringComparison.OrdinalIgnoreCase)))
                    reject = "this computer is already controlled by " + _conns.Values.First(c => c.IsAlive).PeerName;
                else if (_conns.TryGetValue(conn.PeerName, out var old) && old.IsAlive)
                {
                    // Both sides may have dialed each other. Both apply the same rule so exactly one link survives:
                    // prefer the server-initiated link; if both are the same kind the older one is stale.
                    if (conn.InitiatedByServer == old.InitiatedByServer || conn.InitiatedByServer)
                    {
                        replaced = old;
                        _conns[conn.PeerName] = conn;
                    }
                    else reject = "duplicate";
                }
                else _conns[conn.PeerName] = conn;
            }

            if (reject != null)
            {
                if (reject == "duplicate") conn.Close(reject);
                else
                {
                    Log.Warn($"Rejected {conn}: {reject}");
                    conn.Reject(reject);
                }
                return false;
            }

            conn.Closed += OnConnectionClosed;
            conn.MessageReceived += (c, p) => MessageReceived?.Invoke(c, p);
            replaced?.Close("replaced by a newer connection");
            conn.Start();
            Log.Info($"Connected to {conn.PeerName} ({conn.PeerRole.ToString().ToLowerInvariant()}) at {conn.Remote.Address}");
            PeerConnected?.Invoke(conn);
            return true;
        }

        private void OnConnectionClosed(Connection conn, string reason)
        {
            bool removed;
            lock (_lock)
                removed = _conns.TryGetValue(conn.PeerName, out var cur) && cur == conn && _conns.Remove(conn.PeerName);
            if (removed && !_stopped)
            {
                Log.Info($"Disconnected from {conn.PeerName}: {reason}");
                PeerDisconnected?.Invoke(conn);
            }
        }

        private bool HasServer()
        {
            lock (_lock) return _conns.Values.Any(c => c.IsAlive && c.PeerRole == Role.Server);
        }

        // ---------------------------------------------------------------- discovery & auto-connect

        private void StartDiscovery()
        {
            _discovery = new Discovery(new DiscoveryOptions
            {
                Role = _o.Role,
                Name = _o.Name,
                TcpPort = _o.Port,
                UdpPort = DiscoveryPort,
                BindAddress = _o.ListenAddress,
                Broadcast = !IPAddress.IsLoopback(_o.ListenAddress),
                OtherNetworks = _o.DiscoverOtherNetworks,
                ExtraTargets = () => _o.KnownPeers.Select(k => new IPEndPoint(k.Address, k.Port + 1))
                    .Concat(_o.Peers.Select(ParseTarget).Where(t => t != null).Select(t => new IPEndPoint(t.Address, t.Port + 1)))
                    .Concat(_o.ExtraProbeTargets()),
            });
            _discovery.PeerSeen += OnPeerSeen;
            _discovery.Start();
        }

        /// <summary>Connect to a computer discovery found, if it is the other role and we aren't linked yet.</summary>
        private void OnPeerSeen(DiscoveredPeer peer)
        {
            if (_stopped || !_o.AutoConnectDiscovered || peer.Role == _o.Role) return;
            if (_o.OnlyPrivateNetworks && !IsPrivate(peer.Address)) return;
            if (Get(peer.Name) != null || (_o.Role == Role.Client && HasServer())) return;
            AutoDial(new IPEndPoint(peer.Address, peer.Port), peer.Name);
        }

        /// <summary>Re-dial computers this one connected to before, in case discovery packets are blocked.</summary>
        private void KnownPeersLoop()
        {
            if (_stopEvent.WaitOne(2000)) return;
            do
            {
                foreach (var ep in _o.KnownPeers)
                {
                    if (_o.Role == Role.Client && HasServer()) break;
                    if (Connections.Any(c => c.Remote.Address.Equals(ep.Address))) continue;
                    AutoDial(ep, null);
                }
            } while (!_stopEvent.WaitOne(30000));
        }

        private void AutoDial(IPEndPoint ep, string name)
        {
            string key = ep.ToString();
            if (_backoff.TryGetValue(key, out var b) && DateTime.Now < b.until) return;
            if (!_autoDialing.TryAdd(key, true)) return;
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (Dial(ep.Address.ToString(), ep.Port) != null)
                    {
                        _backoff.TryRemove(key, out _);
                        return;
                    }
                    Backoff(key, null);
                }
                catch (Exception ex)
                {
                    Backoff(key, $"Found {name ?? "a computer"} at {ep.Address} but can't connect to it: {ex.Message}");
                }
                finally
                {
                    _autoDialing.TryRemove(key, out _);
                }
            });
        }

        private void Backoff(string key, string message)
        {
            int failures = _backoff.TryGetValue(key, out var b) ? b.failures + 1 : 1;
            _backoff[key] = (DateTime.Now.AddSeconds(Math.Min(600, 30 * (1 << Math.Min(failures - 1, 5)))), failures);
            if (failures == 1 && message != null && !_stopped) Log.Info(message);
        }

        private IPEndPoint ParseTarget(string target)
        {
            int colon = target.LastIndexOf(':');
            string host = target;
            int port = _o.Port;
            if (colon > 0 && target.IndexOf(':') == colon && int.TryParse(target.Substring(colon + 1), out int p))
            {
                host = target.Substring(0, colon);
                port = p;
            }
            return IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : null;
        }

        // ---------------------------------------------------------------- helpers

        public static bool IsPrivate(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var v6 = a.GetAddressBytes();
                return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (v6[0] & 0xFE) == 0xFC;
            }
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT range, used by Tailscale
        }

        public static List<string> LocalIPv4Addresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !ua.Address.ToString().StartsWith("169.254."))
                            list.Add(ua.Address.ToString());
                }
            }
            catch { }
            return list.Distinct().ToList();
        }
    }
}
