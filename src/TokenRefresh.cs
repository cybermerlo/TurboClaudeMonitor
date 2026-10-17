using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TurboClaudeMonitor
{
    // Renews Claude Code's login once it has expired, with the same OAuth refresh Claude Code performs, and writes the
    // new tokens back into its file so Claude Code goes on using them. Switched on from the menu ("Rinnova il login da solo").
    internal static class TokenRefresh
    {
        // Claude Code's public OAuth client. Both can be overridden by environment variable if Anthropic changes them.
        const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        static readonly string[] DefaultUrls = { "https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token" };
        static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        enum Outcome { Success, Rejected, WrongAddress, Failed }

        // Kept as hashes: a refresh token the server refused is never sent again, a failing one waits RetryDelay.
        static string rejectedFingerprint;
        static string failedFingerprint;
        static DateTime failedAtUtc = DateTime.MinValue;

        static string ClientId
        {
            get { return Environment.GetEnvironmentVariable("TURBOCLAUDE_CLIENT_ID") ?? DefaultClientId; }
        }

        static string[] Urls
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("TURBOCLAUDE_TOKEN_URL");
                return string.IsNullOrEmpty(custom) ? DefaultUrls : new[] { custom };
            }
        }

        public static bool IsRejected(string refreshToken)
        {
            return rejectedFingerprint != null && Fingerprint(refreshToken) == rejectedFingerprint;
        }

        // badAccessToken is the token known not to work (expired or refused): a different valid one in the file wins.
        public static bool TryRefresh(string refreshToken, string badAccessToken, out string accessToken, out string problem)
        {
            accessToken = null;
            problem = null;
            string fingerprint = Fingerprint(refreshToken);
            if (fingerprint == rejectedFingerprint)
            {
                problem = "rinnovo rifiutato dal server, serve un nuovo /login";
                return false;
            }
            if (fingerprint == failedFingerprint && DateTime.UtcNow - failedAtUtc < RetryDelay)
            {
                problem = "rinnovo non riuscito, nuovo tentativo tra qualche minuto";
                return false;
            }

            // Serialises renewals between copies of this app (the tray and a --check run).
            using (var mutex = new Mutex(false, @"Local\TurboClaudeMonitor.Refresh"))
            {
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    problem = "rinnovo già in corso";
                    return false;
                }
                try
                {
                    return RefreshLocked(refreshToken, badAccessToken, fingerprint, out accessToken, out problem);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        static bool RefreshLocked(string refreshToken, string badAccessToken, string fingerprint, out string accessToken, out string problem)
        {
            accessToken = null;
            Dictionary<string, object> root, oauth;
            if (!CredentialsFile.Read(out root, out oauth, out problem)) return false;

            // Claude Code (or another copy of this app) may have renewed the login a moment ago.
            Credentials current = Credentials.FromOauth(oauth);
            if (current.AccessToken != badAccessToken && !current.IsExpired)
            {
                accessToken = current.AccessToken;
                return true;
            }
            if (!string.IsNullOrEmpty(current.RefreshToken) && current.RefreshToken != refreshToken)
            {
                refreshToken = current.RefreshToken;
                fingerprint = Fingerprint(refreshToken);
            }

            Dictionary<string, object> reply = null;
            string detail = null;
            Outcome outcome = Outcome.WrongAddress;
            foreach (string url in Urls)
            {
                outcome = Post(url, refreshToken, out reply, out detail);
                if (outcome != Outcome.WrongAddress) break;
            }

            string newAccess = reply == null ? null : UsageParser.Get(reply, "access_token") as string;
            if (outcome == Outcome.Success && string.IsNullOrEmpty(newAccess))
            {
                outcome = Outcome.Failed;
                detail = "risposta senza token";
            }
            if (outcome != Outcome.Success)
            {
                if (outcome == Outcome.Rejected) rejectedFingerprint = fingerprint;
                else
                {
                    failedFingerprint = fingerprint;
                    failedAtUtc = DateTime.UtcNow;
                }
                problem = (outcome == Outcome.Rejected ? "rinnovo rifiutato dal server" : "rinnovo non riuscito") + (detail == null ? "" : ": " + detail);
                Log.Write("Rinnovo del login: " + problem);
                return false;
            }

            accessToken = newAccess;
            string newRefresh = UsageParser.Get(reply, "refresh_token") as string;
            double expiresIn = UsageParser.Number(UsageParser.Get(reply, "expires_in")) ?? 3600;
            DateTime expiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
            string scope = UsageParser.Get(reply, "scope") as string;

            try
            {
                // Re-read right before writing: if Claude Code renewed in the meantime, its version stays.
                if (!CredentialsFile.Read(out root, out oauth, out problem))
                {
                    Log.Write("Login rinnovato ma non salvato: " + problem);
                    problem = null;
                    return true;
                }
                if (UsageParser.Get(oauth, "refreshToken") as string != refreshToken)
                {
                    Log.Write("Login rinnovato anche da Claude Code nel frattempo: tengo il suo file.");
                    return true;
                }
                oauth["accessToken"] = newAccess;
                if (!string.IsNullOrEmpty(newRefresh)) oauth["refreshToken"] = newRefresh;
                oauth["expiresAt"] = (long)(expiresAt - Credentials.UnixEpoch).TotalMilliseconds;
                if (!string.IsNullOrEmpty(scope)) oauth["scopes"] = scope.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                CredentialsFile.Write(root);
                Log.Write("Login rinnovato automaticamente, valido fino alle " + expiresAt.ToLocalTime().ToString("dd/MM HH:mm") + ".");
            }
            catch (Exception ex)
            {
                // The new token still works for this run; Claude Code may ask for /login if the old refresh token was retired.
                Log.Write("Login rinnovato ma non salvato nel file di Claude Code: " + ex.Message);
            }
            return true;
        }

        static Outcome Post(string url, string refreshToken, out Dictionary<string, object> reply, out string detail)
        {
            reply = null;
            detail = null;
            var body = new Dictionary<string, object>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "client_id", ClientId },
            };
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.UserAgent = UsageClient.UserAgent;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.AllowAutoRedirect = false; // a redirected POST is not followed blindly: the next address is tried instead
            request.ContentLength = bytes.Length;
            try
            {
                using (Stream s = request.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code >= 300)
                    {
                        detail = "HTTP " + code;
                        return Outcome.WrongAddress;
                    }
                    reply = ReadJson(response);
                    if (reply != null) return Outcome.Success;
                    detail = "risposta non valida";
                    return Outcome.Failed;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    detail = ex.Message;
                    return ex.Status == WebExceptionStatus.NameResolutionFailure ? Outcome.WrongAddress : Outcome.Failed;
                }
                using (response)
                {
                    int code = (int)response.StatusCode;
                    string error = ErrorCode(ReadJson(response));
                    detail = "HTTP " + code + (error == null ? "" : ", " + error);
                    if (code == 404 || code == 405) return Outcome.WrongAddress;
                    if (error == "invalid_grant") return Outcome.Rejected;
                    return Outcome.Failed;
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return Outcome.Failed;
            }
        }

        static Dictionary<string, object> ReadJson(HttpWebResponse response)
        {
            try
            {
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // OAuth puts a code in "error"; Anthropic's API style nests {type, message} under it.
        static string ErrorCode(Dictionary<string, object> json)
        {
            object error = UsageParser.Get(json, "error");
            var nested = error as Dictionary<string, object>;
            if (nested != null) return UsageParser.Get(nested, "type") as string;
            return error as string;
        }

        static string Fingerprint(string token)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? "")));
        }
    }
}
