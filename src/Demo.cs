using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace TurboClaudeMonitor
{
    // Made-up numbers in the endpoint's own JSON shape, so --demo also exercises the parser.
    internal static class Demo
    {
        public static string Json(DateTime now)
        {
            string session = Iso(now.AddMinutes(133));
            string week = Iso(now.AddDays(3).AddHours(4));
            return "{\"five_hour\":{\"utilization\":42.0,\"resets_at\":\"" + session + "\"}," +
                   "\"seven_day\":{\"utilization\":75.0,\"resets_at\":\"" + week + "\"}," +
                   "\"seven_day_overage_included\":{\"utilization\":31.0,\"resets_at\":\"" + week + "\"}," +
                   "\"seven_day_sonnet\":{\"utilization\":12.0,\"resets_at\":\"" + week + "\"}," +
                   "\"seven_day_opus\":null,\"seven_day_oauth_apps\":null," +
                   "\"limits\":[{\"kind\":\"weekly_scoped\",\"scope\":{\"model\":{\"display_name\":\"Fable\"}},\"percent\":31.0,\"resets_at\":\"" + week + "\"}]," +
                   "\"extra_usage\":{\"is_enabled\":false,\"monthly_limit\":null,\"used_credits\":null,\"utilization\":null}}";
        }

        static string Iso(DateTime local)
        {
            return local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
        }

        public static FetchResult Fetch()
        {
            DateTime now = DateTime.Now;
            return new FetchResult { Status = FetchStatus.Ok, Snapshot = UsageParser.Parse(Json(now), now), Plan = "Max 20x" };
        }
    }

    // --render <folder>: writes enlarged previews of the tray icon and of the popup, without touching the tray.
    internal static class Preview
    {
        sealed class FakeView : IUsageView
        {
            public UsageSnapshot Snapshot { get; set; }
            public string Plan { get; set; }
            public FetchStatus Status { get; set; }
            public string StatusMessage { get; set; }
            public bool Fetching { get { return false; } }
            public bool IsStale { get; set; }
            public bool PaceMarker { get { return true; } }
            public void RequestRefresh() { }
            public void OpenUsagePage() { }
            public void OpenClaudeCode() { }
        }

        public static void RenderAll(string folder)
        {
            Directory.CreateDirectory(folder);
            DateTime now = DateTime.Now;

            var cases = new List<BarSpec[]>
            {
                new[] { new BarSpec(42, 0.56), new BarSpec(75, 0.55), new BarSpec(31, 0.55) },
                new[] { new BarSpec(4, 0.10), new BarSpec(18, 0.30), new BarSpec(0, 0.30) },
                new[] { new BarSpec(86, 0.70), new BarSpec(96, 0.90), new BarSpec(64, 0.90) },
                new[] { new BarSpec(100, 0.80), new BarSpec(100, 0.95), new BarSpec(100, 0.95) },
                new[] { new BarSpec(55, 0.40), new BarSpec(81, 0.60) },
                new BarSpec[0],
            };
            int[] sizes = { 16, 20, 24, 32 };
            foreach (bool light in new[] { true, false })
            {
                RenderSheet(Path.Combine(folder, light ? "icone-barra-chiara.png" : "icone-barra-scura.png"), cases, sizes, light, false);
            }
            RenderSheet(Path.Combine(folder, "icone-dati-vecchi.png"), cases, sizes, true, true);

            UsageSnapshot snap = UsageParser.Parse(Demo.Json(now), now.AddMinutes(-1));
            foreach (bool light in new[] { true, false })
            {
                var view = new FakeView { Snapshot = snap, Plan = "Max 20x", Status = FetchStatus.Ok };
                using (var popup = new DetailsPopup(view))
                using (Bitmap bmp = popup.RenderToBitmap(light))
                    bmp.Save(Path.Combine(folder, light ? "popup-chiaro.png" : "popup-scuro.png"), ImageFormat.Png);
            }

            var expired = new FakeView
            {
                Snapshot = snap,
                Plan = "Max 20x",
                Status = FetchStatus.TokenExpired,
                IsStale = true,
                StatusMessage = "Il login di Claude Code è scaduto. Si rinnova da solo appena usi Claude Code dal terminale; l'icona riprende entro un minuto.",
            };
            using (var popup = new DetailsPopup(expired))
            using (Bitmap bmp = popup.RenderToBitmap(true))
                bmp.Save(Path.Combine(folder, "popup-login-scaduto.png"), ImageFormat.Png);
        }

        // Each row is one icon size, each column one scenario: first at real size, then enlarged 8x.
        static void RenderSheet(string path, List<BarSpec[]> cases, int[] sizes, bool lightTaskbar, bool stale)
        {
            const int zoom = 8, pad = 12;
            int cell = 32 * zoom + pad;
            Color taskbar = lightTaskbar ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28);
            using (var sheet = new Bitmap(pad + cases.Count * (cell + 40), pad + sizes.Length * cell))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(taskbar);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int r = 0; r < sizes.Length; r++)
                    for (int c = 0; c < cases.Count; c++)
                    {
                        int s = sizes[r];
                        int x = pad + c * (cell + 40), y = pad + r * cell;
                        using (Bitmap icon = IconRenderer.Render(s, cases[c], lightTaskbar, stale))
                        {
                            g.DrawImage(icon, new Rectangle(x, y, s, s));
                            g.DrawImage(icon, new Rectangle(x + 40, y, s * zoom, s * zoom));
                        }
                    }
                sheet.Save(path, ImageFormat.Png);
            }
        }
    }
}
