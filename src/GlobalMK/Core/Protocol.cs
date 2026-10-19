using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace GlobalMK
{
    public enum MsgType : byte
    {
        Hello = 1,
        Screens = 2,
        Notice = 3,
        Enter = 10,
        Leave = 11,
        MouseMove = 12,
        MouseButton = 13,
        MouseWheel = 14,
        Key = 15,
        Clipboard = 20,
        Ping = 30,
        Pong = 31,
    }

    public enum MouseButton : byte { Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

    [Flags]
    public enum KeyFlags : byte { None = 0, Extended = 1, Up = 2 }

    public sealed class HelloMsg
    {
        public ushort ProtocolVersion;
        public Role Role;
        public string Name;
        public string AppVersion;
        public ScreenInfo[] Screens;
    }

    /// <summary>Binary encoding of the application messages carried inside secure frames (little-endian).</summary>
    public static class Proto
    {
        public const ushort ProtocolVersion = 1;

        public static MsgType TypeOf(byte[] p) => (MsgType)p[0];

        public static byte[] Hello(HelloMsg h)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((byte)MsgType.Hello);
                w.Write(h.ProtocolVersion);
                w.Write((byte)h.Role);
                w.Write(h.Name ?? "");
                w.Write(h.AppVersion ?? "");
                WriteScreens(w, h.Screens);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static HelloMsg ReadHello(byte[] p)
        {
            if (p.Length < 1 || TypeOf(p) != MsgType.Hello) throw new ProtocolException("Expected a hello message.");
            using (var r = new BinaryReader(new MemoryStream(p, 1, p.Length - 1), Encoding.UTF8))
            {
                return new HelloMsg
                {
                    ProtocolVersion = r.ReadUInt16(),
                    Role = (Role)r.ReadByte(),
                    Name = r.ReadString(),
                    AppVersion = r.ReadString(),
                    Screens = ReadScreens(r),
                };
            }
        }

        public static byte[] Screens(ScreenInfo[] screens)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((byte)MsgType.Screens);
                WriteScreens(w, screens);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static ScreenInfo[] ReadScreens(byte[] p)
        {
            using (var r = new BinaryReader(new MemoryStream(p, 1, p.Length - 1), Encoding.UTF8))
                return ReadScreens(r);
        }

        private static void WriteScreens(BinaryWriter w, ScreenInfo[] screens)
        {
            screens = screens ?? new ScreenInfo[0];
            w.Write((byte)Math.Min(screens.Length, 32));
            for (int i = 0; i < screens.Length && i < 32; i++)
            {
                var b = screens[i].Bounds;
                w.Write(b.X); w.Write(b.Y); w.Write(b.Width); w.Write(b.Height);
                w.Write(screens[i].Primary);
            }
        }

        private static ScreenInfo[] ReadScreens(BinaryReader r)
        {
            int n = r.ReadByte();
            var list = new ScreenInfo[n];
            for (int i = 0; i < n; i++)
            {
                int x = r.ReadInt32(), y = r.ReadInt32(), wd = r.ReadInt32(), ht = r.ReadInt32();
                bool primary = r.ReadBoolean();
                if (wd <= 0 || ht <= 0 || wd > 100000 || ht > 100000) throw new ProtocolException("Bad screen size.");
                list[i] = new ScreenInfo(new Rectangle(x, y, wd, ht), primary);
            }
            return list;
        }

        public static byte[] Notice(string text)
        {
            var t = Encoding.UTF8.GetBytes(text ?? "");
            var p = new byte[1 + t.Length];
            p[0] = (byte)MsgType.Notice;
            Buffer.BlockCopy(t, 0, p, 1, t.Length);
            return p;
        }

        public static string ReadNotice(byte[] p) => Encoding.UTF8.GetString(p, 1, p.Length - 1);

        public static byte[] Simple(MsgType t) => new[] { (byte)t };

        /// <summary>Enter / MouseMove: absolute position in the receiver's own desktop coordinates.</summary>
        public static byte[] Point(MsgType t, int x, int y)
        {
            var p = new byte[9];
            p[0] = (byte)t;
            WriteInt(p, 1, x);
            WriteInt(p, 5, y);
            return p;
        }

        public static void ReadPoint(byte[] p, out int x, out int y)
        {
            x = ReadInt(p, 1);
            y = ReadInt(p, 5);
        }

        public static byte[] Button(MouseButton b, bool down) => new[] { (byte)MsgType.MouseButton, (byte)b, (byte)(down ? 1 : 0) };

        public static byte[] Wheel(int delta, bool horizontal)
        {
            var p = new byte[6];
            p[0] = (byte)MsgType.MouseWheel;
            WriteInt(p, 1, delta);
            p[5] = (byte)(horizontal ? 1 : 0);
            return p;
        }

        public static byte[] Key(ushort vk, ushort scan, KeyFlags flags) =>
            new[] { (byte)MsgType.Key, (byte)vk, (byte)(vk >> 8), (byte)scan, (byte)(scan >> 8), (byte)flags };

        public static void ReadKey(byte[] p, out ushort vk, out ushort scan, out KeyFlags flags)
        {
            vk = (ushort)(p[1] | (p[2] << 8));
            scan = (ushort)(p[3] | (p[4] << 8));
            flags = (KeyFlags)p[5];
        }

        public static byte[] Ping(MsgType t, long value)
        {
            var p = new byte[9];
            p[0] = (byte)t;
            WriteInt(p, 1, (int)value);
            WriteInt(p, 5, (int)(value >> 32));
            return p;
        }

        public static long ReadPing(byte[] p) => (uint)ReadInt(p, 1) | ((long)ReadInt(p, 5) << 32);

        public static void WriteInt(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        public static int ReadInt(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }
}
