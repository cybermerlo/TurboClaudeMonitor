using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TurboClaudeMonitor
{
    internal sealed class TrayApp : ApplicationContext, IUsageView
    {
        static readonly int[] Thresholds = { 80, 95, 100 };
        const string UsagePageUrl = "https://claude.ai/settings/usage";

        readonly bool demo;
        readonly Settings settings;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Timer timer;
        readonly DetailsPopup popup;
        readonly HashSet<string> notified = new HashSet<string>();
        readonly List<ToolStripMenuItem> pollItems = new List<ToolStripMenuItem>();
        ToolStripMenuItem paceItem, notifyItem, refreshItem, autostartItem;

        UsageSnapshot snapshot;
        FetchStatus status = FetchStatus.Ok;
        FetchStatus loggedStatus = FetchStatus.Ok;
        string statusDetail, plan, tooltip, iconSignature;
        bool fetching, seeded, authAlertShown, keysLogged, needsLogin;
        DateTime? lastAttempt;
        DateTime nextFetchAt;
        Icon icon;
        IntPtr iconHandle;

        public TrayApp(bool demo)
        {
            this.demo = demo;
            settings = Settings.Load();
            popup = new DetailsPopup(this);
            GC.KeepAlive(popup.Handle); // the popup's window also marshals SystemEvents back to this thread

            menu = BuildMenu();
            tray = new NotifyIcon { ContextMenuStrip = menu };
            tray.MouseClick += OnTrayClick;
            tray.BalloonTipClicked += delegate { ShowPopup(); };

            if (!demo) LoadCache();
            UpdateVisuals(true);
            tray.Visible = true;

            timer = new Timer { Interval = 15000 };
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            if (!settings.FirstRunDone && !demo)
            {
                settings.FirstRunDone = true;
                settings.Save();
                tray.ShowBalloonTip(10000, "TurboClaudeMonitor è attivo",
                    "Clic sull'icona per i dettagli. Per tenerla sempre visibile: Impostazioni › Personalizzazione › Barra delle applicazioni › Altre icone della barra.",
                    ToolTipIcon.Info);
            }

            nextFetchAt = DateTime.Now;
            FetchNow(false);
        }

        // ---- IUsageView ----

        public UsageSnapshot Snapshot { get { return snapshot; } }
        public string Plan { get { return plan; } }
        public FetchStatus Status { get { return status; } }
        public bool Fetching { get { return fetching; } }
        public bool PaceMarker { get { return settings.PaceMarker; } }

        // Grey icon: no data, a login problem, or data much older than the polling interval.
        public bool IsStale
        {
            get
            {
                if (demo) return false;
                if (snapshot == null) return true;
                if (status == FetchStatus.NoCredentials || status == FetchStatus.TokenExpired || status == FetchStatus.Unauthorized) return true;
                return DateTime.Now - snapshot.FetchedAt > TimeSpan.FromMinutes(settings.PollMinutes * 3 + 1);
            }
        }

        public string StatusMessage
        {
            get
            {
                string detail = string.IsNullOrEmpty(statusDetail) ? "" : " (" + statusDetail + ")";
                string retry = Fmt.AtWhen(nextFetchAt, DateTime.Now);
                switch (status)
                {
                    case FetchStatus.NoCredentials:
                        return "Non trovo il login di Claude Code" + detail + " in " + UsageClient.CredentialsPath +
                               ". Installa Claude Code da terminale, avvialo con claude e accedi con /login: l'icona si attiva da sola entro un minuto.";
                    case FetchStatus.TokenExpired:
                        if (needsLogin)
                            return "Il login di Claude Code è scaduto e il server non accetta più il rinnovo" + detail +
                                   ". Rifai /login in Claude Code dal terminale: l'icona riprende entro un minuto.";
                        if (settings.AutoRefresh)
                            return "Il login di Claude Code è scaduto e il rinnovo automatico per ora non è riuscito" + detail + ". Riprovo da solo tra qualche minuto.";
                        return "Il login di Claude Code è scaduto. Si rinnova da solo appena usi Claude Code dal terminale, oppure attiva «Rinnova il login da solo» dal menu.";
                    case FetchStatus.Unauthorized:
                        return "Il server ha rifiutato il login" + detail + ". Se continua, rifai /login in Claude Code. Nuovo tentativo " + retry + ".";
                    case FetchStatus.RateLimited:
                        return "Il server chiede di rallentare. Nuovo tentativo " + retry + ".";
                    case FetchStatus.NetworkError:
                        return "Connessione non riuscita" + detail + ". Nuovo tentativo " + retry + ".";
                    case FetchStatus.BadResponse:
                        return "Risposta inattesa dal server" + detail + ". Nuovo tentativo " + retry + ".";
                    default:
                        return null;
                }
            }
        }

        public void RequestRefresh()
        {
            FetchNow(true);
        }

        public void OpenUsagePage()
        {
            Launch(new ProcessStartInfo(UsagePageUrl) { UseShellExecute = true });
        }

        public void OpenClaudeCode()
        {
            Launch(new ProcessStartInfo("cmd.exe", "/k claude") { UseShellExecute = true });
        }

        static void Launch(ProcessStartInfo info)
        {
            try { Process.Start(info); }
            catch (Exception ex) { Log.Write("Avvio non riuscito di " + info.FileName + ": " + ex.Message); }
        }

        // ---- Fetching ----

        bool CanFetchEarly
        {
            get
            {
                DateTime now = DateTime.Now;
                if (fetching) return false;
                if (status == FetchStatus.RateLimited && now < nextFetchAt) return false;
                return !lastAttempt.HasValue || now - lastAttempt.Value > TimeSpan.FromSeconds(20);
            }
        }

        async void FetchNow(bool manual)
        {
            if (fetching || (manual && !CanFetchEarly)) return;
            fetching = true;
            popup.RefreshContent();

            FetchResult result;
            bool autoRefresh = settings.AutoRefresh;
            try
            {
                result = demo ? Demo.Fetch() : await Task.Run(() => UsageClient.Fetch(autoRefresh));
            }
            catch (Exception ex)
            {
                result = new FetchResult { Status = FetchStatus.BadResponse, Detail = ex.Message };
            }
            fetching = false;
            lastAttempt = DateTime.Now;
            Apply(result);
        }

        void Apply(FetchResult r)
        {
            DateTime now = DateTime.Now;
            if (r.Plan != null) plan = r.Plan;
            status = r.Status;
            statusDetail = r.Detail;
            needsLogin = r.NeedsLogin;

            TimeSpan wait;
            switch (r.Status)
            {
                case FetchStatus.Ok:
                    UsageSnapshot previous = snapshot;
                    snapshot = r.Snapshot;
                    if (!demo) SaveCache(r.RawJson);
                    CheckThresholds(previous, snapshot, !seeded);
                    seeded = true;
                    authAlertShown = false;
                    wait = TimeSpan.FromMinutes(settings.PollMinutes);
                    break;
                case FetchStatus.RateLimited:
                    wait = r.RetryAfter.HasValue && r.RetryAfter.Value > TimeSpan.FromMinutes(10) ? r.RetryAfter.Value : TimeSpan.FromMinutes(10);
                    break;
                case FetchStatus.NoCredentials:
                case FetchStatus.TokenExpired:
                    wait = TimeSpan.FromMinutes(1); // only the local file is read until the login is back
                    break;
                case FetchStatus.NetworkError:
                    wait = TimeSpan.FromMinutes(2);
                    break;
                default:
                    wait = TimeSpan.FromMinutes(Math.Max(5, settings.PollMinutes));
                    break;
            }
            nextFetchAt = now + wait;

            // Fetch again shortly after a window resets, so the new window shows up quickly.
            if (r.Status == FetchStatus.Ok)
                foreach (UsageLimit l in snapshot.Limits)
                    if (l.ResetsAt.HasValue && l.ResetsAt.Value > now && l.ResetsAt.Value.AddSeconds(45) < nextFetchAt)
                        nextFetchAt = l.ResetsAt.Value.AddSeconds(45);

            bool loginProblem = r.Status == FetchStatus.NoCredentials || r.Status == FetchStatus.TokenExpired || r.Status == FetchStatus.Unauthorized;
            if (loginProblem && !authAlertShown && !demo)
            {
                authAlertShown = true;
                string title = r.Status == FetchStatus.TokenExpired ? "Login di Claude Code scaduto" : "Login di Claude Code non disponibile";
                tray.ShowBalloonTip(10000, title, "L'icona resta grigia finché il login non torna valido. Clic qui per i dettagli.", ToolTipIcon.Warning);
            }

            if (r.Status != loggedStatus || r.Status != FetchStatus.Ok)
            {
                Log.Write("Stato: " + r.Status + (string.IsNullOrEmpty(r.Detail) ? "" : " (" + r.Detail + ")"));
                loggedStatus = r.Status;
            }
            if (r.Status == FetchStatus.Ok && !keysLogged)
            {
                keysLogged = true;
                var keys = new List<string>();
                foreach (UsageLimit l in snapshot.Limits) keys.Add(l.Key + "=" + l.Percent);
                Log.Write("Limiti letti: " + string.Join(", ", keys.ToArray()));
            }

            UpdateVisuals(true);
            popup.RefreshContent();
        }

        void OnTick()
        {
            if (!fetching && DateTime.Now >= nextFetchAt) FetchNow(false);
            UpdateVisuals(false);
            popup.RefreshContent();
        }

        // ---- Notifications ----

        void CheckThresholds(UsageSnapshot before, UsageSnapshot after, bool seedOnly)
        {
            DateTime now = DateTime.Now;
            foreach (UsageLimit l in after.Limits)
            {
                string window = l.Key + "|" + (l.ResetsAt.HasValue ? l.ResetsAt.Value.ToString("yyyyMMddHH") : "-");
                for (int i = Thresholds.Length - 1; i >= 0; i--)
                {
                    if (l.Percent < Thresholds[i]) continue;
                    if (notified.Contains(window + "|" + Thresholds[i])) break;
                    for (int j = 0; j <= i; j++) notified.Add(window + "|" + Thresholds[j]);
                    if (seedOnly || !settings.Notifications) break;

                    string when = l.ResetsAt.HasValue ? " tra " + Fmt.Countdown(l.ResetsAt.Value - now) + " (" + Fmt.When(l.ResetsAt.Value, now) + ")" : "";
                    if (Thresholds[i] >= 100)
                        tray.ShowBalloonTip(10000, l.ShortLabel + ": limite raggiunto", "Torna disponibile" + when + ".", ToolTipIcon.Warning);
                    else
                        tray.ShowBalloonTip(10000, l.ShortLabel + ": " + Fmt.Percent(l.Percent) + " usato", "Si azzera" + when + ".", ToolTipIcon.Warning);
                    break;
                }
            }

            if (before == null || seedOnly || !settings.Notifications) return;
            foreach (UsageLimit l in after.Limits)
            {
                UsageLimit old = before.Find(l.Key);
                if (old != null && old.Percent >= 95 && l.Percent < old.Percent - 50)
                    tray.ShowBalloonTip(8000, l.ShortLabel + " di nuovo disponibile", "Il limite si è azzerato.", ToolTipIcon.Info);
            }
        }

        // ---- Icon and tooltip ----

        void UpdateVisuals(bool force)
        {
            DateTime now = DateTime.Now;
            int size = SystemInformation.SmallIconSize.Width;
            bool lightTaskbar = Theme.TaskbarIsLight();
            bool stale = IsStale;
            var bars = new List<BarSpec>();
            if (snapshot != null)
                foreach (UsageLimit l in snapshot.IconLimits())
                    bars.Add(new BarSpec(l.EffectivePercent(now), settings.PaceMarker ? l.ElapsedFraction(now) : null));

            string signature = IconRenderer.Signature(size, bars, lightTaskbar, stale);
            if (force || signature != iconSignature)
            {
                iconSignature = signature;
                using (Bitmap bmp = IconRenderer.Render(size, bars, lightTaskbar, stale))
                {
                    IntPtr handle = bmp.GetHicon();
                    Icon next = Icon.FromHandle(handle);
                    tray.Icon = next; // the shell keeps its own copy, so the previous handle can go
                    if (icon != null) icon.Dispose();
                    if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                    icon = next;
                    iconHandle = handle;
                }
            }
            SetTooltip(BuildTooltip(now));
        }

        string ShortStatus()
        {
            switch (status)
            {
                case FetchStatus.NoCredentials: return "login non trovato";
                case FetchStatus.TokenExpired: return "login scaduto";
                case FetchStatus.Unauthorized: return "login rifiutato";
                case FetchStatus.RateLimited: return "in pausa";
                case FetchStatus.NetworkError: return "offline";
                case FetchStatus.BadResponse: return "errore";
                default: return "dati vecchi";
            }
        }

        string BuildTooltip(DateTime now)
        {
            var lines = new List<string>();
            if (snapshot == null)
                lines.Add("Claude · " + (fetching || !lastAttempt.HasValue ? "lettura in corso…" : ShortStatus()));
            else if (IsStale)
                lines.Add("Claude · " + ShortStatus() + ", dati " + Fmt.When(snapshot.FetchedAt, now));
            else
                lines.Add("Claude · aggiornato " + Fmt.When(snapshot.FetchedAt, now));

            if (snapshot != null)
                foreach (UsageLimit l in snapshot.IconLimits())
                {
                    string line = l.ShortLabel + " " + Fmt.Percent(l.EffectivePercent(now));
                    if (l.ResetsAt.HasValue && !l.HasReset(now)) line += " · reset " + Fmt.When(l.ResetsAt.Value, now);
                    lines.Add(line);
                }
            string text = string.Join("\n", lines.ToArray());
            return text.Length <= 127 ? text : text.Substring(0, 126) + "…";
        }

        // NotifyIcon.Text stops at 63 characters on .NET Framework, while the shell takes 127:
        // set the private field and push it with UpdateIcon, falling back to the short version.
        void SetTooltip(string text)
        {
            if (text == tooltip) return;
            tooltip = text;
            try
            {
                const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
                Type t = typeof(NotifyIcon);
                t.GetField("text", hidden).SetValue(tray, text);
                if ((bool)t.GetField("added", hidden).GetValue(tray))
                    t.GetMethod("UpdateIcon", hidden).Invoke(tray, new object[] { true });
            }
            catch (Exception ex)
            {
                Log.Write("Tooltip lungo non disponibile, uso quello breve: " + ex.Message);
                tray.Text = text.Length <= 63 ? text : text.Substring(0, 62) + "…";
            }
        }

        // ---- Popup and menu ----

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (popup.Visible) { popup.Hide(); return; }
            // Clicking the icon while the popup is open first deactivates (and hides) it: don't reopen it.
            if ((DateTime.Now - popup.HiddenAt).TotalMilliseconds < 400) return;
            ShowPopup();
        }

        void ShowPopup()
        {
            popup.ShowNear(Cursor.Position);
            if (snapshot == null || DateTime.Now - snapshot.FetchedAt > TimeSpan.FromMinutes(2))
                if (CanFetchEarly) FetchNow(false);
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var show = new ToolStripMenuItem("Mostra dettagli", null, delegate { ShowPopup(); });
            show.Font = new Font(m.Font, FontStyle.Bold);
            m.Items.Add(show);
            m.Items.Add(new ToolStripMenuItem("Aggiorna ora", null, delegate { FetchNow(true); }));
            m.Items.Add(new ToolStripSeparator());

            var poll = new ToolStripMenuItem("Frequenza di aggiornamento");
            foreach (int minutes in Settings.PollChoices)
            {
                int chosen = minutes;
                var item = new ToolStripMenuItem("Ogni " + minutes + " minuti", null, delegate { SetPoll(chosen); }) { Tag = minutes };
                poll.DropDownItems.Add(item);
                pollItems.Add(item);
            }
            m.Items.Add(poll);

            paceItem = new ToolStripMenuItem("Tacca del tempo trascorso", null, delegate
            {
                settings.PaceMarker = !settings.PaceMarker;
                settings.Save();
                UpdateVisuals(true);
                popup.RefreshContent();
            });
            notifyItem = new ToolStripMenuItem("Avvisi a 80%, 95% e 100%", null, delegate
            {
                settings.Notifications = !settings.Notifications;
                settings.Save();
            });
            refreshItem = new ToolStripMenuItem("Rinnova il login da solo", null, delegate
            {
                settings.AutoRefresh = !settings.AutoRefresh;
                settings.Save();
                if (settings.AutoRefresh && status == FetchStatus.TokenExpired) FetchNow(true);
                popup.RefreshContent();
            });
            autostartItem = new ToolStripMenuItem("Avvia con Windows", null, delegate
            {
                try { Settings.Autostart = !Settings.Autostart; }
                catch (Exception ex) { Log.Write("Avvio automatico non modificato: " + ex.Message); }
            });
            m.Items.Add(paceItem);
            m.Items.Add(notifyItem);
            m.Items.Add(refreshItem);
            m.Items.Add(autostartItem);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Apri Utilizzo su claude.ai", null, delegate { OpenUsagePage(); }));
            m.Items.Add(new ToolStripMenuItem("Esci", null, delegate { ExitThread(); }));
            m.Opening += delegate { SyncMenu(); };
            return m;
        }

        void SyncMenu()
        {
            foreach (ToolStripMenuItem item in pollItems) item.Checked = (int)item.Tag == settings.PollMinutes;
            paceItem.Checked = settings.PaceMarker;
            notifyItem.Checked = settings.Notifications;
            refreshItem.Checked = settings.AutoRefresh;
            autostartItem.Checked = Settings.Autostart;
        }

        void SetPoll(int minutes)
        {
            settings.PollMinutes = minutes;
            settings.Save();
            if (status == FetchStatus.Ok && lastAttempt.HasValue)
                nextFetchAt = lastAttempt.Value.AddMinutes(minutes);
        }

        // ---- Cache of the last good answer, so the icon has something to show at startup ----

        static string CachePath
        {
            get { return Path.Combine(Log.Folder, "last-usage.json"); }
        }

        void SaveCache(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                Directory.CreateDirectory(Log.Folder);
                File.WriteAllText(CachePath, snapshot.FetchedAt.ToString("o") + "\n" + (plan ?? "") + "\n" + raw);
            }
            catch (Exception ex)
            {
                Log.Write("Cache non salvata: " + ex.Message);
            }
        }

        void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                string[] parts = File.ReadAllText(CachePath).Split(new[] { '\n' }, 3);
                if (parts.Length < 3) return;
                DateTime fetchedAt = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind);
                snapshot = UsageParser.Parse(parts[2], fetchedAt);
                if (parts[1].Length > 0) plan = parts[1];
            }
            catch (Exception ex)
            {
                Log.Write("Cache non letta: " + ex.Message);
            }
        }

        // ---- System events ----

        void RunOnUi(MethodInvoker action)
        {
            if (popup.IsDisposed) return;
            if (popup.InvokeRequired) popup.BeginInvoke(action);
            else action();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle || e.Category == UserPreferenceCategory.Color)
                RunOnUi(delegate { UpdateVisuals(true); });
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                RunOnUi(delegate { nextFetchAt = DateTime.Now.AddSeconds(20); });
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            RunOnUi(delegate { UpdateVisuals(true); });
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            popup.Dispose();
            if (icon != null) icon.Dispose();
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }
    }
}
