using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace TurboClaudeMonitor
{
    internal struct BarSpec
    {
        public readonly double Percent;
        public readonly double? Elapsed;

        public BarSpec(double percent, double? elapsed)
        {
            Percent = percent;
            Elapsed = elapsed;
        }
    }

    // The tray icon: one horizontal bar per limit (session, week, Fable), pixel-snapped so it stays sharp at 16 px.
    // An optional one-pixel tick marks how much of each window has elapsed: fill past the tick means a pace above budget.
    internal static class IconRenderer
    {
        static void Layout(int size, int count, out int[] tops, out int height)
        {
            int margin = size >= 32 ? 2 : 1;
            int gap = size >= 24 ? 2 : 1;
            if (count <= 2) gap *= 2;
            height = Math.Max(1, (size - 2 * margin - (count - 1) * gap) / count);
            int start = (size - (count * height + (count - 1) * gap)) / 2;
            tops = new int[count];
            for (int i = 0; i < count; i++) tops[i] = start + i * (height + gap);
        }

        static int MarkerWidth(int size)
        {
            return size >= 32 ? 2 : 1;
        }

        static int FillWidth(int size, double pct)
        {
            if (pct <= 0) return 0;
            int px = (int)Math.Round(size * Math.Min(pct, 100) / 100.0);
            if (px == 0) px = 1;                        // any usage at all stays visible
            if (pct < 100 && px >= size) px = size - 1; // only a spent limit looks full
            return px;
        }

        static int MarkerX(int size, double? elapsed)
        {
            if (!elapsed.HasValue) return -1;
            return Math.Max(0, Math.Min(size - MarkerWidth(size), (int)Math.Floor(elapsed.Value * size)));
        }

        // Changes only when the pixels would, so the tray is not redrawn for nothing.
        public static string Signature(int size, IList<BarSpec> bars, bool lightTaskbar, bool stale)
        {
            var sb = new StringBuilder();
            sb.Append(size).Append(lightTaskbar ? 'L' : 'D').Append(stale ? 'S' : 'F');
            foreach (BarSpec b in bars)
                sb.Append('|').Append(FillWidth(size, b.Percent)).Append(',').Append(MarkerX(size, b.Elapsed)).Append(',').Append(Theme.LevelIndex(b.Percent));
            return sb.ToString();
        }

        public static Bitmap Render(int size, IList<BarSpec> bars, bool lightTaskbar, bool stale)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                int count = bars.Count == 0 ? 3 : bars.Count;
                int[] tops;
                int h;
                Layout(size, count, out tops, out h);

                Color track = lightTaskbar ? Color.FromArgb(64, 0, 0, 0) : Color.FromArgb(80, 255, 255, 255);
                Color marker = lightTaskbar ? Color.FromArgb(235, 20, 20, 20) : Color.FromArgb(245, 255, 255, 255);
                using (var trackBrush = new SolidBrush(track))
                using (var markerBrush = new SolidBrush(marker))
                {
                    for (int i = 0; i < count; i++)
                    {
                        g.FillRectangle(trackBrush, 0, tops[i], size, h);
                        if (i >= bars.Count) continue;

                        BarSpec bar = bars[i];
                        int fill = FillWidth(size, bar.Percent);
                        if (fill > 0)
                        {
                            Color c = stale ? Theme.Stale(lightTaskbar) : Theme.Level(bar.Percent, lightTaskbar);
                            using (var b = new SolidBrush(c)) g.FillRectangle(b, 0, tops[i], fill, h);
                        }
                        int mx = MarkerX(size, bar.Elapsed);
                        if (mx >= 0) g.FillRectangle(markerBrush, mx, tops[i], MarkerWidth(size), h);
                    }
                }
            }
            return bmp;
        }

        // The executable's own icon: the same three bars on a dark rounded tile.
        public static Bitmap RenderAppIcon(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath tile = Shapes.RoundRect(new RectangleF(0, 0, size, size), size * 0.22f))
                using (var bg = new SolidBrush(Theme.Rgb(0x262624)))
                    g.FillPath(bg, tile);

                double[] fills = { 0.45, 0.72, 0.9 };
                Color[] colors = { Theme.Rgb(0x3FB950), Theme.Rgb(0xE3B341), Theme.Rgb(0xD97757) };
                float pad = size * 0.17f, gap = size * 0.09f;
                float h = (size - 2 * pad - 2 * gap) / 3f, w = size - 2 * pad;
                using (var trackBrush = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float y = pad + i * (h + gap);
                        using (GraphicsPath t = Shapes.RoundRect(new RectangleF(pad, y, w, h), h / 2))
                            g.FillPath(trackBrush, t);
                        using (GraphicsPath f = Shapes.RoundRect(new RectangleF(pad, y, (float)(w * fills[i]), h), h / 2))
                        using (var fb = new SolidBrush(colors[i]))
                            g.FillPath(fb, f);
                    }
                }
            }
            return bmp;
        }

        // An .ico with PNG-compressed images, which Windows reads at every size.
        public static void WriteAppIcon(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap bmp = RenderAppIcon(s))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim);
                    w.Write(dim);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] png in images) w.Write(png);
            }
        }
    }

    internal static class Shapes
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
