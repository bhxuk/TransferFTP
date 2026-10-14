using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace TransferFTP
{
    public enum Protocol { SFTP, SCP, FTP, FTPS, FTPSImplicit, SMB }

    /// <summary>A saved connection.</summary>
    public class Site
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New site";
        public Protocol Protocol { get; set; } = Protocol.SFTP;
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string User { get; set; } = "";

        /// <summary>DPAPI-encrypted password. Use Password to read or set it.</summary>
        public string PasswordProtected { get; set; } = "";
        public string KeyFile { get; set; } = "";
        public string KeyPassphraseProtected { get; set; } = "";

        /// <summary>For SMB this is the share and folder, e.g. "media/films".</summary>
        public string RemoteFolder { get; set; } = "";
        public string LocalFolder { get; set; } = "";
        public bool ActiveMode { get; set; }
        public string Notes { get; set; } = "";
        public DateTime LastUsed { get; set; }

        [JsonIgnore]
        public string Password
        {
            get => Secrets.Unprotect(PasswordProtected);
            set => PasswordProtected = Secrets.Protect(value);
        }

        [JsonIgnore]
        public string KeyPassphrase
        {
            get => Secrets.Unprotect(KeyPassphraseProtected);
            set => KeyPassphraseProtected = Secrets.Protect(value);
        }

        /// <summary>Password typed at connect time when none is saved. Kept in memory only.</summary>
        [JsonIgnore] public string SessionPassword { get; set; }

        [JsonIgnore] public bool IsSsh => Protocol == Protocol.SFTP || Protocol == Protocol.SCP;
        [JsonIgnore] public bool IsFtp => Protocol == Protocol.FTP || Protocol == Protocol.FTPS || Protocol == Protocol.FTPSImplicit;

        [JsonIgnore] public string HostKey => (Host ?? "").Trim().ToLowerInvariant() + ":" + Port;

        [JsonIgnore]
        public string Summary => Protocol == Protocol.SMB
            ? @"\\" + Host + "\\" + (RemoteFolder ?? "").Replace('/', '\\')
            : ProtocolName(Protocol) + "  " + (string.IsNullOrEmpty(User) ? "" : User + "@") + Host + ":" + Port;

        public static int DefaultPort(Protocol p) => p switch
        {
            Protocol.SFTP => 22,
            Protocol.SCP => 22,
            Protocol.FTPSImplicit => 990,
            Protocol.SMB => 445,
            _ => 21
        };

        public static string ProtocolName(Protocol p) => p switch
        {
            Protocol.FTPS => "FTPS (explicit)",
            Protocol.FTPSImplicit => "FTPS (implicit)",
            Protocol.SMB => "Network share",
            _ => p.ToString()
        };

        public static readonly Protocol[] All = { Protocol.SFTP, Protocol.SCP, Protocol.FTP, Protocol.FTPS, Protocol.FTPSImplicit, Protocol.SMB };

        public Site Clone()
        {
            var s = (Site)MemberwiseClone();
            return s;
        }

        public override string ToString() => Name;
    }

    /// <summary>The saved sites, in %AppData%\TransferFTP\sites.json. Passwords are encrypted with DPAPI.</summary>
    public static class SiteStore
    {
        private static readonly string FilePath = Path.Combine(Settings.Folder, "sites.json");
        private static List<Site> _sites;

        public static event Action Changed;

        public static List<Site> All
        {
            get
            {
                if (_sites == null) _sites = Load();
                return _sites;
            }
        }

        public static Site Find(string id) => All.FirstOrDefault(s => s.Id == id);

        private static List<Site> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<Site>>(File.ReadAllText(FilePath), Opts) ?? new List<Site>();
            }
            catch (Exception ex) { Log.Write("Sites load: " + ex.Message); }
            return new List<Site>();
        }

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(All, Opts));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Log.Error("Couldn't save sites: " + ex.Message); }
            Changed?.Invoke();
        }

        public static void Upsert(Site site)
        {
            int i = All.FindIndex(s => s.Id == site.Id);
            if (i >= 0) All[i] = site; else All.Add(site);
            Save();
        }

        public static void Remove(Site site)
        {
            All.RemoveAll(s => s.Id == site.Id);
            Save();
        }

        public static void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(All.Select(s =>
        {
            // Exports never include passwords: they're tied to this Windows account anyway.
            var c = s.Clone();
            c.PasswordProtected = "";
            c.KeyPassphraseProtected = "";
            return c;
        }).ToList(), Opts));

        public static int Import(string path)
        {
            var list = JsonSerializer.Deserialize<List<Site>>(File.ReadAllText(path), Opts) ?? new List<Site>();
            foreach (var s in list)
            {
                s.Id = Guid.NewGuid().ToString("N");
                s.PasswordProtected = "";
                s.KeyPassphraseProtected = "";
                All.Add(s);
            }
            Save();
            return list.Count;
        }

        public static string FileZillaPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileZilla", "sitemanager.xml");

        /// <summary>Copies sites from FileZilla's site manager. Returns how many were added.</summary>
        public static int ImportFileZilla(string path)
        {
            var doc = XDocument.Load(path);
            int added = 0;
            foreach (var server in doc.Descendants("Server"))
            {
                string host = (string)server.Element("Host") ?? "";
                if (string.IsNullOrWhiteSpace(host)) continue;
                int.TryParse((string)server.Element("Protocol") ?? "0", out int proto);
                int.TryParse((string)server.Element("Port") ?? "0", out int port);
                var p = proto switch
                {
                    1 => Protocol.SFTP,
                    3 => Protocol.FTPSImplicit,
                    4 => Protocol.FTPS,
                    _ => Protocol.FTP
                };
                var site = new Site
                {
                    Name = (string)server.Element("Name") ?? host,
                    Host = host,
                    Protocol = p,
                    Port = port > 0 ? port : Site.DefaultPort(p),
                    User = (string)server.Element("User") ?? "",
                    KeyFile = (string)server.Element("Keyfile") ?? "",
                    RemoteFolder = ParseFileZillaPath((string)server.Element("RemoteDir")),
                    LocalFolder = (string)server.Element("LocalDir") ?? ""
                };
                var pass = server.Element("Pass");
                if (pass != null)
                {
                    string enc = (string)pass.Attribute("encoding");
                    string v = pass.Value ?? "";
                    if (enc == "base64")
                    {
                        try { site.Password = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v)); } catch { }
                    }
                    else if (string.IsNullOrEmpty(enc)) site.Password = v;
                    // "crypt" passwords are protected by a FileZilla master password and can't be read.
                }
                All.Add(site);
                added++;
            }
            if (added > 0) Save();
            return added;
        }

        /// <summary>FileZilla stores remote paths like "1 0 4 home 4 shaam". Turn that into /home/shaam.</summary>
        private static string ParseFileZillaPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            try
            {
                var parts = raw.Split(' ');
                if (parts.Length < 2 || parts[0] != "1") return "";
                var segs = new List<string>();
                int i = 2;
                while (i < parts.Length)
                {
                    if (!int.TryParse(parts[i], out int len)) break;
                    i++;
                    string seg = "";
                    while (i < parts.Length && seg.Length < len) { seg += (seg.Length > 0 ? " " : "") + parts[i]; i++; }
                    segs.Add(seg);
                }
                return "/" + string.Join("/", segs);
            }
            catch { return ""; }
        }
    }

    /// <summary>Remembers which SSH host keys and FTPS certificates the person has trusted.</summary>
    public static class TrustStore
    {
        private class Data
        {
            public Dictionary<string, string> HostKeys { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> Certificates { get; set; } = new Dictionary<string, string>();
        }

        private static readonly string FilePath = Path.Combine(Settings.Folder, "trusted.json");
        private static readonly object Gate = new object();
        private static Data _data;

        private static Data D
        {
            get
            {
                if (_data != null) return _data;
                try
                {
                    if (File.Exists(FilePath)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                }
                catch (Exception ex) { Log.Write("Trust load: " + ex.Message); }
                return _data ??= new Data();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(D, Json.Options));
            }
            catch (Exception ex) { Log.Write("Trust save: " + ex.Message); }
        }

        /// <summary>Checks an SSH server's key. Prompts the first time and if it ever changes.</summary>
        public static bool CheckHostKey(Site site, string keyType, string fingerprint)
        {
            string entry = keyType + " SHA256:" + fingerprint;
            lock (Gate)
            {
                if (D.HostKeys.TryGetValue(site.HostKey, out var known))
                {
                    if (known == entry) return true;
                    int pick = Dialogs.Choose("Server key has changed",
                        "The key for " + site.Host + " is different from last time. This happens after a server is rebuilt, " +
                        "but it can also mean someone is intercepting the connection.\n\nSaved:  " + known + "\nNow:    " + entry,
                        new[] { "Cancel", "Trust the new key" }, out _);
                    if (pick != 1) return false;
                }
                else
                {
                    int pick = Dialogs.Choose("Trust this server?",
                        "First connection to " + site.Host + ". Check the fingerprint matches the server before trusting it.\n\n" + entry,
                        new[] { "Trust and connect", "Connect once", "Cancel" }, out _);
                    if (pick == 1) return true;
                    if (pick != 0) return false;
                }
                D.HostKeys[site.HostKey] = entry;
                Save();
                return true;
            }
        }

        /// <summary>Checks an FTPS certificate. Valid public certificates pass straight through.</summary>
        public static bool CheckCertificate(Site site, X509Certificate cert, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (cert == null) return false;
            string thumb;
            using (var c2 = new X509Certificate2(cert)) thumb = c2.Thumbprint;
            lock (Gate)
            {
                if (D.Certificates.TryGetValue(site.HostKey, out var known) && known == thumb) return true;
                int pick = Dialogs.Choose("Certificate problem",
                    site.Host + " sent a certificate Windows doesn't trust (" + errors + "). Self-signed certificates on home servers often look like this.\n\n" +
                    "Issued to: " + cert.Subject + "\nIssuer: " + cert.Issuer + "\nThumbprint: " + thumb,
                    new[] { "Cancel", "Trust this certificate" }, out _);
                if (pick != 1) return false;
                D.Certificates[site.HostKey] = thumb;
                Save();
                return true;
            }
        }
    }
}
