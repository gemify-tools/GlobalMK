using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace GlobalMK
{
    internal sealed class EditorMachine
    {
        public string Name;
        public bool IsLocal;
        public bool Online;
        public bool Active;
        public Rectangle[] Monitors;   // the machine's own coordinates
        public int PrimaryIndex;
        public Point Position;         // global position of the bounding box's top-left corner

        public Rectangle Bounds
        {
            get
            {
                var b = Monitors[0];
                foreach (var r in Monitors) b = Rectangle.Union(b, r);
                return b;
            }
        }

        public IEnumerable<Rectangle> GlobalMonitors(Point pos)
        {
            var b = Bounds;
            return Monitors.Select(r => new Rectangle(r.X - b.X + pos.X, r.Y - b.Y + pos.Y, r.Width, r.Height));
        }

        public static EditorMachine From(MachineGeom m, string activeName) => new EditorMachine
        {
            Name = m.Name,
            IsLocal = m.IsLocal,
            Online = m.Online,
            Active = activeName == null ? m.IsLocal : string.Equals(activeName, m.Name, StringComparison.OrdinalIgnoreCase),
            Monitors = m.Monitors,
            PrimaryIndex = m.PrimaryIndex,
            Position = m.Position,
        };
    }

    /// <summary>Drag-and-drop editor for arranging computers' screens. Edges snap together.</summary>
    internal sealed class LayoutEditor : Control
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(37, 99, 235),   // blue
            Color.FromArgb(5, 150, 105),   // green
            Color.FromArgb(217, 119, 6),   // amber
            Color.FromArgb(124, 58, 237),  // violet
            Color.FromArgb(219, 39, 119),  // pink
            Color.FromArgb(8, 145, 178),   // cyan
        };

        private const int Margin_ = 24;
        private const int SnapPixels = 12;

        private List<EditorMachine> _machines = new List<EditorMachine>();
        private List<EditorMachine> _pending;
        private float _scale = 0.05f;
        private PointF _worldOrigin;
        private EditorMachine _drag;
        private Point _dragStart;
        private Size _grab;
        private string _flash;
        private readonly Timer _flashTimer = new Timer { Interval = 3500 };

        public event Action<string, Point> MachineMoved;
        public event Action<string> ForgetRequested;

        public string EmptyHint { get; set; } =
            "Other computers appear here once they connect. Then drag them next to this computer's screens.";

        public LayoutEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(243, 244, 246);
            _flashTimer.Tick += (s, e) => { _flash = null; _flashTimer.Stop(); Invalidate(); };
        }

        public void SetMachines(IEnumerable<EditorMachine> machines)
        {
            var list = machines.ToList();
            if (_drag != null) { _pending = list; return; }
            _machines = list;
            RecomputeView();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_drag == null) RecomputeView();
        }

        private void RecomputeView()
        {
            if (_machines.Count == 0 || Width <= 2 * Margin_ || Height <= 2 * Margin_) return;
            Rectangle world = Rectangle.Empty;
            foreach (var m in _machines)
                foreach (var r in m.GlobalMonitors(m.Position))
                    world = world.IsEmpty ? r : Rectangle.Union(world, r);
            // Leave room around everything so computers can be dragged to any side.
            int pad = Math.Max(world.Width, world.Height) / 5;
            world.Inflate(pad, pad);
            float sx = (Width - 2f * Margin_) / world.Width;
            float sy = (Height - 2f * Margin_ - 30) / world.Height;
            _scale = Math.Min(sx, sy);
            float cx = world.X + world.Width / 2f, cy = world.Y + world.Height / 2f;
            _worldOrigin = new PointF(cx - (Width / 2f) / _scale, cy - (Height / 2f - 15) / _scale);
        }

        private RectangleF ToScreen(Rectangle r) =>
            new RectangleF((r.X - _worldOrigin.X) * _scale, (r.Y - _worldOrigin.Y) * _scale, r.Width * _scale, r.Height * _scale);

        private Point ToWorld(Point p) =>
            new Point((int)Math.Round(p.X / _scale + _worldOrigin.X), (int)Math.Round(p.Y / _scale + _worldOrigin.Y));

        private EditorMachine HitTest(Point screenPt)
        {
            var w = ToWorld(screenPt);
            for (int i = _machines.Count - 1; i >= 0; i--)
                if (_machines[i].GlobalMonitors(_machines[i].Position).Any(r => r.Contains(w))) return _machines[i];
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var hit = HitTest(e.Location);
            if (hit == null) return;
            if (e.Button == MouseButtons.Right)
            {
                var menu = new ContextMenuStrip();
                var forget = menu.Items.Add("Forget this computer");
                forget.Enabled = !hit.IsLocal && !hit.Online;
                forget.Click += (s, a) => ForgetRequested?.Invoke(hit.Name);
                if (hit.IsLocal || hit.Online)
                    menu.Items.Add(new ToolStripLabel(hit.IsLocal ? "This is this computer" : "Connected computers can't be forgotten") { ForeColor = SystemColors.GrayText });
                menu.Show(this, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            _drag = hit;
            _dragStart = hit.Position;
            var w = ToWorld(e.Location);
            _grab = new Size(w.X - hit.Position.X, w.Y - hit.Position.Y);
            _machines.Remove(hit);
            _machines.Add(hit); // draw on top while dragging
            Capture = true;
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag == null)
            {
                Cursor = HitTest(e.Location) != null ? Cursors.Hand : Cursors.Default;
                return;
            }
            var w = ToWorld(e.Location);
            _drag.Position = Snap(_drag, new Point(w.X - _grab.Width, w.Y - _grab.Height));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag == null) return;
            var m = _drag;
            _drag = null;
            Capture = false;
            Cursor = Cursors.Default;

            var mine = m.GlobalMonitors(m.Position).ToList();
            bool overlaps = _machines.Where(o => o != m).Any(o => LayoutSnapshot.Overlaps(mine, o.GlobalMonitors(o.Position)));
            if (overlaps)
            {
                m.Position = _dragStart;
                Flash("Screens can't overlap — moved back. Place computers edge to edge.");
            }
            else if (m.Position != _dragStart)
            {
                MachineMoved?.Invoke(m.Name, m.Position);
            }

            if (_pending != null)
            {
                var p = _pending;
                _pending = null;
                SetMachines(p);
            }
            else
            {
                RecomputeView();
                Invalidate();
            }
        }

        private void Flash(string text)
        {
            _flash = text;
            _flashTimer.Stop();
            _flashTimer.Start();
            Invalidate();
        }

        private Point Snap(EditorMachine m, Point pos)
        {
            int threshold = (int)Math.Ceiling(SnapPixels / _scale);
            var mine = m.GlobalMonitors(pos).ToList();
            int bestX = threshold + 1, bestY = threshold + 1;
            foreach (var o in _machines)
            {
                if (o == m) continue;
                foreach (var or in o.GlobalMonitors(o.Position))
                {
                    foreach (var mr in mine)
                    {
                        foreach (int d in new[] { or.Right - mr.Left, or.Left - mr.Right, or.Left - mr.Left, or.Right - mr.Right })
                            if (Math.Abs(d) < Math.Abs(bestX)) bestX = d;
                        foreach (int d in new[] { or.Bottom - mr.Top, or.Top - mr.Bottom, or.Top - mr.Top, or.Bottom - mr.Bottom })
                            if (Math.Abs(d) < Math.Abs(bestY)) bestY = d;
                    }
                }
            }
            if (Math.Abs(bestX) <= threshold) pos.X += bestX;
            if (Math.Abs(bestY) <= threshold) pos.Y += bestY;
            return pos;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            using (var small = new Font(Font.FontFamily, Font.Size * 0.9f))
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                // Colors follow names, not drawing order, so they stay put while dragging.
                var remotes = _machines.Where(x => !x.IsLocal).Select(x => x.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var m in _machines)
                {
                    var hue = !m.Online ? Color.FromArgb(156, 163, 175)
                        : m.IsLocal ? Palette[0]
                        : Palette[1 + remotes.IndexOf(m.Name) % (Palette.Length - 1)];
                    DrawMachine(g, m, hue, small, bold);
                }

                if (_machines.Count <= 1)
                    DrawBanner(g, EmptyHint, SystemColors.GrayText, small, bottom: true);
                if (_flash != null)
                    DrawBanner(g, _flash, Color.FromArgb(185, 28, 28), Font, bottom: false);
            }
        }

        private void DrawMachine(Graphics g, EditorMachine m, Color hue, Font small, Font bold)
        {
            var monitors = m.GlobalMonitors(m.Position).ToList();
            var fill = Blend(hue, Color.White, m.Online ? 0.82f : 0.9f);
            var edge = m.Active ? Blend(hue, Color.Black, 0.25f) : hue;
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(edge, m.Active ? 3f : 1.5f))
            using (var text = new SolidBrush(Blend(hue, Color.Black, 0.45f)))
            {
                if (!m.Online) pen.DashStyle = DashStyle.Dash;
                for (int i = 0; i < monitors.Count; i++)
                {
                    var r = ToScreen(monitors[i]);
                    r.Inflate(-1, -1);
                    using (var path = Rounded(r, 4))
                    {
                        g.FillPath(brush, path);
                        g.DrawPath(pen, path);
                    }
                    var size = $"{m.Monitors[i].Width}×{m.Monitors[i].Height}";
                    var sz = g.MeasureString(size, small);
                    if (sz.Width < r.Width - 6 && sz.Height * 2.4f < r.Height)
                        g.DrawString(size, small, text, r.X + (r.Width - sz.Width) / 2, r.Bottom - sz.Height - 4);
                }

                var primary = ToScreen(monitors[Math.Min(m.PrimaryIndex, monitors.Count - 1)]);
                string title = m.Name;
                string sub = m.IsLocal ? "this computer" : m.Online ? "connected" : "offline";
                if (m.Active) sub += " · has the mouse";
                var clip = new RectangleF(primary.X + 6, primary.Y + 4, Math.Max(10, primary.Width - 12), Math.Max(10, primary.Height - 8));
                using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    g.DrawString(title, bold, text, clip, fmt);
                    var th = g.MeasureString(title, bold).Height;
                    if (clip.Height > th * 2)
                        g.DrawString(sub, small, text, new RectangleF(clip.X, clip.Y + th, clip.Width, clip.Height - th), fmt);
                }
            }
        }

        private void DrawBanner(Graphics g, string message, Color color, Font font, bool bottom)
        {
            var area = new RectangleF(12, bottom ? Height - 34 : 8, Width - 24, 26);
            using (var b = new SolidBrush(color))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(message, font, b, area, fmt);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _flashTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
