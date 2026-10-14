using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TransferFTP
{
    public class LogLine
    {
        public string Time { get; set; }
        public string Text { get; set; }
        public Brush Color { get; set; }
    }

    /// <summary>Two file panes side by side, with the transfer queue underneath.</summary>
    public class MainWindow : ThemedWindow
    {
        private readonly PaneSide _leftSide, _rightSide;
        private FilePane _active;
        private readonly bool _primary;

        /// <summary>How many main windows are open. Shared clean-up only happens when the last one closes.</summary>
        private static int _openWindows;

        private FilePane _left => _leftSide.Current;
        private FilePane _right => _rightSide.Current;
        private IEnumerable<FilePane> AllPanes => _leftSide.Tabs.Concat(_rightSide.Tabs);
        private readonly ListView _queue;
        private readonly ListView _edits;
        private readonly ListBox _log;
        private readonly ObservableCollection<LogLine> _logLines = new ObservableCollection<LogLine>();
        private readonly TextBlock _queueSummary;
        private readonly FlatButton _pauseAll;
        private readonly FlatButton _tabTransfers, _tabEdits, _tabLog;
        private readonly Grid _bottomHost;
        private readonly RowDefinition _queueRow;
        private readonly DispatcherTimer _tick;

        public MainWindow(bool primary = true) : base(false)
        {
            _primary = primary;
            _openWindows++;
            var s = Settings.Current;
            Title = "TransferFTP";
            Width = Math.Max(900, s.WindowWidth);
            Height = Math.Max(600, s.WindowHeight);
            MinWidth = 900;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (s.WindowMaximized && primary) WindowState = WindowState.Maximized;
            if (!primary)
            {
                // Open new windows a little offset from the one they came from.
                WindowStartupLocation = WindowStartupLocation.Manual;
                var from = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
                if (from != null && from.WindowState == WindowState.Normal) { Left = from.Left + 36; Top = from.Top + 36; Width = from.Width; Height = from.Height; }
                else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            // Header
            var logo = UI.H(10, new Image { Source = Theme.AppIcon(), Width = 26, Height = 26 }, UI.Text("TransferFTP", 18, Theme.Text, FontWeights.Bold, true));
            logo.VerticalAlignment = VerticalAlignment.Center;
            var actions = UI.H(4,
                FlatButton.Tool(Icons.Server, "Sites", "Saved sites (Ctrl+S)", () => OpenSites(null)),
                FlatButton.Tool(Icons.Sync, "Sync", "Compare and sync the two folders", () => OpenSync(_active ?? _left)),
                FlatButton.Tool(Icons.Terminal, "Terminal", "SSH terminal for the active side (F9)", () => OpenTerminal(_active ?? _left)),
                FlatButton.Tool(Icons.Window, null, "New window (Ctrl+N)", NewWindow),
                FlatButton.Tool(Icons.Settings, null, "Settings", OpenSettings));
            var header = new DockPanel { Margin = new Thickness(18, 12, 18, 10) };
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
            header.Children.Add(logo);

            // Panes
            _leftSide = new PaneSide("left");
            _rightSide = new PaneSide("right");
            _leftSide.OtherCurrent = () => _rightSide.Current;
            _rightSide.OtherCurrent = () => _leftSide.Current;
            foreach (var side in new[] { _leftSide, _rightSide })
            {
                side.Wire = p =>
                {
                    p.Activated += SetActive;
                    p.RequestSync = OpenSync;
                    p.RequestTerminal = OpenTerminal;
                    p.RequestSiteManager = () => OpenSites(null);
                };
                side.CurrentChanged += SetActive;
            }
            var panes = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            var vsplit = new GridSplitter { Width = 12, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = Cursors.SizeWE };
            Grid.SetColumn(vsplit, 1);
            Grid.SetColumn(_rightSide, 2);
            panes.Children.Add(_leftSide);
            panes.Children.Add(vsplit);
            panes.Children.Add(_rightSide);

            // Bottom: transfers, editing, log
            _queue = new ListView { SelectionMode = SelectionMode.Extended, ItemsSource = TransferQueue.Instance.Jobs };
            var qv = new GridView();
            qv.Columns.Add(UI.Column("File", 380,
                "<DockPanel>" +
                "<Viewbox Width='16' Height='16' Margin='4,0,10,0' DockPanel.Dock='Left'><Canvas Width='24' Height='24'>" +
                "<Path Data='{Binding DirectionIcon}' Stroke='{Binding StatusBrush}' StrokeThickness='1.8' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>" +
                "</Canvas></Viewbox>" +
                "<StackPanel ToolTip='{Binding FullDetail}'>" +
                "<TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis'/>" +
                "<TextBlock Text='{Binding Detail}' Foreground='{MUTED}' FontSize='11' TextTrimming='CharacterEllipsis'/>" +
                "</StackPanel></DockPanel>"));
            qv.Columns.Add(UI.Column("Size", 80, "<TextBlock Text='{Binding SizeText}' Foreground='{MUTED}' TextAlignment='Right' Margin='0,0,6,0'/>"));
            qv.Columns.Add(UI.Column("Progress", 180, "<ProgressBar Value='{Binding Percent, Mode=OneWay}' Maximum='100' Foreground='{Binding StatusBrush}' VerticalAlignment='Center' Margin='4,0,8,0'/>"));
            qv.Columns.Add(UI.Column("Speed", 90, "<TextBlock Text='{Binding SpeedText}' Foreground='{MUTED}'/>"));
            qv.Columns.Add(UI.Column("Time left", 80, "<TextBlock Text='{Binding EtaText}' Foreground='{MUTED}'/>"));
            qv.Columns.Add(UI.Column("Status", 260, "<TextBlock Text='{Binding StatusText}' Foreground='{Binding StatusBrush}' TextTrimming='CharacterEllipsis' ToolTip='{Binding StatusText}'/>"));
            _queue.View = qv;
            _queue.ContextMenuOpening += (o, e) => _queue.ContextMenu = QueueMenu();
            _queue.ContextMenu = new ContextMenu();
            _queue.KeyDown += (o, e) =>
            {
                if (e.Key == Key.Delete) { foreach (var j in SelectedJobs()) TransferQueue.Instance.Cancel(j); e.Handled = true; }
            };

            _edits = new ListView { ItemsSource = EditManager.Sessions };
            var ev = new GridView();
            ev.Columns.Add(UI.Column("File", 260, "<TextBlock Text='{Binding Name}' FontWeight='SemiBold'/>"));
            ev.Columns.Add(UI.Column("Saves back to", 460, "<TextBlock Text='{Binding Where}' Foreground='{MUTED}' TextTrimming='CharacterEllipsis'/>"));
            ev.Columns.Add(UI.Column("", 140, "<Button Content='Stop watching' Tag='stop' Cursor='Hand' Foreground='{TEXT}' Background='Transparent' BorderThickness='0' Padding='8,2'/>"));
            _edits.View = ev;
            _edits.AddHandler(Button.ClickEvent, new RoutedEventHandler((o, e) =>
            {
                if (e.OriginalSource is Button b && b.DataContext is EditSession es) EditManager.Stop(es);
            }));

            _log = new ListBox { ItemsSource = _logLines, FontFamily = Theme.Mono, FontSize = 12 };
            _log.ItemTemplate = UI.Template(
                "<StackPanel Orientation='Horizontal'>" +
                "<TextBlock Text='{Binding Time}' Foreground='{MUTED}' Margin='0,0,12,0'/>" +
                "<TextBlock Text='{Binding Text}' Foreground='{Binding Color}' TextWrapping='Wrap'/>" +
                "</StackPanel>");
            Log.Line += OnLogLine;

            _tabTransfers = Tab("Transfers", () => ShowTab(0));
            _tabEdits = Tab("Editing", () => ShowTab(1));
            _tabLog = Tab("Log", () => ShowTab(2));
            _pauseAll = FlatButton.Tool(Icons.Pause, "Pause all", "Pause or resume every transfer", TogglePauseAll);
            _queueSummary = UI.Text("", 12, Theme.Muted);
            _queueSummary.VerticalAlignment = VerticalAlignment.Center;
            _queueSummary.Margin = new Thickness(14, 0, 0, 0);
            var queueTools = UI.H(2,
                _pauseAll,
                FlatButton.Tool(Icons.Retry, "Retry failed", "Try failed transfers again", () => TransferQueue.Instance.RetryFailed()),
                FlatButton.Tool(Icons.Check, "Clear finished", "Remove finished transfers from the list", () => TransferQueue.Instance.ClearFinished()),
                FlatButton.Tool(Icons.Close, "Cancel all", "Cancel all transfers", () =>
                {
                    if (TransferQueue.Instance.ActiveCount == 0 || Dialogs.Confirm("Cancel all transfers?", "Files already copied stay where they are.", "Cancel all")) TransferQueue.Instance.CancelAll();
                }));
            var bottomBar = new DockPanel { Margin = new Thickness(10, 8, 10, 4) };
            DockPanel.SetDock(queueTools, Dock.Right);
            bottomBar.Children.Add(queueTools);
            bottomBar.Children.Add(UI.H(4, _tabTransfers, _tabEdits, _tabLog, _queueSummary));

            _bottomHost = new Grid { Margin = new Thickness(6, 0, 6, 6) };
            _bottomHost.Children.Add(_queue);
            _bottomHost.Children.Add(_edits);
            _bottomHost.Children.Add(_log);
            var bottom = new DockPanel();
            DockPanel.SetDock(bottomBar, Dock.Top);
            bottom.Children.Add(bottomBar);
            bottom.Children.Add(_bottomHost);
            var bottomCard = new Border { Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = bottom, Margin = new Thickness(14, 0, 14, 14) };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            _queueRow = new RowDefinition { Height = new GridLength(Math.Max(120, s.QueueHeight)), MinHeight = 110 };
            root.RowDefinitions.Add(_queueRow);
            var hsplit = new GridSplitter { Height = 12, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = GridResizeDirection.Rows, Cursor = Cursors.SizeNS };
            Grid.SetRow(panes, 1);
            Grid.SetRow(hsplit, 2);
            Grid.SetRow(bottomCard, 3);
            root.Children.Add(header);
            root.Children.Add(panes);
            root.Children.Add(hsplit);
            root.Children.Add(bottomCard);
            Content = root;

            ShowTab(0);
            TransferQueue.Instance.Finished += OnJobFinished;
            TransferQueue.Instance.Changed += UpdateSummary;
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (o, e) => UpdateSummary();
            _tick.Start();

            PreviewKeyDown += GlobalKeys;
            Loaded += (o, e) => RestoreSession();
            Closing += OnClosing;
            Closed += (o, e) =>
            {
                Log.Line -= OnLogLine;
                TransferQueue.Instance.Finished -= OnJobFinished;
                TransferQueue.Instance.Changed -= UpdateSummary;
                _tick.Stop();
                // Keep dialogs centred on a window that's still open.
                if (Application.Current.MainWindow == this)
                    Application.Current.MainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w != this);
            };
        }

        private void OnLogLine(DateTime time, string text, bool error)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _logLines.Add(new LogLine { Time = time.ToString("HH:mm:ss"), Text = text, Color = error ? Theme.Red : Theme.Text });
                while (_logLines.Count > 2000) _logLines.RemoveAt(0);
                _log.ScrollIntoView(_logLines[_logLines.Count - 1]);
                if (error) _tabLog.Content = "Log (!)";
            }));
        }

        private void NewWindow()
        {
            var w = new MainWindow(false);
            w.Show();
            w.Activate();
        }

        private static FlatButton Tab(string text, Action click)
        {
            var b = new FlatButton { Content = text, Background = Brushes.Transparent, MinHeight = 30, Height = 30, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(15) };
            b.Click += (o, e) => click();
            return b;
        }

        private void ShowTab(int i)
        {
            _queue.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _edits.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            _log.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            _tabTransfers.Background = i == 0 ? Theme.AccentTint : Brushes.Transparent;
            _tabEdits.Background = i == 1 ? Theme.AccentTint : Brushes.Transparent;
            _tabLog.Background = i == 2 ? Theme.AccentTint : Brushes.Transparent;
            if (i == 2) _tabLog.Content = "Log";
        }

        private void SetActive(FilePane p)
        {
            _active = p;
            foreach (var pane in AllPanes) pane.SetActive(pane == p);
        }

        private PaneSide ActiveSide => _active != null && _rightSide.Tabs.Contains(_active) ? _rightSide : _leftSide;

        private void RestoreSession()
        {
            var s = Settings.Current;
            if (_primary)
            {
                // Tabs from last time, or the single folder saved by version 1.0.0.
                var left = s.LeftTabs ?? new List<TabState> { new TabState { Source = s.LeftSource ?? "local", Path = s.LeftPath ?? "" } };
                var right = s.RightTabs ?? new List<TabState> { new TabState { Source = s.RightSource ?? "local", Path = s.RightPath ?? "" } };
                _leftSide.Restore(left, s.LeftActiveTab);
                _rightSide.Restore(right, s.RightActiveTab);
            }
            else
            {
                _leftSide.Restore(null, 0);
                _rightSide.Restore(null, 0);
            }
            SetActive(_left);
            _left.FocusList();
        }

        private void UpdateSummary()
        {
            var q = TransferQueue.Instance;
            int active = q.ActiveCount;
            double speed = q.TotalSpeed;
            _queueSummary.Text = active == 0 ? (q.Jobs.Count == 0 ? "Nothing transferring" : "All done") :
                active + " to go" + (speed > 0 ? "  ·  " + Fmt.Speed(speed) : "");
            _tabTransfers.Content = active > 0 ? "Transfers (" + active + ")" : "Transfers";
            _tabEdits.Content = EditManager.Sessions.Count > 0 ? "Editing (" + EditManager.Sessions.Count + ")" : "Editing";
            _pauseAll.Content = UI.H(8, UI.Icon(q.PausedAll ? Icons.Play : Icons.Pause, 16, Theme.Text),
                UI.Text(q.PausedAll ? "Resume all" : "Pause all", 13, Theme.Text, FontWeights.SemiBold));
            Title = active > 0 ? "TransferFTP (" + active + ")" : "TransferFTP";
        }

        private void TogglePauseAll()
        {
            if (TransferQueue.Instance.PausedAll) TransferQueue.Instance.ResumeAll(); else TransferQueue.Instance.PauseAll();
            UpdateSummary();
        }

        private System.Collections.Generic.List<TransferJob> SelectedJobs() => _queue.SelectedItems.Cast<TransferJob>().ToList();

        private ContextMenu QueueMenu()
        {
            var m = new ContextMenu();
            var sel = SelectedJobs();
            void Item(string header, Action a, bool enabled)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled };
                mi.Click += (o, e) => a();
                m.Items.Add(mi);
            }
            var q = TransferQueue.Instance;
            Item("Pause", () => sel.ForEach(q.Pause), sel.Any(j => j.State == JobState.Running || j.State == JobState.Queued));
            Item("Resume", () => sel.ForEach(q.Resume), sel.Any(j => j.State == JobState.Paused));
            Item("Retry", () => sel.ForEach(q.Retry), sel.Any(j => j.State == JobState.Failed || j.State == JobState.Cancelled));
            Item("Cancel", () => sel.ForEach(q.Cancel), sel.Any(j => j.IsActive));
            m.Items.Add(new Separator());
            Item("Copy error details", () =>
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, sel.Where(j => j.State == JobState.Failed).Select(j => j.FullDetail + "  " + j.Error))); } catch { }
            }, sel.Any(j => j.State == JobState.Failed));
            Item("Clear finished", q.ClearFinished, true);
            return m;
        }

        private void OnJobFinished(TransferJob j)
        {
            foreach (var pane in AllPanes)
            {
                var fs = pane.Fs;
                if (fs == null) continue;
                bool hit = false;
                if (j.Dst != null && j.Dst.Key == fs.Key && j.DstPath != null && Touches(fs, pane.CurrentPath, j.DstPath)) hit = true;
                if (j.Src != null && j.Src.Key == fs.Key && (j.Kind == JobKind.Delete || j.DeleteSourceAfter) && Touches(fs, pane.CurrentPath, j.SrcPath)) hit = true;
                if (hit) pane.RefreshSoon();
            }
            if (j.Tag == "edit" && j.State == JobState.Done) Log.Info("Saved " + j.Name + " to " + j.Dst.Title);
        }

        private static bool Touches(FileSystem fs, string current, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string parent = fs.Parent(path);
            return string.Equals(parent?.TrimEnd('/', '\\'), current?.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        public void OpenSites(Site select)
        {
            var w = new SiteManagerWindow(select)
            {
                ConnectRequested = (site, sideName) =>
                {
                    var side = sideName == "right" ? _rightSide : _leftSide;
                    var pane = side.Current;
                    // Keep an existing connection open: put a different site in a new tab instead of replacing it.
                    if (pane.Fs?.Site != null && pane.Fs.IsConnected && pane.Fs.Site.Id != site.Id) pane = side.AddTab();
                    pane.Connect(site, null);
                    SetActive(pane);
                    if (!string.IsNullOrWhiteSpace(site.LocalFolder) && System.IO.Directory.Exists(site.LocalFolder))
                        pane.Other.OpenLocal(site.LocalFolder);
                }
            };
            w.ShowDialog();
        }

        private void OpenSync(FilePane from)
        {
            var a = _left;
            var b = _right;
            if (a.Fs == null || b.Fs == null) { Dialogs.Info("Sync", "Open a folder on both sides first."); return; }
            if ((a.Fs is LocalFs && !(a.Fs is SmbFs) && string.IsNullOrEmpty(a.CurrentPath)) ||
                (b.Fs is LocalFs && !(b.Fs is SmbFs) && string.IsNullOrEmpty(b.CurrentPath)))
            {
                Dialogs.Info("Sync", "Open a folder on both sides (not the drive list), then compare them.");
                return;
            }
            if ((a.Fs.Site != null && !a.Fs.IsConnected) || (b.Fs.Site != null && !b.Fs.IsConnected))
            {
                Dialogs.Info("Sync", "Connect both sides first.");
                return;
            }
            new SyncWindow(a.Fs, a.CurrentPath, b.Fs, b.CurrentPath).Show();
        }

        private void OpenTerminal(FilePane from)
        {
            var pane = from?.Fs?.Site != null && from.Fs.Site.IsSsh ? from : new[] { _active, _left, _right }.FirstOrDefault(p => p?.Fs?.Site != null && p.Fs.Site.IsSsh);
            if (pane == null)
            {
                Dialogs.Info("Terminal", "Connect one side to an SFTP or SCP site to open a terminal on it.");
                return;
            }
            new TerminalWindow(pane.Fs.Site, pane.CurrentPath).Show();
        }

        private void OpenSettings()
        {
            new SettingsWindow().ShowDialog();
            foreach (var p in AllPanes) p.Refresh();
        }

        private void GlobalKeys(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && e.Key == Key.S) OpenSites(null);
            else if (e.Key == Key.F9) OpenTerminal(_active);
            else if (ctrl && e.Key == Key.T) ActiveSide.Duplicate();
            else if (ctrl && e.Key == Key.W) ActiveSide.CloseTab(ActiveSide.Current);
            else if (ctrl && e.Key == Key.Tab) ActiveSide.Cycle(shift ? -1 : 1);
            else if (ctrl && e.Key == Key.N) NewWindow();
            else return;
            e.Handled = true;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool last = _openWindows <= 1;
            if (last && TransferQueue.Instance.IsBusy &&
                !Dialogs.Confirm("Transfers are still running", "Closing now stops them. Paused and unfinished files can be resumed next time by copying them again.", "Close anyway"))
            {
                e.Cancel = true;
                return;
            }
            _openWindows--;
            var s = Settings.Current;
            if (_primary)
            {
                s.WindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; }
                s.QueueHeight = _queueRow.ActualHeight;
                s.LeftTabs = _leftSide.State();
                s.RightTabs = _rightSide.State();
                s.LeftActiveTab = _leftSide.Tabs.IndexOf(_leftSide.Current);
                s.RightActiveTab = _rightSide.Tabs.IndexOf(_rightSide.Current);
            }
            s.Save();
            if (last)
            {
                TransferQueue.Instance.CancelAll();
                TransferQueue.Instance.DisposePool();
                EditManager.StopAll();
            }
            _leftSide.CloseAll();
            _rightSide.CloseAll();
        }
    }
}
