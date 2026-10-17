using System;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;

namespace TurboClaudeMonitor
{
    internal static class Theme
    {
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        // Taskbar colour mode, which decides how the tray icon must contrast.
        public static bool TaskbarIsLight()
        {
            return ReadFlag("SystemUsesLightTheme", false);
        }

        public static bool AppsAreLight()
        {
            return ReadFlag("AppsUseLightTheme", true);
        }

        static bool ReadFlag(string name, bool fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    object v = key == null ? null : key.GetValue(name);
                    return v == null ? fallback : Convert.ToInt32(v) != 0;
                }
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // 0 green under 50%, 1 amber, 2 orange from 80%, 3 red from 95%.
        public static int LevelIndex(double pct)
        {
            return pct >= 95 ? 3 : pct >= 80 ? 2 : pct >= 50 ? 1 : 0;
        }

        static readonly int[] OnLight = { 0x1A7F37, 0xB07D00, 0xD15704, 0xCF222E };
        static readonly int[] OnDark = { 0x3FB950, 0xE3B341, 0xF0883E, 0xF85149 };

        public static Color Level(double pct, bool lightBackground)
        {
            return Rgb((lightBackground ? OnLight : OnDark)[LevelIndex(pct)]);
        }

        public static Color Stale(bool lightBackground)
        {
            return lightBackground ? Rgb(0x8A8A8A) : Rgb(0x9A9A9A);
        }

        public static Color Rgb(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }
    }

    internal sealed class Palette
    {
        public bool IsLight;
        public Color Back, Border, Text, Muted, Track, Link, Separator, Marker, Warn, Badge;

        public static Palette Get(bool light)
        {
            if (light)
                return new Palette
                {
                    IsLight = true,
                    Back = Theme.Rgb(0xF9F9F9),
                    Border = Theme.Rgb(0xDADADA),
                    Text = Theme.Rgb(0x1B1B1B),
                    Muted = Theme.Rgb(0x5F5F5F),
                    Track = Theme.Rgb(0xE3E3E3),
                    Link = Theme.Rgb(0x005FB8),
                    Separator = Theme.Rgb(0xE5E5E5),
                    Marker = Color.FromArgb(210, 0x1B, 0x1B, 0x1B),
                    Warn = Theme.Rgb(0xB3261E),
                    Badge = Theme.Rgb(0xECECEC),
                };
            return new Palette
            {
                IsLight = false,
                Back = Theme.Rgb(0x2B2B2B),
                Border = Theme.Rgb(0x454545),
                Text = Theme.Rgb(0xFFFFFF),
                Muted = Theme.Rgb(0xB4B4B4),
                Track = Theme.Rgb(0x474747),
                Link = Theme.Rgb(0x60CDFF),
                Separator = Theme.Rgb(0x3B3B3B),
                Marker = Color.FromArgb(230, 255, 255, 255),
                Warn = Theme.Rgb(0xFF9A8F),
                Badge = Theme.Rgb(0x3A3A3A),
            };
        }
    }

    // Italian wording for percentages, times and countdowns.
    internal static class Fmt
    {
        static readonly CultureInfo It = new CultureInfo("it-IT");

        // Rounded down, like Claude's own /usage.
        public static string Percent(double pct)
        {
            return Math.Floor(pct + 1e-9).ToString("0", It) + "%";
        }

        public static string When(DateTime t, DateTime now)
        {
            if (t.Date == now.Date) return t.ToString("HH:mm", It);
            if (t.Date == now.Date.AddDays(1)) return "domani " + t.ToString("HH:mm", It);
            if (t.Date == now.Date.AddDays(-1)) return "ieri " + t.ToString("HH:mm", It);
            if (Math.Abs((t.Date - now.Date).TotalDays) < 7) return t.ToString("ddd HH:mm", It);
            return t.ToString("d MMM HH:mm", It);
        }

        // "alle 16:10" today, "domani 09:00" or "ven 09:00" otherwise.
        public static string AtWhen(DateTime t, DateTime now)
        {
            return t.Date == now.Date ? "alle " + When(t, now) : When(t, now);
        }

        public static string Countdown(TimeSpan d)
        {
            if (d.TotalSeconds <= 0) return "ora";
            if (d.TotalMinutes < 1) return "meno di 1 min";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " min";
            if (d.TotalDays < 1) return (int)d.TotalHours + " h " + d.Minutes + " min";
            return (int)d.TotalDays + " g " + d.Hours + " h";
        }
    }
}
