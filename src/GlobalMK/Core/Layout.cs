using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace GlobalMK
{
    /// <summary>One monitor of a machine, in that machine's own desktop pixel coordinates.</summary>
    public struct ScreenInfo
    {
        public Rectangle Bounds;
        public bool Primary;

        public ScreenInfo(Rectangle bounds, bool primary)
        {
            Bounds = bounds;
            Primary = primary;
        }

        public override string ToString() => $"{Bounds.Width}x{Bounds.Height}@{Bounds.X},{Bounds.Y}{(Primary ? "*" : "")}";
    }

    public enum Direction { Left, Right, Up, Down }

    /// <summary>
    /// A machine placed in the shared layout. Its monitors keep their own Windows arrangement;
    /// the whole desktop is placed so that its bounding box's top-left corner sits at <see cref="Position"/>
    /// in the global layout space (1 unit = 1 pixel of whichever machine is there).
    /// </summary>
    public sealed class MachineGeom
    {
        public readonly string Name;
        public readonly bool IsLocal;
        public readonly bool Online;
        public readonly Connection Conn;
        public readonly Rectangle[] Monitors;
        public readonly int PrimaryIndex;
        public readonly Rectangle Bounds;
        public readonly Point Position;

        public MachineGeom(string name, bool isLocal, ScreenInfo[] screens, Point position, bool online, Connection conn)
        {
            if (screens == null || screens.Length == 0)
                screens = new[] { new ScreenInfo(new Rectangle(0, 0, 1920, 1080), true) };
            Name = name;
            IsLocal = isLocal;
            Online = online;
            Conn = conn;
            Monitors = screens.Select(s => s.Bounds).ToArray();
            PrimaryIndex = Math.Max(0, Array.FindIndex(screens, s => s.Primary));
            Rectangle b = Monitors[0];
            foreach (var r in Monitors) b = Rectangle.Union(b, r);
            Bounds = b;
            Position = position;
        }

        public Point ToGlobal(Point p) => new Point(p.X - Bounds.X + Position.X, p.Y - Bounds.Y + Position.Y);
        public Point ToLocal(Point g) => new Point(g.X - Position.X + Bounds.X, g.Y - Position.Y + Bounds.Y);
        public Rectangle ToGlobal(Rectangle r) => new Rectangle(ToGlobal(r.Location), r.Size);
        public Rectangle GlobalBounds => new Rectangle(Position, Bounds.Size);
        public IEnumerable<Rectangle> GlobalMonitors => Monitors.Select(ToGlobal);

        public Point PrimaryCenter
        {
            get
            {
                var r = Monitors[PrimaryIndex];
                return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            }
        }

        public bool Contains(Point p)
        {
            foreach (var r in Monitors)
                if (r.Contains(p)) return true;
            return false;
        }

        public int MonitorIndexAt(Point p)
        {
            for (int i = 0; i < Monitors.Length; i++)
                if (Monitors[i].Contains(p)) return i;
            return -1;
        }

        public Rectangle NearestMonitor(Point p)
        {
            Rectangle best = Monitors[0];
            long bestD = long.MaxValue;
            foreach (var r in Monitors)
            {
                long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - (r.Right - 1) : 0;
                long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - (r.Bottom - 1) : 0;
                long d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        public static Point Clamp(Point p, Rectangle r) =>
            new Point(Math.Min(Math.Max(p.X, r.Left), r.Right - 1), Math.Min(Math.Max(p.Y, r.Top), r.Bottom - 1));
    }

    public sealed class RayHit
    {
        public readonly MachineGeom Machine;
        public readonly Point EntryGlobal;
        public readonly Direction Direction;

        public RayHit(MachineGeom machine, Point entryGlobal, Direction direction)
        {
            Machine = machine;
            EntryGlobal = entryGlobal;
            Direction = direction;
        }

        /// <summary>Entry point in the destination machine's own coordinates.</summary>
        public Point EntryLocal => Machine.ToLocal(EntryGlobal);
    }

    /// <summary>Immutable picture of the whole layout; swapped atomically when anything changes.</summary>
    public sealed class LayoutSnapshot
    {
        public readonly MachineGeom[] Machines;
        public readonly MachineGeom Local;

        public LayoutSnapshot(IEnumerable<MachineGeom> machines)
        {
            Machines = machines.ToArray();
            Local = Machines.FirstOrDefault(m => m.IsLocal);
        }

        public MachineGeom Find(string name) =>
            Machines.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Casts a ray from a global edge pixel in <paramref name="dir"/> and returns the first monitor hit.
        /// Gaps between machines are allowed; the ray just needs to line up with a monitor.
        /// Returns null when nothing is hit or when the first hit belongs to <paramref name="from"/>.
        /// </summary>
        public RayHit RayCast(Point o, Direction dir, MachineGeom from)
        {
            MachineGeom best = null;
            Rectangle bestRect = Rectangle.Empty;
            long bestDist = long.MaxValue;
            foreach (var m in Machines)
            {
                foreach (var lr in m.Monitors)
                {
                    var r = m.ToGlobal(lr);
                    long dist;
                    switch (dir)
                    {
                        case Direction.Left:
                            if (o.Y < r.Top || o.Y >= r.Bottom || r.Right > o.X) continue;
                            dist = o.X - r.Right;
                            break;
                        case Direction.Right:
                            if (o.Y < r.Top || o.Y >= r.Bottom || r.Left <= o.X) continue;
                            dist = r.Left - o.X;
                            break;
                        case Direction.Up:
                            if (o.X < r.Left || o.X >= r.Right || r.Bottom > o.Y) continue;
                            dist = o.Y - r.Bottom;
                            break;
                        default:
                            if (o.X < r.Left || o.X >= r.Right || r.Top <= o.Y) continue;
                            dist = r.Top - o.Y;
                            break;
                    }
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = m;
                        bestRect = r;
                    }
                }
            }
            if (best == null || best == from) return null;
            Point entry;
            switch (dir)
            {
                case Direction.Left: entry = new Point(bestRect.Right - 1, o.Y); break;
                case Direction.Right: entry = new Point(bestRect.Left, o.Y); break;
                case Direction.Up: entry = new Point(o.X, bestRect.Bottom - 1); break;
                default: entry = new Point(o.X, bestRect.Top); break;
            }
            return new RayHit(best, entry, dir);
        }

        /// <summary>
        /// For the machine whose real cursor is at <paramref name="p"/> (local coords): if the cursor sits in the
        /// 1-pixel "jump zone" at the outer edge of its desktop and another machine lies beyond, return it.
        /// </summary>
        public RayHit FindEdgeExit(MachineGeom m, Point p, int cornerSize)
        {
            var mon = m.NearestMonitor(p);
            var cp = MachineGeom.Clamp(p, mon);
            RayHit hit;
            if (p.X <= mon.Left && !m.Contains(new Point(mon.Left - 1, cp.Y)) && !InCorner(cp.Y, mon.Top, mon.Bottom, cornerSize)
                && (hit = RayCast(m.ToGlobal(new Point(mon.Left, cp.Y)), Direction.Left, m)) != null) return hit;
            if (p.X >= mon.Right - 1 && !m.Contains(new Point(mon.Right, cp.Y)) && !InCorner(cp.Y, mon.Top, mon.Bottom, cornerSize)
                && (hit = RayCast(m.ToGlobal(new Point(mon.Right - 1, cp.Y)), Direction.Right, m)) != null) return hit;
            if (p.Y <= mon.Top && !m.Contains(new Point(cp.X, mon.Top - 1)) && !InCorner(cp.X, mon.Left, mon.Right, cornerSize)
                && (hit = RayCast(m.ToGlobal(new Point(cp.X, mon.Top)), Direction.Up, m)) != null) return hit;
            if (p.Y >= mon.Bottom - 1 && !m.Contains(new Point(cp.X, mon.Bottom)) && !InCorner(cp.X, mon.Left, mon.Right, cornerSize)
                && (hit = RayCast(m.ToGlobal(new Point(cp.X, mon.Bottom - 1)), Direction.Down, m)) != null) return hit;
            return null;
        }

        /// <summary>
        /// For a remote machine's virtual cursor moving from <paramref name="v"/> (inside) to <paramref name="nv"/>
        /// (outside all its monitors): return the machine the cursor crosses into, if any.
        /// </summary>
        public RayHit FindOvershootExit(MachineGeom m, Point v, Point nv, int cornerSize)
        {
            int idx = m.MonitorIndexAt(v);
            var mon = idx >= 0 ? m.Monitors[idx] : m.NearestMonitor(v);
            var dirs = Math.Abs(nv.X - v.X) >= Math.Abs(nv.Y - v.Y)
                ? new[] { Direction.Left, Direction.Right, Direction.Up, Direction.Down }
                : new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right };
            foreach (var d in dirs)
            {
                Point edge;
                switch (d)
                {
                    case Direction.Left:
                        if (nv.X >= mon.Left) continue;
                        edge = new Point(mon.Left, Math.Min(Math.Max(nv.Y, mon.Top), mon.Bottom - 1));
                        if (InCorner(edge.Y, mon.Top, mon.Bottom, cornerSize)) continue;
                        break;
                    case Direction.Right:
                        if (nv.X < mon.Right) continue;
                        edge = new Point(mon.Right - 1, Math.Min(Math.Max(nv.Y, mon.Top), mon.Bottom - 1));
                        if (InCorner(edge.Y, mon.Top, mon.Bottom, cornerSize)) continue;
                        break;
                    case Direction.Up:
                        if (nv.Y >= mon.Top) continue;
                        edge = new Point(Math.Min(Math.Max(nv.X, mon.Left), mon.Right - 1), mon.Top);
                        if (InCorner(edge.X, mon.Left, mon.Right, cornerSize)) continue;
                        break;
                    default:
                        if (nv.Y < mon.Bottom) continue;
                        edge = new Point(Math.Min(Math.Max(nv.X, mon.Left), mon.Right - 1), mon.Bottom - 1);
                        if (InCorner(edge.X, mon.Left, mon.Right, cornerSize)) continue;
                        break;
                }
                var hit = RayCast(m.ToGlobal(edge), d, m);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>Clamp a remote virtual cursor back onto the monitor it was on.</summary>
        public static Point ClampMove(MachineGeom m, Point v, Point nv)
        {
            if (m.Contains(nv)) return nv;
            int idx = m.MonitorIndexAt(v);
            var mon = idx >= 0 ? m.Monitors[idx] : m.NearestMonitor(v);
            return MachineGeom.Clamp(nv, mon);
        }

        /// <summary>Nudge an entry point one pixel further in the travel direction so it is not in the jump zone.</summary>
        public static Point StepInside(MachineGeom m, Point p, Direction d)
        {
            Point q = p;
            switch (d)
            {
                case Direction.Left: q.X--; break;
                case Direction.Right: q.X++; break;
                case Direction.Up: q.Y--; break;
                default: q.Y++; break;
            }
            return m.Contains(q) ? q : p;
        }

        public static bool Overlaps(IEnumerable<Rectangle> a, IEnumerable<Rectangle> b)
        {
            var bl = b.ToList();
            foreach (var r in a)
                foreach (var s in bl)
                    if (r.IntersectsWith(s)) return true;
            return false;
        }

        private static bool InCorner(int v, int lo, int hiExclusive, int size) =>
            size > 0 && (v - lo < size || (hiExclusive - 1) - v < size);
    }
}
