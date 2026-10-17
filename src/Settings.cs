using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TurboClaudeMonitor
{
    // Preferences live in HKCU\Software\TurboClaudeMonitor; autostart is the usual HKCU Run entry.
    internal sealed class Settings
    {
        const string KeyPath = @"Software\TurboClaudeMonitor";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "TurboClaudeMonitor";

        public static readonly int[] PollChoices = { 3, 5, 10, 15 };

        public int PollMinutes = 5;
        public bool PaceMarker = true;
        public bool Notifications = true;
        public bool AutoRefresh = true;
        public bool FirstRunDone;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return s;
                    int poll = Convert.ToInt32(key.GetValue("PollMinutes", s.PollMinutes));
                    if (Array.IndexOf(PollChoices, poll) >= 0) s.PollMinutes = poll;
                    s.PaceMarker = Convert.ToInt32(key.GetValue("PaceMarker", 1)) != 0;
                    s.Notifications = Convert.ToInt32(key.GetValue("Notifications", 1)) != 0;
                    s.AutoRefresh = Convert.ToInt32(key.GetValue("AutoRefresh", 1)) != 0;
                    s.FirstRunDone = Convert.ToInt32(key.GetValue("FirstRunDone", 0)) != 0;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Impostazioni non lette: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key.SetValue("PollMinutes", PollMinutes, RegistryValueKind.DWord);
                    key.SetValue("PaceMarker", PaceMarker ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("Notifications", Notifications ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("AutoRefresh", AutoRefresh ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("FirstRunDone", FirstRunDone ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Impostazioni non salvate: " + ex.Message);
            }
        }

        public static bool Autostart
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                        return key != null && !string.IsNullOrEmpty(key.GetValue(RunValueName) as string);
                }
                catch (Exception)
                {
                    return false;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (value) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(RunValueName, false);
                }
            }
        }
    }

    // A small log next to the cache, without tokens, to tell why the icon went grey.
    internal static class Log
    {
        static readonly object Gate = new object();

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboClaudeMonitor"); }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    string path = Path.Combine(Folder, "monitor.log");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 256 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never take the monitor down.
            }
        }
    }
}
