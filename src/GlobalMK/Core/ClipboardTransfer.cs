using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GlobalMK
{
    public enum ClipFormat : byte { Text = 1, Png = 2 }

    /// <summary>Clipboard content in transport form: UTF-8 text or PNG image bytes.</summary>
    public sealed class ClipData
    {
        public ClipFormat Format;
        public byte[] Data;
        public string Origin;

        public ClipData(ClipFormat format, byte[] data, string origin)
        {
            Format = format;
            Data = data;
            Origin = origin;
        }

        public static ClipData FromText(string text, string origin) => new ClipData(ClipFormat.Text, Encoding.UTF8.GetBytes(text), origin);

        public string Text => Encoding.UTF8.GetString(Data);

        public string HashKey()
        {
            using (var sha = SHA256.Create())
            {
                sha.TransformBlock(new[] { (byte)Format }, 0, 1, null, 0);
                sha.TransformFinalBlock(Data, 0, Data.Length);
                return Convert.ToBase64String(sha.Hash);
            }
        }
    }

    /// <summary>
    /// Splits clipboard content into small chunks so a large copy never blocks mouse/keyboard traffic
    /// (chunks go on the connection's low-priority queue), and reassembles them on the other side.
    /// </summary>
    public static class ClipboardTransfer
    {
        public const int ChunkSize = 60000;
        public const int MaxWireBytes = 32 * 1024 * 1024;
        public const int MaxTextBytes = 64 * 1024 * 1024;
        private static int _nextId = Environment.TickCount & 0xFFFF;

        public static List<byte[]> Encode(ClipData clip)
        {
            byte[] wire = clip.Format == ClipFormat.Text ? Deflate(clip.Data) : clip.Data;
            if (wire.Length > MaxWireBytes) throw new InvalidOperationException("Clipboard content too large to share.");
            uint id = (uint)Interlocked.Increment(ref _nextId);
            byte[] origin = Encoding.UTF8.GetBytes(clip.Origin ?? "");
            var chunks = new List<byte[]>();
            int offset = 0;
            do
            {
                int n = Math.Min(ChunkSize, wire.Length - offset);
                var p = new byte[1 + 4 + 1 + 4 + 4 + 2 + origin.Length + n];
                p[0] = (byte)MsgType.Clipboard;
                Proto.WriteInt(p, 1, (int)id);
                p[5] = (byte)clip.Format;
                Proto.WriteInt(p, 6, wire.Length);
                Proto.WriteInt(p, 10, offset);
                p[14] = (byte)origin.Length;
                p[15] = (byte)(origin.Length >> 8);
                Buffer.BlockCopy(origin, 0, p, 16, origin.Length);
                Buffer.BlockCopy(wire, offset, p, 16 + origin.Length, n);
                chunks.Add(p);
                offset += n;
            } while (offset < wire.Length);
            return chunks;
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        internal static byte[] Inflate(byte[] data)
        {
            using (var z = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                var buf = new byte[81920];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxTextBytes) throw new ProtocolException("Clipboard text too large.");
                }
                return ms.ToArray();
            }
        }
    }

    /// <summary>Reassembles clipboard chunks from one connection. Not thread-safe; use from the reader thread.</summary>
    public sealed class ClipboardAssembler
    {
        private uint _id;
        private byte[] _buffer;
        private int _received;
        private ClipFormat _format;
        private string _origin;

        public ClipData Add(byte[] p)
        {
            if (p.Length < 16) throw new ProtocolException("Bad clipboard chunk.");
            uint id = (uint)Proto.ReadInt(p, 1);
            var format = (ClipFormat)p[5];
            int total = Proto.ReadInt(p, 6);
            int offset = Proto.ReadInt(p, 10);
            int originLen = p[14] | (p[15] << 8);
            int dataStart = 16 + originLen;
            int n = p.Length - dataStart;
            if (total < 0 || total > ClipboardTransfer.MaxWireBytes || offset < 0 || n < 0 || offset + n > total)
                throw new ProtocolException("Bad clipboard chunk.");
            if (format != ClipFormat.Text && format != ClipFormat.Png) return null;

            if (offset == 0 || id != _id || _buffer == null)
            {
                if (offset != 0) return null; // missed the start of this transfer; wait for the next one
                _id = id;
                _buffer = new byte[total];
                _received = 0;
                _format = format;
                _origin = Encoding.UTF8.GetString(p, 16, originLen);
            }
            if (offset != _received) { _buffer = null; return null; }
            Buffer.BlockCopy(p, dataStart, _buffer, offset, n);
            _received += n;
            if (_received < total) return null;

            var wire = _buffer;
            _buffer = null;
            var data = _format == ClipFormat.Text ? ClipboardTransfer.Inflate(wire) : wire;
            return new ClipData(_format, data, _origin);
        }
    }
}
