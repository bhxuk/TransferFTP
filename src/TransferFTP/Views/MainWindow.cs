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
        private readonly FlatButton _updateBtn;
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
                FlatButton.Tool(Icons.Server, "Sites", "Sites|Add, edit and connect to your saved servers and shares (Ctrl+S)", () => OpenSites(null)),
                FlatButton.Tool(Icons.Sync, "Sync", "Compare and sync|Compare the folders open on the left and right, then copy the differences (preview first)", () => OpenSync(_active ?? _left)),
                FlatButton.Tool(Icons.Terminal, "Terminal", "Terminal|Choose a server and open an SSH terminal on it (F9)", PickTerminal),
                FlatButton.Tool(Icons.Window, null, "New window|Open another TransferFTP window. All windows share one transfer queue (Ctrl+N)", NewWindow),
                FlatButton.Tool(Icons.Settings, null, "Settings|Transfers, editor, theme and updates", OpenSettings));
            // Shown only when a new version is out: one click downloads, installs and restarts.
            _updateBtn = FlatButton.Primary("Update", () => InstallUpdate());
            _updateBtn.MinHeight = 32;
            _updateBtn.Height = 32;
            _updateBtn.Padding = new Thickness(14, 0, 14, 0);
            _updateBtn.Margin = new Thickness(0, 0, 10, 0);
            _updateBtn.Visibility = Visibility.Collapsed;
            Updates.Changed += ShowUpdate;
            ShowUpdate();
            var header = new DockPanel { Margin = new Thickness(18, 12, 18, 10) };
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
            DockPanel.SetDock(_updateBtn, Dock.Right);
            header.Children.Add(_updateBtn);
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
                side.NewTabPrompt = PickNewTab;
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
            Tips.Set(_tabTransfers, "Transfers|Files copying, waiting, finished or failed");
            _tabEdits = Tab("Editing", () => ShowTab(1));
            Tips.Set(_tabEdits, "Editing|Server files open in your editor. Each save uploads automatically");
            _tabLog = Tab("Log", () => ShowTab(2));
            Tips.Set(_tabLog, "Log|Connections, transfers and errors from this session");
            _pauseAll = FlatButton.Tool(Icons.Pause, "Pause all", "Pause all|Pause every transfer. Press again to carry on from where they stopped", TogglePauseAll);
            _queueSummary = UI.Text("", 12, Theme.Muted);
            _queueSummary.VerticalAlignment = VerticalAlignment.Center;
            _queueSummary.Margin = new Thickness(14, 0, 0, 0);
            var queueTools = UI.H(2,
                _pauseAll,
                FlatButton.Tool(Icons.Retry, "Retry failed", "Retry failed|Try every failed transfer again, resuming where possible", () => TransferQueue.Instance.RetryFailed()),
                FlatButton.Tool(Icons.Check, "Clear finished", "Clear finished|Remove finished, skipped and cancelled transfers from the list", () => TransferQueue.Instance.ClearFinished()),
                FlatButton.Tool(Icons.Close, "Cancel all", "Cancel all|Stop every transfer. Files already copied stay where they are", () =>
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
                Updates.Changed -= ShowUpdate;
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

        private void ShowUpdate()
        {
            if (Updates.Installing) return;
            _updateBtn.Visibility = Updates.Available ? Visibility.Visible : Visibility.Collapsed;
            if (!Updates.Available) return;
            _updateBtn.Content = UI.H(8, UI.Icon(Icons.Download, 15, Theme.Dark, 2), UI.Text("Update to " + Updates.LatestVersion, 13, Theme.Dark, FontWeights.SemiBold));
            Tips.Set(_updateBtn, "Update to " + Updates.LatestVersion + "|One click downloads it, installs it and reopens TransferFTP with your tabs as they were. You're on " + Updates.CurrentText + ".");
        }

        private async void InstallUpdate()
        {
            _updateBtn.IsEnabled = false;
            await Updates.Install(s => Dispatcher.BeginInvoke(new Action(() =>
                _updateBtn.Content = UI.Text(s, 13, Theme.Dark, FontWeights.SemiBold))));
            if (!Updates.Installing) { _updateBtn.IsEnabled = true; ShowUpdate(); }
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
            if (FileSystem.IsTopList(a.Fs, a.CurrentPath) ||
                FileSystem.IsTopList(b.Fs, b.CurrentPath))
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

        private HashSet<string> ConnectedSiteIds() =>
            new HashSet<string>(AllPanes.Where(p => p.Fs?.Site != null && p.Fs.IsConnected).Select(p => p.Fs.Site.Id));

        /// <summary>"Where should the new tab open?" This PC, the same place, a saved site, a quick link, or a new site.</summary>
        private void PickNewTab(PaneSide side)
        {
            var current = side.Current;
            var choices = new List<PlaceChoice>
            {
                new PlaceChoice { Kind = PlaceKind.Local, Title = "This PC", Detail = "Your drives and folders", Group = "Start", Icon = Icons.Folder },
            };
            if (current?.Fs != null)
                choices.Add(new PlaceChoice { Kind = PlaceKind.Duplicate, Title = "Same as this tab", Detail = current.TabTitle + (string.IsNullOrEmpty(current.CurrentPath) ? "" : "  \u00b7  " + current.CurrentPath), Group = "Start", Icon = Icons.Copy });
            choices.AddRange(PlacePickerWindow.Sites(s => true, ConnectedSiteIds(), "Saved sites"));
            foreach (var q in Settings.Current.QuickLinks.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                string where = q.Source == "local" ? "This PC" : SiteStore.Find(q.Source)?.Name;
                if (where == null) continue;
                choices.Add(new PlaceChoice { Kind = PlaceKind.QuickLink, Title = q.Name, Detail = where + "  \u00b7  " + q.Path, Group = "Quick links", Icon = Icons.Star, Source = q.Source, Path = q.Path });
            }
            choices.Add(new PlaceChoice { Kind = PlaceKind.NewSite, Title = "New connection...", Detail = "Add a server or network share and open it in the new tab", Group = "New", Icon = Icons.Plus });

            var w = new PlacePickerWindow("New tab", "Choose what to open in the new tab on the " + side.Side + " side.", choices, "Open");
            if (w.ShowDialog() != true || w.Picked == null) return;
            var c = w.Picked;
            switch (c.Kind)
            {
                case PlaceKind.Local: side.AddTab().OpenLocal(null); break;
                case PlaceKind.Duplicate: side.Duplicate(); break;
                case PlaceKind.Site: side.AddTab().Connect(c.Site, null); break;
                case PlaceKind.QuickLink: side.AddTab().OpenPlace(c.Source, c.Path); break;
                case PlaceKind.NewSite:
                    new SiteManagerWindow(startNew: true, actionLabel: "Open in new tab",
                        actionTip: "Open in new tab|Save this site, then connect to it in a new tab",
                        action: site => { var t = side.AddTab(); t.Connect(site, null); SetActive(t); }).ShowDialog();
                    return;
            }
            SetActive(side.Current);
            side.Current.FocusList();
        }

        /// <summary>"Which server?" for the Terminal button: any saved SFTP or SCP site, or a new one.</summary>
        private void PickTerminal()
        {
            var connected = ConnectedSiteIds();
            var choices = PlacePickerWindow.Sites(s => s.IsSsh, connected, "SSH servers").ToList();
            choices.Add(new PlaceChoice { Kind = PlaceKind.NewSite, Title = "New SSH connection...", Detail = "Add a server you sign in to with SSH, then open a terminal", Group = "New", Icon = Icons.Plus });
            var preselectSite = new[] { _active, _left, _right }.FirstOrDefault(p => p?.Fs?.Site != null && p.Fs.Site.IsSsh)?.Fs.Site;
            var preselect = choices.FirstOrDefault(c => c.Site != null && c.Site.Id == preselectSite?.Id);
            string subtitle = choices.Count == 1
                ? "You don't have any SFTP or SCP sites yet. Add one to open a terminal on it."
                : "Pick the server to open an SSH terminal on.";
            var w = new PlacePickerWindow("Open a terminal", subtitle, choices, "Open terminal", preselect);
            if (w.ShowDialog() != true || w.Picked == null) return;
            if (w.Picked.Kind == PlaceKind.NewSite)
            {
                new SiteManagerWindow(startNew: true, newProtocol: Protocol.SFTP, actionLabel: "Open terminal",
                    actionTip: "Open terminal|Save this site, then open an SSH terminal on it",
                    action: site => new TerminalWindow(site, string.IsNullOrWhiteSpace(site.RemoteFolder) ? null : site.RemoteFolder).Show()).ShowDialog();
                return;
            }
            var siteChosen = w.Picked.Site;
            // Start in the folder already open for that server, if there is one.
            var open = new[] { _active }.Concat(AllPanes).FirstOrDefault(p => p?.Fs?.Site?.Id == siteChosen.Id && p.Fs.IsConnected);
            string dir = open?.CurrentPath ?? (string.IsNullOrWhiteSpace(siteChosen.RemoteFolder) ? null : siteChosen.RemoteFolder);
            new TerminalWindow(siteChosen, dir).Show();
        }

        private void OpenTerminal(FilePane from)
        {
            // The terminal button inside a pane opens straight onto that pane's server; otherwise ask which one.
            if (from?.Fs?.Site == null || !from.Fs.Site.IsSsh) { PickTerminal(); return; }
            var pane = from;
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
            else if (e.Key == Key.F9) PickTerminal();
            else if (ctrl && e.Key == Key.T) ActiveSide.NewTab();
            else if (ctrl && e.Key == Key.W) ActiveSide.CloseTab(ActiveSide.Current);
            else if (ctrl && e.Key == Key.Tab) ActiveSide.Cycle(shift ? -1 : 1);
            else if (ctrl && e.Key == Key.N) NewWindow();
            else return;
            e.Handled = true;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool last = _openWindows <= 1;
            if (last && !Updates.Installing && TransferQueue.Instance.IsBusy &&
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
