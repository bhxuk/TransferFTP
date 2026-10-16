using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TransferFTP
{
    public enum JobKind { Copy, MakeDir, Delete }
    public enum JobState { Queued, Running, Paused, Done, Failed, Cancelled, Skipped }

    /// <summary>One file copy, folder creation or delete in the queue.</summary>
    public class TransferJob : INotifyPropertyChanged
    {
        private static int _nextId;

        public int Id { get; } = Interlocked.Increment(ref _nextId);
        public JobKind Kind { get; set; } = JobKind.Copy;
        public FileSystem Src { get; set; }
        public string SrcPath { get; set; }
        public FileSystem Dst { get; set; }
        public string DstPath { get; set; }
        public long Size { get; set; }
        public DateTime SrcModified { get; set; }
        public bool DeleteSourceAfter { get; set; }

        /// <summary>For Delete jobs: the path is a folder tree.</summary>
        public bool IsDir { get; set; }

        /// <summary>Jobs added together share a batch so "do this for the rest" choices apply to all of them.</summary>
        public string Batch { get; set; } = "";

        /// <summary>Runs only after every other job in the batch has finished (used to tidy up after a move).</summary>
        public bool WaitForBatch { get; set; }

        /// <summary>Forced answer for "the file already exists" (Overwrite, Skip, Resume, Newer, Rename).</summary>
        public string Decision { get; set; }
        public bool ResumeNext { get; set; }
        public int Attempts { get; set; }
        public string Tag { get; set; } = "";

        internal CancellationTokenSource Cts;
        internal bool PauseRequested;

        private JobState _state;
        private long _done;
        private double _speed;
        private string _error = "";

        public JobState State { get => _state; set { _state = value; Raise(nameof(State), nameof(StatusText), nameof(IsActive), nameof(IsFinished), nameof(StatusBrush)); } }
        public long Transferred { get => _done; set { _done = value; } }
        public double Speed { get => _speed; set { _speed = value; } }
        public string Error { get => _error; set { _error = value ?? ""; Raise(nameof(Error), nameof(StatusText)); } }

        public bool IsActive => _state == JobState.Queued || _state == JobState.Running || _state == JobState.Paused;
        public bool IsFinished => !IsActive;

        public string Name => Kind == JobKind.Delete ? NameOf(SrcPath) : NameOf(Kind == JobKind.MakeDir ? DstPath : SrcPath);
        public string Detail => Kind switch
        {
            JobKind.MakeDir => "New folder in " + Dst?.Title,
            JobKind.Delete => "Delete from " + Src?.Title,
            _ => (Src?.Title ?? "") + "  →  " + (Dst?.Title ?? "") + (DeleteSourceAfter ? "  (move)" : "")
        };
        public string FullDetail => Kind == JobKind.Copy ? SrcPath + "\n→ " + DstPath : (SrcPath ?? DstPath);
        public string DirectionIcon => Kind == JobKind.Delete ? Icons.Trash
            : Kind == JobKind.MakeDir ? Icons.FolderPlus
            : Src != null && Src.IsLocal && Dst != null && !Dst.IsLocal ? Icons.Upload
            : Src != null && !Src.IsLocal && Dst != null && Dst.IsLocal ? Icons.Download
            : Icons.Right;
        public string SizeText => Kind == JobKind.Copy ? Fmt.Size(Size) : "";
        public double Percent => Size > 0 ? Math.Min(100, _done * 100.0 / Size) : (_state == JobState.Done ? 100 : 0);
        public string SpeedText => _state == JobState.Running && Kind == JobKind.Copy ? Fmt.Speed(_speed) : "";
        public string EtaText => _state == JobState.Running && _speed > 0 && Size > 0 ? Fmt.Eta((Size - _done) / _speed) : "";

        public string StatusText => _state switch
        {
            JobState.Running => Kind == JobKind.Copy && Size > 0 ? ((int)Percent) + "%" : "Working",
            JobState.Failed => "Failed: " + _error,
            JobState.Skipped => string.IsNullOrEmpty(_error) ? "Skipped" : "Skipped: " + _error,
            _ => _state.ToString()
        };

        public System.Windows.Media.Brush StatusBrush => _state switch
        {
            JobState.Done => Theme.Green,
            JobState.Failed => Theme.Red,
            JobState.Running => Theme.Accent,
            _ => Theme.Muted
        };

        private static string NameOf(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string t = p.TrimEnd('/', '\\');
            int i = Math.Max(t.LastIndexOf('/'), t.LastIndexOf('\\'));
            return i < 0 ? t : t.Substring(i + 1);
        }

        /// <summary>Pushes progress numbers to the screen. Called a few times a second, not per block.</summary>
        public void RaiseProgress() => Raise(nameof(Percent), nameof(SpeedText), nameof(EtaText), nameof(StatusText));

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(params string[] names)
        {
            var h = PropertyChanged;
            if (h == null) return;
            foreach (var n in names) h(this, new PropertyChangedEventArgs(n));
        }
    }

    /// <summary>Runs transfers in the background, a few at a time, with pause, resume, retry and overwrite rules.</summary>
    public class TransferQueue
    {
        public static TransferQueue Instance { get; } = new TransferQueue();

        public ObservableCollection<TransferJob> Jobs { get; } = new ObservableCollection<TransferJob>();

        /// <summary>Raised on the UI thread when a job finishes, so panes can refresh.</summary>
        public event Action<TransferJob> Finished;

        /// <summary>Raised on the UI thread when the overall numbers change.</summary>
        public event Action Changed;

        private readonly List<TransferJob> _all = new List<TransferJob>();
        private readonly object _gate = new object();
        private readonly object _askGate = new object();
        private readonly Dictionary<string, Stack<FileSystem>> _pool = new Dictionary<string, Stack<FileSystem>>();
        private readonly Dictionary<string, string> _batchDecision = new Dictionary<string, string>();
        private readonly HashSet<string> _knownDirs = new HashSet<string>();
        private int _running;
        private int _planning;
        private bool _pausedAll;

        public bool IsBusy { get { lock (_gate) return _running > 0 || _planning > 0 || _all.Any(j => j.State == JobState.Queued); } }
        public bool PausedAll => _pausedAll;

        public int ActiveCount { get { lock (_gate) return _all.Count(j => j.IsActive); } }
        public double TotalSpeed { get { lock (_gate) return _all.Where(j => j.State == JobState.Running).Sum(j => j.Speed); } }

        private static void Ui(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) a(); else d.BeginInvoke(a);
        }

        public void Add(IEnumerable<TransferJob> jobs)
        {
            var list = jobs.ToList();
            if (list.Count == 0) return;
            lock (_gate) _all.AddRange(list);
            Ui(() => { foreach (var j in list) Jobs.Add(j); Changed?.Invoke(); });
            Pump();
        }

        public void Add(TransferJob job) => Add(new[] { job });

        /// <summary>Copies (or moves) items from one place to a folder in another, walking folders in the background.</summary>
        /// <param name="renameTo">Optional new names by source path, used when pasting a copy into the same folder.</param>
        public void Copy(FileSystem src, IList<FsItem> items, FileSystem dst, string dstDir, bool move, IDictionary<string, string> renameTo = null)
        {
            string batch = Guid.NewGuid().ToString("N");
            var picked = items.Where(i => !i.IsParent && !i.IsDrive).ToList();
            if (picked.Count == 0) return;
            Interlocked.Increment(ref _planning);
            Task.Run(() =>
            {
                FileSystem walker = null;
                try
                {
                    walker = Acquire(src);
                    foreach (var item in picked)
                    {
                        string name = renameTo != null && renameTo.TryGetValue(item.FullPath, out var n) ? n : item.Name;
                        string target = dst.Combine(dstDir, name);
                        if (item.IsDir)
                        {
                            Log.Info("Scanning " + item.FullPath);
                            Walk(walker, src, item.FullPath, dst, target, batch, move);
                            if (move) Add(new TransferJob { Kind = JobKind.Delete, Src = src, SrcPath = item.FullPath, IsDir = true, Batch = batch, WaitForBatch = true });
                        }
                        else
                        {
                            Add(new TransferJob
                            {
                                Src = src, SrcPath = item.FullPath, Dst = dst, DstPath = target, Size = Math.Max(0, item.Size),
                                SrcModified = item.Modified, DeleteSourceAfter = move, Batch = batch
                            });
                        }
                    }
                }
                catch (Exception ex) { Log.Error("Couldn't read " + src.Title + ": " + ex.Message); }
                finally
                {
                    if (walker != null) Release(walker, false);
                    Interlocked.Decrement(ref _planning);
                    Pump();
                }
            });
        }

        private void Walk(FileSystem walker, FileSystem src, string srcDir, FileSystem dst, string dstDir, string batch, bool move)
        {
            Add(new TransferJob { Kind = JobKind.MakeDir, Src = src, SrcPath = srcDir, Dst = dst, DstPath = dstDir, Batch = batch });
            var jobs = new List<TransferJob>();
            var subdirs = new List<FsItem>();
            foreach (var child in walker.List(srcDir))
            {
                if (child.IsParent) continue;
                if (child.IsDir) { if (!child.IsLink) subdirs.Add(child); continue; }
                jobs.Add(new TransferJob
                {
                    Src = src, SrcPath = child.FullPath, Dst = dst, DstPath = dst.Combine(dstDir, child.Name), Size = Math.Max(0, child.Size),
                    SrcModified = child.Modified, DeleteSourceAfter = move, Batch = batch
                });
            }
            Add(jobs);
            foreach (var d in subdirs) Walk(walker, src, d.FullPath, dst, dst.Combine(dstDir, d.Name), batch, move);
        }

        /// <summary>Deletes items through the queue so big folders show progress and don't freeze the window.</summary>
        public void Delete(FileSystem fs, IList<FsItem> items)
        {
            string batch = Guid.NewGuid().ToString("N");
            Add(items.Where(i => !i.IsParent && !i.IsDrive).Select(i => new TransferJob
            {
                Kind = JobKind.Delete, Src = fs, SrcPath = i.FullPath, IsDir = i.IsDir && !i.IsLink, Batch = batch
            }));
        }

        public void PauseAll()
        {
            _pausedAll = true;
            lock (_gate) foreach (var j in _all.Where(j => j.State == JobState.Running)) RequestPause(j);
            Ui(() => Changed?.Invoke());
        }

        public void ResumeAll()
        {
            _pausedAll = false;
            lock (_gate) foreach (var j in _all.Where(j => j.State == JobState.Paused)) { j.ResumeNext = true; j.State = JobState.Queued; }
            Ui(() => Changed?.Invoke());
            Pump();
        }

        public void Pause(TransferJob j)
        {
            if (j.State == JobState.Running) RequestPause(j);
            else if (j.State == JobState.Queued) j.State = JobState.Paused;
        }

        public void Resume(TransferJob j)
        {
            if (j.State != JobState.Paused) return;
            j.ResumeNext = true;
            j.State = JobState.Queued;
            Pump();
        }

        private static void RequestPause(TransferJob j)
        {
            j.PauseRequested = true;
            try { j.Cts?.Cancel(); } catch { }
        }

        public void Cancel(TransferJob j)
        {
            if (j.State == JobState.Running) { j.PauseRequested = false; try { j.Cts?.Cancel(); } catch { } }
            else if (j.IsActive) j.State = JobState.Cancelled;
        }

        public void CancelAll()
        {
            lock (_gate) foreach (var j in _all.Where(j => j.IsActive).ToList()) Cancel(j);
        }

        public void Retry(TransferJob j)
        {
            if (j.State != JobState.Failed && j.State != JobState.Cancelled) return;
            j.Attempts = 0;
            j.Error = "";
            j.ResumeNext = true;
            j.State = JobState.Queued;
            Pump();
        }

        public void RetryFailed()
        {
            List<TransferJob> failed;
            lock (_gate) failed = _all.Where(j => j.State == JobState.Failed).ToList();
            foreach (var j in failed) Retry(j);
        }

        public void ClearFinished()
        {
            lock (_gate) _all.RemoveAll(j => j.IsFinished);
            Ui(() =>
            {
                for (int i = Jobs.Count - 1; i >= 0; i--) if (Jobs[i].IsFinished) Jobs.RemoveAt(i);
                Changed?.Invoke();
            });
        }

        /// <summary>Starts workers until the parallel limit is reached.</summary>
        private void Pump()
        {
            if (_pausedAll) return;
            while (true)
            {
                TransferJob next = null;
                lock (_gate)
                {
                    if (_running >= Settings.MaxParallel) return;
                    foreach (var j in _all)
                    {
                        if (j.State != JobState.Queued) continue;
                        if (!HasRoom(j)) continue;
                        if (j.WaitForBatch)
                        {
                            var others = _all.Where(o => o != j && o.Batch == j.Batch).ToList();
                            if (others.Any(o => o.IsActive)) continue;
                            if (_planning > 0) continue;
                            if (others.Any(o => o.State == JobState.Failed || o.State == JobState.Cancelled))
                            {
                                j.Error = "kept the original because some files didn't copy";
                                j.State = JobState.Skipped;
                                continue;
                            }
                        }
                        next = j;
                        break;
                    }
                    if (next == null) { if (_running == 0 && _planning == 0) ScheduleIdleCleanup(); return; }
                    next.State = JobState.Running;
                    next.Cts = new CancellationTokenSource();
                    next.PauseRequested = false;
                    _running++;
                }
                var job = next;
                Task.Run(() => Run(job));
            }
        }

        /// <summary>The job's limit is the lowest of its sites' limits (or the Settings number for PC-only copies).</summary>
        private static int LimitFor(TransferJob j)
        {
            int limit = int.MaxValue;
            // Use the saved site, so a change in Sites applies straight away without reconnecting.
            if (j.Src?.Site != null) limit = Math.Min(limit, (SiteStore.Find(j.Src.Site.Id) ?? j.Src.Site).Parallel);
            if (j.Dst?.Site != null) limit = Math.Min(limit, (SiteStore.Find(j.Dst.Site.Id) ?? j.Dst.Site).Parallel);
            if (limit == int.MaxValue) limit = Settings.Current.ParallelTransfers;
            return Math.Max(1, Math.Min(Settings.MaxParallel, limit));
        }

        /// <summary>Call with _gate held. True when starting this job keeps every site it uses within its limit.</summary>
        private bool HasRoom(TransferJob j)
        {
            int limit = LimitFor(j);
            string a = j.Src?.Site?.Id, b = j.Dst?.Site?.Id;
            int running = 0;
            foreach (var r in _all)
            {
                if (r.State != JobState.Running) continue;
                if (a == null && b == null)
                {
                    if (r.Src?.Site == null && r.Dst?.Site == null) running++;
                }
                else
                {
                    string ra = r.Src?.Site?.Id, rb = r.Dst?.Site?.Id;
                    if ((a != null && (ra == a || rb == a)) || (b != null && (ra == b || rb == b))) running++;
                }
            }
            return running < limit;
        }

        private void Run(TransferJob job)
        {
            FileSystem src = null, dst = null;
            bool broken = false;
            try
            {
                switch (job.Kind)
                {
                    case JobKind.MakeDir:
                        dst = Acquire(job.Dst);
                        EnsureDir(dst, job.DstPath);
                        job.State = JobState.Done;
                        break;
                    case JobKind.Delete:
                        src = Acquire(job.Src);
                        src.DeleteTree(job.SrcPath, job.IsDir);
                        lock (_knownDirs) _knownDirs.Clear();
                        job.State = JobState.Done;
                        break;
                    default:
                        src = Acquire(job.Src);
                        dst = Acquire(job.Dst);
                        broken = true;   // until the copy completes cleanly, don't reuse these connections
                        CopyFile(job, src, dst);
                        broken = false;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                job.State = job.PauseRequested ? JobState.Paused : JobState.Cancelled;
                if (job.State == JobState.Paused) job.ResumeNext = true;
            }
            catch (Exception ex)
            {
                broken = true;
                job.Attempts++;
                string msg = Friendly(ex);
                Log.Write("Transfer error (" + job.SrcPath + "): " + ex);
                if (job.Kind == JobKind.Copy && job.Attempts <= Settings.Current.RetryCount && Transient(ex))
                {
                    Log.Info("Retrying " + job.Name + " (" + msg + ")");
                    job.Error = msg;
                    job.ResumeNext = true;
                    Thread.Sleep(Math.Min(10000, 1000 * job.Attempts * job.Attempts));
                    job.State = JobState.Queued;
                }
                else
                {
                    job.Error = msg;
                    job.State = JobState.Failed;
                    Log.Error(job.Name + ": " + msg);
                }
            }
            finally
            {
                if (src != null) Release(src, broken);
                if (dst != null && dst != src) Release(dst, broken);
                job.Speed = 0;
                job.RaiseProgress();
                lock (_gate) _running--;
                if (job.IsFinished) Ui(() => { Finished?.Invoke(job); Changed?.Invoke(); });
                else Ui(() => Changed?.Invoke());
                Pump();
            }
        }

        private void CopyFile(TransferJob job, FileSystem src, FileSystem dst)
        {
            var token = job.Cts.Token;
            long offset = 0;
            bool append = false;

            var existing = dst.Stat(job.DstPath);
            if (existing != null && existing.IsDir) throw new IOException("A folder with that name already exists");
            if (existing != null)
            {
                bool canResume = existing.Size >= 0 && existing.Size < job.Size;
                if (job.ResumeNext && canResume)
                {
                    offset = existing.Size;
                    append = true;
                }
                else if (!job.ResumeNext)
                {
                    string decision = Decide(job, existing);
                    switch (decision)
                    {
                        case "Skip":
                            job.State = JobState.Skipped;
                            return;
                        case "Newer":
                            if (job.SrcModified != DateTime.MinValue && job.SrcModified <= existing.Modified.AddSeconds(Settings.Current.SyncToleranceSeconds))
                            {
                                job.Error = "the copy there is the same age or newer";
                                job.State = JobState.Skipped;
                                return;
                            }
                            break;
                        case "Resume":
                            if (canResume) { offset = existing.Size; append = true; }
                            else if (existing.Size == job.Size) { job.Transferred = job.Size; job.State = JobState.Done; return; }
                            break;
                        case "Rename":
                            job.DstPath = UniqueName(dst, job.DstPath);
                            break;
                    }
                }
            }
            job.ResumeNext = false;

            string parent = dst.Parent(job.DstPath);
            if (!string.IsNullOrEmpty(parent)) EnsureDir(dst, parent);

            job.Transferred = offset;
            var buffer = new byte[256 * 1024];
            var clock = Stopwatch.StartNew();
            long lastBytes = offset;
            double lastTick = 0;
            Stream rs = null, ws = null;
            bool clean = false;
            try
            {
                rs = src.OpenRead(job.SrcPath, offset);
                ws = dst.OpenWrite(job.DstPath, append);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int n = rs.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    ws.Write(buffer, 0, n);
                    job.Transferred += n;
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - lastTick >= 0.4)
                    {
                        double inst = (job.Transferred - lastBytes) / (now - lastTick);
                        job.Speed = job.Speed <= 0 ? inst : job.Speed * 0.6 + inst * 0.4;
                        lastBytes = job.Transferred;
                        lastTick = now;
                        job.RaiseProgress();
                    }
                }
                ws.Flush();
                clean = true;
            }
            finally
            {
                // After an abort, skip FTP's end-of-transfer handshake: the connection is thrown away instead.
                if (!clean)
                {
                    if (rs is CallbackStream crs) crs.SkipCallback = true;
                    if (ws is CallbackStream cws) cws.SkipCallback = true;
                }
                try { ws?.Dispose(); } catch when (!clean) { }
                try { rs?.Dispose(); } catch when (!clean) { }
            }

            if (job.Size == 0 || job.Transferred > job.Size) job.Size = job.Transferred;
            if (Settings.Current.PreserveTimestamps && job.SrcModified != DateTime.MinValue) dst.SetModified(job.DstPath, job.SrcModified);
            if (job.DeleteSourceAfter) src.DeleteFile(job.SrcPath);
            job.State = JobState.Done;
        }

        /// <summary>Works out what to do when the target already exists, asking the person if the setting says so.</summary>
        private string Decide(TransferJob job, FsItem existing)
        {
            if (!string.IsNullOrEmpty(job.Decision)) return job.Decision;
            lock (_askGate)
            {
                lock (_gate) if (_batchDecision.TryGetValue(job.Batch, out var d)) return d;
                string setting = Settings.Current.WhenFileExists;
                if (setting != "Ask") return setting;
                var options = new[] { "Overwrite", "Skip", "Resume", "Newer", "Rename" };
                int pick = Dialogs.Choose("File already exists",
                    job.DstPath + "\n\nThere: " + Fmt.Size(existing.Size) + ", " + Fmt.Date(existing.Modified) +
                    "\nNew:   " + Fmt.Size(job.Size) + ", " + Fmt.Date(job.SrcModified),
                    new[] { "Overwrite", "Skip", "Resume", "Overwrite if newer", "Keep both" }, out bool remember, "Do this for the rest of these files");
                string choice = pick < 0 ? "Skip" : options[pick];
                if (remember) lock (_gate) _batchDecision[job.Batch] = choice;
                return choice;
            }
        }

        private static string UniqueName(FileSystem fs, string path)
        {
            string dir = fs.Parent(path) ?? "";
            string name = fs.NameOf(path);
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int i = 2; i < 1000; i++)
            {
                string candidate = fs.Combine(dir, stem + " (" + i + ")" + ext);
                if (fs.Stat(candidate) == null) return candidate;
            }
            return fs.Combine(dir, stem + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext);
        }

        private void EnsureDir(FileSystem fs, string dir)
        {
            string key = fs.Key + "|" + dir;
            lock (_knownDirs) if (_knownDirs.Contains(key)) return;
            fs.EnsureDir(dir);
            lock (_knownDirs) _knownDirs.Add(key);
        }

        // ---- Connection pool: transfers use their own connections so the panes stay responsive ----

        private FileSystem Acquire(FileSystem template)
        {
            if (template is LocalFs) return template;
            lock (_pool)
            {
                if (_pool.TryGetValue(template.Key, out var stack))
                {
                    while (stack.Count > 0)
                    {
                        var fs = stack.Pop();
                        if (fs.IsConnected) return fs;
                        fs.Dispose();
                    }
                }
            }
            var fresh = template.CreateSibling();
            fresh.Connect();
            return fresh;
        }

        private void Release(FileSystem fs, bool broken)
        {
            if (fs is LocalFs) return;
            if (broken || !fs.IsConnected) { try { fs.Dispose(); } catch { } return; }
            lock (_pool)
            {
                if (!_pool.TryGetValue(fs.Key, out var stack)) _pool[fs.Key] = stack = new Stack<FileSystem>();
                stack.Push(fs);
            }
        }

        private int _idleTicket;

        private void ScheduleIdleCleanup()
        {
            int ticket = Interlocked.Increment(ref _idleTicket);
            Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ =>
            {
                if (ticket != _idleTicket || IsBusy) return;
                DisposePool();
            });
        }

        public void DisposePool()
        {
            List<FileSystem> all;
            lock (_pool)
            {
                all = _pool.Values.SelectMany(s => s).ToList();
                _pool.Clear();
            }
            foreach (var fs in all) { try { fs.Dispose(); } catch { } }
        }

        private static bool Transient(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is FileNotFoundException || ex is DirectoryNotFoundException) return false;
            string t = ex.GetType().Name;
            if (t.Contains("PermissionDenied") || t.Contains("PathNotFound") || t.Contains("Authentication")) return false;
            return ex is IOException || ex is TimeoutException || ex is System.Net.Sockets.SocketException
                || t.Contains("Connection") || t.Contains("Timeout") || t.Contains("Ssh") || t.Contains("Ftp");
        }

        public static string Friendly(Exception ex)
        {
            var e = ex;
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            string t = e.GetType().Name;
            if (t.Contains("PermissionDenied") || e is UnauthorizedAccessException) return "permission denied";
            if (t.Contains("PathNotFound") || e is FileNotFoundException || e is DirectoryNotFoundException) return "not found";
            if (t.Contains("Authentication")) return "sign-in failed: " + e.Message;
            return e.Message;
        }
    }
}
