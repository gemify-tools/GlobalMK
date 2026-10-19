using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace GlobalMK
{
    internal static class Screens
    {
        /// <summary>All monitors of this computer in physical pixels, primary first.</summary>
        public static ScreenInfo[] Enumerate()
        {
            var list = new List<ScreenInfo>();
            Native.MonitorEnumProc callback = (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (Native.GetMonitorInfo(hMon, ref mi))
                {
                    var rc = mi.rcMonitor;
                    list.Add(new ScreenInfo(Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom), (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            if (list.Count == 0)
            {
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    list.Add(new ScreenInfo(s.Bounds, s.Primary));
            }
            return list.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();
        }
    }
}
