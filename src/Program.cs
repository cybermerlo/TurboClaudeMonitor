using System;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("TurboClaudeMonitor")]
[assembly: AssemblyProduct("TurboClaudeMonitor")]
[assembly: AssemblyDescription("Limiti di utilizzo di Claude nella barra di Windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TurboClaudeMonitor
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); } // TLS 1.2 + 1.3
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool demo = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--demo") demo = true;
                else if (a == "--render" && i + 1 < args.Length)
                {
                    Preview.RenderAll(args[i + 1]);
                    return 0;
                }
                else if (a == "--export-ico" && i + 1 < args.Length)
                {
                    IconRenderer.WriteAppIcon(args[i + 1]);
                    return 0;
                }
                else if (a == "--check" && i + 1 < args.Length)
                {
                    return Check(args[i + 1]);
                }
            }

            bool firstInstance;
            using (var mutex = new Mutex(true, demo ? @"Local\TurboClaudeMonitor.Demo" : @"Local\TurboClaudeMonitor", out firstInstance))
            {
                if (!firstInstance) return 0;
                Application.Run(new TrayApp(demo));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        // --check <file>: one read of the endpoint, summarised in a text file (never the token), for diagnosis.
        static int Check(string path)
        {
            bool autoRefresh = Settings.Load().AutoRefresh;
            FetchResult r = UsageClient.Fetch(autoRefresh);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Credenziali: " + UsageClient.CredentialsPath);
            sb.AppendLine("Stato: " + r.Status + (string.IsNullOrEmpty(r.Detail) ? "" : " (" + r.Detail + ")"));
            sb.AppendLine("Rinnovo automatico: " + (autoRefresh ? "attivo" : "spento") + (r.Refreshed ? ", login rinnovato ora" : ""));
            if (r.Plan != null) sb.AppendLine("Piano: " + r.Plan);
            if (r.Snapshot != null)
                foreach (UsageLimit l in r.Snapshot.Limits)
                    sb.AppendLine(l.Key + ": " + l.Percent + "% reset " + (l.ResetsAt.HasValue ? l.ResetsAt.Value.ToString("yyyy-MM-dd HH:mm") : "-"));
            System.IO.File.WriteAllText(path, sb.ToString());
            return r.Status == FetchStatus.Ok ? 0 : 1;
        }
    }
}
