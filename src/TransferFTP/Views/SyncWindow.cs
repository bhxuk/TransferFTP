using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TransferFTP
{
    public enum SyncAction { Same, ToRight, ToLeft, MakeDirRight, MakeDirLeft, DeleteRight, DeleteLeft, Conflict }

    /// <summary>One row in the sync preview.</summary>
    public class SyncEntry : INotifyPropertyChanged
    {
        public string Rel { get; set; }
        public FsItem Left { get; set; }
        public FsItem Right { get; set; }
        public SyncAction Action { get; set; }
        public string Reason { get; set; } = "";

        private bool _include;
        public bool Include { get => _include; set { _include = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include))); } }

        public string LeftText => Describe(Left);
        public string RightText => Describe(Right);
        public bool IsDir => (Left ?? Right)?.IsDir == true;

        private static string Describe(FsItem i)
        {
            if (i == null) return "";
            return i.IsDir ? "folder" : Fmt.Size(i.Size) + "   " + Fmt.Date(i.Modified);
        }

        public string ActionText => Action switch
        {
            SyncAction.ToRight => "Copy  →",
            SyncAction.ToLeft => "←  Copy",
            SyncAction.MakeDirRight => "New folder  →",
            SyncAction.MakeDirLeft => "←  New folder",
            SyncAction.DeleteRight => "Delete  →",
            SyncAction.DeleteLeft => "←  Delete",
            SyncAction.Conflict => "Conflict",
            _ => "Same"
        };

        public Brush ActionBrush => Action switch
        {
            SyncAction.DeleteRight => Theme.Red,
            SyncAction.DeleteLeft => Theme.Red,
            SyncAction.Conflict => Theme.Red,
            SyncAction.Same => Theme.Muted,
            SyncAction.ToLeft => Theme.Teal,
            SyncAction.MakeDirLeft => Theme.Teal,
            _ => Theme.Accent
        };

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Compares two folders and shows exactly what would change before anything moves.</summary>
    public class SyncWindow : ThemedWindow
    {
        private readonly FileSystem _left, _right;
        private readonly string _leftPath, _rightPath;
        private readonly ComboBox _direction;
        private readonly ComboBox _compare;
        private readonly CheckBox _subfolders, _deleteExtra, _showSame;
        private readonly TextBox _ignore;
        private readonly ListView _list;
        private readonly TextBlock _summary;
        private readonly FlatButton _run, _scan;
        private List<SyncEntry> _entries = new List<SyncEntry>();
        private ListCollectionView _view;
        private CancellationTokenSource _cts;

        private static readonly string[] Directions = { "Left to right (make the right side match)", "Right to left (make the left side match)", "Both ways (newer file wins)" };

        public SyncWindow(FileSystem left, string leftPath, FileSystem right, string rightPath)
        {
            _left = left; _right = right; _leftPath = leftPath; _rightPath = rightPath;
            Title = "Compare and sync";
            Width = 1080;
            Height = 720;
            MinWidth = 760;
            MinHeight = 480;

            var leftCard = Place("LEFT", left, leftPath);
            var rightCard = Place("RIGHT", right, rightPath);
            var places = new Grid();
            places.ColumnDefinitions.Add(new ColumnDefinition());
            places.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            places.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(rightCard, 2);
            places.Children.Add(leftCard);
            places.Children.Add(rightCard);

            _direction = UI.Combo(Directions, Directions[0], 330);
            _compare = UI.Combo(new[] { "Size and date", "Size only" }, "Size and date", 150);
            _subfolders = new CheckBox { Content = "Include subfolders", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            _deleteExtra = new CheckBox { Content = "Delete files that only exist on the target", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
            _ignore = UI.Input(".git;node_modules;Thumbs.db;.DS_Store;*.tmp", 300);
            _scan = FlatButton.Primary("Compare", Scan);
            var options = new WrapPanel();
            foreach (var el in new UIElement[]
            {
                UI.Field("Direction", _direction), UI.Field("Compare by", _compare),
                UI.Field("Skip these (separate with ;)", _ignore)
            })
            {
                ((FrameworkElement)el).Margin = new Thickness(0, 0, 14, 10);
                options.Children.Add(el);
            }
            var ticks = UI.H(20, _subfolders, _deleteExtra);
            var optRow = new DockPanel();
            DockPanel.SetDock(_scan, Dock.Right);
            _scan.VerticalAlignment = VerticalAlignment.Bottom;
            _scan.Margin = new Thickness(0, 0, 0, 10);
            optRow.Children.Add(_scan);
            optRow.Children.Add(UI.V(4, options, ticks));

            var grid = new GridView();
            grid.Columns.Add(UI.Column("", 40, "<CheckBox IsChecked='{Binding Include, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}' HorizontalAlignment='Center'/>"));
            grid.Columns.Add(UI.Column("File", 360, "<TextBlock Text='{Binding Rel}' TextTrimming='CharacterEllipsis' ToolTip='{Binding Reason}'/>"));
            grid.Columns.Add(UI.Column("Left", 200, "<TextBlock Text='{Binding LeftText}' Foreground='{MUTED}'/>"));
            grid.Columns.Add(UI.Column("Action", 130, "<TextBlock Text='{Binding ActionText}' Foreground='{Binding ActionBrush}' FontWeight='SemiBold' TextAlignment='Center'/>"));
            grid.Columns.Add(UI.Column("Right", 200, "<TextBlock Text='{Binding RightText}' Foreground='{MUTED}'/>"));
            _list = new ListView { View = grid, SelectionMode = SelectionMode.Extended };
            var listCard = new Border { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(6), Child = _list };

            _summary = UI.Text("Press Compare to see what's different. Nothing is copied until you press Sync.", 13, Theme.Muted);
            _summary.VerticalAlignment = VerticalAlignment.Center;
            _showSame = new CheckBox { Content = "Show identical files", VerticalAlignment = VerticalAlignment.Center };
            _showSame.Click += (s, e) => _view?.Refresh();
            _run = FlatButton.Primary("Sync", Run);
            _run.IsEnabled = false;
            var close = FlatButton.Secondary("Close", Close);
            var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var right = UI.H(10, _showSame, close, _run);
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);
            bottom.Children.Add(_summary);

            var root = new DockPanel { Margin = new Thickness(20) };
            var header = UI.V(4, UI.Text("Compare and sync", 22, Theme.Text, FontWeights.SemiBold, true),
                UI.Text("Preview every change first. Untick anything you want to leave alone.", 13, Theme.Muted));
            header.Margin = new Thickness(0, 0, 0, 14);
            places.Margin = new Thickness(0, 0, 0, 14);
            optRow.Margin = new Thickness(0, 0, 0, 6);
            foreach (var el in new UIElement[] { header, places, optRow }) { DockPanel.SetDock(el, Dock.Top); root.Children.Add(el); }
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(listCard);
            Content = root;
            Closed += (s, e) => _cts?.Cancel();
        }

        private static Border Place(string label, FileSystem fs, string path)
        {
            var t = UI.Text(path, 13, Theme.Text, FontWeights.SemiBold);
            t.FontFamily = Theme.Mono;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.TextWrapping = TextWrapping.NoWrap;
            return UI.Card(UI.V(4, UI.Label(label + "  ·  " + fs.Title), t), 14, Theme.Panel, 12);
        }

        private int Mode => Array.IndexOf(Directions, _direction.SelectedItem as string);

        private List<Regex> IgnoreRules()
        {
            return (_ignore.Text ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase))
                .ToList();
        }

        private void Scan()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int mode = Mode;
            bool sizeOnly = (_compare.SelectedItem as string) == "Size only";
            bool recurse = _subfolders.IsChecked == true;
            bool deleteExtra = _deleteExtra.IsChecked == true && mode != 2;
            var ignore = IgnoreRules();
            int tolerance = Math.Max(0, Settings.Current.SyncToleranceSeconds);
            _scan.IsEnabled = false;
            _run.IsEnabled = false;
            _summary.Text = "Comparing...";
            _list.ItemsSource = null;

            Task.Run(() =>
            {
                using var l = _left.CreateSibling();
                using var r = _right.CreateSibling();
                l.Connect();
                r.Connect();
                var result = new List<SyncEntry>();
                Compare(l, _leftPath, r, _rightPath, "", result, mode, sizeOnly, recurse, deleteExtra, ignore, tolerance, token);
                return result;
            }, token).ContinueWith(t =>
            {
                _scan.IsEnabled = true;
                if (t.IsCanceled) return;
                if (t.IsFaulted)
                {
                    _summary.Text = "Couldn't compare: " + TransferQueue.Friendly(t.Exception.GetBaseException());
                    return;
                }
                _entries = t.Result;
                _view = new ListCollectionView(_entries) { Filter = o => _showSame.IsChecked == true || ((SyncEntry)o).Action != SyncAction.Same };
                _list.ItemsSource = _view;
                foreach (var e in _entries) e.PropertyChanged += (s, a) => UpdateSummary();
                UpdateSummary();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Compare(FileSystem l, string lDir, FileSystem r, string rDir, string rel, List<SyncEntry> result,
            int mode, bool sizeOnly, bool recurse, bool deleteExtra, List<Regex> ignore, int tolerance, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Dispatcher.BeginInvoke(new Action(() => _summary.Text = "Comparing " + (rel.Length == 0 ? "top folder" : rel) + "..."));
            var lItems = lDir == null ? new List<FsItem>() : l.List(lDir).Where(i => !i.IsParent && !Ignored(i.Name, ignore)).ToList();
            var rItems = rDir == null ? new List<FsItem>() : r.List(rDir).Where(i => !i.IsParent && !Ignored(i.Name, ignore)).ToList();
            // Windows names aren't case sensitive; servers usually are. Match exactly first, then ignoring case.
            var rByName = new Dictionary<string, FsItem>(StringComparer.Ordinal);
            foreach (var i in rItems) rByName[i.Name] = i;
            var rLower = new Dictionary<string, FsItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in rItems) if (!rLower.ContainsKey(i.Name)) rLower[i.Name] = i;
            var matchedRight = new HashSet<FsItem>();

            foreach (var li in lItems.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                string childRel = rel.Length == 0 ? li.Name : rel + "/" + li.Name;
                if (!rByName.TryGetValue(li.Name, out var ri)) rLower.TryGetValue(li.Name, out ri);
                if (ri != null) matchedRight.Add(ri);
                Pair(l, r, li, ri, childRel, rDir, result, mode, sizeOnly, recurse, deleteExtra, ignore, tolerance, token);
            }
            foreach (var ri in rItems.Where(i => !matchedRight.Contains(i)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                string childRel = rel.Length == 0 ? ri.Name : rel + "/" + ri.Name;
                Pair(l, r, null, ri, childRel, lDir, result, mode, sizeOnly, recurse, deleteExtra, ignore, tolerance, token);
            }
        }

        private void Pair(FileSystem l, FileSystem r, FsItem li, FsItem ri, string rel, string otherDir, List<SyncEntry> result,
            int mode, bool sizeOnly, bool recurse, bool deleteExtra, List<Regex> ignore, int tolerance, CancellationToken token)
        {
            var e = new SyncEntry { Rel = rel, Left = li, Right = ri };
            bool dir = (li ?? ri).IsDir;

            if (li != null && ri != null)
            {
                if (li.IsDir != ri.IsDir) { e.Action = SyncAction.Conflict; e.Reason = "A file on one side and a folder on the other"; result.Add(e); return; }
                if (dir)
                {
                    if (recurse && !li.IsLink && !ri.IsLink)
                        Compare(l, li.FullPath, r, ri.FullPath, rel, result, mode, sizeOnly, recurse, deleteExtra, ignore, tolerance, token);
                    return;
                }
                bool sizeDiff = li.Size != ri.Size;
                double secs = (li.Modified - ri.Modified).TotalSeconds;
                bool timeDiff = !sizeOnly && Math.Abs(secs) > tolerance;
                if (!sizeDiff && !timeDiff) { e.Action = SyncAction.Same; result.Add(e); return; }
                e.Reason = sizeDiff ? "Different sizes" : "Different dates";
                if (mode == 0) e.Action = SyncAction.ToRight;
                else if (mode == 1) e.Action = SyncAction.ToLeft;
                else if (Math.Abs(secs) <= tolerance) { e.Action = SyncAction.Conflict; e.Reason = "Same date but different sizes, so it isn't clear which is newer"; }
                else e.Action = secs > 0 ? SyncAction.ToRight : SyncAction.ToLeft;
                e.Include = e.Action != SyncAction.Conflict;
                result.Add(e);
                return;
            }

            bool onlyLeft = li != null;
            bool copyOut = mode == 2 || (mode == 0 && onlyLeft) || (mode == 1 && !onlyLeft);
            if (copyOut)
            {
                e.Reason = "Only on the " + (onlyLeft ? "left" : "right");
                if (dir)
                {
                    e.Action = onlyLeft ? SyncAction.MakeDirRight : SyncAction.MakeDirLeft;
                    e.Include = true;
                    result.Add(e);
                    if (recurse && !(li ?? ri).IsLink)
                    {
                        if (onlyLeft) Compare(l, li.FullPath, r, null, rel, result, mode, sizeOnly, recurse, deleteExtra, ignore, tolerance, token);
                        else Compare(l, null, r, ri.FullPath, rel, result, mode, sizeOnly, recurse, deleteExtra, ignore, tolerance, token);
                    }
                    return;
                }
                e.Action = onlyLeft ? SyncAction.ToRight : SyncAction.ToLeft;
                e.Include = true;
                result.Add(e);
                return;
            }
            if (deleteExtra)
            {
                e.Action = onlyLeft ? SyncAction.DeleteLeft : SyncAction.DeleteRight;
                e.Reason = "Only on the " + (onlyLeft ? "left" : "right") + ", so it would be deleted to match";
                e.Include = true;
                result.Add(e);
            }
        }

        private static bool Ignored(string name, List<Regex> rules) => rules.Any(r => r.IsMatch(name));

        private void UpdateSummary()
        {
            int toR = _entries.Count(e => e.Include && (e.Action == SyncAction.ToRight || e.Action == SyncAction.MakeDirRight));
            int toL = _entries.Count(e => e.Include && (e.Action == SyncAction.ToLeft || e.Action == SyncAction.MakeDirLeft));
            int del = _entries.Count(e => e.Include && (e.Action == SyncAction.DeleteLeft || e.Action == SyncAction.DeleteRight));
            int same = _entries.Count(e => e.Action == SyncAction.Same);
            int conflicts = _entries.Count(e => e.Action == SyncAction.Conflict);
            long bytes = _entries.Where(e => e.Include && (e.Action == SyncAction.ToRight || e.Action == SyncAction.ToLeft))
                .Sum(e => Math.Max(0, (e.Action == SyncAction.ToRight ? e.Left : e.Right)?.Size ?? 0));
            var parts = new List<string>();
            if (toR > 0) parts.Add(toR + " to the right");
            if (toL > 0) parts.Add(toL + " to the left");
            if (del > 0) parts.Add(del + " to delete");
            if (conflicts > 0) parts.Add(conflicts + " conflict" + (conflicts == 1 ? "" : "s") + " (skipped)");
            parts.Add(same + " identical");
            _summary.Text = (toR + toL + del + conflicts == 0 ? "Everything matches. " : "") + string.Join(", ", parts) + (bytes > 0 ? "  ·  " + Fmt.Size(bytes) + " to copy" : "");
            _run.IsEnabled = toR + toL + del > 0;
        }

        private string Join(FileSystem fs, string root, string rel)
        {
            string p = root;
            foreach (var seg in rel.Split('/')) p = fs.Combine(p, seg);
            return p;
        }

        private void Run()
        {
            var picked = _entries.Where(e => e.Include && e.Action != SyncAction.Same && e.Action != SyncAction.Conflict).ToList();
            int deletes = picked.Count(e => e.Action == SyncAction.DeleteLeft || e.Action == SyncAction.DeleteRight);
            if (deletes > 0 && !Dialogs.Confirm("Delete " + deletes + " item" + (deletes == 1 ? "" : "s") + "?",
                    "The sync will delete files or folders that only exist on the target side. This can't be undone.", "Sync and delete", danger: true)) return;

            string batch = "sync-" + Guid.NewGuid().ToString("N");
            var jobs = new List<TransferJob>();
            foreach (var e in picked)
            {
                switch (e.Action)
                {
                    case SyncAction.ToRight:
                        jobs.Add(new TransferJob { Src = _left, SrcPath = e.Left.FullPath, Dst = _right, DstPath = e.Right?.FullPath ?? Join(_right, _rightPath, e.Rel), Size = Math.Max(0, e.Left.Size), SrcModified = e.Left.Modified, Decision = "Overwrite", Batch = batch, Tag = "sync" });
                        break;
                    case SyncAction.ToLeft:
                        jobs.Add(new TransferJob { Src = _right, SrcPath = e.Right.FullPath, Dst = _left, DstPath = e.Left?.FullPath ?? Join(_left, _leftPath, e.Rel), Size = Math.Max(0, e.Right.Size), SrcModified = e.Right.Modified, Decision = "Overwrite", Batch = batch, Tag = "sync" });
                        break;
                    case SyncAction.MakeDirRight:
                        jobs.Add(new TransferJob { Kind = JobKind.MakeDir, Src = _left, SrcPath = e.Left.FullPath, Dst = _right, DstPath = Join(_right, _rightPath, e.Rel), Batch = batch, Tag = "sync" });
                        break;
                    case SyncAction.MakeDirLeft:
                        jobs.Add(new TransferJob { Kind = JobKind.MakeDir, Src = _right, SrcPath = e.Right.FullPath, Dst = _left, DstPath = Join(_left, _leftPath, e.Rel), Batch = batch, Tag = "sync" });
                        break;
                    case SyncAction.DeleteRight:
                        jobs.Add(new TransferJob { Kind = JobKind.Delete, Src = _right, SrcPath = e.Right.FullPath, IsDir = e.Right.IsDir, Batch = batch, Tag = "sync" });
                        break;
                    case SyncAction.DeleteLeft:
                        jobs.Add(new TransferJob { Kind = JobKind.Delete, Src = _left, SrcPath = e.Left.FullPath, IsDir = e.Left.IsDir, Batch = batch, Tag = "sync" });
                        break;
                }
            }
            // Folders first so the files have somewhere to land.
            TransferQueue.Instance.Add(jobs.Where(j => j.Kind == JobKind.MakeDir));
            TransferQueue.Instance.Add(jobs.Where(j => j.Kind != JobKind.MakeDir));
            Log.Info("Sync queued " + jobs.Count + " changes");
            Close();
        }
    }
}
