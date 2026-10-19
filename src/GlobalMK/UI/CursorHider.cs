using System;
using System.Drawing;
using System.Windows.Forms;

namespace GlobalMK
{
    /// <summary>
    /// A tiny, nearly transparent, top-most window placed under the parked cursor. Its window class cursor is
    /// blank, so the server's cursor disappears while the mouse is controlling another computer.
    /// </summary>
    internal sealed class CursorHider : Form
    {
        private const int Size_ = 64;
        private static Cursor _blank;

        public CursorHider()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.01;
            TopMost = true;
            Size = new Size(Size_, Size_);
            Cursor = BlankCursor();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/;
                return cp;
            }
        }

        public void ShowAt(Point p)
        {
            Bounds = new Rectangle(p.X - Size_ / 2, p.Y - Size_ / 2, Size_, Size_);
            if (!Visible) Show();
        }

        private static Cursor BlankCursor()
        {
            if (_blank != null) return _blank;
            var and = new byte[32 * 32 / 8];
            for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
            var xor = new byte[32 * 32 / 8];
            IntPtr h = Native.CreateCursor(Native.GetModuleHandle(null), 0, 0, 32, 32, and, xor);
            _blank = h != IntPtr.Zero ? new Cursor(h) : Cursors.Default;
            return _blank;
        }
    }
}
