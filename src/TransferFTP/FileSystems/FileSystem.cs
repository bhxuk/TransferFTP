using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TransferFTP
{
    /// <summary>One row in a file list.</summary>
    public class FsItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDir { get; set; }
        public bool IsLink { get; set; }
        public bool IsParent { get; set; }
        public bool IsDrive { get; set; }
        public long Size { get; set; } = -1;
        public DateTime Modified { get; set; }
        public string Perms { get; set; } = "";

        /// <summary>For folders: set once the size of everything inside has been added up.</summary>
        public bool SizeComputed { get; set; }

        public string SizeText => IsDir ? (IsDrive && Size > 0 ? Fmt.Size(Size) + " free" : SizeComputed ? Fmt.Size(Size) : "") : Fmt.Size(Size);
        public string ModifiedText => IsParent ? "" : Fmt.Date(Modified);
        public string Kind => IsParent ? "" : IsDrive ? "Drive" : IsDir ? "Folder" : Ext.Length > 0 ? Ext.ToUpperInvariant().TrimStart('.') + " file" : "File";
        public string Ext => IsDir ? "" : Path.GetExtension(Name ?? "") ?? "";

        /// <summary>Sorts folders first, then by name, the way Explorer does.</summary>
        public string SortKey => (IsParent ? "0" : IsDir ? "1" : "2") + (Name ?? "").ToLowerInvariant();

        public override string ToString() => Name;
    }

    /// <summary>Free and total space where a folder lives. Total is 0 when the server only reports free space.</summary>
    public class SpaceInfo
    {
        public long Free;
        public long Total;
        public double UsedFraction => Total > 0 ? Math.Max(0, Math.Min(1, 1 - (double)Free / Total)) : -1;
    }

    /// <summary>Anything that can list, read and write files: the local disk, an SFTP or FTP server, a network share.</summary>
    public abstract class FileSystem : IDisposable
    {
        protected readonly object Gate = new object();

        public Site Site { get; protected set; }

        /// <summary>Identity used to share connections between transfers: one key per site.</summary>
        public string Key => Site?.Id ?? "local";

        public abstract string Title { get; }
        public abstract char Sep { get; }
        public abstract bool IsConnected { get; }
        public virtual bool IsLocal => false;

        /// <summary>The top of the tree (drive list locally, "/" on servers, the share root on SMB).</summary>
        public virtual string Root => "/";

        /// <summary>Where a new session starts.</summary>
        public string Home { get; protected set; } = "/";

        public abstract void Connect();
        public abstract List<FsItem> List(string path);

        /// <summary>Details for one path, or null when it doesn't exist.</summary>
        public abstract FsItem Stat(string path);

        public abstract Stream OpenRead(string path, long offset);
        public abstract Stream OpenWrite(string path, bool append);
        public abstract void MakeDir(string path);
        public abstract void DeleteFile(string path);

        /// <summary>Removes an empty folder.</summary>
        public abstract void DeleteDir(string path);
        public abstract void Rename(string from, string to);
        public virtual void SetModified(string path, DateTime local) { }
        public virtual bool CanChmod => false;
        public virtual void Chmod(string path, int mode) { }

        /// <summary>Free space on the drive or server holding this path, or null if it can't be found out.</summary>
        public virtual SpaceInfo GetSpace(string path) => null;

        /// <summary>Adds up every file under a folder. Progress reports bytes and files counted so far.</summary>
        public long TreeSize(string path, System.Threading.CancellationToken token, Action<long, int> progress, ref int files)
        {
            long total = 0;
            foreach (var item in List(path))
            {
                token.ThrowIfCancellationRequested();
                if (item.IsParent) continue;
                if (item.IsDir)
                {
                    if (!item.IsLink) total += TreeSize(item.FullPath, token, progress, ref files);
                    continue;
                }
                total += Math.Max(0, item.Size);
                files++;
                if (files % 200 == 0) progress?.Invoke(total, files);
            }
            return total;
        }

        /// <summary>A second, independent connection to the same place, so transfers don't block browsing.</summary>
        public abstract FileSystem CreateSibling();

        public abstract void Dispose();

        public virtual string Combine(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir)) return name;
            return dir[dir.Length - 1] == Sep ? dir + name : dir + Sep + name;
        }

        public virtual string Parent(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return null;
            string p = path.TrimEnd('/');
            int i = p.LastIndexOf('/');
            return i <= 0 ? "/" : p.Substring(0, i);
        }

        public string NameOf(string path)
        {
            string p = path.TrimEnd(Sep);
            int i = p.LastIndexOf(Sep);
            return i < 0 ? p : p.Substring(i + 1);
        }

        /// <summary>Makes a folder and any missing folders above it.</summary>
        public virtual void EnsureDir(string path)
        {
            var missing = new Stack<string>();
            string p = path;
            while (!string.IsNullOrEmpty(p) && p != Root && Stat(p) == null)
            {
                missing.Push(p);
                p = Parent(p);
            }
            while (missing.Count > 0) MakeDir(missing.Pop());
        }

        /// <summary>Deletes a file or a whole folder tree.</summary>
        public void DeleteTree(string path, bool isDir, Action<string> progress = null)
        {
            if (!isDir) { progress?.Invoke(path); DeleteFile(path); return; }
            foreach (var item in List(path).Where(i => !i.IsParent))
            {
                if (item.IsDir && !item.IsLink) DeleteTree(item.FullPath, true, progress);
                else { progress?.Invoke(item.FullPath); DeleteFile(item.FullPath); }
            }
            DeleteDir(path);
        }

        /// <summary>Adds the ".." row and applies the hidden-files setting.</summary>
        protected List<FsItem> Finish(string path, IEnumerable<FsItem> items)
        {
            var list = items.Where(i => i.Name != "." && i.Name != "..");
            if (!Settings.Current.ShowHiddenFiles && !IsLocal) list = list.Where(i => !i.Name.StartsWith("."));
            var result = list.ToList();
            string parent = Parent(path);
            if (parent != null) result.Insert(0, new FsItem { Name = "..", FullPath = parent, IsDir = true, IsParent = true });
            return result;
        }

        /// <summary>
        /// True at the "list of places" level: This PC's drive list, or the list of shares on a network computer.
        /// Files can't be copied into these.
        /// </summary>
        public static bool IsTopList(FileSystem fs, string path)
        {
            if (fs is SmbFs smb) return smb.IsServerRoot(path);
            return fs is LocalFs && string.IsNullOrEmpty(path);
        }

        public static FileSystem For(Site site)
        {
            if (site == null) return new LocalFs();
            return site.Protocol switch
            {
                Protocol.SFTP => new SftpFs(site),
                Protocol.SCP => new SftpFs(site),
                Protocol.SMB => new SmbFs(site),
                _ => new FtpFs(site)
            };
        }

        /// <summary>Unix permission bits as "rwxr-xr-x".</summary>
        public static string PermString(int mode, bool dir)
        {
            if (mode < 0) return "";
            char[] c = new char[10];
            c[0] = dir ? 'd' : '-';
            string rwx = "rwx";
            for (int i = 0; i < 9; i++) c[i + 1] = (mode & (1 << (8 - i))) != 0 ? rwx[i % 3] : '-';
            return new string(c);
        }

        public static int ParseMode(string perms)
        {
            if (string.IsNullOrEmpty(perms) || perms.Length < 10) return -1;
            int m = 0;
            for (int i = 0; i < 9; i++) if (perms[i + 1] != '-') m |= 1 << (8 - i);
            return m;
        }
    }

    /// <summary>A stream wrapper that runs an action after it closes (FTP needs a reply read after each transfer).</summary>
    public class CallbackStream : Stream
    {
        private readonly Stream _inner;
        private Action _onClose;

        public CallbackStream(Stream inner, Action onClose) { _inner = inner; _onClose = onClose; }

        /// <summary>Set after an aborted transfer so the closing action doesn't run.</summary>
        public bool SkipCallback { get; set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); }
                finally
                {
                    var a = _onClose;
                    _onClose = null;
                    if (!SkipCallback) a?.Invoke();
                }
            }
            base.Dispose(disposing);
        }
    }
}
