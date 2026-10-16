using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;

namespace TransferFTP
{
    /// <summary>
    /// Copy, cut and paste for files, shared by every tab and window.
    /// Local files also go on the Windows clipboard, so you can paste them into Explorer,
    /// and files copied in Explorer can be pasted into any folder or server here.
    /// </summary>
    public static class FileClipboard
    {
        private const string Marker = "TransferFTP.Clipboard";

        public static FileSystem Fs { get; private set; }
        public static List<FsItem> Items { get; private set; } = new List<FsItem>();
        public static string SourceDir { get; private set; }
        public static bool Cut { get; private set; }
        private static string _ticket;

        public static event Action Changed;

        public static void Set(FileSystem fs, string sourceDir, IEnumerable<FsItem> items, bool cut)
        {
            Items = items.Where(i => !i.IsParent && !i.IsDrive).ToList();
            if (Items.Count == 0) return;
            Fs = fs;
            SourceDir = sourceDir;
            Cut = cut;
            _ticket = Guid.NewGuid().ToString("N");
            try
            {
                var data = new DataObject();
                data.SetData(Marker, _ticket);
                // Files on this PC or a network share can be pasted into Explorer too.
                if (fs is LocalFs)
                {
                    var list = new StringCollection();
                    list.AddRange(Items.Select(i => i.FullPath).ToArray());
                    data.SetFileDropList(list);
                    data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
                }
                data.SetText(string.Join(Environment.NewLine, Items.Select(i => i.FullPath)));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex) { Log.Write("Clipboard: " + ex.Message); }
            Log.Info((cut ? "Cut " : "Copied ") + Describe(Items) + ". Open another folder and press Ctrl+V to paste.");
            Changed?.Invoke();
        }

        public static void Clear()
        {
            Items = new List<FsItem>();
            Fs = null;
            _ticket = null;
            Changed?.Invoke();
        }

        public static string Describe(List<FsItem> items) => items.Count == 1 ? "\"" + items[0].Name + "\"" : items.Count + " items";

        /// <summary>True when there's something to paste, from here or from Explorer.</summary>
        public static bool HasContent
        {
            get
            {
                if (OursIsCurrent()) return Items.Count > 0;
                try { return Clipboard.ContainsFileDropList(); } catch { return false; }
            }
        }

        /// <summary>Our copy is still the newest thing on the Windows clipboard (nobody copied anything since).</summary>
        private static bool OursIsCurrent()
        {
            if (_ticket == null) return false;
            try
            {
                if (!Clipboard.ContainsData(Marker)) return false;
                return Clipboard.GetData(Marker) as string == _ticket;
            }
            catch { return Items.Count > 0; }
        }

        /// <summary>What to paste: either our own items, or files copied in Explorer.</summary>
        public static bool TryGet(out FileSystem fs, out List<FsItem> items, out bool cut, out string sourceDir)
        {
            if (OursIsCurrent() && Items.Count > 0)
            {
                fs = Fs; items = Items; cut = Cut; sourceDir = SourceDir;
                return true;
            }
            fs = null; items = null; cut = false; sourceDir = null;
            try
            {
                if (!Clipboard.ContainsFileDropList()) return false;
                var local = new LocalFs();
                var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
                items = paths.Select(p => local.Stat(p)).Where(i => i != null).ToList();
                if (items.Count == 0) return false;
                if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    cut = (BitConverter.ToInt32(b, 0) & 2) != 0;
                }
                fs = local;
                sourceDir = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
                return true;
            }
            catch (Exception ex) { Log.Write("Clipboard read: " + ex.Message); return false; }
        }

        /// <summary>"report.pdf" becomes "report - Copy.pdf", then "report - Copy (2).pdf", like Explorer.</summary>
        public static string CopyName(string name, bool isDir, ICollection<string> taken)
        {
            string stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
            string ext = isDir ? "" : Path.GetExtension(name);
            string candidate = stem + " - Copy" + ext;
            for (int i = 2; taken.Contains(candidate); i++) candidate = stem + " - Copy (" + i + ")" + ext;
            return candidate;
        }
    }
}
