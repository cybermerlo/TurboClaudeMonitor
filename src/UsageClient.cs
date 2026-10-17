using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace TurboClaudeMonitor
{
    internal enum FetchStatus { Ok, NoCredentials, TokenExpired, Unauthorized, RateLimited, NetworkError, BadResponse }

    internal sealed class FetchResult
    {
        public FetchStatus Status;
        public UsageSnapshot Snapshot;
        public string RawJson;
        public string Plan;
        public string Detail;
        public TimeSpan? RetryAfter;
        public bool Refreshed;
        public bool NeedsLogin; // the server refused the renewal: only a new /login helps
    }

    // Asks the usage endpoint, read-only, with the login Claude Code keeps in ~/.claude/.credentials.json.
    // With autoRefresh the login is renewed when expired or refused (see TokenRefresh); otherwise the file is only read.
    internal static class UsageClient
    {
        public const string UserAgent = "TurboClaudeMonitor/1.1";
        const string DefaultEndpoint = "https://api.anthropic.com/api/oauth/usage";
        const string BetaHeader = "oauth-2025-04-20";

        public static string CredentialsPath
        {
            get { return CredentialsFile.Location; }
        }

        static string Endpoint
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("TURBOCLAUDE_USAGE_URL");
                return string.IsNullOrEmpty(custom) ? DefaultEndpoint : custom;
            }
        }

        public static FetchResult Fetch(bool autoRefresh)
        {
            Credentials creds;
            string problem;
            if (!Credentials.TryLoad(out creds, out problem))
                return new FetchResult { Status = FetchStatus.NoCredentials, Detail = problem };

            bool canRefresh = autoRefresh && !string.IsNullOrEmpty(creds.RefreshToken);
            bool refreshed = false;
            string token = creds.AccessToken;
            if (creds.IsExpired)
            {
                if (!canRefresh)
                    return new FetchResult { Status = FetchStatus.TokenExpired, Plan = creds.Plan };
                if (!TokenRefresh.TryRefresh(creds.RefreshToken, creds.AccessToken, out token, out problem))
                    return new FetchResult
                    {
                        Status = FetchStatus.TokenExpired,
                        Plan = creds.Plan,
                        Detail = problem,
                        NeedsLogin = TokenRefresh.IsRejected(creds.RefreshToken),
                    };
                refreshed = true;
            }

            FetchResult result = CallUsage(token);
            if (result.Status == FetchStatus.Unauthorized && canRefresh && !refreshed)
            {
                string renewed;
                if (TokenRefresh.TryRefresh(creds.RefreshToken, token, out renewed, out problem))
                {
                    result = CallUsage(renewed);
                    refreshed = true;
                }
                else
                {
                    result.Detail += "; " + problem;
                }
            }
            result.Plan = creds.Plan;
            result.Refreshed = refreshed;
            return result;
        }

        static FetchResult CallUsage(string token)
        {
            var result = new FetchResult();
            var request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = UserAgent;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Headers["Authorization"] = "Bearer " + token;
            request.Headers["anthropic-beta"] = BetaHeader;

            try
            {
                string body;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    body = reader.ReadToEnd();
                try
                {
                    result.Snapshot = UsageParser.Parse(body, DateTime.Now);
                    result.RawJson = body;
                    result.Status = FetchStatus.Ok;
                }
                catch (Exception ex)
                {
                    result.Status = FetchStatus.BadResponse;
                    result.Detail = ex.Message;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    result.Status = FetchStatus.NetworkError;
                    result.Detail = ex.Status == WebExceptionStatus.Timeout ? "tempo scaduto" : ex.Message;
                    return result;
                }
                using (response)
                {
                    int code = (int)response.StatusCode;
                    result.Detail = "HTTP " + code;
                    if (code == 401 || code == 403) result.Status = FetchStatus.Unauthorized;
                    else if (code == 429)
                    {
                        result.Status = FetchStatus.RateLimited;
                        result.RetryAfter = ParseRetryAfter(response.Headers["Retry-After"]);
                    }
                    else if (code >= 500) result.Status = FetchStatus.NetworkError;
                    else result.Status = FetchStatus.BadResponse;
                }
            }
            catch (Exception ex)
            {
                result.Status = FetchStatus.NetworkError;
                result.Detail = ex.Message;
            }
            return result;
        }

        static TimeSpan? ParseRetryAfter(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            int seconds;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                return TimeSpan.FromSeconds(seconds);
            DateTime when;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                return when - DateTime.UtcNow;
            return null;
        }
    }
}
