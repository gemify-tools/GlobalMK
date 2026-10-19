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
    public sealed class DiscoveredPeer
    {
        public string Name;
        public Role Role;
        public IPAddress Address;
        public int Port;
        public DateTime LastSeen;
        public bool OtherNetwork;
    }

    public sealed class DiscoveryOptions
    {
        public Role Role;
        public string Name;
        public int TcpPort = AppConfig.DefaultPort;
        public int UdpPort = AppConfig.DefaultPort + 1;
        public IPAddress BindAddress = IPAddress.Any;
        /// <summary>Announce on this computer's own home network with a broadcast.</summary>
        public bool Broadcast = true;
        /// <summary>Probe every address on both home networks, so computers on the other one are found.</summary>
        public bool OtherNetworks = true;
        /// <summary>How often the probes go out. Each round covers the whole home network.</summary>
        public int TargetedIntervalMs = 60000;
        /// <summary>Extra UDP endpoints to probe every round: computers seen before and configured addresses.</summary>
        public Func<IEnumerable<IPEndPoint>> ExtraTargets = () => Enumerable.Empty<IPEndPoint>();
    }

    /// <summary>
    /// Finds other copies of the app without any configuration.
    ///
    /// Only the two home networks, 192.168.0.x and 192.168.1.x, are used. Messages from any other address are
    /// ignored, and nothing is sent to other addresses.
    ///
    /// Same network: every instance broadcasts a small announcement every few seconds.
    ///
    /// Other network: broadcasts don't cross routers, so every minute each instance sends a tiny UDP "who's
    /// there?" probe to every address on both home networks. Every instance answers a probe with its
    /// announcement. Probes use a time-to-live of <see cref="ProbeTtl"/>, enough to cross the two home routers.
    ///
    /// Whichever side hears an answer connects, so only one side needs to be able to reach the other.
    /// </summary>
    public sealed class Discovery : IDisposable
    {
        /// <summary>
        /// Hops a probe may take. A router won't forward a packet whose time-to-live is 1 or less, so a computer
        /// behind N routers needs a time-to-live of N + 1. Two routers, one behind the other, need 3. Probes that
        /// nobody answers die a few hops past the home routers, and private addresses are never routed on the internet.
        /// </summary>
        public const int ProbeTtl = 3;

        private const string Magic = "GMK1";

        private readonly DiscoveryOptions _o;
        private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new ConcurrentDictionary<string, DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IPEndPoint> _remembered = new ConcurrentDictionary<string, IPEndPoint>();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private UdpClient _main;   // bound to the discovery port: announcements, answers to probes
        private UdpClient _probe;  // ephemeral port, limited to ProbeTtl hops: our probes and the answers to them

        public event Action<DiscoveredPeer> PeerSeen;

        public Discovery(DiscoveryOptions options) => _o = options;

        public DiscoveredPeer[] Peers =>
            _peers.Values.Where(p => (DateTime.Now - p.LastSeen).TotalSeconds < (p.OtherNetwork ? _o.TargetedIntervalMs / 1000 * 2.5 : 15))
                .OrderBy(p => p.Name).ToArray();

        public void Start()
        {
            try
            {
                _main = new UdpClient();
                _main.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _main.Client.Bind(new IPEndPoint(_o.BindAddress, _o.UdpPort));
                _main.EnableBroadcast = true;
                new Thread(() => ReceiveLoop(_main)) { IsBackground = true, Name = "GMK discovery rx" }.Start();
            }
            catch (Exception ex)
            {
                Log.Warn($"Discovery unavailable (UDP {_o.UdpPort}): {ex.Message}");
                _main = null;
            }

            if (_o.Broadcast && _main != null)
                new Thread(AnnounceLoop) { IsBackground = true, Name = "GMK discovery tx" }.Start();

            if (_o.OtherNetworks)
            {
                _probe = new UdpClient(new IPEndPoint(_o.BindAddress, 0));
                new Thread(() => ReceiveLoop(_probe)) { IsBackground = true, Name = "GMK probe rx" }.Start();
                new Thread(ProbeLoop) { IsBackground = true, Name = "GMK probe tx" }.Start();
            }
        }

        public void Dispose()
        {
            _stop.Set();
            try { _main?.Close(); } catch { }
            try { _probe?.Close(); } catch { }
        }

        // ------------------------------------------------------------------ receiving

        private void ReceiveLoop(UdpClient socket)
        {
            while (!_stop.WaitOne(0))
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var data = socket.Receive(ref ep);
                    if (data.Length > 512 || !IsAllowedSource(ep.Address, _o.BindAddress)) continue;
                    string text = Encoding.UTF8.GetString(data);
                    if (text == Magic + "|probe")
                    {
                        // The answer is a normal announcement, sent straight back to the prober.
                        if (_main != null)
                        {
                            var reply = Announcement();
                            _main.Send(reply, reply.Length, ep);
                        }
                        continue;
                    }
                    if (TryParseAnnouncement(text, out var role, out var name, out int port) &&
                        !string.Equals(name, _o.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var peer = new DiscoveredPeer
                        {
                            Name = name,
                            Role = role,
                            Address = ep.Address,
                            Port = port,
                            LastSeen = DateTime.Now,
                            OtherNetwork = !IsOnLocalSubnet(ep.Address),
                        };
                        bool isNew = !_peers.TryGetValue(name, out var old) || !old.Address.Equals(peer.Address);
                        _peers[name] = peer;
                        if (peer.OtherNetwork) _remembered[peer.Address.ToString()] = new IPEndPoint(peer.Address, port + 1);
                        if (isNew && peer.OtherNetwork)
                            Log.Info($"Found {name} ({role.ToString().ToLowerInvariant()}) on the other home network at {peer.Address}.");
                        PeerSeen?.Invoke(peer);
                    }
                }
                catch
                {
                    if (_stop.WaitOne(200)) return;
                }
            }
        }

        /// <summary>Only the two home networks. Loopback is also accepted when bound to loopback (tests).</summary>
        internal static bool IsAllowedSource(IPAddress source, IPAddress bind) =>
            IsHomeNetworkAddress(source) || (IPAddress.IsLoopback(bind) && IPAddress.IsLoopback(source));

        public static bool TryParseAnnouncement(string text, out Role role, out string name, out int port)
        {
            role = Role.Server;
            name = null;
            port = 0;
            var parts = text.Split('|');
            if (parts.Length != 4 || parts[0] != Magic || (parts[1] != "server" && parts[1] != "client")) return false;
            if (!int.TryParse(parts[3], out port) || port < 1 || port > 65535) return false;
            if (parts[2].Length == 0 || parts[2].Length > 64) return false;
            role = parts[1] == "server" ? Role.Server : Role.Client;
            name = parts[2];
            return true;
        }

        private byte[] Announcement() =>
            Encoding.UTF8.GetBytes($"{Magic}|{(_o.Role == Role.Server ? "server" : "client")}|{_o.Name}|{_o.TcpPort}");

        // ------------------------------------------------------------------ same network: broadcast

        private void AnnounceLoop()
        {
            while (!_stop.WaitOne(0))
            {
                var msg = Announcement();
                foreach (var bcast in BroadcastAddresses())
                {
                    try { _main.Send(msg, msg.Length, new IPEndPoint(bcast, _o.UdpPort)); } catch { }
                }
                if (_stop.WaitOne(3000)) return;
            }
        }

        // ------------------------------------------------------------------ other network: probes

        private void ProbeLoop()
        {
            if (_stop.WaitOne(1500)) return;
            while (!_stop.WaitOne(0))
            {
                foreach (var ep in TargetedEndpoints()) SendProbe(ep);
                if (_stop.WaitOne(_o.TargetedIntervalMs)) return;
            }
        }

        private void SendProbe(IPEndPoint ep)
        {
            try
            {
                _probe.Client.Ttl = (short)(IPAddress.IsLoopback(ep.Address) ? 1 : ProbeTtl);
                var msg = Encoding.ASCII.GetBytes(Magic + "|probe");
                _probe.Send(msg, msg.Length, ep);
            }
            catch { /* unreachable addresses are expected */ }
        }

        /// <summary>Every address on both home networks, plus configured and remembered computers.</summary>
        private IEnumerable<IPEndPoint> TargetedEndpoints()
        {
            var targets = new List<IPEndPoint>();
            targets.AddRange(_o.ExtraTargets());
            targets.AddRange(_remembered.Values);
            if (!IPAddress.IsLoopback(_o.BindAddress))
                foreach (var ip in HomeHosts())
                    targets.Add(new IPEndPoint(ip, _o.UdpPort));
            return targets;
        }

        // ------------------------------------------------------------------ addresses

        /// <summary>True for 192.168.0.x and 192.168.1.x, the two home networks.</summary>
        public static bool IsHomeNetworkAddress(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = a.GetAddressBytes();
            return b[0] == 192 && b[1] == 168 && (b[2] == 0 || b[2] == 1);
        }

        /// <summary>Every host address (.1 to .254) on the two home networks: 508 addresses.</summary>
        public static IEnumerable<IPAddress> HomeHosts()
        {
            foreach (int third in new[] { 0, 1 })
                for (int h = 1; h < 255; h++)
                    yield return new IPAddress(new byte[] { 192, 168, (byte)third, (byte)h });
        }

        private static IEnumerable<UnicastIPAddressInformation> LocalIPv4(bool homeOnly)
        {
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { yield break; }
            foreach (var ni in nics)
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.IPv4Mask != null
                        && (!homeOnly || IsHomeNetworkAddress(ua.Address)))
                        yield return ua;
            }
        }

        private static bool IsOnLocalSubnet(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            var ab = a.GetAddressBytes();
            foreach (var ua in LocalIPv4(false))
            {
                var ib = ua.Address.GetAddressBytes();
                var mb = ua.IPv4Mask.GetAddressBytes();
                bool same = true;
                for (int i = 0; i < 4; i++) same &= (ab[i] & mb[i]) == (ib[i] & mb[i]);
                if (same) return true;
            }
            return false;
        }

        /// <summary>
        /// Directed broadcast for each adapter on a home network. The all-networks broadcast is not used: it can
        /// leave by any adapter, such as a virtual one.
        /// </summary>
        private static IEnumerable<IPAddress> BroadcastAddresses()
        {
            var result = new HashSet<IPAddress>();
            foreach (var ua in LocalIPv4(true))
            {
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new IPAddress(b));
            }
            return result;
        }
    }
}
