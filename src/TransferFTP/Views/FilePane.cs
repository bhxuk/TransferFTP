using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TransferFTP
{
    /// <summary>What travels with a drag between panes.</summary>
    public class DragPayload
    {
        public FilePane Pane;
        public List<FsItem> Items;
    }

    /// <summary>One side of the window: a place (this PC or a site), a folder path and its file list.</summary>
    public class FilePane : Border
    {
        public const string DragFormat = "TransferFTP.Items";

        public FileSystem Fs { get; private set; }
        public string CurrentPath { get; private set; } = "";
        public Func<FilePane> OtherProvider;
        public FilePane Other => OtherProvider?.Invoke();

        /// <summary>Raised when the place or folder changes, so the tab can update its title.</summary>
        public event Action<FilePane> TitleChanged;

        /// <summary>Asks the side to open a quick link or folder in a new tab.</summary>
        public Action<string, string> RequestNewTab;

        /// <summary>"local" or the site id: the key quick links and recent folders are stored under.</summary>
        public string SourceKey => Fs?.Site?.Id ?? "local";

        public string TabTitle
        {
            get
            {
                if (Fs == null) return "New tab";
                string folder = string.IsNullOrEmpty(CurrentPath) ? "" : Fs.NameOf(CurrentPath);
                if (string.IsNullOrEmpty(folder)) folder = CurrentPath;
                if (Fs.Site == null) return string.IsNullOrEmpty(CurrentPath) ? "This PC" : folder;
                return Fs.Site.Name + (string.IsNullOrEmpty(folder) ? "" : ":  " + folder);
            }
        }
        public string Side { get; }

        public event Action<FilePane> Activated;
        public Action<FilePane> RequestSync;
        public Action<FilePane> RequestTerminal;
        public Action RequestSiteManager;

        private readonly ComboBox _source;
        private readonly FlatButton _connectBtn;
        private readonly FlatButton _terminalBtn;
        private readonly TextBox _path;
        private readonly TextBox _filter;
        private readonly ListView _list;
        private readonly GridView _grid;
        private readonly GridViewColumn _permsCol;
        private readonly TextBlock _status;
        private readonly FlatButton _sizeBtn;
        private readonly TextBlock _spaceText;
        private readonly Border _spaceBar, _spaceFill;
        private readonly StackPanel _spaceBox;
        private int _spaceTicket;
        private System.Threading.CancellationTokenSource _sizeCts;
        private bool _sizing;
        private long _treeTotal = -1;       // folder size including subfolders, once worked out
        private string _sizeProgress;
        private string _flash;
        private DispatcherTimer _flashTimer;

        /// <summary>Shows a short message in the status bar for a few seconds.</summary>
        private void Flash(string text)
        {
            _flash = text;
            _flashTimer?.Stop();
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _flashTimer.Tick += (s, e) => { _flashTimer.Stop(); _flash = null; UpdateStatus(); };
            _flashTimer.Start();
            UpdateStatus();
        }
        private readonly TextBlock _conn;
        private readonly Border _overlay;
        private readonly TextBlock _overlayText;
        private readonly FlatButton _overlayRetry;
        private readonly Border _accentBar;
        private readonly WrapPanel _links;
        private readonly Stack<string> _back = new Stack<string>();
        private List<FsItem> _items = new List<FsItem>();
        private ListCollectionView _view;
        private string _sortBy = "Name";
        private bool _sortDesc;
        private Point _dragStart;
        private bool _dragArmed;
        private bool _suppressSourceChange;
        private int _loadTicket;
        private readonly DispatcherTimer _refreshTimer;

        public FilePane(string side)
        {
            Side = side;
            Background = Theme.Panel;
            BorderBrush = Theme.Line;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(14);
            Padding = new Thickness(0);
            ClipToBounds = true;

            // Row 1: where we are connected
            _source = new ComboBox { MinWidth = 220, FontSize = 13 };
            Tips.Set(_source, "Place|Choose This PC or one of your saved sites to show on this side");
            _source.SelectionChanged += (s, e) => { if (!_suppressSourceChange) SourcePicked(); };
            _connectBtn = FlatButton.Tool(Icons.Plug, "Connect", "Connect or disconnect|Close the connection to this site, or open it again", () => ToggleConnection());
            _terminalBtn = FlatButton.Tool(Icons.Terminal, null, "Terminal on this server|Open an SSH terminal in the folder you're looking at", () => RequestTerminal?.Invoke(this));
            _conn = UI.Text("", 12, Theme.Muted);
            _conn.VerticalAlignment = VerticalAlignment.Center;
            _conn.TextTrimming = TextTrimming.CharacterEllipsis;
            _conn.TextWrapping = TextWrapping.NoWrap;
            var top = new DockPanel { Margin = new Thickness(12, 12, 12, 6), LastChildFill = true };
            var topRight = UI.H(4, _terminalBtn, _connectBtn);
            DockPanel.SetDock(_source, Dock.Left);
            DockPanel.SetDock(topRight, Dock.Right);
            top.Children.Add(_source);
            top.Children.Add(topRight);
            _conn.Margin = new Thickness(12, 0, 8, 0);
            top.Children.Add(_conn);

            // Row 2: navigation and path
            _path = new TextBox { Height = 32, FontSize = 13, FontFamily = Theme.Mono };
            Tips.Set(_path, "Folder path|Type or paste a path and press Enter to go there (Ctrl+L)");
            _path.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Navigate(_path.Text.Trim()); e.Handled = true; } };
            var filterBox = new SearchBox("Filter this folder", 180);
            Tips.Set(filterBox, "Filter|Type part of a name to show only matching files. Wildcards like *.jpg work too (Ctrl+F)");
            _filter = filterBox.Box;
            _filter.TextChanged += (s, e) => _view?.Refresh();
            var nav = new DockPanel { Margin = new Thickness(12, 0, 12, 8) };
            var navLeft = UI.H(2,
                FlatButton.Tool(Icons.Back, null, "Back|Go to the folder you were in before (Alt+Left)", GoBack),
                FlatButton.Tool(Icons.Up, null, "Up a folder|Open the folder that contains this one (Backspace)", GoUp),
                FlatButton.Tool(Icons.Home, null, "Home|Go to your home folder, or the site's start folder", GoHome),
                FlatButton.Tool(Icons.Refresh, null, "Refresh|Reload the list to see the latest files (Ctrl+R)", Refresh),
                FlatButton.Tool(Icons.FolderPlus, null, "New folder|Create a folder here (F7)", NewFolder),
                FlatButton.Tool(Icons.Star, null, "Quick links|Jump to a saved folder or a recent one, or save this folder (Ctrl+D)", ShowLinksMenu));
            navLeft.Margin = new Thickness(0, 0, 8, 0);
            filterBox.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(navLeft, Dock.Left);
            DockPanel.SetDock(filterBox, Dock.Right);
            nav.Children.Add(navLeft);
            nav.Children.Add(filterBox);
            nav.Children.Add(_path);

            // Quick links for this place, as chips under the path
            _links = new WrapPanel { Margin = new Thickness(12, 0, 12, 6) };

            // The list
            _grid = new GridView { AllowsColumnReorder = false };
            _grid.Columns.Add(UI.Column("Name", 300, NameCell));
            _grid.Columns.Add(UI.Column("Size", 90, "<TextBlock Text='{Binding SizeText}' TextAlignment='Right' Foreground='{MUTED}' Margin='0,0,6,0'/>"));
            _grid.Columns.Add(UI.Column("Modified", 135, "<TextBlock Text='{Binding ModifiedText}' Foreground='{MUTED}'/>"));
            _grid.Columns.Add(UI.Column("Type", 90, "<TextBlock Text='{Binding Kind}' Foreground='{MUTED}' TextTrimming='CharacterEllipsis'/>"));
            _permsCol = UI.Column("Permissions", 100, "<TextBlock Text='{Binding Perms}' Foreground='{MUTED}' FontFamily='Cascadia Mono, Consolas'/>");
            _list = new ListView { View = _grid, SelectionMode = SelectionMode.Extended, AllowDrop = true, Margin = new Thickness(6, 0, 6, 0) };
            TextSearch.SetTextPath(_list, "Name");
            _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(HeaderClicked));
            _list.MouseDoubleClick += (s, e) => { var it = ItemAt(e.OriginalSource); if (it != null) Activate(it); };
            _list.PreviewKeyDown += ListKeyDown;
            _list.SelectionChanged += (s, e) => UpdateStatus();
            _list.GotKeyboardFocus += (s, e) => Activated?.Invoke(this);
            _list.PreviewMouseLeftButtonDown += (s, e) =>
            {
                Activated?.Invoke(this);
                _dragStart = e.GetPosition(null);
                _dragArmed = ItemAt(e.OriginalSource) != null;
            };
            _list.PreviewMouseMove += (s, e) =>
            {
                if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragArmed = false;
                StartDrag();
            };
            _list.PreviewMouseLeftButtonUp += (s, e) => _dragArmed = false;
            _list.DragOver += OnDragOver;
            _list.DragEnter += OnDragOver;
            _list.Drop += OnDrop;
            _list.ContextMenuOpening += (s, e) => { _list.ContextMenu = BuildMenu(); };
            _list.ContextMenu = new ContextMenu();

            _overlayText = UI.Text("", 13, Theme.Muted);
            _overlayText.TextAlignment = TextAlignment.Center;
            _overlayRetry = FlatButton.Secondary("Try again", Refresh, "Try again|Connect or load this folder again");
            _overlayRetry.HorizontalAlignment = HorizontalAlignment.Center;
            var overlayStack = UI.V(12, _overlayText, _overlayRetry);
            overlayStack.MaxWidth = 380;
            overlayStack.VerticalAlignment = VerticalAlignment.Center;
            overlayStack.HorizontalAlignment = HorizontalAlignment.Center;
            _overlay = new Border { Background = Theme.Panel, Child = overlayStack, Visibility = Visibility.Collapsed };

            var listHost = new Grid();
            listHost.Children.Add(_list);
            listHost.Children.Add(_overlay);

            _status = UI.Text("", 12, Theme.Muted);
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.VerticalAlignment = VerticalAlignment.Center;

            // "Include subfolders": adds up everything below this folder (or the selected folders).
            _sizeBtn = new FlatButton
            {
                Content = UI.Text("Include subfolders", 12, Theme.Accent, FontWeights.SemiBold),
                Background = Brushes.Transparent, MinHeight = 22, Height = 22, Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(11), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            Tips.Set(_sizeBtn, "Add up folder sizes|Count everything inside the subfolders too. If folders are selected, only those are added up. Sizes appear in the Size column");
            _sizeBtn.Click += (s, e) => { if (_sizing) CancelSizing(); else CalculateSizes(); };

            // Free space on the drive or server, with a small usage bar.
            _spaceFill = new Border { CornerRadius = new CornerRadius(3), Background = Theme.Teal, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _spaceBar = new Border { Width = 70, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Subtle, Child = _spaceFill, VerticalAlignment = VerticalAlignment.Center };
            _spaceText = UI.Text("", 12, Theme.Muted);
            _spaceText.TextWrapping = TextWrapping.NoWrap;
            _spaceText.VerticalAlignment = VerticalAlignment.Center;
            _spaceBox = UI.H(8, _spaceBar, _spaceText);
            _spaceBox.Visibility = Visibility.Collapsed;
            _spaceBox.Margin = new Thickness(12, 0, 0, 0);

            var statusRow = new DockPanel { Margin = new Thickness(14, 4, 12, 8), MinHeight = 26 };
            DockPanel.SetDock(_spaceBox, Dock.Right);
            DockPanel.SetDock(_sizeBtn, Dock.Right);   // inside statusLeft: sits just after the text
            statusRow.Children.Add(_spaceBox);
            var statusLeft = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
            statusLeft.Children.Add(_sizeBtn);
            statusLeft.Children.Add(_status);
            statusRow.Children.Add(statusLeft);
            var statusHost = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusRow };

            _accentBar = new Border { Height = 2, Background = Brushes.Transparent };

            var root = new DockPanel();
            DockPanel.SetDock(_accentBar, Dock.Top);
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(nav, Dock.Top);
            DockPanel.SetDock(_links, Dock.Top);
            DockPanel.SetDock(statusHost, Dock.Bottom);
            root.Children.Add(_accentBar);
            root.Children.Add(top);
            root.Children.Add(nav);
            root.Children.Add(_links);
            root.Children.Add(statusHost);
            root.Children.Add(listHost);
            Child = root;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); Refresh(); };

            SiteStore.Changed += FillSources;
            Settings.QuickLinksChanged += FillLinks;
            FillSources();
        }

        private static readonly string NameCell =
            "<DockPanel>" +
            "  <Viewbox Width='16' Height='16' Margin='4,0,10,0' DockPanel.Dock='Left'>" +
            "    <Canvas Width='24' Height='24'>" +
            "      <Path x:Name='ic' Stroke='{MUTED}' StrokeThickness='1.8' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Data='" + Icons.File + "'/>" +
            "    </Canvas>" +
            "  </Viewbox>" +
            "  <TextBlock x:Name='tx' Text='{Binding Name}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>" +
            "</DockPanel>" +
            "<DataTemplate.Triggers>" +
            "  <DataTrigger Binding='{Binding IsDir}' Value='True'><Setter TargetName='ic' Property='Data' Value='" + Icons.Folder + "'/><Setter TargetName='ic' Property='Stroke' Value='{ACCENT}'/></DataTrigger>" +
            "  <DataTrigger Binding='{Binding IsDrive}' Value='True'><Setter TargetName='ic' Property='Data' Value='" + Icons.Server + "'/><Setter TargetName='ic' Property='Stroke' Value='{TEAL}'/></DataTrigger>" +
            "  <DataTrigger Binding='{Binding IsParent}' Value='True'><Setter TargetName='ic' Property='Data' Value='" + Icons.Up + "'/><Setter TargetName='ic' Property='Stroke' Value='{MUTED}'/></DataTrigger>" +
            "  <DataTrigger Binding='{Binding IsLink}' Value='True'><Setter TargetName='tx' Property='FontStyle' Value='Italic'/></DataTrigger>" +
            "</DataTemplate.Triggers>";

        // ---------------- Sources and connections ----------------

        private class SourceItem
        {
            public Site Site;
            public string Label;
            public bool Manage;
            public override string ToString() => Label;
        }

        private void FillSources()
        {
            _suppressSourceChange = true;
            try
            {
                string currentId = Fs?.Site?.Id;
                _source.Items.Clear();
                _source.Items.Add(new SourceItem { Label = "This PC" });
                foreach (var site in SiteStore.All.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                    _source.Items.Add(new SourceItem { Site = site, Label = site.Name });
                _source.Items.Add(new SourceItem { Label = "Manage sites...", Manage = true });
                SelectSource(currentId);
            }
            finally { _suppressSourceChange = false; }
        }

        private void SelectSource(string siteId)
        {
            foreach (SourceItem it in _source.Items)
            {
                if (!it.Manage && it.Site?.Id == siteId) { _source.SelectedItem = it; return; }
            }
            _source.SelectedIndex = 0;
        }

        private void SourcePicked()
        {
            if (!(_source.SelectedItem is SourceItem it)) return;
            if (it.Manage)
            {
                _suppressSourceChange = true;
                SelectSource(Fs?.Site?.Id);
                _suppressSourceChange = false;
                RequestSiteManager?.Invoke();
                return;
            }
            if (it.Site == null) OpenLocal(null);
            else if (Fs?.Site?.Id != it.Site.Id || !Fs.IsConnected) Connect(it.Site, null);
        }

        public void OpenLocal(string path)
        {
            Detach();
            Fs = new LocalFs();
            _suppressSourceChange = true;
            SelectSource(null);
            _suppressSourceChange = false;
            AfterConnect(path ?? Fs.Home);
        }

        public void Connect(Site site, string path)
        {
            Detach();
            var fs = FileSystem.For(site);
            Fs = fs;
            _suppressSourceChange = true;
            SelectSource(site.Id);
            _suppressSourceChange = false;
            ShowOverlay("Connecting to " + site.Host + "...", false);
            _conn.Text = "Connecting...";
            UpdateConnectButton();
            int ticket = ++_loadTicket;
            Task.Run(() => fs.Connect()).ContinueWith(t =>
            {
                if (ticket != _loadTicket || Fs != fs) return;
                if (t.IsFaulted)
                {
                    var ex = t.Exception?.GetBaseException();
                    string msg = ex is OperationCanceledException ? "Connection cancelled." : "Couldn't connect: " + TransferQueue.Friendly(ex);
                    Log.Error(site.Name + ": " + msg);
                    ShowOverlay(msg, true);
                    _conn.Text = "Not connected";
                    UpdateConnectButton();
                    return;
                }
                site.LastUsed = DateTime.Now;
                SiteStore.Save();
                Log.Info("Connected to " + site.Name);
                AfterConnect(string.IsNullOrEmpty(path) ? fs.Home : path);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void AfterConnect(string path)
        {
            _back.Clear();
            _grid.Columns.Remove(_permsCol);
            if (Fs.CanChmod) _grid.Columns.Add(_permsCol);
            _conn.Text = Fs.Site == null ? "" : Fs.Site.Summary;
            UpdateConnectButton();
            Navigate(path, false);
        }

        private void UpdateConnectButton()
        {
            bool remote = Fs?.Site != null;
            bool on = remote && Fs.IsConnected;
            _connectBtn.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            _connectBtn.Content = UI.H(8, UI.Icon(on ? Icons.Unplug : Icons.Plug, 16, Theme.Text), UI.Text(on ? "Disconnect" : "Reconnect", 13, Theme.Text, FontWeights.SemiBold));
            _terminalBtn.Visibility = remote && Fs.Site.IsSsh ? Visibility.Visible : Visibility.Collapsed;
            UpdateSpace();
            FillLinks();
            TitleChanged?.Invoke(this);
        }

        private void ToggleConnection()
        {
            if (Fs?.Site == null) return;
            if (Fs.IsConnected)
            {
                var site = Fs.Site;
                Detach();
                Fs = FileSystem.For(site);
                _items = new List<FsItem>();
                Bind();
                ShowOverlay("Disconnected from " + site.Name + ".", true);
                _overlayRetry.Content = "Reconnect";
                _conn.Text = "Not connected";
                UpdateConnectButton();
                Log.Info("Disconnected from " + site.Name);
            }
            else Connect(Fs.Site, CurrentPath);
        }

        private void Detach()
        {
            var old = Fs;
            Fs = null;
            if (old != null && !(old is LocalFs)) Task.Run(() => { try { old.Dispose(); } catch { } });
            else if (old is SmbFs) old.Dispose();
        }

        public void Close()
        {
            SiteStore.Changed -= FillSources;
            Settings.QuickLinksChanged -= FillLinks;
            _refreshTimer.Stop();
            Detach();
        }

        // ---------------- Navigation ----------------

        public void Navigate(string path, bool remember = true)
        {
            if (Fs == null) return;
            if (path == null) return;
            var fs = Fs;
            string target = path;
            int ticket = ++_loadTicket;
            if (target != CurrentPath) { CancelSizing(); _treeTotal = -1; }
            ShowOverlay("Loading...", false, delayed: true);
            Task.Run(() =>
            {
                try { return fs.List(target); }
                catch when (!fs.IsLocal && !fs.IsConnected)
                {
                    // Dropped connection: reconnect once and try again.
                    fs.Connect();
                    return fs.List(target);
                }
            }).ContinueWith(t =>
            {
                if (ticket != _loadTicket || Fs != fs) return;
                if (t.IsFaulted)
                {
                    string msg = TransferQueue.Friendly(t.Exception.GetBaseException());
                    if (string.IsNullOrEmpty(CurrentPath) || CurrentPath == target || _items.Count == 0)
                    {
                        CurrentPath = target;
                        _path.Text = target;
                        ShowOverlay("Couldn't open " + target + "\n" + msg, true);
                    }
                    else
                    {
                        HideOverlay();
                        _path.Text = CurrentPath;
                        Log.Error("Couldn't open " + target + ": " + msg);
                        Dialogs.Info("Couldn't open folder", target + "\n\n" + msg);
                    }
                    return;
                }
                if (remember && !string.IsNullOrEmpty(CurrentPath) && CurrentPath != target) _back.Push(CurrentPath);
                bool sameFolder = CurrentPath == target;
                var keep = sameFolder ? Selected().Select(i => i.Name).ToHashSet() : new HashSet<string>();
                CurrentPath = target;
                _path.Text = target;
                if (!sameFolder) _filter.Text = "";
                // Keep folder sizes already worked out when refreshing the same folder.
                if (sameFolder && _items != null)
                {
                    var known = _items.Where(i => i.IsDir && i.SizeComputed).ToDictionary(i => i.Name, i => i.Size);
                    foreach (var it in t.Result) if (it.IsDir && known.TryGetValue(it.Name, out long sz)) { it.Size = sz; it.SizeComputed = true; }
                    if (!_sizing) _treeTotal = -1;
                }
                _items = t.Result;
                Bind();
                UpdateSpace();
                if (keep.Count > 0) foreach (var it in _items.Where(i => keep.Contains(i.Name))) _list.SelectedItems.Add(it);
                HideOverlay();
                RememberLocation();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void RememberLocation()
        {
            if (Fs == null) return;
            if (!string.IsNullOrEmpty(CurrentPath)) Settings.Current.AddRecent(SourceKey, CurrentPath);
            FillLinks();
            TitleChanged?.Invoke(this);
        }

        // ---------------- Quick links and recent folders ----------------

        private void FillLinks()
        {
            _links.Children.Clear();
            if (Fs == null) { _links.Visibility = Visibility.Collapsed; return; }
            var mine = Settings.Current.QuickLinks.Where(q => q.Source == SourceKey).ToList();
            foreach (var q in mine)
            {
                var link = q;
                bool here = string.Equals(link.Path.TrimEnd('/', '\\'), (CurrentPath ?? "").TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
                var chip = new FlatButton
                {
                    Content = UI.H(6, UI.Icon(Icons.Folder, 13, here ? Theme.Accent : Theme.Muted), UI.Text(link.Name, 12, Theme.Text, FontWeights.SemiBold)),
                    Background = here ? Theme.AccentTint : Theme.Subtle,
                    MinHeight = 26,
                    Height = 26,
                    Padding = new Thickness(10, 0, 12, 0),
                    CornerRadius = new CornerRadius(13),
                    Margin = new Thickness(0, 0, 6, 4),
                };
                Tips.Set(chip, link.Name + "|" + link.Path + "\nClick to open. Middle-click opens a new tab. Right-click to rename or remove.");
                chip.Click += (s, e) => OpenLink(link, false);
                chip.MouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) { OpenLink(link, true); e.Handled = true; } };
                chip.ContextMenu = LinkMenu(link);
                _links.Children.Add(chip);
            }
            _links.Visibility = _links.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private ContextMenu LinkMenu(QuickLink link)
        {
            var m = new ContextMenu();
            var open = new MenuItem { Header = "Open" };
            open.Click += (s, e) => OpenLink(link, false);
            var tab = new MenuItem { Header = "Open in new tab" };
            tab.Click += (s, e) => OpenLink(link, true);
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) =>
            {
                string name = Dialogs.Prompt("Rename quick link", link.Path, link.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                link.Name = name.Trim();
                Settings.Current.RenamedQuickLink();
            };
            var remove = new MenuItem { Header = "Remove from quick links" };
            remove.Click += (s, e) => Settings.Current.RemoveQuickLink(link);
            m.Items.Add(open);
            m.Items.Add(tab);
            m.Items.Add(new Separator());
            m.Items.Add(rename);
            m.Items.Add(remove);
            return m;
        }

        public void AddQuickLink()
        {
            if (Fs == null || string.IsNullOrEmpty(CurrentPath)) return;
            string suggested = Fs.NameOf(CurrentPath);
            if (string.IsNullOrEmpty(suggested)) suggested = CurrentPath;
            string name = Dialogs.Prompt("Add quick link", (Fs.Site == null ? "This PC" : Fs.Site.Name) + "\n" + CurrentPath, suggested);
            if (string.IsNullOrWhiteSpace(name)) return;
            Settings.Current.AddQuickLink(new QuickLink { Name = name.Trim(), Source = SourceKey, Path = CurrentPath });
        }

        /// <summary>Opens a link here, connecting to its site first if needed, or in a new tab.</summary>
        public void OpenLink(QuickLink link, bool newTab)
        {
            if (newTab && RequestNewTab != null) { RequestNewTab(link.Source, link.Path); return; }
            OpenPlace(link.Source, link.Path);
        }

        public void OpenPlace(string source, string path)
        {
            if (source == SourceKey && Fs != null && (Fs.Site == null || Fs.IsConnected)) { Navigate(path); return; }
            if (source == "local") { OpenLocal(path); return; }
            var site = SiteStore.Find(source);
            if (site == null) { Dialogs.Info("Site not found", "The site for this link has been deleted."); return; }
            Connect(site, path);
        }

        private void ShowLinksMenu()
        {
            var m = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            void Item(string header, Action a, bool enabled = true, string gesture = null)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
            }
            void Label(string text)
            {
                m.Items.Add(new MenuItem { Header = UI.Label(text), IsEnabled = false });
            }

            bool canAdd = Fs != null && !string.IsNullOrEmpty(CurrentPath);
            Item("Add this folder to quick links", AddQuickLink, canAdd, "Ctrl+D");

            var all = Settings.Current.QuickLinks;
            if (all.Count > 0)
            {
                m.Items.Add(new Separator());
                Label("Quick links");
                foreach (var q in all.OrderBy(x => x.Source == SourceKey ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var link = q;
                    string where = link.Source == "local" ? "This PC" : SiteStore.Find(link.Source)?.Name ?? "Deleted site";
                    Item(link.Name + "   \u00b7  " + where, () => OpenLink(link, false));
                }
            }

            var recent = Settings.Current.Recent(SourceKey).Where(p => p != CurrentPath).Take(10).ToList();
            if (recent.Count > 0)
            {
                m.Items.Add(new Separator());
                Label("Recent folders here");
                foreach (var r in recent)
                {
                    string path = r;
                    Item(path, () => Navigate(path));
                }
            }
            m.IsOpen = true;
        }

        public void Refresh()
        {
            if (Fs == null) return;
            if (Fs.Site != null && !Fs.IsConnected && _items.Count == 0) { Connect(Fs.Site, CurrentPath); return; }
            Navigate(CurrentPath, false);
        }

        /// <summary>Refreshes soon, merging several requests (used when transfers finish).</summary>
        public void RefreshSoon()
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        public void GoUp()
        {
            if (Fs == null) return;
            string p = Fs.Parent(CurrentPath);
            if (p != null) Navigate(p);
        }

        public void GoBack()
        {
            if (_back.Count > 0) Navigate(_back.Pop(), false);
        }

        public void GoHome() { if (Fs != null) Navigate(Fs.Home); }

        public void FocusPath() { _path.Focus(); _path.SelectAll(); }

        public void FocusList()
        {
            _list.Focus();
            if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
            if (_list.ItemContainerGenerator.ContainerFromItem(_list.SelectedItem) is ListViewItem li) li.Focus();
        }

        public void SetActive(bool active)
        {
            _accentBar.Background = active ? Theme.Accent : Brushes.Transparent;
        }

        private void Bind()
        {
            _view = new ListCollectionView(_items) { CustomSort = new Sorter(_sortBy, _sortDesc), Filter = FilterItem };
            _list.ItemsSource = _view;
            UpdateStatus();
        }

        private bool FilterItem(object o)
        {
            string f = _filter.Text;
            if (string.IsNullOrWhiteSpace(f)) return true;
            var it = (FsItem)o;
            if (it.IsParent) return true;
            if (f.Contains('*') || f.Contains('?'))
            {
                string rx = "^" + Regex.Escape(f.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(it.Name, rx, RegexOptions.IgnoreCase);
            }
            return it.Name.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void HeaderClicked(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader h) || h.Column == null) return;
            string col = h.Column.Header as string;
            if (col == null) return;
            if (_sortBy == col) _sortDesc = !_sortDesc; else { _sortBy = col; _sortDesc = false; }
            if (_view != null) _view.CustomSort = new Sorter(_sortBy, _sortDesc);
        }

        private class Sorter : IComparer
        {
            private readonly string _by;
            private readonly bool _desc;
            public Sorter(string by, bool desc) { _by = by; _desc = desc; }

            public int Compare(object x, object y)
            {
                var a = (FsItem)x;
                var b = (FsItem)y;
                if (a.IsParent != b.IsParent) return a.IsParent ? -1 : 1;
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int r = _by switch
                {
                    "Size" => a.Size.CompareTo(b.Size),
                    "Modified" => a.Modified.CompareTo(b.Modified),
                    "Type" => string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase),
                    "Permissions" => string.CompareOrdinal(a.Perms, b.Perms),
                    _ => 0
                };
                if (r == 0) r = NaturalCompare(a.Name, b.Name);
                return _desc ? -r : r;
            }

            /// <summary>"file2" sorts before "file10".</summary>
            private static int NaturalCompare(string a, string b)
            {
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                        if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                        int c = string.CompareOrdinal(na, nb);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                return (a.Length - i).CompareTo(b.Length - j);
            }
        }

        private void UpdateStatus()
        {
            bool driveList = FileSystem.IsTopList(Fs, CurrentPath);
            _sizeBtn.Visibility = _view == null || driveList || _items.All(i => !i.IsDir || i.IsParent) && !_sizing ? Visibility.Collapsed : Visibility.Visible;
            if (_view == null) { _status.Text = ""; return; }
            var real = _items.Where(i => !i.IsParent).ToList();
            int folders = real.Count(i => i.IsDir), files = real.Count - folders;
            var sel = Selected();
            if (_sizing)
            {
                _status.Text = _sizeProgress ?? "Adding up...";
                return;
            }
            if (_flash != null) { _status.Text = _flash; return; }
            if (sel.Count > 0)
            {
                long bytes = sel.Sum(i => (!i.IsDir || i.SizeComputed) ? Math.Max(0, i.Size) : 0);
                bool partial = sel.Any(i => i.IsDir && !i.SizeComputed);
                _status.Text = string.Format("{0} of {1} selected{2}", sel.Count, real.Count,
                    bytes > 0 ? ", " + Fmt.Size(bytes) + (partial ? " (not counting folders)" : "") : "");
                return;
            }
            if (driveList) { _status.Text = real.Count + " places"; return; }
            long here = real.Where(i => !i.IsDir).Sum(i => Math.Max(0, i.Size));
            string counts = (folders > 0 ? folders + (folders == 1 ? " folder, " : " folders, ") : "") + files + (files == 1 ? " file" : " files");
            string size = _treeTotal >= 0
                ? Fmt.Size(_treeTotal) + " including subfolders"
                : (files > 0 ? Fmt.Size(here) + (folders > 0 ? " in files here" : "") : "");
            _status.Text = counts + (size.Length > 0 ? "  \u00b7  " + size : "");
        }

        // ---------------- Folder sizes ----------------

        private void CancelSizing()
        {
            try { _sizeCts?.Cancel(); } catch { }
        }

        /// <summary>Adds up the selected folders, or every subfolder here, filling in the Size column as each one finishes.</summary>
        public void CalculateSizes()
        {
            if (Fs == null || _sizing) return;
            var picked = Selected().Where(i => i.IsDir && !i.IsDrive && !i.IsLink).ToList();
            bool wholeFolder = picked.Count == 0;
            var targets = wholeFolder ? _items.Where(i => i.IsDir && !i.IsParent && !i.IsLink).ToList() : picked;
            long filesHere = wholeFolder ? _items.Where(i => !i.IsDir).Sum(i => Math.Max(0, i.Size)) : 0;
            var fs = Fs;
            string folder = CurrentPath;
            _sizeCts = new System.Threading.CancellationTokenSource();
            var token = _sizeCts.Token;
            _sizing = true;
            _sizeProgress = "Adding up...";
            _sizeBtn.Content = UI.Text("Stop", 12, Theme.Accent, FontWeights.SemiBold);
            UpdateStatus();
            var started = DateTime.Now;

            Task.Run(() =>
            {
                // Servers get their own connection so browsing stays quick while this runs.
                FileSystem walker = fs.IsLocal || fs is LocalFs ? fs : fs.CreateSibling();
                try
                {
                    if (walker != fs) walker.Connect();
                    long grand = filesHere;
                    int files = 0;
                    foreach (var dir in targets)
                    {
                        token.ThrowIfCancellationRequested();
                        long before = grand;
                        long size = walker.TreeSize(dir.FullPath, token, (bytes, count) =>
                        {
                            string text = "Adding up " + dir.Name + "...  " + count.ToString("N0") + " files, " + Fmt.Size(before + bytes);
                            Dispatcher.BeginInvoke(new Action(() => { if (_sizing && CurrentPath == folder) { _sizeProgress = text; UpdateStatus(); } }));
                        }, ref files);
                        grand += size;
                        var d = dir;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            d.Size = size;
                            d.SizeComputed = true;
                            if (CurrentPath == folder) _view?.Refresh();
                        }));
                    }
                    return grand;
                }
                finally
                {
                    if (walker != fs) { try { walker.Dispose(); } catch { } }
                }
            }, token).ContinueWith(t =>
            {
                _sizing = false;
                _sizeProgress = null;
                _sizeBtn.Content = UI.Text("Include subfolders", 12, Theme.Accent, FontWeights.SemiBold);
                if (t.IsFaulted)
                {
                    string msg = TransferQueue.Friendly(t.Exception.GetBaseException());
                    Log.Error("Couldn't add up folder sizes: " + msg);
                    Dialogs.Info("Couldn't add up the sizes", msg);
                }
                else if (!t.IsCanceled && wholeFolder && CurrentPath == folder && Fs == fs)
                {
                    _treeTotal = t.Result;
                }
                if (CurrentPath == folder) { _view?.Refresh(); UpdateStatus(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Looks up free space in the background; quietly hides the bar when a server won't say.</summary>
        private void UpdateSpace()
        {
            var fs = Fs;
            string path = CurrentPath;
            int ticket = ++_spaceTicket;
            if (fs == null || FileSystem.IsTopList(fs, path) || (fs.Site != null && !fs.IsConnected))
            {
                _spaceBox.Visibility = Visibility.Collapsed;
                return;
            }
            Task.Run(() => fs.GetSpace(path)).ContinueWith(t =>
            {
                if (ticket != _spaceTicket || Fs != fs) return;
                var info = t.IsFaulted ? null : t.Result;
                if (info == null)
                {
                    _spaceText.Text = "Free space not reported";
                    _spaceBar.Visibility = Visibility.Collapsed;
                    _spaceBox.Visibility = Visibility.Visible;
                    Tips.Set(_spaceBox, "Free space|This server doesn't say how much space is left. Plain FTP servers often don't");
                    return;
                }
                double used = info.UsedFraction;
                _spaceBar.Visibility = used >= 0 ? Visibility.Visible : Visibility.Collapsed;
                if (used >= 0)
                {
                    _spaceFill.Width = Math.Max(2, _spaceBar.Width * used);
                    _spaceFill.Background = used >= 0.9 ? Theme.Red : used >= 0.75 ? Theme.Accent : Theme.Teal;
                }
                _spaceText.Text = info.Total > 0 ? Fmt.Size(info.Free) + " free of " + Fmt.Size(info.Total) : Fmt.Size(info.Free) + " free";
                _spaceText.Foreground = used >= 0.9 ? Theme.Red : Theme.Muted;
                string where = fs.Site == null ? "this drive" : fs.Site.Name;
                Tips.Set(_spaceBox, "Free space on " + where + "|" + (info.Total > 0
                    ? Fmt.Size(info.Free) + " free, " + Fmt.Size(info.Total - info.Free) + " used (" + (int)Math.Round(used * 100) + "%) of " + Fmt.Size(info.Total)
                    : Fmt.Size(info.Free) + " free. This server doesn't report the total size"));
                _spaceBox.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowOverlay(string text, bool retry, bool delayed = false)
        {
            _overlayText.Text = text;
            _overlayRetry.Content = "Try again";
            _overlayRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            if (!delayed) { _overlay.Visibility = Visibility.Visible; return; }
            // Only show "Loading" if it takes a moment, so quick folders don't flicker.
            int ticket = _loadTicket;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (s, e) => { t.Stop(); if (ticket == _loadTicket && _overlayText.Text == text) _overlay.Visibility = Visibility.Visible; };
            t.Start();
        }

        private void HideOverlay()
        {
            _overlayText.Text = "";
            _overlay.Visibility = Visibility.Collapsed;
        }

        // ---------------- Selection and actions ----------------

        public List<FsItem> Selected() => _list.SelectedItems.Cast<FsItem>().Where(i => !i.IsParent).ToList();

        private static FsItem ItemAt(object source)
        {
            var d = source as DependencyObject;
            while (d != null && !(d is ListViewItem))
            {
                if (d is GridViewColumnHeader || d is ScrollBar) return null;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return (d as ListViewItem)?.DataContext as FsItem;
        }

        private void Activate(FsItem it)
        {
            if (it.IsDir || it.IsParent) { Navigate(it.FullPath); return; }
            if (it.IsLink)
            {
                // Might be a link to a folder we couldn't detect.
                Navigate(it.FullPath);
                return;
            }
            OpenFile(it);
        }

        private void OpenFile(FsItem it)
        {
            if (Fs is LocalFs)
            {
                try { Process.Start(new ProcessStartInfo(it.FullPath) { UseShellExecute = true }); }
                catch (Exception ex) { Dialogs.Info("Couldn't open file", ex.Message); }
            }
            else EditManager.Edit(Fs, it);
        }

        private bool CanTransfer(out string why)
        {
            why = null;
            if (Fs == null || Other?.Fs == null) { why = "Both sides need to be open."; return false; }
            if (Other.Fs.Site != null && !Other.Fs.IsConnected) { why = "The other side isn't connected."; return false; }
            if (FileSystem.IsTopList(Other.Fs, Other.CurrentPath)) { why = "Open a folder on the other side first (not the list of drives or shares)."; return false; }
            return true;
        }

        // ---------------- Copy, cut and paste ----------------

        /// <summary>Puts the selection on the clipboard. Paste it into any folder, tab, window, server or Explorer.</summary>
        public void CopySelection(bool cut)
        {
            var items = Selected().Where(i => !i.IsDrive).ToList();
            if (items.Count == 0 || Fs == null) return;
            FileClipboard.Set(Fs, CurrentPath, items, cut);
            Flash((cut ? "Cut " : "Copied ") + FileClipboard.Describe(items) + ". Open a folder and press Ctrl+V to paste.");
        }

        private static string Norm(string p) => (p ?? "").TrimEnd('/', '\\');

        /// <summary>Pastes into the folder that's open here. Same folder makes "name - Copy"; cut moves.</summary>
        public void PasteHere()
        {
            if (Fs == null) return;
            if (FileSystem.IsTopList(Fs, CurrentPath)) { Dialogs.Info("Open a folder first", "Paste goes into a folder. Open one, then press Ctrl+V."); return; }
            if (Fs.Site != null && !Fs.IsConnected) { Dialogs.Info("Not connected", "Connect to " + Fs.Site.Name + " first, then paste."); return; }
            if (!FileClipboard.TryGet(out var srcFs, out var items, out bool cut, out string srcDir))
            {
                Dialogs.Info("Nothing to paste", "Select files and press Ctrl+C (copy) or Ctrl+X (cut) first. Files copied in Windows Explorer can be pasted here too.");
                return;
            }
            var dst = Fs;
            string target = CurrentPath;
            bool sameFs = srcFs.Key == dst.Key;
            var cmp = dst.IsLocal || dst is LocalFs ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool sameDir = sameFs && string.Equals(Norm(srcDir), Norm(target), cmp);

            // A folder can't go inside itself.
            foreach (var d in items.Where(i => i.IsDir))
            {
                string src = Norm(d.FullPath);
                if (sameFs && (string.Equals(src, Norm(target), cmp) || Norm(target).StartsWith(src + dst.Sep, cmp)))
                {
                    Dialogs.Info("Can't paste here", "\"" + d.Name + "\" can't be pasted inside itself.");
                    return;
                }
            }

            bool ours = srcFs == FileClipboard.Fs;
            if (cut)
            {
                if (sameDir) return;   // already there
                bool canRename = sameFs && !(dst is LocalFs && !(dst is SmbFs) &&
                    !string.Equals(Path.GetPathRoot(srcDir ?? ""), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
                if (canRename)
                {
                    // Same place: a quick rename instead of copying everything.
                    var list = items.ToList();
                    RunThenRefresh(() =>
                    {
                        foreach (var it in list)
                        {
                            string to = dst.Combine(target, it.Name);
                            if (dst.Stat(to) != null) throw new IOException("\"" + it.Name + "\" already exists in this folder. Nothing after it was moved.");
                            dst.Rename(it.FullPath, to);
                        }
                    }, "Couldn't move", null);
                    Other?.RefreshSoon();
                }
                else TransferQueue.Instance.Copy(srcFs, items, dst, target, true);
                Log.Info("Moving " + FileClipboard.Describe(items) + " to " + target);
                Flash("Moving " + FileClipboard.Describe(items) + " here...");
                if (ours) FileClipboard.Clear();
                return;
            }

            Dictionary<string, string> rename = null;
            if (sameDir)
            {
                // Pasting into the same folder makes copies alongside, like Explorer.
                var taken = new HashSet<string>(_items.Select(i => i.Name), dst.IsLocal || dst is LocalFs ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                rename = new Dictionary<string, string>();
                foreach (var it in items)
                {
                    string n = FileClipboard.CopyName(it.Name, it.IsDir, taken);
                    taken.Add(n);
                    rename[it.FullPath] = n;
                }
            }
            TransferQueue.Instance.Copy(srcFs, items, dst, target, false, rename);
            Log.Info("Pasting " + FileClipboard.Describe(items) + " into " + target);
            Flash("Pasting " + FileClipboard.Describe(items) + "...");
        }

        public void SendToOther(bool move)
        {
            var items = Selected().Where(i => !i.IsDrive).ToList();
            if (items.Count == 0) return;
            if (!CanTransfer(out string why)) { Dialogs.Info("Can't copy yet", why); return; }
            if (move && !Dialogs.Confirm("Move " + Describe(items) + "?", "They'll be copied to " + Other.CurrentPath + " on " + Other.Fs.Title + ", then removed from here.", "Move")) return;
            TransferQueue.Instance.Copy(Fs, items, Other.Fs, Other.CurrentPath, move);
        }

        private static string Describe(List<FsItem> items) => items.Count == 1 ? "\"" + items[0].Name + "\"" : items.Count + " items";

        public void NewFolder()
        {
            if (Fs == null || FileSystem.IsTopList(Fs, CurrentPath)) return;
            string name = Dialogs.Prompt("New folder", "Name for the new folder in " + CurrentPath, "New folder");
            if (string.IsNullOrWhiteSpace(name)) return;
            var fs = Fs;
            string path = fs.Combine(CurrentPath, name.Trim());
            RunThenRefresh(() => fs.MakeDir(path), "Couldn't create folder", name.Trim());
        }

        public void RenameSelected()
        {
            var sel = Selected();
            if (sel.Count != 1 || sel[0].IsDrive) return;
            var it = sel[0];
            string name = Dialogs.Prompt("Rename", "New name for " + it.Name, it.Name);
            if (string.IsNullOrWhiteSpace(name) || name == it.Name) return;
            var fs = Fs;
            string parent = fs.Parent(it.FullPath) ?? CurrentPath;
            string to = fs.Combine(parent, name.Trim());
            RunThenRefresh(() => fs.Rename(it.FullPath, to), "Couldn't rename", name.Trim());
        }

        public void DeleteSelected()
        {
            var sel = Selected().Where(i => !i.IsDrive).ToList();
            if (sel.Count == 0 || Fs == null) return;
            if (Settings.Current.ConfirmDelete)
            {
                string where = Fs is LocalFs && !(Fs is SmbFs) ? "This deletes them permanently (not to the Recycle Bin)." : "This deletes them from " + Fs.Title + ". It can't be undone.";
                if (!Dialogs.Confirm("Delete " + Describe(sel) + "?", where, "Delete", danger: true)) return;
            }
            TransferQueue.Instance.Delete(Fs, sel);
        }

        private void ChangePermissions()
        {
            var sel = Selected();
            if (sel.Count == 0 || !Fs.CanChmod) return;
            int mode = FileSystem.ParseMode(sel[0].Perms);
            var w = new PermissionsWindow(sel.Count == 1 ? sel[0].Name : sel.Count + " items", mode < 0 ? 420 : mode);
            if (w.ShowDialog() != true) return;
            var fs = Fs;
            int m = w.Mode;
            RunThenRefresh(() => { foreach (var it in sel) fs.Chmod(it.FullPath, m); }, "Couldn't change permissions", null);
        }

        private void RunThenRefresh(Action work, string errorTitle, string selectName)
        {
            Task.Run(work).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    string msg = TransferQueue.Friendly(t.Exception.GetBaseException());
                    Log.Error(errorTitle + ": " + msg);
                    Dialogs.Info(errorTitle, msg);
                }
                Refresh();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void CopyPaths()
        {
            var sel = Selected();
            string text = sel.Count == 0 ? CurrentPath : string.Join(Environment.NewLine, sel.Select(i => i.FullPath));
            try { Clipboard.SetText(text); } catch { }
        }

        private ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            var sel = Selected();
            bool any = sel.Count > 0;
            bool one = sel.Count == 1;
            bool local = Fs is LocalFs && !(Fs is SmbFs);
            string arrow = Side == "left" ? "→" : "←";

            MenuItem Item(string header, string gesture, Action a, bool enabled = true)
            {
                var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
                return mi;
            }

            if (one && !sel[0].IsDir) Item(local ? "Open" : "Edit (saves back automatically)", "Enter", () => OpenFile(sel[0]));
            if (one && sel[0].IsDir) Item("Open", "Enter", () => Navigate(sel[0].FullPath));
            bool canPaste = Fs != null && !FileSystem.IsTopList(Fs, CurrentPath) && FileClipboard.HasContent;
            Item("Cut", "Ctrl+X", () => CopySelection(true), any);
            Item("Copy", "Ctrl+C", () => CopySelection(false), any);
            Item(FileClipboard.HasContent && FileClipboard.Items.Count > 0 && FileClipboard.Fs != null
                    ? "Paste " + FileClipboard.Describe(FileClipboard.Items) + " here" : "Paste", "Ctrl+V", PasteHere, canPaste);
            m.Items.Add(new Separator());
            Item("Copy to other side " + arrow, "F5", () => SendToOther(false), any);
            Item("Move to other side " + arrow, "F6", () => SendToOther(true), any);
            m.Items.Add(new Separator());
            Item("Rename", "F2", RenameSelected, one);
            Item("New folder", "F7", NewFolder);
            Item("Delete", "Del", DeleteSelected, any);
            if (Fs != null && Fs.CanChmod) Item("Permissions...", null, ChangePermissions, any);
            m.Items.Add(new Separator());
            Item(any ? "Copy path" : "Copy folder path", null, CopyPaths);
            if (local || Fs is SmbFs)
                Item("Show in Explorer", null, () =>
                {
                    try
                    {
                        string arg = any ? "/select,\"" + sel[0].FullPath + "\"" : "\"" + CurrentPath + "\"";
                        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
                    }
                    catch { }
                }, !string.IsNullOrEmpty(CurrentPath));
            if (one && sel[0].IsDir && RequestNewTab != null) Item("Open in new tab", null, () => RequestNewTab(SourceKey, sel[0].FullPath));
            Item(one && sel[0].IsDir ? "Add folder to quick links" : "Add this folder to quick links", "Ctrl+D", () =>
            {
                if (one && sel[0].IsDir)
                    Settings.Current.AddQuickLink(new QuickLink { Name = sel[0].Name, Source = SourceKey, Path = sel[0].FullPath });
                else AddQuickLink();
            }, Fs != null && (!string.IsNullOrEmpty(CurrentPath) || (one && sel[0].IsDir)));
            Item("Compare and sync with other side...", null, () => RequestSync?.Invoke(this));
            if (Fs?.Site != null && Fs.Site.IsSsh) Item("Open terminal here", null, () => RequestTerminal?.Invoke(this));
            Item(any && sel.Any(i => i.IsDir) ? "Calculate folder size" : "Calculate size with subfolders", null, CalculateSizes, !_sizing && _items.Any(i => i.IsDir && !i.IsParent));
            Item("Refresh", "Ctrl+R", Refresh);
            return m;
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Enter:
                    if (_list.SelectedItem is FsItem it) Activate(it);
                    break;
                case Key.Back:
                    GoUp();
                    break;
                case Key.Left when alt:
                    GoBack();
                    break;
                case Key.Delete:
                    DeleteSelected();
                    break;
                case Key.F2:
                    RenameSelected();
                    break;
                case Key.F4:
                    if (Selected().FirstOrDefault(i => !i.IsDir) is FsItem f) OpenFile(f);
                    break;
                case Key.F5:
                    SendToOther(false);
                    break;
                case Key.F6:
                    SendToOther(true);
                    break;
                case Key.F7:
                    NewFolder();
                    break;
                case Key.R when ctrl:
                    Refresh();
                    break;
                case Key.L when ctrl:
                    FocusPath();
                    break;
                case Key.F when ctrl:
                    _filter.Focus();
                    break;
                case Key.D when ctrl:
                    AddQuickLink();
                    break;
                case Key.C when ctrl:
                    CopySelection(false);
                    break;
                case Key.X when ctrl:
                    CopySelection(true);
                    break;
                case Key.V when ctrl:
                    PasteHere();
                    break;
                case Key.Insert when ctrl:
                    CopySelection(false);
                    break;
                case Key.Insert when shift:
                    PasteHere();
                    break;

                case Key.Tab when !ctrl:
                    Other?.FocusList();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        // ---------------- Drag and drop ----------------

        private void StartDrag()
        {
            var items = Selected().Where(i => !i.IsDrive).ToList();
            if (items.Count == 0 || Fs == null) return;
            var data = new DataObject();
            data.SetData(DragFormat, new DragPayload { Pane = this, Items = items });
            if (Fs is LocalFs) data.SetData(DataFormats.FileDrop, items.Select(i => i.FullPath).ToArray());
            try { DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Move); }
            catch (Exception ex) { Log.Write("Drag: " + ex.Message); }
        }

        private string DropTarget(DragEventArgs e)
        {
            var it = ItemAt(e.OriginalSource);
            if (it != null && (it.IsDir || it.IsParent) && !it.IsLink) return it.FullPath;
            return CurrentPath;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            bool ours = e.Data.GetDataPresent(DragFormat);
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            string target = DropTarget(e);
            bool driveList = FileSystem.IsTopList(Fs, target);
            if (Fs != null && (ours || files) && !driveList && (Fs.Site == null || Fs.IsConnected))
            {
                if (ours && e.Data.GetData(DragFormat) is DragPayload p && p.Pane == this && target == CurrentPath) e.Effects = DragDropEffects.None;
                else e.Effects = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
            }
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (Fs == null) return;
            string target = DropTarget(e);
            if (FileSystem.IsTopList(Fs, target)) return;
            bool move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            Activated?.Invoke(this);
            if (e.Data.GetData(DragFormat) is DragPayload p)
            {
                if (p.Pane == this && target == CurrentPath) return;
                var items = p.Items.Where(i => !(p.Pane == this && i.FullPath == target)).ToList();
                if (items.Count == 0) return;
                if (p.Pane == this)
                {
                    // Dropped onto a folder in the same list: a move within the same place.
                    var fs = Fs;
                    RunThenRefresh(() => { foreach (var i in items) fs.Rename(i.FullPath, fs.Combine(target, i.Name)); }, "Couldn't move", null);
                    return;
                }
                TransferQueue.Instance.Copy(p.Pane.Fs, items, Fs, target, move);
                return;
            }
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                var local = new LocalFs();
                var items = paths.Select(x => local.Stat(x)).Where(x => x != null).ToList();
                if (items.Count > 0) TransferQueue.Instance.Copy(local, items, Fs, target, false);
            }
        }
    }

    /// <summary>Tick boxes for owner, group and everyone, plus the octal number.</summary>
    public class PermissionsWindow : ThemedWindow
    {
        public int Mode { get; private set; }
        private readonly CheckBox[] _boxes = new CheckBox[9];
        private readonly TextBox _octal;
        private bool _syncing;

        public PermissionsWindow(string what, int mode)
        {
            Title = "Permissions";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            Mode = mode;
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(110) : new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            string[] cols = { "Read", "Write", "Run" };
            string[] rows = { "Owner", "Group", "Everyone" };
            for (int c = 0; c < 3; c++) { var t = UI.Label(cols[c]); Grid.SetColumn(t, c + 1); grid.Children.Add(t); }
            for (int r = 0; r < 3; r++)
            {
                var t = UI.Text(rows[r], 13);
                t.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(t, r + 1);
                grid.Children.Add(t);
                for (int c = 0; c < 3; c++)
                {
                    int bit = 8 - (r * 3 + c);
                    var cb = new CheckBox { IsChecked = (mode & (1 << bit)) != 0, VerticalAlignment = VerticalAlignment.Center };
                    cb.Click += (s, e) => FromBoxes();
                    _boxes[r * 3 + c] = cb;
                    Grid.SetRow(cb, r + 1);
                    Grid.SetColumn(cb, c + 1);
                    grid.Children.Add(cb);
                }
            }
            _octal = UI.Input(Convert.ToString(mode, 8).PadLeft(3, '0'), 90);
            _octal.FontFamily = Theme.Mono;
            _octal.TextChanged += (s, e) => FromOctal();
            var ok = FlatButton.Primary("Apply", () => { DialogResult = true; }, "Apply|Set these permissions on the selected files");
            ok.IsDefault = true;
            var cancel = FlatButton.Secondary("Cancel", () => { DialogResult = false; });
            cancel.IsCancel = true;
            var buttons = UI.H(10, cancel, ok);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            Content = new Border
            {
                Padding = new Thickness(24),
                Child = UI.V(14, UI.Text("Permissions", 18, Theme.Text, FontWeights.SemiBold, true), UI.Text(what, 13, Theme.Muted), grid,
                    UI.H(10, UI.Text("As a number", 13, Theme.Muted), _octal), buttons)
            };
        }

        private void FromBoxes()
        {
            if (_syncing) return;
            int m = 0;
            for (int i = 0; i < 9; i++) if (_boxes[i].IsChecked == true) m |= 1 << (8 - i);
            Mode = m;
            _syncing = true;
            _octal.Text = Convert.ToString(m, 8).PadLeft(3, '0');
            _syncing = false;
        }

        private void FromOctal()
        {
            if (_syncing) return;
            try
            {
                string t = _octal.Text.Trim();
                if (t.Length == 0 || t.Length > 4 || t.Any(ch => ch < '0' || ch > '7')) return;
                int m = Convert.ToInt32(t, 8) & 511;
                Mode = m;
                _syncing = true;
                for (int i = 0; i < 9; i++) _boxes[i].IsChecked = (m & (1 << (8 - i))) != 0;
                _syncing = false;
            }
            catch { }
        }
    }
}
