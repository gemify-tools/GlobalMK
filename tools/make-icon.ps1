# Generates src/GlobalMK/app.ico (multi-size). Run with Windows PowerShell:
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot "..\src\GlobalMK\app.ico"))

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconMaker
{
    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        float k = s / 256f;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            using (var path = RoundRect(new RectangleF(6 * k, 6 * k, 244 * k, 244 * k), 54 * k))
            using (var b = new SolidBrush(Color.FromArgb(37, 99, 235)))
                g.FillPath(b, path);

            // Two screens side by side.
            using (var pen = new Pen(Color.FromArgb(191, 219, 254), Math.Max(1.2f, 14 * k)))
            {
                g.DrawRectangle(pen, 34 * k, 58 * k, 84 * k, 62 * k);
                g.DrawRectangle(pen, 138 * k, 58 * k, 84 * k, 62 * k);
            }

            // Pointer crossing between them.
            var arrow = new[]
            {
                new PointF(104, 84), new PointF(104, 212), new PointF(134, 184), new PointF(156, 228),
                new PointF(180, 216), new PointF(158, 172), new PointF(198, 172),
            };
            for (int i = 0; i < arrow.Length; i++) arrow[i] = new PointF(arrow[i].X * k, arrow[i].Y * k);
            using (var b = new SolidBrush(Color.White)) g.FillPolygon(b, arrow);
            using (var pen = new Pen(Color.FromArgb(30, 64, 175), Math.Max(1f, 8 * k)) { LineJoin = LineJoin.Round })
                g.DrawPolygon(pen, arrow);
        }
        return bmp;
    }

    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width;
        int maskRow = ((s + 31) / 32) * 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(s * s * 4 + maskRow * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var row = new byte[s * 4];
        for (int y = s - 1; y >= 0; y--)
        {
            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
            w.Write(row);
        }
        bmp.UnlockBits(data);
        w.Write(new byte[maskRow * s]);
        return ms.ToArray();
    }

    public static void Write(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Draw(s))
            {
                if (s == 256)
                {
                    var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
                else images.Add(Dib(bmp));
            }
        }
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
'@

$full = [System.IO.Path]::GetFullPath($Out)
[IconMaker]::Write($full)
Write-Host "Wrote $full"
