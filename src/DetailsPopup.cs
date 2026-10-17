using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TurboClaudeMonitor
{
    // What the popup needs from the tray app; the preview renderer supplies a fake one.
    internal interface IUsageView
    {
        UsageSnapshot Snapshot { get; }
        string Plan { get; }
        FetchStatus Status { get; }
        string StatusMessage { get; }
        bool Fetching { get; }
        bool IsStale { get; }
        bool PaceMarker { get; }
        void RequestRefresh();
        void OpenUsagePage();
        void OpenClaudeCode();
    }

    // The flyout shown when the tray icon is clicked: every limit with its bar, reset time and projection.
    internal sealed class DetailsPopup : Form
    {
        const TextFormatFlags LineFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;

        sealed class LinkArea
        {
            public Rectangle Bounds;
            public Action Click;
        }

        readonly IUsageView source;
        readonly float scale;
        readonly Font fTitle, fLabel, fPercent, fSmall, fSmallBold, fSmallUnderline;
        readonly List<LinkArea> links = new List<LinkArea>();
        Palette pal;
        int hoverLink = -1;
        bool growUp = true;
        bool dwmRounded;
        DateTime hiddenAt = DateTime.MinValue;

        public DetailsPopup(IUsageView source)
        {
            this.source = source;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Utilizzo di Claude";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            fTitle = new Font("Segoe UI Semibold", 11f);
            fLabel = new Font("Segoe UI", 9.5f);
            fPercent = new Font("Segoe UI Semibold", 11f);
            fSmall = new Font("Segoe UI", 8.5f);
            fSmallBold = new Font("Segoe UI Semibold", 8.5f);
            fSmallUnderline = new Font("Segoe UI", 8.5f, FontStyle.Underline);
            pal = Palette.Get(Theme.AppsAreLight());
            BackColor = pal.Back;
            Width = S(340);
        }

        public DateTime HiddenAt
        {
            get { return hiddenAt; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW: no Alt+Tab entry
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowFrame();
        }

        // Windows 11 rounds the corners and draws the border; older systems get a painted border.
        void ApplyWindowFrame()
        {
            try
            {
                int round = Native.DWMWCP_ROUND;
                dwmRounded = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
                int border = ColorTranslator.ToWin32(pal.Border);
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch (Exception)
            {
                dwmRounded = false;
            }
        }

        int S(float px)
        {
            return (int)Math.Round(px * scale);
        }

        public void ShowNear(Point anchor)
        {
            pal = Palette.Get(Theme.AppsAreLight());
            BackColor = pal.Back;
            if (IsHandleCreated) ApplyWindowFrame();
            hoverLink = -1;
            Width = S(340);
            Height = MeasureHeight();
            Location = PlaceNear(anchor, Size);
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            Invalidate();
        }

        public void RefreshContent()
        {
            if (!Visible) return;
            int h = MeasureHeight();
            if (h != Height)
            {
                int bottom = Bottom;
                Height = h;
                if (growUp) Top = bottom - h;
            }
            Invalidate();
        }

        int MeasureHeight()
        {
            using (Graphics g = CreateGraphics())
                return Render(g, false);
        }

        Point PlaceNear(Point anchor, Size size)
        {
            Screen screen = Screen.FromPoint(anchor);
            Rectangle wa = screen.WorkingArea, b = screen.Bounds;
            int gap = S(12), x, y;
            growUp = false;
            if (wa.Bottom < b.Bottom) { x = anchor.X - size.Width / 2; y = wa.Bottom - size.Height - gap; growUp = true; }
            else if (wa.Top > b.Top) { x = anchor.X - size.Width / 2; y = wa.Top + gap; }
            else if (wa.Left > b.Left) { x = wa.Left + gap; y = anchor.Y - size.Height / 2; }
            else if (wa.Right < b.Right) { x = wa.Right - size.Width - gap; y = anchor.Y - size.Height / 2; }
            else { x = anchor.X - size.Width / 2; y = anchor.Y - size.Height - gap; growUp = true; }
            x = Math.Max(wa.Left + gap, Math.Min(x, wa.Right - size.Width - gap));
            y = Math.Max(wa.Top + gap, Math.Min(y, wa.Bottom - size.Height - gap));
            return new Point(x, y);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) hiddenAt = DateTime.Now;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.KeyCode == Keys.F5) source.RequestRefresh();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int found = links.FindIndex(l => l.Bounds.Contains(e.Location));
            if (found == hoverLink) return;
            hoverLink = found;
            Cursor = found >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverLink < 0) return;
            hoverLink = -1;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (LinkArea l in links.ToArray())
                if (l.Bounds.Contains(e.Location))
                {
                    l.Click();
                    return;
                }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(pal.Back);
            Render(e.Graphics, true);
            if (!dwmRounded)
                using (var pen = new Pen(pal.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        // For the preview images: the popup as it would look, without showing a window.
        public Bitmap RenderToBitmap(bool light)
        {
            pal = Palette.Get(light);
            Width = S(340);
            int h = MeasureHeight();
            var bmp = new Bitmap(Width, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb); // opaque, so ClearType text renders cleanly
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(pal.Back);
                Render(g, true);
                using (var pen = new Pen(pal.Border))
                    g.DrawRectangle(pen, 0, 0, Width - 1, h - 1);
            }
            return bmp;
        }

        // Lays out the content top to bottom, painting only when asked; returns the height used.
        int Render(Graphics g, bool paint)
        {
            links.Clear();
            DateTime now = DateTime.Now;
            UsageSnapshot snap = source.Snapshot;
            bool stale = source.IsStale;
            int w = Width, pad = S(18), cw = w - 2 * pad, x = pad, y = S(16);

            Line(g, "Utilizzo di Claude", fTitle, pal.Text, x, y, paint);
            if (!string.IsNullOrEmpty(source.Plan))
            {
                Size ps = MeasureLine(g, source.Plan, fSmallBold);
                var badge = new Rectangle(x + cw - ps.Width - S(16), y + (fTitle.Height - ps.Height - S(6)) / 2, ps.Width + S(16), ps.Height + S(6));
                if (paint)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath path = Shapes.RoundRect(badge, badge.Height / 2f))
                    using (var brush = new SolidBrush(pal.Badge))
                        g.FillPath(brush, path);
                    TextRenderer.DrawText(g, source.Plan, fSmallBold, new Point(badge.X + S(8), badge.Y + S(3)), pal.Muted, LineFlags);
                }
            }
            y += fTitle.Height + S(14);

            string message = source.StatusMessage;
            if (message != null)
            {
                y += Wrapped(g, message, fSmall, pal.Warn, x, y, cw, paint) + S(6);
                if (source.Status == FetchStatus.TokenExpired)
                {
                    Link(g, "Apri Claude Code", x, y, source.OpenClaudeCode, paint);
                    y += fSmall.Height + S(6);
                }
                y += S(10);
            }

            if (snap != null && snap.Limits.Count > 0)
            {
                for (int i = 0; i < snap.Limits.Count; i++)
                {
                    if (i > 0) y += S(16);
                    y = DrawLimit(g, snap.Limits[i], x, y, cw, now, stale, paint);
                }
                if (snap.Extra != null && snap.Extra.Enabled)
                {
                    y += S(16);
                    string extra = "Crediti extra attivi";
                    if (snap.Extra.Percent.HasValue) extra += " · " + Fmt.Percent(snap.Extra.Percent.Value) + " del tetto mensile usato";
                    Line(g, extra, fSmall, pal.Muted, x, y, paint);
                    y += fSmall.Height;
                }
                if (source.PaceMarker)
                {
                    y += S(16);
                    y += Wrapped(g, "La tacca segna il tempo già trascorso della finestra: se la barra la supera, stai consumando più in fretta di quanto ti basterebbe fino al reset.", fSmall, pal.Muted, x, y, cw, paint);
                }
                y += S(16);
            }
            else if (message == null)
            {
                Line(g, "Lettura dei dati in corso…", fLabel, pal.Muted, x, y, paint);
                y += fLabel.Height + S(16);
            }

            if (paint)
                using (var pen = new Pen(pal.Separator))
                    g.DrawLine(pen, 0, y, w, y);
            y += S(12);

            string footer;
            if (source.Fetching) footer = "Aggiornamento…";
            else if (snap == null) footer = "Nessun dato ancora";
            else footer = (stale ? "Ultimi dati: " : "Aggiornato ") + Fmt.AtWhen(snap.FetchedAt, now);
            Line(g, footer, fSmall, pal.Muted, x, y, paint);

            int rx = x + cw - MeasureLine(g, "claude.ai", fSmall).Width;
            Link(g, "claude.ai", rx, y, source.OpenUsagePage, paint);
            rx -= S(16) + MeasureLine(g, "Aggiorna", fSmall).Width;
            Link(g, "Aggiorna", rx, y, source.RequestRefresh, paint);
            y += fSmall.Height + S(14);
            return y;
        }

        int DrawLimit(Graphics g, UsageLimit l, int x, int y, int w, DateTime now, bool stale, bool paint)
        {
            double pct = l.EffectivePercent(now);
            Color level = stale ? Theme.Stale(pal.IsLight) : Theme.Level(pct, pal.IsLight);
            string pctText = Fmt.Percent(pct);
            Size ps = MeasureLine(g, pctText, fPercent);
            Line(g, l.Label, fLabel, pal.Text, x, y + (ps.Height - fLabel.Height) / 2, paint);
            Line(g, pctText, fPercent, level, x + w - ps.Width, y, paint);
            y += ps.Height + S(5);

            int bh = S(6);
            double? elapsed = l.ElapsedFraction(now);
            if (paint)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath track = Shapes.RoundRect(new RectangleF(x, y, w, bh), bh / 2f))
                using (var brush = new SolidBrush(pal.Track))
                    g.FillPath(brush, track);
                int fill = (int)Math.Round(w * Math.Min(pct, 100) / 100.0);
                if (pct > 0)
                    using (GraphicsPath bar = Shapes.RoundRect(new RectangleF(x, y, Math.Max(fill, bh), bh), bh / 2f))
                    using (var brush = new SolidBrush(level))
                        g.FillPath(brush, bar);
                if (source.PaceMarker && elapsed.HasValue)
                {
                    g.SmoothingMode = SmoothingMode.None;
                    int mx = x + (int)Math.Round(w * elapsed.Value);
                    using (var brush = new SolidBrush(pal.Marker))
                        g.FillRectangle(brush, mx - S(1), y - S(3), Math.Max(2, S(2)), bh + S(6));
                }
            }
            y += bh + S(7);

            string reset;
            if (l.HasReset(now)) reset = "Finestra azzerata, in attesa dei nuovi dati";
            else if (l.ResetsAt.HasValue) reset = "Si azzera tra " + Fmt.Countdown(l.ResetsAt.Value - now) + " · " + Fmt.When(l.ResetsAt.Value, now);
            else reset = l.Key == "five_hour" ? "Nessuna sessione in corso" : "Nessun utilizzo in questa finestra";
            Line(g, reset, fSmall, pal.Muted, x, y, paint);
            y += fSmall.Height;

            string projection = null;
            Color projectionColor = pal.Muted;
            // A projection from old numbers would drift with the clock alone, so it is shown only on fresh data.
            DateTime? exhaustion = stale ? null : l.ProjectedExhaustion(now);
            double? atReset = stale ? null : l.ProjectedAtReset(now);
            if (pct >= 100)
            {
                projection = "Limite raggiunto";
                projectionColor = level;
            }
            else if (exhaustion.HasValue)
            {
                projection = "A questo ritmo si esaurisce " + Fmt.AtWhen(exhaustion.Value, now);
                projectionColor = Theme.Level(85, pal.IsLight);
            }
            else if (atReset.HasValue)
            {
                projection = "A questo ritmo arrivi al reset con circa " + Fmt.Percent(atReset.Value);
            }
            if (projection != null)
            {
                y += S(2);
                Line(g, projection, fSmall, projectionColor, x, y, paint);
                y += fSmall.Height;
            }
            return y;
        }

        void Line(Graphics g, string text, Font font, Color color, int x, int y, bool paint)
        {
            if (paint) TextRenderer.DrawText(g, text, font, new Point(x, y), color, LineFlags);
        }

        static Size MeasureLine(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), LineFlags);
        }

        static int Wrapped(Graphics g, string text, Font font, Color color, int x, int y, int width, bool paint)
        {
            Size s = TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), WrapFlags);
            if (paint) TextRenderer.DrawText(g, text, font, new Rectangle(x, y, width, s.Height), color, WrapFlags);
            return s.Height;
        }

        void Link(Graphics g, string text, int x, int y, Action click, bool paint)
        {
            Size s = MeasureLine(g, text, fSmall);
            int index = links.Count;
            links.Add(new LinkArea { Bounds = new Rectangle(x - S(3), y - S(3), s.Width + S(6), s.Height + S(6)), Click = click });
            if (paint) TextRenderer.DrawText(g, text, index == hoverLink ? fSmallUnderline : fSmall, new Point(x, y), pal.Link, LineFlags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fTitle.Dispose();
                fLabel.Dispose();
                fPercent.Dispose();
                fSmall.Dispose();
                fSmallBold.Dispose();
                fSmallUnderline.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
