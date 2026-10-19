using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GlobalMK
{
    internal static class AppIcon
    {
        private static Icon _base;

        private static Icon Base
        {
            get
            {
                if (_base != null) return _base;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlobalMK.app.ico"))
                    _base = s != null ? new Icon(s) : SystemIcons.Application;
                return _base;
            }
        }

        public static Icon Large => new Icon(Base, SystemInformation.IconSize);

        /// <summary>Notification-area icon with a small status dot in the corner.</summary>
        public static Icon Small(Color dot)
        {
            var size = SystemInformation.SmallIconSize;
            using (var ic = new Icon(Base, size))
            using (var bmp = new Bitmap(size.Width, size.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawIcon(ic, new Rectangle(0, 0, size.Width, size.Height));
                    float d = size.Width * 0.45f;
                    var r = new RectangleF(size.Width - d, size.Height - d, d - 0.5f, d - 0.5f);
                    using (var b = new SolidBrush(dot)) g.FillEllipse(b, r);
                    using (var p = new Pen(Color.White, Math.Max(1f, size.Width / 16f))) g.DrawEllipse(p, r);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
