using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TransferFTP
{
    /// <summary>App settings, saved as JSON in %AppData%\TransferFTP.</summary>
    public class Settings
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransferFTP");
        private static readonly string FilePath = Path.Combine(Folder, "settings.json");

        public static Settings Current { get; private set; } = Load();

        public string Theme { get; set; } = "Dark";
        public string Accent { get; set; } = "Amber";

        /// <summary>How many files transfer at the same time.</summary>
        public int ParallelTransfers { get; set; } = 2;

        /// <summary>Ask, Overwrite, Skip, Newer, Resume.</summary>
        public string WhenFileExists { get; set; } = "Ask";

        public bool ShowHiddenFiles { get; set; } = true;
        public bool ConfirmDelete { get; set; } = true;
        public bool PreserveTimestamps { get; set; } = true;
        public int RetryCount { get; set; } = 3;

        /// <summary>Program used by "Edit". Empty means the Windows default for that file type.</summary>
        public string EditorPath { get; set; } = "";

        /// <summary>Seconds of clock difference that sync treats as "the same time".</summary>
        public int SyncToleranceSeconds { get; set; } = 2;

        public string LeftSource { get; set; } = "local";
        public string LeftPath { get; set; } = "";
        public string RightSource { get; set; } = "local";
        public string RightPath { get; set; } = "";

        public double WindowWidth { get; set; } = 1320;
        public double WindowHeight { get; set; } = 860;
        public bool WindowMaximized { get; set; }
        public double QueueHeight { get; set; } = 230;

        private static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { Log.Write("Settings load: " + ex.Message); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Log.Write("Settings save: " + ex.Message); }
        }
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };
    }

    /// <summary>Encrypts secrets with Windows DPAPI so only this Windows account can read them.</summary>
    public static class Secrets
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TransferFTP.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(data);
            }
            catch (Exception ex) { Log.Write("Protect: " + ex.Message); return ""; }
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { Log.Write("Unprotect: " + ex.Message); return ""; }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();

        /// <summary>Raised for every line so the in-app log can show it.</summary>
        public static event Action<DateTime, string, bool> Line;

        public static void Info(string message) { Write(message); Line?.Invoke(DateTime.Now, message, false); }
        public static void Error(string message) { Write("ERROR " + message); Line?.Invoke(DateTime.Now, message, true); }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Folder);
                    string path = Path.Combine(Settings.Folder, "transferftp.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    public static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes < 0) return "";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? bytes + " B" : v.ToString(v < 10 ? "0.0" : "0") + " " + units[u];
        }

        public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Size((long)bytesPerSecond) + "/s";

        public static string Date(DateTime d) => d == DateTime.MinValue ? "" : d.ToString("dd/MM/yyyy HH:mm");

        public static string Eta(double seconds)
        {
            if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1}s", t.Minutes, t.Seconds);
            return t.Seconds + "s";
        }
    }
}
