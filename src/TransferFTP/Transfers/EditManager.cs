using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TransferFTP
{
    /// <summary>A remote file open in an editor. Each save is uploaded back automatically.</summary>
    public class EditSession
    {
        public FileSystem Fs { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
        public string Name => Path.GetFileName(LocalPath);
        public string Where => (Fs?.Title ?? "") + "  " + RemotePath;
        public DateTime LastUpload { get; set; }
        internal FileSystemWatcher Watcher;
        internal DispatcherTimer Debounce;
        internal DateTime LastSeenWrite;
        internal long LastSeenSize = -1;
    }

    public static class EditManager
    {
        public static ObservableCollection<EditSession> Sessions { get; } = new ObservableCollection<EditSession>();

        private static readonly string Root = Path.Combine(Path.GetTempPath(), "TransferFTP", "edit");

        public static void Edit(FileSystem fs, FsItem item)
        {
            if (fs == null || item == null || item.IsDir) return;
            var existing = Sessions.FirstOrDefault(s => s.Fs.Key == fs.Key && s.RemotePath == item.FullPath);
            if (existing != null && File.Exists(existing.LocalPath)) { Open(existing.LocalPath); return; }

            if (item.Size > 200L * 1024 * 1024 &&
                !Dialogs.Confirm("Large file", item.Name + " is " + Fmt.Size(item.Size) + ". Download it to edit anyway?", "Download")) return;

            string folder = Path.Combine(Root, Guid.NewGuid().ToString("N").Substring(0, 10));
            string local = Path.Combine(folder, item.Name);
            Log.Info("Opening " + item.Name + " for editing");
            Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                using var conn = fs.CreateSibling();
                conn.Connect();
                using (var rs = conn.OpenRead(item.FullPath, 0))
                using (var ws = File.Create(local))
                    rs.CopyTo(ws);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    string msg = TransferQueue.Friendly(t.Exception.GetBaseException());
                    Log.Error("Couldn't download " + item.Name + ": " + msg);
                    Dialogs.Info("Couldn't open file", msg);
                    return;
                }
                var session = new EditSession { Fs = fs, RemotePath = item.FullPath, LocalPath = local };
                var fi = new FileInfo(local);
                session.LastSeenWrite = fi.LastWriteTimeUtc;
                session.LastSeenSize = fi.Length;
                Watch(session);
                Sessions.Add(session);
                Open(local);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static void Open(string local)
        {
            try
            {
                string editor = Settings.Current.EditorPath;
                if (!string.IsNullOrWhiteSpace(editor) && File.Exists(editor))
                    Process.Start(new ProcessStartInfo(editor, "\"" + local + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No app is registered for this file type: fall back to Notepad.
                try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + local + "\"")); }
                catch (Exception ex2) { Dialogs.Info("Couldn't open file", ex2.Message); }
            }
        }

        private static void Watch(EditSession s)
        {
            s.Debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            s.Debounce.Tick += (o, e) => { s.Debounce.Stop(); Upload(s); };
            var w = new FileSystemWatcher(Path.GetDirectoryName(s.LocalPath))
            {
                Filter = "*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false
            };
            string name = Path.GetFileName(s.LocalPath);
            void Changed(string changedName)
            {
                if (!string.Equals(changedName, name, StringComparison.OrdinalIgnoreCase)) return;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => { s.Debounce.Stop(); s.Debounce.Start(); }));
            }
            // Many editors save to a temp file and rename it over the original, so watch renames and creates too.
            w.Changed += (o, e) => Changed(e.Name);
            w.Created += (o, e) => Changed(e.Name);
            w.Renamed += (o, e) => Changed(e.Name);
            w.EnableRaisingEvents = true;
            s.Watcher = w;
        }

        private static void Upload(EditSession s)
        {
            try
            {
                var fi = new FileInfo(s.LocalPath);
                if (!fi.Exists) return;
                if (fi.LastWriteTimeUtc == s.LastSeenWrite && fi.Length == s.LastSeenSize) return;
                s.LastSeenWrite = fi.LastWriteTimeUtc;
                s.LastSeenSize = fi.Length;
                s.LastUpload = DateTime.Now;
                Log.Info("Saving " + s.Name + " back to " + s.Fs.Title);
                TransferQueue.Instance.Add(new TransferJob
                {
                    Src = new LocalFs(),
                    SrcPath = s.LocalPath,
                    Dst = s.Fs,
                    DstPath = s.RemotePath,
                    Size = fi.Length,
                    Decision = "Overwrite",
                    Tag = "edit",
                    Batch = "edit-" + s.LocalPath
                });
            }
            catch (Exception ex) { Log.Error("Edit upload: " + ex.Message); }
        }

        public static void Stop(EditSession s)
        {
            try { s.Watcher?.Dispose(); } catch { }
            s.Debounce?.Stop();
            Sessions.Remove(s);
            try { Directory.Delete(Path.GetDirectoryName(s.LocalPath), true); } catch { }
        }

        public static void StopAll()
        {
            foreach (var s in Sessions.ToList()) Stop(s);
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }
}
