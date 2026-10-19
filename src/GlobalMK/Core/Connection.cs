using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GlobalMK
{
    /// <summary>
    /// An authenticated link to one peer. A reader thread dispatches incoming messages; a writer thread drains
    /// two queues: input/control messages first (consecutive mouse moves are coalesced), then clipboard chunks.
    /// </summary>
    public sealed class Connection
    {
        public const int HeartbeatMs = 2000;
        public const int TimeoutMs = 10000;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly SecureChannel _channel;
        private readonly ConcurrentQueue<byte[]> _high = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _bulk = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _closed;
        private long _lastSendMs;

        public string PeerName { get; }
        public Role PeerRole { get; }
        public string PeerVersion { get; }
        public IPEndPoint Remote { get; }
        public bool Initiator { get; }
        public bool InitiatedByServer { get; }
        public DateTime ConnectedAt { get; } = DateTime.Now;
        public volatile ScreenInfo[] PeerScreens;
        public int RttMs { get; private set; } = -1;
        public string CloseReason { get; private set; }
        public bool IsAlive => Volatile.Read(ref _closed) == 0;

        public event Action<Connection, byte[]> MessageReceived;
        public event Action<Connection, string> Closed;

        internal Connection(TcpClient tcp, NetworkStream stream, SecureChannel channel, HelloMsg peer, bool initiator, Role myRole)
        {
            _tcp = tcp;
            _stream = stream;
            _channel = channel;
            PeerName = peer.Name;
            PeerRole = peer.Role;
            PeerVersion = peer.AppVersion;
            PeerScreens = peer.Screens;
            Remote = (IPEndPoint)tcp.Client.RemoteEndPoint;
            Initiator = initiator;
            InitiatedByServer = initiator ? myRole == Role.Server : peer.Role == Role.Server;
        }

        internal void Start()
        {
            _stream.ReadTimeout = TimeoutMs;
            _stream.WriteTimeout = TimeoutMs;
            new Thread(ReadLoop) { IsBackground = true, Name = "GMK reader " + PeerName }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "GMK writer " + PeerName, Priority = ThreadPriority.AboveNormal }.Start();
        }

        /// <summary>Queue an input or control message. Never blocks; safe from hook callbacks.</summary>
        public void Send(byte[] payload)
        {
            if (!IsAlive) return;
            _high.Enqueue(payload);
            _signal.Set();
        }

        /// <summary>Queue a low-priority message (clipboard chunks).</summary>
        public void SendBulk(byte[] payload)
        {
            if (!IsAlive) return;
            _bulk.Enqueue(payload);
            _signal.Set();
        }

        /// <summary>Tell the peer why it is being refused, then close. Only before <see cref="Start"/>.</summary>
        internal void Reject(string reason)
        {
            try { _channel.WriteFrame(Proto.Notice("connection rejected: " + reason)); } catch { }
            Close(reason);
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            CloseReason = reason;
            try { _tcp.Close(); } catch { }
            _signal.Set();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Closed?.Invoke(this, reason); } catch (Exception ex) { Log.Error("Close handler: " + ex.Message); }
            });
        }

        private void ReadLoop()
        {
            try
            {
                while (IsAlive)
                {
                    var p = _channel.ReadFrame();
                    if (p.Length == 0) continue;
                    switch (Proto.TypeOf(p))
                    {
                        case MsgType.Ping:
                            Send(Proto.Ping(MsgType.Pong, Proto.ReadPing(p)));
                            break;
                        case MsgType.Pong:
                            RttMs = (int)Math.Max(0, _clock.ElapsedMilliseconds - Proto.ReadPing(p));
                            break;
                        case MsgType.Notice:
                            Log.Warn($"{PeerName} says: {Proto.ReadNotice(p)}");
                            break;
                        default:
                            MessageReceived?.Invoke(this, p);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Close(Describe(ex));
            }
        }

        private void WriteLoop()
        {
            var buf = new MemoryStream();
            var batch = new List<byte[]>();
            try
            {
                while (IsAlive)
                {
                    _signal.WaitOne(HeartbeatMs / 2);
                    if (!IsAlive) break;

                    batch.Clear();
                    while (_high.TryDequeue(out var p)) batch.Add(p);
                    buf.SetLength(0);
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var p = batch[i];
                        // Only the latest absolute position matters when moves pile up.
                        if (p[0] == (byte)MsgType.MouseMove && i + 1 < batch.Count && batch[i + 1][0] == (byte)MsgType.MouseMove)
                            continue;
                        _channel.EncryptFrameTo(buf, p, 0, p.Length);
                    }

                    if (_high.IsEmpty && _bulk.TryDequeue(out var chunk))
                    {
                        _channel.EncryptFrameTo(buf, chunk, 0, chunk.Length);
                        if (!_bulk.IsEmpty) _signal.Set();
                    }

                    long now = _clock.ElapsedMilliseconds;
                    if (buf.Length == 0 && now - _lastSendMs >= HeartbeatMs)
                    {
                        var ping = Proto.Ping(MsgType.Ping, now);
                        _channel.EncryptFrameTo(buf, ping, 0, ping.Length);
                    }

                    if (buf.Length > 0)
                    {
                        _stream.Write(buf.GetBuffer(), 0, (int)buf.Length);
                        _lastSendMs = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Close(Describe(ex));
            }
        }

        private string Describe(Exception ex)
        {
            if (!IsAlive && CloseReason != null) return CloseReason;
            if (ex is IOException io && io.InnerException is SocketException se)
            {
                if (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock)
                    return "no response for " + TimeoutMs / 1000 + "s";
                return se.Message;
            }
            return ex.Message;
        }

        public override string ToString() => $"{PeerName} ({Remote})";
    }
}
