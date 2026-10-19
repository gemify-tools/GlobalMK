using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace GlobalMK.Tests
{
    internal static partial class Tests
    {
        private static void RunHelperAndDiscoveryTests()
        {
            Run("helper: only exact input messages pass validation", HelperValidation);
            Run("helper: pipe link delivers input in order", HelperPipeRoundTrip);
            Run("helper: malformed frame drops the client", HelperRejectsGarbage);
            Run("helper: non-SYSTEM pipe server is not trusted", HelperRejectsImpostor);
            Run("helper: injector falls back to local input", InjectorFallback);
            Run("discovery: only 192.168.0.x and 192.168.1.x are used", DiscoveryHomeNetworks);
            Run("discovery: announcement parsing", DiscoveryParsing);
            Run("discovery: probe gets an answer", DiscoveryProbe);
            Run("discovery: found computer is connected automatically", DiscoveryAutoConnect);
        }

        /// <summary>The helper re-creates its pipe after each client, so allow it a moment.</summary>
        private static void ConnectWithin(HelperLink link)
        {
            var deadline = DateTime.Now.AddSeconds(3);
            link.TryConnect();
            while (!link.Connected && DateTime.Now < deadline)
            {
                Thread.Sleep(50);
                link.TryConnect();
            }
        }

        private static string UniquePipe() => "GlobalMK-test-" + Guid.NewGuid().ToString("N");

        // ------------------------------------------------------------------ helper

        private static void HelperValidation()
        {
            Assert(HelperProtocol.IsValidInput(Proto.Point(MsgType.MouseMove, 5, 6)), "move");
            Assert(HelperProtocol.IsValidInput(Proto.Point(MsgType.Enter, 5, 6)), "enter");
            Assert(HelperProtocol.IsValidInput(Proto.Simple(MsgType.Leave)), "leave");
            Assert(HelperProtocol.IsValidInput(Proto.Button(MouseButton.X2, true)), "button");
            Assert(HelperProtocol.IsValidInput(Proto.Wheel(-120, true)), "wheel");
            Assert(HelperProtocol.IsValidInput(Proto.Key(0x41, 0x1E, KeyFlags.Up | KeyFlags.Extended)), "key");
            Assert(HelperProtocol.IsValidInput(Proto.Ping(MsgType.Ping, 0)), "keep-alive");

            Assert(!HelperProtocol.IsValidInput(Proto.Hello(new HelloMsg { Name = "x", Screens = new ScreenInfo[0] })), "hello rejected");
            Assert(!HelperProtocol.IsValidInput(Proto.Notice("x")), "notice rejected");
            Assert(!HelperProtocol.IsValidInput(ClipboardTransfer.Encode(ClipData.FromText("x", "o"))[0]), "clipboard rejected");
            Assert(!HelperProtocol.IsValidInput(new byte[] { (byte)MsgType.MouseMove, 1, 2 }), "short move rejected");
            Assert(!HelperProtocol.IsValidInput(new byte[] { (byte)MsgType.MouseButton, 9, 1 }), "unknown button rejected");
            Assert(!HelperProtocol.IsValidInput(new byte[] { (byte)MsgType.Key, 0x41, 0, 0x1E, 0, 0x80 }), "unknown key flags rejected");
            Assert(!HelperProtocol.IsValidInput(new byte[0]), "empty rejected");
            Assert(HelperProtocol.IsHello(HelperProtocol.Hello()), "hello");
            Assert(!HelperProtocol.IsHello(Encoding.ASCII.GetBytes("GMKP\x02")), "other version");
        }

        private sealed class TestHelper : IDisposable
        {
            public readonly string Name = UniquePipe();
            public readonly BlockingCollection<byte[]> Received = new BlockingCollection<byte[]>();
            public int Disconnects;
            private readonly HelperPipeServer _server;
            private readonly Thread _thread;

            public TestHelper()
            {
                _server = new HelperPipeServer(Name, HelperPipeServer.CurrentUserOnly, p => Received.Add(p), () => Interlocked.Increment(ref Disconnects));
                _thread = new Thread(_server.Run) { IsBackground = true };
                _thread.Start();
                Thread.Sleep(100);
            }

            public void Dispose()
            {
                _server.Dispose();
                _thread.Join(2000);
            }
        }

        private static void HelperPipeRoundTrip()
        {
            using (var helper = new TestHelper())
            using (var link = new HelperLink(helper.Name, _ => true))
            {
                ConnectWithin(link);
                Eq(HelperLinkState.Connected, link.State, "link state");
                Assert(link.TrySend(Proto.Point(MsgType.Enter, 1, 2)), "send enter");
                Assert(link.TrySend(Proto.Key(0x41, 0x1E, KeyFlags.None)), "send key");
                Assert(link.TrySend(Proto.Ping(MsgType.Ping, 0)), "keep-alive is accepted");
                Assert(link.TrySend(Proto.Simple(MsgType.Leave)), "send leave");
                var got = new List<MsgType>();
                while (got.Count < 3 && helper.Received.TryTake(out var p, 3000)) got.Add(Proto.TypeOf(p));
                Eq("Enter,Key,Leave", string.Join(",", got), "messages in order (keep-alive not forwarded)");
            }
        }

        private static void HelperRejectsGarbage()
        {
            using (var helper = new TestHelper())
            {
                using (var raw = new NamedPipeClientStream(".", helper.Name, PipeDirection.Out))
                {
                    raw.Connect(2000);
                    var hello = HelperProtocol.Frame(HelperProtocol.Hello());
                    raw.Write(hello, 0, hello.Length);
                    var notInput = HelperProtocol.Frame(Proto.Notice("click OK please"));
                    raw.Write(notInput, 0, notInput.Length);
                    var deadline = DateTime.Now.AddSeconds(3);
                    while (helper.Disconnects == 0 && DateTime.Now < deadline) Thread.Sleep(20);
                }
                Eq(1, helper.Disconnects, "server dropped the client (and released keys)");
                Eq(0, helper.Received.Count, "nothing was injected");

                using (var link = new HelperLink(helper.Name, _ => true))
                {
                    ConnectWithin(link);
                    Assert(link.Connected && link.TrySend(Proto.Simple(MsgType.Leave)), "server accepts the next client");
                    Assert(helper.Received.TryTake(out _, 3000), "next client works");
                }
            }
        }

        private static void HelperRejectsImpostor()
        {
            using (var helper = new TestHelper())
            using (var link = new HelperLink(helper.Name)) // default check: server must be LocalSystem
            {
                link.TryConnect();
                Eq(HelperLinkState.Untrusted, link.State, "a pipe run by a normal user is refused");
                Assert(!link.TrySend(Proto.Simple(MsgType.Leave)), "nothing is sent to it");
            }
            using (var link = new HelperLink(UniquePipe()))
            {
                link.TryConnect();
                Eq(HelperLinkState.NotInstalled, link.State, "missing helper");
            }
        }

        private static void InjectorFallback()
        {
            var local = new List<Native.INPUT>();
            using (var helper = new TestHelper())
            using (var link = new HelperLink(helper.Name, _ => true))
            {
                var injector = new InputInjector(inputs => { local.AddRange(inputs); return true; }) { Forward = link };

                injector.Handle(Proto.Key(0x41, 0x1E, KeyFlags.None)); // helper not connected yet: local
                Eq(1, local.Count, "injected locally while the helper is unavailable");

                ConnectWithin(link);
                injector.Handle(Proto.Point(MsgType.Enter, 10, 10));
                injector.Handle(Proto.Key(0x42, 0x30, KeyFlags.None));
                Eq(1, local.Count, "nothing injected locally while the helper is connected");
                Assert(injector.Active, "active after enter");

                injector.Handle(Proto.Simple(MsgType.Leave));
                Assert(!injector.Active, "inactive after leave");
                Eq(2, local.Count, "the key pressed locally earlier is released locally");
                Assert((local[1].u.ki.dwFlags & Native.KEYEVENTF_KEYUP) != 0 && local[1].u.ki.wScan == 0x1E, "release is a key-up for that key");

                var forwarded = new List<MsgType>();
                while (helper.Received.TryTake(out var p, 1000)) forwarded.Add(Proto.TypeOf(p));
                Eq("Enter,Key,Leave", string.Join(",", forwarded), "helper got the rest");
            }
        }

        // ------------------------------------------------------------------ discovery

        private static IPAddress Ip(string s) => s == null ? null : IPAddress.Parse(s);

        private static void DiscoveryHomeNetworks()
        {
            Assert(Discovery.IsHomeNetworkAddress(Ip("192.168.0.1")), "192.168.0.x is a home network");
            Assert(Discovery.IsHomeNetworkAddress(Ip("192.168.1.254")), "192.168.1.x is a home network");
            Assert(!Discovery.IsHomeNetworkAddress(Ip("192.168.2.1")), "192.168.2.x is not");
            Assert(!Discovery.IsHomeNetworkAddress(Ip("192.168.10.1")), "192.168.10.x is not (not a prefix match)");
            Assert(!Discovery.IsHomeNetworkAddress(Ip("192.168.56.1")), "a virtual adapter's 192.168.56.x is not");
            Assert(!Discovery.IsHomeNetworkAddress(Ip("10.1.2.3")), "10.x is not");
            Assert(!Discovery.IsHomeNetworkAddress(Ip("100.64.3.4")), "100.64.x (Tailscale) is not");

            Assert(Discovery.IsAllowedSource(Ip("192.168.1.5"), IPAddress.Any), "a home network sender is accepted");
            Assert(!Discovery.IsAllowedSource(Ip("10.0.0.5"), IPAddress.Any), "other senders are ignored");
            Assert(!Discovery.IsAllowedSource(Ip("192.168.2.5"), IPAddress.Any), "a third 192.168 network is ignored");
            Assert(!Discovery.IsAllowedSource(Ip("127.0.0.1"), IPAddress.Any), "loopback is ignored in normal use");
            Assert(Discovery.IsAllowedSource(Ip("127.0.0.1"), IPAddress.Loopback), "loopback is accepted in tests");

            var hosts = Discovery.HomeHosts().ToList();
            Eq(2 * 254, hosts.Count, "every host on both networks is probed");
            Eq(hosts.Count, hosts.Select(h => h.ToString()).Distinct().Count(), "no duplicates");
            Assert(hosts.All(h => Discovery.IsHomeNetworkAddress(h) && h.GetAddressBytes()[3] != 0 && h.GetAddressBytes()[3] != 255),
                "only host addresses (no network or broadcast) on the two networks");
            Eq("192.168.0.1", hosts[0].ToString(), "first host");
            Eq("192.168.1.254", hosts[hosts.Count - 1].ToString(), "last host");
        }

        private static void DiscoveryParsing()
        {
            Assert(Discovery.TryParseAnnouncement("GMK1|server|OFFICE-PC|24860", out var role, out var name, out int port), "valid");
            Eq(Role.Server, role, "role");
            Eq("OFFICE-PC", name, "name");
            Eq(24860, port, "port");
            Assert(!Discovery.TryParseAnnouncement("GMK1|probe", out _, out _, out _), "probe is not an announcement");
            Assert(!Discovery.TryParseAnnouncement("GMK1|admin|X|1", out _, out _, out _), "unknown role");
            Assert(!Discovery.TryParseAnnouncement("GMK1|client|X|99999", out _, out _, out _), "bad port");
            Assert(!Discovery.TryParseAnnouncement("GMK1|client||24860", out _, out _, out _), "empty name");
        }

        private static Discovery LoopbackDiscovery(string name, Role role, int udpPort, params int[] probe) => new Discovery(new DiscoveryOptions
        {
            Role = role,
            Name = name,
            TcpPort = udpPort - 1,
            UdpPort = udpPort,
            BindAddress = IPAddress.Loopback,
            Broadcast = false,
            OtherNetworks = true,
            TargetedIntervalMs = 500,
            ExtraTargets = () => probe.Select(p => new IPEndPoint(IPAddress.Loopback, p)),
        });

        private static void DiscoveryProbe()
        {
            int pa = Interlocked.Add(ref _nextPort, 4) + 1, pb = Interlocked.Add(ref _nextPort, 4) + 1;
            using (var a = LoopbackDiscovery("ALPHA", Role.Server, pa, pb, pa))
            using (var b = LoopbackDiscovery("BRAVO", Role.Client, pb))
            {
                var seen = new BlockingCollection<DiscoveredPeer>();
                a.PeerSeen += p => seen.Add(p);
                b.Start();
                a.Start();
                Assert(seen.TryTake(out var peer, 5000), "ALPHA heard an answer");
                Eq("BRAVO", peer.Name, "answer from BRAVO (own answers are ignored)");
                Eq(Role.Client, peer.Role, "role");
                Eq(pb - 1, peer.Port, "TCP port");
                Assert(a.Peers.Any(p => p.Name == "BRAVO"), "listed");
                Assert(!b.Peers.Any(), "a probe alone doesn't make ALPHA known to BRAVO");
            }
        }

        private static void DiscoveryAutoConnect()
        {
            int sPort = Interlocked.Add(ref _nextPort, 4);
            int cPort = Interlocked.Add(ref _nextPort, 4);
            var server = new Node(new NodeOptions
            {
                Role = Role.Server, Name = "SRV6", Port = sPort, PassphraseKey = KeyA, ListenAddress = IPAddress.Loopback,
                // Only the server can reach the client (like a computer behind a second router): no addresses are configured.
                ExtraProbeTargets = () => new[] { new IPEndPoint(IPAddress.Loopback, cPort + 1) },
            });
            var client = new Node(new NodeOptions
            {
                Role = Role.Client, Name = "CLI6", Port = cPort, PassphraseKey = KeyA, ListenAddress = IPAddress.Loopback,
            });
            client.Start();
            server.Start();
            try
            {
                var deadline = DateTime.Now.AddSeconds(15);
                while (DateTime.Now < deadline && (server.Get("CLI6") == null || client.Get("SRV6") == null)) Thread.Sleep(100);
                Assert(server.Get("CLI6") != null && client.Get("SRV6") != null, "connected without any configured address");
                Assert(server.Get("CLI6").Initiator, "the side that found the other one connected to it");
            }
            finally
            {
                server.Stop();
                client.Stop();
            }
        }
    }
}
