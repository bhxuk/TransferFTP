using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace TransferFTP
{
    /// <summary>This PC. An empty path means "the list of drives".</summary>
    public class LocalFs : FileSystem
    {
        public LocalFs()
        {
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public override string Title => "This PC";
        public override char Sep => '\\';
        public override bool IsConnected => true;
        public override bool IsLocal => true;
        public override string Root => "";

        public override void Connect() { }

        public override string Parent(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Length > 3 ? path.TrimEnd('\\') : path;
            string parent = Path.GetDirectoryName(p);
            return parent ?? "";
        }

        public override string Combine(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir)) return name;
            return Path.Combine(dir, name);
        }

        public override List<FsItem> List(string path)
        {
            if (string.IsNullOrEmpty(path)) return Drives();
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) throw new DirectoryNotFoundException("Folder not found: " + path);
            var items = new List<FsItem>();
            bool hidden = Settings.Current.ShowHiddenFiles;
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                try
                {
                    var a = info.Attributes;
                    if (!hidden && ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0)) continue;
                    var item = new FsItem
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        IsDir = (a & FileAttributes.Directory) != 0,
                        IsLink = (a & FileAttributes.ReparsePoint) != 0,
                        Modified = info.LastWriteTime
                    };
                    if (!item.IsDir) item.Size = ((FileInfo)info).Length;
                    items.Add(item);
                }
                catch { }
            }
            return Finish(path, items);
        }

        private static List<FsItem> Drives()
        {
            var list = new List<FsItem>();
            void Add(string name, Environment.SpecialFolder f)
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) list.Add(new FsItem { Name = name, FullPath = p, IsDir = true });
            }
            Add("Desktop", Environment.SpecialFolder.DesktopDirectory);
            Add("Documents", Environment.SpecialFolder.MyDocuments);
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloads)) list.Add(new FsItem { Name = "Downloads", FullPath = downloads, IsDir = true });
            Add(Environment.UserName, Environment.SpecialFolder.UserProfile);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    var item = new FsItem { FullPath = d.RootDirectory.FullName, IsDir = true, IsDrive = true, Name = d.Name.TrimEnd('\\') };
                    if (d.IsReady)
                    {
                        if (!string.IsNullOrEmpty(d.VolumeLabel)) item.Name += "  " + d.VolumeLabel;
                        item.Size = d.AvailableFreeSpace;
                    }
                    list.Add(item);
                }
                catch { }
            }
            return list;
        }

        public override FsItem Stat(string path)
        {
            if (string.IsNullOrEmpty(path)) return new FsItem { Name = "", FullPath = "", IsDir = true };
            if (Directory.Exists(path))
            {
                var d = new DirectoryInfo(path);
                return new FsItem { Name = d.Name, FullPath = d.FullName, IsDir = true, Modified = d.LastWriteTime };
            }
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return new FsItem { Name = f.Name, FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime };
            }
            return null;
        }

        public override Stream OpenRead(string path, long offset)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
            return fs;
        }

        public override Stream OpenWrite(string path, bool append)
        {
            if (!append && File.Exists(path))
            {
                var a = File.GetAttributes(path);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            }
            return new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        }

        public override void MakeDir(string path) => Directory.CreateDirectory(path);
        public override void EnsureDir(string path) => Directory.CreateDirectory(path);

        public override void DeleteFile(string path)
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        public override void DeleteDir(string path) => Directory.Delete(path, false);

        public override void Rename(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }

        public override void SetModified(string path, DateTime local)
        {
            try { File.SetLastWriteTime(path, local); } catch { }
        }

        public override FileSystem CreateSibling() => new LocalFs();

        public override void Dispose() { }
    }

    /// <summary>A Windows or NAS share, reached as \\host\share with optional sign-in.</summary>
    public class SmbFs : LocalFs
    {
        private readonly string _shareRoot;
        private bool _connected;

        public SmbFs(Site site)
        {
            Site = site;
            string folder = (site.RemoteFolder ?? "").Replace('/', '\\').Trim('\\');
            string share = folder.Split('\\')[0];
            _shareRoot = @"\\" + site.Host.Trim().TrimStart('\\') + "\\" + share;
            Home = string.IsNullOrEmpty(folder) ? _shareRoot : @"\\" + site.Host.Trim().TrimStart('\\') + "\\" + folder;
        }

        public override string Title => Site.Name;
        public override bool IsConnected => _connected;
        public override bool IsLocal => false;
        public override string Root => _shareRoot;

        public override void Connect()
        {
            if (string.IsNullOrWhiteSpace(Site.RemoteFolder))
                throw new InvalidOperationException("Add the share name in the site's \"Share and folder\" box, for example media or media/films.");
            if (!string.IsNullOrEmpty(Site.User))
            {
                string pwd = Passwords.Get(Site, "Password for " + Site.User + " on " + _shareRoot);
                if (pwd == null) throw new OperationCanceledException("Cancelled");
                int r = Native.ConnectShare(_shareRoot, Site.User, pwd);
                if (r != 0) throw new Win32Exception(r);
            }
            if (!Directory.Exists(Home)) throw new DirectoryNotFoundException("Can't open " + Home);
            _connected = true;
        }

        public override string Parent(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (string.Equals(path.TrimEnd('\\'), _shareRoot, StringComparison.OrdinalIgnoreCase)) return null;
            return base.Parent(path);
        }

        public override List<FsItem> List(string path) => base.List(string.IsNullOrEmpty(path) ? _shareRoot : path);

        public override FileSystem CreateSibling()
        {
            var s = new SmbFs(Site);
            s._connected = true;   // the Windows session is shared, no need to sign in again
            return s;
        }
    }
}
