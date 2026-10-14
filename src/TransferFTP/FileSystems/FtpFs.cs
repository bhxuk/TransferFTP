using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentFTP;

namespace TransferFTP
{
    /// <summary>FTP and FTPS (explicit or implicit TLS).</summary>
    public class FtpFs : FileSystem
    {
        private FtpClient _c;

        public FtpFs(Site site) { Site = site; }

        public override string Title => Site.Name;
        public override char Sep => '/';
        public override bool IsConnected => _c != null && _c.IsConnected;

        public override void Connect()
        {
            lock (Gate)
            {
                if (IsConnected) return;
                _c?.Dispose();
                string user = string.IsNullOrEmpty(Site.User) ? "anonymous" : Site.User;
                string pwd;
                if (user == "anonymous" && string.IsNullOrEmpty(Site.Password)) pwd = "anonymous@";
                else
                {
                    pwd = Passwords.Get(Site, "Password for " + user + "@" + Site.Host);
                    if (pwd == null) throw new OperationCanceledException("Cancelled");
                }

                var c = new FtpClient(Site.Host.Trim(), user, pwd, Site.Port);
                c.Config.EncryptionMode = Site.Protocol switch
                {
                    Protocol.FTPS => FtpEncryptionMode.Explicit,
                    Protocol.FTPSImplicit => FtpEncryptionMode.Implicit,
                    _ => FtpEncryptionMode.None
                };
                c.Config.DataConnectionType = Site.ActiveMode ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive;
                c.Config.ConnectTimeout = 20000;
                c.Config.ReadTimeout = 60000;
                c.Config.DataConnectionConnectTimeout = 20000;
                c.Config.DataConnectionReadTimeout = 60000;
                var site = Site;
                c.ValidateCertificate += (control, e) =>
                {
                    e.Accept = TrustStore.CheckCertificate(site, e.Certificate, e.PolicyErrors);
                };
                c.Connect();
                _c = c;
                string start = (Site.RemoteFolder ?? "").Trim();
                Home = string.IsNullOrEmpty(start) ? _c.GetWorkingDirectory() : start;
                if (string.IsNullOrEmpty(Home)) Home = "/";
            }
        }

        private FtpClient C
        {
            get
            {
                if (!IsConnected) Connect();
                return _c;
            }
        }

        public override List<FsItem> List(string path)
        {
            lock (Gate)
            {
                var items = new List<FsItem>();
                foreach (var f in C.GetListing(path, FtpListOption.AllFiles))
                {
                    bool dir = f.Type == FtpObjectType.Directory;
                    bool link = f.Type == FtpObjectType.Link;
                    if (link && f.LinkObject != null) dir = f.LinkObject.Type == FtpObjectType.Directory;
                    items.Add(new FsItem
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        IsDir = dir,
                        IsLink = link,
                        Size = dir ? -1 : f.Size,
                        Modified = f.Modified,
                        Perms = f.Chmod > 0 ? PermString(OctalToMode(f.Chmod), dir) : ""
                    });
                }
                return Finish(path, items);
            }
        }

        /// <summary>FluentFTP reports permissions as a decimal number that reads like octal (755).</summary>
        private static int OctalToMode(int chmod)
        {
            int owner = chmod / 100 % 10, group = chmod / 10 % 10, other = chmod % 10;
            return (owner << 6) | (group << 3) | other;
        }

        private static int ModeToOctal(int mode) => ((mode >> 6) & 7) * 100 + ((mode >> 3) & 7) * 10 + (mode & 7);

        public override FsItem Stat(string path)
        {
            lock (Gate)
            {
                if (C.DirectoryExists(path)) return new FsItem { Name = NameOf(path), FullPath = path, IsDir = true };
                if (!C.FileExists(path)) return null;
                var item = new FsItem { Name = NameOf(path), FullPath = path, Size = C.GetFileSize(path) };
                try { item.Modified = C.GetModifiedTime(path); } catch { }
                return item;
            }
        }

        public override Stream OpenRead(string path, long offset)
        {
            var s = C.OpenRead(path, FtpDataType.Binary, offset, false);
            return new CallbackStream(s, Reply);
        }

        public override Stream OpenWrite(string path, bool append)
        {
            var s = append ? C.OpenAppend(path, FtpDataType.Binary, false) : C.OpenWrite(path, FtpDataType.Binary, false);
            return new CallbackStream(s, Reply);
        }

        private void Reply()
        {
            try { _c?.GetReply(); } catch (Exception ex) { Log.Write("FTP reply: " + ex.Message); }
        }

        public override void MakeDir(string path) { lock (Gate) C.CreateDirectory(path, true); }
        public override void DeleteFile(string path) { lock (Gate) C.DeleteFile(path); }
        public override void DeleteDir(string path) { lock (Gate) C.DeleteDirectory(path); }
        public override void Rename(string from, string to) { lock (Gate) C.Rename(from, to); }

        public override void SetModified(string path, DateTime local)
        {
            lock (Gate)
            {
                try { C.SetModifiedTime(path, local); } catch (Exception ex) { Log.Write("FTP SetModified: " + ex.Message); }
            }
        }

        public override bool CanChmod => true;
        public override void Chmod(string path, int mode) { lock (Gate) C.SetFilePermissions(path, ModeToOctal(mode)); }

        private bool _noAvbl;

        /// <summary>FTP has no standard way to ask; servers that support AVBL (Pure-FTPd, ProFTPD, some NAS boxes) report free space.</summary>
        public override SpaceInfo GetSpace(string path)
        {
            if (_noAvbl) return null;
            try
            {
                FtpReply reply;
                lock (Gate) reply = C.Execute("AVBL " + path);
                if (reply.Success)
                {
                    string digits = new string((reply.Message ?? "").Trim().TakeWhile(char.IsDigit).ToArray());
                    if (long.TryParse(digits, out long free)) return new SpaceInfo { Free = free, Total = 0 };
                }
                _noAvbl = true;
            }
            catch (Exception ex) { Log.Write("AVBL: " + ex.Message); _noAvbl = true; }
            return null;
        }

        public override FileSystem CreateSibling() => new FtpFs(Site);

        public override void Dispose()
        {
            lock (Gate)
            {
                try { _c?.Disconnect(); } catch { }
                _c?.Dispose();
                _c = null;
            }
        }
    }
}
