using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TransferFTP
{
    /// <summary>Builds SSH sign-in details for a site, asking for a password or key passphrase if none is saved.</summary>
    public static class SshAuth
    {
        public static ConnectionInfo Build(Site s)
        {
            var methods = new List<AuthenticationMethod>();
            bool hasKey = !string.IsNullOrWhiteSpace(s.KeyFile);
            if (hasKey)
            {
                if (!File.Exists(s.KeyFile)) throw new FileNotFoundException("Key file not found: " + s.KeyFile);
                methods.Add(new PrivateKeyAuthenticationMethod(s.User, LoadKey(s)));
            }

            string pwd = s.Password;
            if (string.IsNullOrEmpty(pwd)) pwd = s.SessionPassword;
            if (string.IsNullOrEmpty(pwd) && !hasKey)
            {
                pwd = Dialogs.Prompt("Password", "Password for " + s.User + "@" + s.Host, "", true);
                if (pwd == null) throw new OperationCanceledException("Cancelled");
                s.SessionPassword = pwd;
            }
            if (!string.IsNullOrEmpty(pwd))
            {
                methods.Add(new PasswordAuthenticationMethod(s.User, pwd));
                var kbd = new KeyboardInteractiveAuthenticationMethod(s.User);
                string answer = pwd;
                kbd.AuthenticationPrompt += (o, e) => { foreach (var p in e.Prompts) p.Response = answer; };
                methods.Add(kbd);
            }
            if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(s.User));

            return new ConnectionInfo(s.Host.Trim(), s.Port, s.User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
        }

        private static PrivateKeyFile LoadKey(Site s)
        {
            string pass = s.KeyPassphrase;
            if (string.IsNullOrEmpty(pass)) pass = s.SessionPassword;
            try
            {
                return string.IsNullOrEmpty(pass) ? new PrivateKeyFile(s.KeyFile) : new PrivateKeyFile(s.KeyFile, pass);
            }
            catch (Exception ex) when (string.IsNullOrEmpty(pass))
            {
                Log.Write("Key needs passphrase: " + ex.Message);
                pass = Dialogs.Prompt("Key passphrase", "Passphrase for " + Path.GetFileName(s.KeyFile), "", true);
                if (pass == null) throw new OperationCanceledException("Cancelled");
                var key = new PrivateKeyFile(s.KeyFile, pass);
                s.SessionPassword = pass;
                return key;
            }
        }

        /// <summary>Hooks host key checking onto any SSH.NET client.</summary>
        public static void Guard(BaseClient client, Site site)
        {
            client.HostKeyReceived += (o, e) =>
            {
                e.CanTrust = TrustStore.CheckHostKey(site, e.HostKeyName, e.FingerPrintSHA256);
            };
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        }
    }

    /// <summary>SFTP, and SCP (browsing over SFTP, file copies over SCP).</summary>
    public class SftpFs : FileSystem
    {
        private SftpClient _sftp;
        private ScpClient _scp;

        public SftpFs(Site site) { Site = site; }

        public override string Title => Site.Name;
        public override char Sep => '/';
        public override bool IsConnected => _sftp != null && _sftp.IsConnected;
        private bool UseScp => Site.Protocol == Protocol.SCP;

        public override void Connect()
        {
            lock (Gate)
            {
                if (IsConnected) return;
                _sftp?.Dispose();
                var ci = SshAuth.Build(Site);
                _sftp = new SftpClient(ci) { OperationTimeout = TimeSpan.FromSeconds(60) };
                SshAuth.Guard(_sftp, Site);
                _sftp.Connect();
                string start = (Site.RemoteFolder ?? "").Trim();
                Home = string.IsNullOrEmpty(start) ? _sftp.WorkingDirectory : start;
                if (UseScp)
                {
                    _scp?.Dispose();
                    _scp = new ScpClient(SshAuth.Build(Site));
                    SshAuth.Guard(_scp, Site);
                    _scp.Connect();
                }
            }
        }

        private SftpClient C
        {
            get
            {
                if (!IsConnected) Connect();
                return _sftp;
            }
        }

        public override List<FsItem> List(string path)
        {
            lock (Gate)
            {
                var items = new List<FsItem>();
                foreach (var f in C.ListDirectory(path))
                {
                    if (f.Name == "." || f.Name == "..") continue;
                    var item = new FsItem
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        IsDir = f.IsDirectory,
                        IsLink = f.IsSymbolicLink,
                        Size = f.IsDirectory ? -1 : f.Length,
                        Modified = f.LastWriteTime,
                        Perms = Perms(f.IsDirectory, f.OwnerCanRead, f.OwnerCanWrite, f.OwnerCanExecute, f.GroupCanRead, f.GroupCanWrite,
                                      f.GroupCanExecute, f.OthersCanRead, f.OthersCanWrite, f.OthersCanExecute)
                    };
                    if (item.IsLink)
                    {
                        // Follow links so linked folders open like folders.
                        item.IsDir = IsDirectoryTarget(f.FullName);
                        if (item.IsDir) item.Size = -1;
                    }
                    items.Add(item);
                }
                return Finish(path, items);
            }
        }

        private bool IsDirectoryTarget(string path)
        {
            try
            {
                C.ChangeDirectory(path);
                return true;
            }
            catch { return false; }
        }

        private static string Perms(bool dir, params bool[] bits)
        {
            const string rwx = "rwx";
            var c = new char[10];
            c[0] = dir ? 'd' : '-';
            for (int i = 0; i < 9; i++) c[i + 1] = bits[i] ? rwx[i % 3] : '-';
            return new string(c);
        }

        public override FsItem Stat(string path)
        {
            lock (Gate)
            {
                try
                {
                    if (!C.Exists(path)) return null;
                    var a = C.GetAttributes(path);
                    return new FsItem { Name = NameOf(path), FullPath = path, IsDir = a.IsDirectory, Size = a.IsDirectory ? -1 : a.Size, Modified = a.LastWriteTime };
                }
                catch (SftpPathNotFoundException) { return null; }
            }
        }

        public override Stream OpenRead(string path, long offset)
        {
            if (UseScp)
            {
                // SCP has no streaming read: copy to a temp file first, then read that.
                string tmp = Path.Combine(Path.GetTempPath(), "TransferFTP", "scp", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                using (var f = File.Create(tmp)) { if (!_scp.IsConnected) Connect(); _scp.Download(path, f); }
                var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.DeleteOnClose);
                if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
                return fs;
            }
            var s = C.Open(path, FileMode.Open, FileAccess.Read);
            if (offset > 0) s.Seek(offset, SeekOrigin.Begin);
            return s;
        }

        public override Stream OpenWrite(string path, bool append)
        {
            if (UseScp && !append)
            {
                // Buffer to a temp file, then send it with SCP when the stream closes.
                string tmp = Path.Combine(Path.GetTempPath(), "TransferFTP", "scp", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                return new CallbackStream(fs, () =>
                {
                    try
                    {
                        using var f = File.OpenRead(tmp);
                        if (!_scp.IsConnected) Connect();
                        _scp.Upload(f, path);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                });
            }
            if (append)
            {
                var a = C.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
                a.Seek(0, SeekOrigin.End);
                return a;
            }
            return C.Open(path, FileMode.Create, FileAccess.Write);
        }

        public override void MakeDir(string path) { lock (Gate) C.CreateDirectory(path); }
        public override void DeleteFile(string path) { lock (Gate) C.DeleteFile(path); }
        public override void DeleteDir(string path) { lock (Gate) C.DeleteDirectory(path); }
        public override void Rename(string from, string to) { lock (Gate) C.RenameFile(from, to); }

        public override void SetModified(string path, DateTime local)
        {
            lock (Gate)
            {
                try { C.SetLastWriteTime(path, local); } catch (Exception ex) { Log.Write("SetModified: " + ex.Message); }
            }
        }

        public override bool CanChmod => true;
        public override void Chmod(string path, int mode) { lock (Gate) C.ChangePermissions(path, (short)mode); }

        /// <summary>Runs a shell command on the server and returns its output.</summary>
        public string Run(string command)
        {
            using var ssh = new SshClient(SshAuth.Build(Site));
            SshAuth.Guard(ssh, Site);
            ssh.Connect();
            var cmd = ssh.RunCommand(command);
            return string.IsNullOrEmpty(cmd.Error) ? cmd.Result : cmd.Result + cmd.Error;
        }

        public override FileSystem CreateSibling() => new SftpFs(Site);

        public override void Dispose()
        {
            lock (Gate)
            {
                try { _sftp?.Disconnect(); } catch { }
                _sftp?.Dispose();
                _sftp = null;
                try { _scp?.Disconnect(); } catch { }
                _scp?.Dispose();
                _scp = null;
            }
        }
    }
}
