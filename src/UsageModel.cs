using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace TurboClaudeMonitor
{
    // One usage window as reported by Claude: the 5-hour session, the week, or a per-model weekly bucket.
    internal sealed class UsageLimit
    {
        public string Key;
        public string Label;
        public string ShortLabel;
        public double Percent;
        public DateTime? ResetsAt;
        public TimeSpan Window;

        public bool HasReset(DateTime now)
        {
            return ResetsAt.HasValue && ResetsAt.Value <= now;
        }

        // Once the reset time has passed the window starts again from zero, even before the next fetch confirms it.
        public double EffectivePercent(DateTime now)
        {
            return HasReset(now) ? 0 : Percent;
        }

        // Share of the window already elapsed (0..1), or null when no window is running.
        public double? ElapsedFraction(DateTime now)
        {
            if (!ResetsAt.HasValue || HasReset(now) || Window.TotalSeconds <= 0) return null;
            double f = 1.0 - (ResetsAt.Value - now).TotalSeconds / Window.TotalSeconds;
            return Math.Max(0.0, Math.Min(1.0, f));
        }

        // Usage expected at the reset if the pace so far continues; null while it is too early to tell.
        public double? ProjectedAtReset(DateTime now)
        {
            double? f = ElapsedFraction(now);
            if (!f.HasValue || f.Value < 0.08 || Percent <= 0) return null;
            return Percent / f.Value;
        }

        // When the limit runs out at the current pace, if that happens before the reset.
        public DateTime? ProjectedExhaustion(DateTime now)
        {
            double? projected = ProjectedAtReset(now);
            if (!projected.HasValue || projected.Value < 100 || Percent >= 100) return null;
            DateTime start = ResetsAt.Value - Window;
            return start + TimeSpan.FromTicks((long)((now - start).Ticks * (100.0 / Percent)));
        }
    }

    internal sealed class ExtraUsage
    {
        public bool Enabled;
        public double? Percent;
    }

    internal sealed class UsageSnapshot
    {
        public readonly List<UsageLimit> Limits = new List<UsageLimit>();
        public ExtraUsage Extra;
        public DateTime FetchedAt;

        public UsageLimit Find(string key)
        {
            foreach (UsageLimit l in Limits)
                if (l.Key == key) return l;
            return null;
        }

        // The bars of the tray icon, top to bottom: session, week, Fable.
        public List<UsageLimit> IconLimits()
        {
            var list = new List<UsageLimit>();
            foreach (string key in new[] { "five_hour", "seven_day", "seven_day_overage_included" })
            {
                UsageLimit l = Find(key);
                if (l != null) list.Add(l);
            }
            if (list.Count < 3)
                foreach (UsageLimit l in Limits)
                    if (l.Key.StartsWith("model:") && l.ShortLabel.IndexOf("Fable", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(l);
                        break;
                    }
            if (list.Count == 0)
                for (int i = 0; i < Limits.Count && i < 3; i++) list.Add(Limits[i]);
            return list;
        }
    }

    // Reads the JSON of GET /api/oauth/usage, the same endpoint Claude Code's /usage reads.
    internal static class UsageParser
    {
        static readonly string[][] Known =
        {
            new[] { "five_hour", "Sessione · 5 ore", "Sessione", "5h" },
            new[] { "seven_day", "Settimana · tutti i modelli", "Settimana", "7d" },
            new[] { "seven_day_overage_included", "Fable · settimana", "Fable", "7d" },
            new[] { "seven_day_opus", "Opus · settimana", "Opus", "7d" },
            new[] { "seven_day_sonnet", "Sonnet · settimana", "Sonnet", "7d" },
            new[] { "seven_day_cowork", "Cowork · settimana", "Cowork", "7d" },
        };

        public static UsageSnapshot Parse(string json, DateTime fetchedAt)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("il JSON non è un oggetto");

            var snap = new UsageSnapshot { FetchedAt = fetchedAt };
            foreach (string[] k in Known)
            {
                var window = Get(root, k[0]) as Dictionary<string, object>;
                if (window == null) continue;
                double? pct = Number(Get(window, "utilization"));
                if (!pct.HasValue) continue;
                snap.Limits.Add(new UsageLimit
                {
                    Key = k[0],
                    Label = k[1],
                    ShortLabel = k[2],
                    Percent = pct.Value,
                    ResetsAt = Time(Get(window, "resets_at")),
                    Window = k[3] == "5h" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7),
                });
            }

            // Newer accounts also list per-model weekly limits by display name.
            AddModelScoped(snap, Get(root, "limits"));
            AddModelScoped(snap, Get(root, "model_scoped"));

            var extra = Get(root, "extra_usage") as Dictionary<string, object>;
            if (extra != null)
            {
                object enabled = Get(extra, "is_enabled");
                snap.Extra = new ExtraUsage
                {
                    Enabled = enabled is bool && (bool)enabled,
                    Percent = Number(Get(extra, "utilization")),
                };
            }
            return snap;
        }

        static void AddModelScoped(UsageSnapshot snap, object node)
        {
            var items = node as IEnumerable;
            if (items == null || node is string) return;
            foreach (object item in items)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null) continue;
                string kind = Get(entry, "kind") as string;
                if (kind != null && kind != "weekly_scoped") continue;

                string name = Get(entry, "display_name") as string;
                var scope = Get(entry, "scope") as Dictionary<string, object>;
                var model = scope == null ? null : Get(scope, "model") as Dictionary<string, object>;
                if (model != null) name = Get(model, "display_name") as string ?? name;
                if (string.IsNullOrEmpty(name)) continue;

                double? pct = Number(Get(entry, "percent")) ?? Number(Get(entry, "utilization"));
                if (!pct.HasValue) continue;

                bool duplicate = false;
                foreach (UsageLimit l in snap.Limits)
                    if (name.IndexOf(l.ShortLabel, StringComparison.OrdinalIgnoreCase) >= 0) duplicate = true;
                if (duplicate) continue;

                snap.Limits.Add(new UsageLimit
                {
                    Key = "model:" + name.ToLowerInvariant(),
                    Label = name + " · settimana",
                    ShortLabel = name,
                    Percent = pct.Value,
                    ResetsAt = Time(Get(entry, "resets_at")),
                    Window = TimeSpan.FromDays(7),
                });
            }
        }

        internal static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        internal static double? Number(object v)
        {
            if (v == null || v is bool) return null;
            string s = v as string;
            if (s != null)
            {
                double parsed;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
            }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        // resets_at comes as an ISO 8601 string; epoch seconds or milliseconds are accepted too.
        static DateTime? Time(object v)
        {
            string s = v as string;
            if (s != null)
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                    return dto.LocalDateTime;
                return null;
            }
            double? n = Number(v);
            if (!n.HasValue || n.Value <= 0) return null;
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (n.Value > 1e12 ? epoch.AddMilliseconds(n.Value) : epoch.AddSeconds(n.Value)).ToLocalTime();
        }
    }
}
