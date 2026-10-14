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

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

        /// <summary>Works for local drives and network shares (\\server\share) alike.</summary>
        public override SpaceInfo GetSpace(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string dir = path.EndsWith("\\") ? path : path + "\\";
            if (!GetDiskFreeSpaceEx(dir, out ulong free, out ulong total, out _)) return null;
            return new SpaceInfo { Free = (long)free, Total = (long)total };
        }

        public override FileSystem CreateSibling() => new LocalFs();

        public override void Dispose() { }
    }

    /// <summary>A Windows or NAS share, reached as \\host\share with optional sign-in.</summary>
    /// <summary>
    /// A Windows or NAS machine reached over SMB. With no share named, it opens on a list of the
    /// machine's shares (like \\server in Explorer); with a share, it opens there and Up still goes back to the list.
    /// </summary>
    public class SmbFs : LocalFs
    {
        private readonly string _server;      // \\host
        private readonly string _shareRoot;   // \\host\share, or \\host when no share was given
        private bool _connected;

        public SmbFs(Site site)
        {
            Site = site;
            string host = (site.Host ?? "").Trim().Trim('\\', '/');
            if (host.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) host = host.Substring(6).Trim('/');
            _server = @"\\" + host;
            string folder = (site.RemoteFolder ?? "").Replace('/', '\\').Trim('\\');
            string share = folder.Split('\\')[0];
            _shareRoot = string.IsNullOrEmpty(share) ? _server : _server + "\\" + share;
            Home = string.IsNullOrEmpty(folder) ? _shareRoot : _server + "\\" + folder;
        }

        public override string Title => Site.Name;
        public override bool IsConnected => _connected;
        public override bool IsLocal => false;
        public override string Root => _server;

        public bool IsServerRoot(string path) => string.Equals((path ?? "").TrimEnd('\\'), _server, StringComparison.OrdinalIgnoreCase);

        public override void Connect()
        {
            if (_server.Length <= 2) throw new InvalidOperationException("Enter the computer's name or address in Host.");
            if (!string.IsNullOrEmpty(Site.User))
            {
                string pwd = Passwords.Get(Site, "Password for " + Site.User + " on " + _server);
                if (pwd == null) throw new OperationCanceledException("Cancelled");
                // Signing in to IPC$ sets up the session for every share on that machine.
                int r = Native.ConnectShare(_server + "\\IPC$", Site.User, pwd);
                if (r != 0 && _shareRoot != _server) r = Native.ConnectShare(_shareRoot, Site.User, pwd);
                if (r != 0) throw new InvalidOperationException(Explain(r));
            }
            if (IsServerRoot(Home)) Shares();   // throws with a clear message if the machine can't be reached
            else if (!Directory.Exists(Home)) throw new DirectoryNotFoundException("Can't open " + Home + ". Check the share name is right.");
            _connected = true;
        }

        private static string Explain(int code) => code switch
        {
            5 => "Access denied. Check the username and password. For a Windows PC, use an account on that PC (try COMPUTERNAME\\username).",
            53 or 67 => "Can't find that computer or share. Check the name, and that file sharing is allowed through its firewall (Tailscale often counts as a Public network).",
            86 or 1326 => "The username or password is wrong.",
            1219 => "Windows is already signed in to this computer with a different account. Disconnect other connections to it (net use) and try again.",
            1203 or 1231 => "The network path wasn't found. Check the computer is on and reachable, and that port 445 isn't blocked.",
            _ => new Win32Exception(code).Message + " (error " + code + ")"
        };

        public override string Parent(string path)
        {
            if (string.IsNullOrEmpty(path) || IsServerRoot(path)) return null;
            string p = path.TrimEnd('\\');
            // \\host\share goes up to the list of shares.
            if (p.Count(c => c == '\\') == 3) return _server;
            return base.Parent(path);
        }

        public override List<FsItem> List(string path)
        {
            if (string.IsNullOrEmpty(path)) path = Home;
            if (IsServerRoot(path)) return Shares();
            return base.List(path);
        }

        public override SpaceInfo GetSpace(string path) => IsServerRoot(path) ? null : base.GetSpace(path);

        public override FsItem Stat(string path) => IsServerRoot(path) ? new FsItem { Name = _server, FullPath = _server, IsDir = true } : base.Stat(path);

        // ---------------- Listing shares ----------------

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct SHARE_INFO_1
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] public string Name;
            public uint Type;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] public string Remark;
        }

        [System.Runtime.InteropServices.DllImport("Netapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int prefMaxLen, out int read, out int total, ref int resume);

        [System.Runtime.InteropServices.DllImport("Netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        private List<FsItem> Shares()
        {
            int resume = 0;
            int r = NetShareEnum(_server, 1, out IntPtr buf, -1, out int read, out _, ref resume);
            if (r != 0 && r != 234) throw new InvalidOperationException("Couldn't list the shares on " + _server + ". " + Explain(r));
            var items = new List<FsItem>();
            try
            {
                int size = System.Runtime.InteropServices.Marshal.SizeOf<SHARE_INFO_1>();
                for (int i = 0; i < read; i++)
                {
                    var info = System.Runtime.InteropServices.Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * size);
                    bool disk = (info.Type & 0xFF) == 0;            // folders and drives, not printers or IPC$
                    bool special = (info.Type & 0x80000000) != 0;   // admin shares like C$
                    if (!disk) continue;
                    if (special && !Settings.Current.ShowHiddenFiles) continue;
                    string full = _server + "\\" + info.Name;
                    var item = new FsItem { Name = info.Name, FullPath = full, IsDir = true, IsDrive = true, Perms = info.Remark ?? "" };
                    if (GetDiskFreeSpaceEx(full + "\\", out ulong free, out _, out _)) item.Size = (long)free;
                    items.Add(item);
                }
            }
            finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
            // Normal shares first, then admin shares (C$, D$...).
            return items.OrderBy(x => x.Name.EndsWith("$") ? 1 : 0).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

        public override FileSystem CreateSibling()
        {
            var s = new SmbFs(Site);
            s._connected = true;   // the Windows session is shared, no need to sign in again
            return s;
        }
    }
}
