using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TurboClaudeMonitor
{
    // The login Claude Code keeps in ~/.claude/.credentials.json, under "claudeAiOauth".
    internal sealed class Credentials
    {
        public string AccessToken;
        public string RefreshToken;
        public DateTime? ExpiresAtUtc;
        public string Plan;

        // A minute of margin, so a token is never sent in its very last seconds.
        public bool IsExpired
        {
            get { return ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= DateTime.UtcNow.AddSeconds(60); }
        }

        public static bool TryLoad(out Credentials creds, out string problem)
        {
            creds = null;
            Dictionary<string, object> root, oauth;
            if (!CredentialsFile.Read(out root, out oauth, out problem)) return false;
            creds = FromOauth(oauth);
            return true;
        }

        public static Credentials FromOauth(Dictionary<string, object> oauth)
        {
            var c = new Credentials
            {
                AccessToken = UsageParser.Get(oauth, "accessToken") as string,
                RefreshToken = UsageParser.Get(oauth, "refreshToken") as string,
                Plan = PlanLabel(UsageParser.Get(oauth, "subscriptionType") as string, UsageParser.Get(oauth, "rateLimitTier") as string),
            };
            double? expires = UsageParser.Number(UsageParser.Get(oauth, "expiresAt"));
            if (expires.HasValue && expires.Value > 0) c.ExpiresAtUtc = UnixEpoch.AddMilliseconds(expires.Value);
            return c;
        }

        public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static string PlanLabel(string subscription, string tier)
        {
            string t = (tier ?? "").ToLowerInvariant();
            if (t.Contains("max_20x")) return "Max 20x";
            if (t.Contains("max_5x")) return "Max 5x";
            string s = (subscription ?? "").Trim();
            if (s.Length == 0) return null;
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        }
    }

    internal static class CredentialsFile
    {
        public static string Location
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Path.Combine(dir, ".credentials.json");
            }
        }

        // Returns the whole document too, so a rewrite keeps every field it does not touch.
        public static bool Read(out Dictionary<string, object> root, out Dictionary<string, object> oauth, out string problem)
        {
            root = null;
            oauth = null;
            problem = null;
            string path = Location;
            if (!File.Exists(path))
            {
                problem = "file non trovato";
                return false;
            }

            string text = null;
            for (int attempt = 0; text == null; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        text = reader.ReadToEnd();
                }
                catch (IOException)
                {
                    // Claude Code may be rewriting the file right now.
                    if (attempt >= 3) { problem = "file non leggibile"; return false; }
                    Thread.Sleep(200);
                }
            }

            try
            {
                root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                oauth = UsageParser.Get(root, "claudeAiOauth") as Dictionary<string, object>;
            }
            catch (Exception)
            {
                problem = "file non valido";
                return false;
            }
            if (oauth == null || string.IsNullOrEmpty(UsageParser.Get(oauth, "accessToken") as string))
            {
                problem = "login di claude.ai assente";
                return false;
            }
            return true;
        }

        // Written to a temporary file and swapped in, so Claude Code never reads half a file.
        // UTF-8 without BOM: Node's JSON.parse rejects a BOM.
        public static void Write(Dictionary<string, object> root)
        {
            string path = Location;
            string temp = path + ".turbo-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
                try
                {
                    File.Replace(temp, path, null);
                }
                catch (IOException)
                {
                    File.Copy(temp, path, true);
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
