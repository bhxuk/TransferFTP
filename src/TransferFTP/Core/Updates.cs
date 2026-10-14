using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace TransferFTP
{
    /// <summary>Checks GitHub for a newer release and installs it with one click.</summary>
    public static class Updates
    {
        public const string Repo = "bhxuk/TransferFTP";
        private const string AssetName = "TransferFTP-Setup.exe";

        public static Version Current
        {
            get
            {
                var v = typeof(Updates).Assembly.GetName().Version ?? new Version(1, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string CurrentText => Current.ToString(3);
        public static string LatestVersion { get; private set; }
        public static string LatestNotesUrl { get; private set; } = "https://github.com/" + Repo + "/releases/latest";
        public static bool Available { get; private set; }
        public static bool Installing { get; private set; }

        /// <summary>Raised on the UI thread when a check finds (or stops finding) an update.</summary>
        public static event Action Changed;

        private static string _setupUrl;
        private static long _setupSize;

        private static HttpClient Client(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TransferFTP/" + CurrentText);
            return http;
        }

        /// <summary>Returns true when a newer version is ready to install.</summary>
        public static async Task<bool> Check()
        {
            try
            {
                using var http = Client(TimeSpan.FromSeconds(15));
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest"));
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                if (root.TryGetProperty("html_url", out var html)) LatestNotesUrl = html.GetString() ?? LatestNotesUrl;
                _setupUrl = null;
                _setupSize = 0;
                if (root.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                        if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            _setupUrl = a.GetProperty("browser_download_url").GetString();
                            if (a.TryGetProperty("size", out var size)) _setupSize = size.GetInt64();
                        }
                LatestVersion = tag.TrimStart('v', 'V');
                Available = _setupUrl != null && Version.TryParse(LatestVersion, out var latest) && latest > Current;
                Settings.Current.LastUpdateCheck = DateTime.Now;
                Log.Write("Update check: running " + CurrentText + ", latest " + LatestVersion + (Available ? " (update available)" : ""));
            }
            catch (Exception ex)
            {
                Log.Write("Update check failed: " + ex.Message);
                Available = false;
            }
            RaiseChanged();
            return Available;
        }

        private static void RaiseChanged()
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) Changed?.Invoke(); else d.BeginInvoke(new Action(() => Changed?.Invoke()));
        }

        /// <summary>
        /// Downloads the new Setup.exe, runs it silently and closes TransferFTP.
        /// The installer replaces the files and starts TransferFTP again, with your tabs as they were.
        /// </summary>
        public static async Task<bool> Install(Action<string> status, bool askIfBusy = true)
        {
            if (Installing) return false;
            if (!Available && !await Check())
            {
                status?.Invoke("You're on the latest version");
                return false;
            }
            if (askIfBusy && TransferQueue.Instance.IsBusy &&
                !Dialogs.Confirm("Update now?", "Transfers are still running. Updating closes TransferFTP and stops them. Unfinished files can be resumed afterwards.", "Update now"))
                return false;

            Installing = true;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "TransferFTP", "update");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "TransferFTP-Setup-" + LatestVersion + ".exe");

                using (var http = Client(TimeSpan.FromMinutes(10)))
                using (var resp = await http.GetAsync(_setupUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? _setupSize;
                    using var input = await resp.Content.ReadAsStreamAsync();
                    using var output = File.Create(path);
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int lastPct = -1;
                    int n;
                    while ((n = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, n);
                        done += n;
                        int pct = total > 0 ? (int)(done * 100 / total) : -1;
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            status?.Invoke(pct >= 0 ? "Downloading " + pct + "%" : "Downloading " + Fmt.Size(done));
                        }
                    }
                    if (total > 0 && done < total) throw new IOException("The download stopped early. Try again.");
                }

                status?.Invoke("Installing...");
                Log.Info("Installing TransferFTP " + LatestVersion);
                // The installer closes this copy, updates the files and starts TransferFTP again when it's done.
                Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
                await Task.Delay(400);
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                return true;
            }
            catch (Exception ex)
            {
                Installing = false;
                Log.Error("Update failed: " + ex.Message);
                status?.Invoke("Update failed");
                Dialogs.Info("Couldn't update", ex.Message + "\n\nYou can download the latest version from GitHub instead.");
                return false;
            }
        }

        /// <summary>Checks at start-up and every few hours, if the setting is on. Installs straight away when that's turned on too.</summary>
        public static void StartBackgroundChecks()
        {
            async Task Run()
            {
                if (!Settings.Current.CheckForUpdates) return;
                if (!await Check()) return;
                if (Settings.Current.InstallUpdatesAutomatically && !TransferQueue.Instance.IsBusy && EditManager.Sessions.Count == 0)
                    await Application.Current.Dispatcher.InvokeAsync(() => Install(null, false));
            }

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += async (s, e) => await Run();
            timer.Start();
            var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            first.Tick += async (s, e) => { first.Stop(); await Run(); };
            first.Start();
        }
    }
}
