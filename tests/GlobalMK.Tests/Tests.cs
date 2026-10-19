using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalMK.Tests
{
    internal static partial class Tests
    {
        private static int _passed, _failed;
        private static readonly ConcurrentQueue<string> LogLines = new ConcurrentQueue<string>();
        private static readonly byte[] KeyA = SecureChannel.DerivePassphraseKey("correct horse battery staple");
        private static readonly byte[] KeyB = SecureChannel.DerivePassphraseKey("a different passphrase");
        private static int _nextPort = 34100 + new Random().Next(0, 2000) * 4;

        private static int Main()
        {
            Log.Line += l => LogLines.Enqueue(l);
            Run("layout: edge exit to the right", LayoutEdgeRight);
            Run("layout: gaps, misalignment, own monitors", LayoutGapsAndOwnMonitors);
            Run("layout: negative desktop coordinates", LayoutNegativeCoords);
            Run("layout: remote overshoot and clamp", LayoutOvershoot);
            Run("layout: corner dead zone", LayoutCorners);
            Run("config: defaults survive missing fields", ConfigDefaults);
            Run("protocol: message encodings round-trip", ProtocolRoundTrip);
            Run("crypto: frames round-trip, tampering detected", CryptoTamper);
            Run("crypto: wrong passphrase fails", CryptoWrongPassphrase);
            Run("network: client connects, input flows", NetworkBasic);
            Run("network: wrong passphrase is refused", NetworkWrongPassphrase);
            Run("network: two servers are refused", NetworkRoleConflict);
            Run("network: dialing both ways keeps one link", NetworkCrossDial);
            Run("clipboard: large image and text transfer", ClipboardTransferTest);
            RunHelperAndDiscoveryTests();
            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                test();
                _passed++;
                Console.WriteLine($"PASS  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static void Assert(bool cond, string what)
        {
            if (!cond) throw new Exception("assertion failed: " + what);
        }

        private static void Eq<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static ScreenInfo[] Screens(params Rectangle[] rects) =>
            rects.Select((r, i) => new ScreenInfo(r, i == 0)).ToArray();

        // ------------------------------------------------------------------ layout

        private static void LayoutEdgeRight()
        {
            var local = new MachineGeom("A", true, Screens(new Rectangle(0, 0, 1920, 1080)), new Point(0, 0), true, null);
            var b = new MachineGeom("B", false, Screens(new Rectangle(0, 0, 2560, 1440)), new Point(1920, -200), true, null);
            var L = new LayoutSnapshot(new[] { local, b });

            var hit = L.FindEdgeExit(local, new Point(1919, 500), 0);
            Assert(hit != null && hit.Machine == b, "right edge reaches B");
            Eq(new Point(0, 700), hit.EntryLocal, "entry on B");
            Eq(Direction.Right, hit.Direction, "direction");
            Assert(L.FindEdgeExit(local, new Point(1918, 500), 0) == null, "one pixel inside does not switch");
            Assert(L.FindEdgeExit(local, new Point(2000, 500), 0)?.Machine == b, "unclamped position beyond edge also switches");
            Assert(L.FindEdgeExit(local, new Point(500, 0), 0) == null, "nothing above");
            Assert(L.FindEdgeExit(local, new Point(0, 500), 0) == null, "nothing to the left");
        }

        private static void LayoutGapsAndOwnMonitors()
        {
            var local = new MachineGeom("A", true, Screens(new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080)), new Point(0, 0), true, null);
            var gap = new MachineGeom("B", false, Screens(new Rectangle(0, 0, 1920, 1080)), new Point(4500, 0), true, null);
            var L = new LayoutSnapshot(new[] { local, gap });
            Assert(L.FindEdgeExit(local, new Point(1919, 500), 0) == null, "edge between own monitors is not an exit");
            var hit = L.FindEdgeExit(local, new Point(3839, 500), 0);
            Assert(hit?.Machine == gap, "gap is bridged");
            Eq(new Point(0, 500), hit.EntryLocal, "entry across gap");

            var off = new MachineGeom("C", false, Screens(new Rectangle(0, 0, 1920, 1080)), new Point(3840, 2000), true, null);
            var L2 = new LayoutSnapshot(new[] { local, off });
            Assert(L2.FindEdgeExit(local, new Point(3839, 500), 0) == null, "vertically misaligned machine is not reachable");

            var below = new MachineGeom("D", false, Screens(new Rectangle(0, 0, 1280, 800)), new Point(2000, 1080), true, null);
            var L3 = new LayoutSnapshot(new[] { local, below });
            var down = L3.FindEdgeExit(local, new Point(2500, 1079), 0);
            Assert(down?.Machine == below, "machine below second monitor");
            Eq(new Point(500, 0), down.EntryLocal, "entry below");
            Assert(L3.FindEdgeExit(local, new Point(1000, 1079), 0) == null, "first monitor has nothing below");
        }

        private static void LayoutNegativeCoords()
        {
            // Secondary monitor left of the primary: Windows coordinates start at -1920.
            var local = new MachineGeom("A", true, Screens(new Rectangle(0, 0, 2560, 1440), new Rectangle(-1920, 360, 1920, 1080)), new Point(0, 0), true, null);
            var b = new MachineGeom("B", false, Screens(new Rectangle(0, 0, 1920, 1200)), new Point(4480, 0), true, null);
            var L = new LayoutSnapshot(new[] { local, b });
            Eq(new Point(4480, 0), local.ToGlobal(new Point(2560, 0)), "global of primary right edge");
            var hit = L.FindEdgeExit(local, new Point(2559, 100), 0);
            Assert(hit?.Machine == b, "primary right edge reaches B");
            Eq(new Point(0, 100), hit.EntryLocal, "entry on B");

            var back = L.FindOvershootExit(b, new Point(0, 100), new Point(-4, 100), 0);
            Assert(back?.Machine == local, "B left edge returns to A");
            Eq(new Point(2559, 100), back.EntryLocal, "entry back on A");
            Eq(new Point(2558, 100), LayoutSnapshot.StepInside(local, back.EntryLocal, back.Direction), "step out of jump zone");
        }

        private static void LayoutOvershoot()
        {
            var local = new MachineGeom("A", true, Screens(new Rectangle(0, 0, 1920, 1080)), new Point(0, 0), true, null);
            var b = new MachineGeom("B", false, Screens(new Rectangle(0, 0, 2560, 1440)), new Point(1920, -200), true, null);
            var L = new LayoutSnapshot(new[] { local, b });

            var hit = L.FindOvershootExit(b, new Point(0, 700), new Point(-3, 702), 0);
            Assert(hit?.Machine == local, "B left edge returns to A");
            Eq(new Point(1919, 502), hit.EntryLocal, "entry on A");

            Assert(L.FindOvershootExit(b, new Point(0, 100), new Point(-3, 100), 0) == null, "above A's top there is nothing");
            Eq(new Point(0, 100), LayoutSnapshot.ClampMove(b, new Point(0, 100), new Point(-3, 100)), "clamped to B left edge");
            Eq(new Point(2559, 0), LayoutSnapshot.ClampMove(b, new Point(2550, 5), new Point(2570, -10)), "clamped to corner");
            Eq(new Point(10, 20), LayoutSnapshot.ClampMove(b, new Point(5, 5), new Point(10, 20)), "inside moves untouched");
        }

        private static void LayoutCorners()
        {
            var local = new MachineGeom("A", true, Screens(new Rectangle(0, 0, 1920, 1080)), new Point(0, 0), true, null);
            var b = new MachineGeom("B", false, Screens(new Rectangle(0, 0, 1920, 1080)), new Point(1920, 0), true, null);
            var L = new LayoutSnapshot(new[] { local, b });
            Assert(L.FindEdgeExit(local, new Point(1919, 10), 50) == null, "top-right corner ignored");
            Assert(L.FindEdgeExit(local, new Point(1919, 1075), 50) == null, "bottom-right corner ignored");
            Assert(L.FindEdgeExit(local, new Point(1919, 500), 50) != null, "middle of edge switches");
            Assert(L.FindEdgeExit(local, new Point(1919, 10), 0) != null, "corner switches when dead zone is 0");
        }

        // ------------------------------------------------------------------ config & protocol

        private static void ConfigDefaults()
        {
            var ser = new DataContractJsonSerializer(typeof(AppConfig));
            var cfg = (AppConfig)ser.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes("{\"Role\":\"client\",\"Port\":25000}")));
            Eq(Role.Client, cfg.RoleValue, "role");
            Eq(25000, cfg.Port, "port");
            Assert(cfg.ClipboardSync && cfg.OnlyPrivateNetworks && cfg.AutoConnectDiscovered, "boolean defaults");
            Assert(cfg.Layout != null && cfg.Peers != null, "lists");
            cfg.Passphrase = "secret passphrase";
            var copy = cfg.Clone();
            Eq("secret passphrase", copy.Passphrase, "passphrase survives clone (DPAPI)");
        }

        private static void ProtocolRoundTrip()
        {
            var hello = Proto.ReadHello(Proto.Hello(new HelloMsg
            {
                ProtocolVersion = Proto.ProtocolVersion, Role = Role.Client, Name = "Laptop ñ", AppVersion = "1.0.0",
                Screens = Screens(new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 3840, 2160)),
            }));
            Eq("Laptop ñ", hello.Name, "name");
            Eq(Role.Client, hello.Role, "role");
            Eq(2, hello.Screens.Length, "screen count");
            Eq(new Rectangle(-1920, 0, 1920, 1080), hello.Screens[0].Bounds, "screen 0");

            Proto.ReadPoint(Proto.Point(MsgType.MouseMove, -1234, 56789), out int x, out int y);
            Eq(-1234, x, "x");
            Eq(56789, y, "y");
            Proto.ReadKey(Proto.Key(0xA5, 0x138, KeyFlags.Extended | KeyFlags.Up), out ushort vk, out ushort scan, out KeyFlags f);
            Eq((ushort)0xA5, vk, "vk");
            Eq((ushort)0x138, scan, "scan");
            Eq(KeyFlags.Extended | KeyFlags.Up, f, "flags");
            var wheel = Proto.Wheel(-240, true);
            Eq(-240, Proto.ReadInt(wheel, 1), "wheel delta");
            Eq(1234567890123L, Proto.ReadPing(Proto.Ping(MsgType.Ping, 1234567890123L)), "ping");
        }

        // ------------------------------------------------------------------ crypto

        private static (SecureChannel a, SecureChannel b, TcpClient ta, TcpClient tb) Pair(byte[] keyA, byte[] keyB)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ta = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync();
            ta.Connect(IPAddress.Loopback, port);
            var tb = acceptTask.Result;
            listener.Stop();
            ta.ReceiveTimeout = tb.ReceiveTimeout = 5000;
            var hb = Task.Run(() => SecureChannel.Handshake(tb.GetStream(), keyB, false));
            var a = SecureChannel.Handshake(ta.GetStream(), keyA, true);
            return (a, hb.Result, ta, tb);
        }

        private static void CryptoTamper()
        {
            var (a, b, ta, tb) = Pair(KeyA, KeyA);
            using (ta) using (tb)
            {
                var msg = Encoding.UTF8.GetBytes("hello over an encrypted channel");
                a.WriteFrame(msg);
                Eq(Encoding.UTF8.GetString(msg), Encoding.UTF8.GetString(b.ReadFrame()), "frame 1");
                b.WriteFrame(new byte[0]);
                Eq(0, a.ReadFrame().Length, "empty frame back");
                var big = new byte[SecureChannel.MaxFrame / 2];
                new Random(1).NextBytes(big);
                var sendBig = Task.Run(() => a.WriteFrame(big));
                Assert(big.SequenceEqual(b.ReadFrame()), "2 MB frame");
                sendBig.Wait();

                var ms = new MemoryStream();
                a.EncryptFrameTo(ms, msg, 0, msg.Length);
                var raw = ms.ToArray();
                raw[6] ^= 0x01; // flip one ciphertext bit
                ta.GetStream().Write(raw, 0, raw.Length);
                try
                {
                    b.ReadFrame();
                    throw new Exception("tampered frame was accepted");
                }
                catch (PeerAuthenticationException) { }
            }
        }

        private static void CryptoWrongPassphrase()
        {
            var (a, b, ta, tb) = Pair(KeyA, KeyB);
            using (ta) using (tb)
            {
                a.WriteFrame(new byte[] { 1, 2, 3 });
                try
                {
                    b.ReadFrame();
                    throw new Exception("frame accepted with wrong passphrase");
                }
                catch (PeerAuthenticationException) { }
            }
        }

        // ------------------------------------------------------------------ network

        private sealed class TestNode : IDisposable
        {
            public readonly Node Node;
            public readonly int Port;
            public readonly BlockingCollection<byte[]> Received = new BlockingCollection<byte[]>();
            public readonly ManualResetEventSlim Connected = new ManualResetEventSlim();
            public int ConnectCount;

            public TestNode(Role role, string name, byte[] key, params string[] peers)
            {
                Port = Interlocked.Add(ref _nextPort, 4);
                Node = new Node(new NodeOptions
                {
                    Role = role,
                    Name = name,
                    Port = Port,
                    PassphraseKey = key,
                    Peers = peers.ToList(),
                    EnableDiscovery = false,
                    ListenAddress = IPAddress.Loopback,
                    GetScreens = () => Screens(new Rectangle(0, 0, 1920, 1080)),
                });
                Node.PeerConnected += c => { Interlocked.Increment(ref ConnectCount); Connected.Set(); };
                Node.MessageReceived += (c, p) => Received.Add(p);
            }

            public TestNode Start()
            {
                Node.Start();
                return this;
            }

            public void Dispose() => Node.Stop();
        }

        private static string Peer(TestNode n) => "127.0.0.1:" + n.Port;

        private static void NetworkBasic()
        {
            using (var server = new TestNode(Role.Server, "SRV", KeyA).Start())
            using (var client = new TestNode(Role.Client, "CLI", KeyA, Peer(server)).Start())
            {
                Assert(server.Connected.Wait(10000) && client.Connected.Wait(10000), "both sides connected");
                var link = server.Node.Get("CLI");
                Assert(link != null, "server sees client by name");
                Eq(Role.Client, link.PeerRole, "peer role");
                Eq(1, link.PeerScreens.Length, "client screens arrived in hello");

                link.Send(Proto.Point(MsgType.Enter, 10, 20));
                for (int i = 0; i < 200; i++) link.Send(Proto.Point(MsgType.MouseMove, i, i * 2));
                link.Send(Proto.Key(0x41, 0x1E, KeyFlags.None));
                link.Send(Proto.Simple(MsgType.Leave));

                var types = new List<MsgType>();
                Point last = Point.Empty;
                var deadline = DateTime.Now.AddSeconds(5);
                while (DateTime.Now < deadline && !types.Contains(MsgType.Leave))
                {
                    if (!client.Received.TryTake(out var p, 200)) continue;
                    types.Add(Proto.TypeOf(p));
                    if (Proto.TypeOf(p) == MsgType.MouseMove)
                    {
                        Proto.ReadPoint(p, out int x, out int y);
                        last = new Point(x, y);
                    }
                }
                Eq(MsgType.Enter, types.First(), "first message");
                Eq(MsgType.Leave, types.Last(), "last message");
                Assert(types.Contains(MsgType.Key), "key arrived");
                Eq(new Point(199, 398), last, "final mouse position (moves may be coalesced, never reordered)");

                client.Node.Get("SRV").Send(Proto.Screens(Screens(new Rectangle(0, 0, 800, 600), new Rectangle(800, 0, 800, 600))));
                Assert(server.Received.TryTake(out var screens, 5000) && Proto.TypeOf(screens) == MsgType.Screens, "screens update arrives");
                Eq(2, Proto.ReadScreens(screens).Length, "two screens");
            }
        }

        private static void NetworkWrongPassphrase()
        {
            while (LogLines.TryDequeue(out _)) { }
            using (var server = new TestNode(Role.Server, "SRV2", KeyA).Start())
            using (var client = new TestNode(Role.Client, "CLI2", KeyB, Peer(server)).Start())
            {
                Assert(!server.Connected.Wait(3000), "no connection with mismatched passphrase");
                Assert(LogLines.Any(l => l.Contains("passphrases don't match")), "clear log message about passphrase");
            }
        }

        private static void NetworkRoleConflict()
        {
            while (LogLines.TryDequeue(out _)) { }
            using (var a = new TestNode(Role.Server, "S1", KeyA).Start())
            using (var b = new TestNode(Role.Server, "S2", KeyA, Peer(a)).Start())
            {
                Assert(!a.Connected.Wait(3000), "two servers don't connect");
                Assert(LogLines.Any(l => l.Contains("both computers are set to Server")), "explains the role conflict");
            }
        }

        private static void NetworkCrossDial()
        {
            int sPort = Interlocked.Add(ref _nextPort, 4);
            int cPort = Interlocked.Add(ref _nextPort, 4);
            var sConnects = 0;
            var server = new Node(new NodeOptions
            {
                Role = Role.Server, Name = "SRV4", Port = sPort, PassphraseKey = KeyA, EnableDiscovery = false,
                ListenAddress = IPAddress.Loopback, Peers = { "127.0.0.1:" + cPort },
            });
            var client = new Node(new NodeOptions
            {
                Role = Role.Client, Name = "CLI4", Port = cPort, PassphraseKey = KeyA, EnableDiscovery = false,
                ListenAddress = IPAddress.Loopback, Peers = { "127.0.0.1:" + sPort },
            });
            server.PeerConnected += c => Interlocked.Increment(ref sConnects);
            server.Start();
            client.Start();
            try
            {
                var deadline = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < deadline && (server.Connections.Length != 1 || client.Connections.Length != 1)) Thread.Sleep(100);
                Eq(1, server.Connections.Length, "server has one link");
                Eq(1, client.Connections.Length, "client has one link");
                var s1 = server.Get("CLI4");
                Thread.Sleep(7000); // longer than the dialers' retry interval
                Eq(1, server.Connections.Length, "still one link on server");
                Eq(1, client.Connections.Length, "still one link on client");
                Assert(server.Get("CLI4") == s1, "link is stable (no flapping)");
                Assert(s1.InitiatedByServer || sConnects == 1, "server-initiated link preferred when both exist");
            }
            finally
            {
                client.Stop();
                server.Stop();
            }
        }

        // ------------------------------------------------------------------ clipboard

        private static void ClipboardTransferTest()
        {
            using (var server = new TestNode(Role.Server, "SRV5", KeyA).Start())
            using (var client = new TestNode(Role.Client, "CLI5", KeyA, Peer(server)).Start())
            {
                Assert(server.Connected.Wait(10000), "connected");
                var link = server.Node.Get("CLI5");

                var png = new byte[3 * 1024 * 1024 + 123];
                new Random(7).NextBytes(png);
                var text = string.Concat(Enumerable.Repeat("Ünïcödé clipboard text 📋 ", 20000));
                foreach (var chunk in ClipboardTransfer.Encode(new ClipData(ClipFormat.Png, png, "SRV5"))) link.SendBulk(chunk);
                foreach (var chunk in ClipboardTransfer.Encode(ClipData.FromText(text, "SRV5"))) link.SendBulk(chunk);
                // Input sent after the bulk data must not wait for it.
                var sw = Stopwatch.StartNew();
                link.Send(Proto.Point(MsgType.MouseMove, 1, 2));

                var asm = new ClipboardAssembler();
                var got = new List<ClipData>();
                long moveLatency = -1;
                var deadline = DateTime.Now.AddSeconds(15);
                while (got.Count < 2 && DateTime.Now < deadline)
                {
                    if (!client.Received.TryTake(out var p, 200)) continue;
                    if (Proto.TypeOf(p) == MsgType.MouseMove && moveLatency < 0) moveLatency = sw.ElapsedMilliseconds;
                    if (Proto.TypeOf(p) == MsgType.Clipboard)
                    {
                        var done = asm.Add(p);
                        if (done != null) got.Add(done);
                    }
                }
                Eq(2, got.Count, "both clipboard items arrived");
                Assert(got[0].Format == ClipFormat.Png && got[0].Data.SequenceEqual(png), "image bytes intact");
                Eq("SRV5", got[0].Origin, "origin");
                Eq(text, got[1].Text, "text intact");
                Assert(moveLatency >= 0 && moveLatency < 500, $"mouse move overtook bulk transfer (took {moveLatency} ms)");
            }
        }
    }
}
