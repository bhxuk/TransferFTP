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
        public FilePane Other { get; set; }
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
        private readonly TextBlock _conn;
        private readonly Border _overlay;
        private readonly TextBlock _overlayText;
        private readonly FlatButton _overlayRetry;
        private readonly Border _accentBar;
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
            _source.SelectionChanged += (s, e) => { if (!_suppressSourceChange) SourcePicked(); };
            _connectBtn = FlatButton.Tool(Icons.Plug, "Connect", "Connect", () => ToggleConnection());
            _terminalBtn = FlatButton.Tool(Icons.Terminal, null, "Open an SSH terminal", () => RequestTerminal?.Invoke(this));
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
            _path.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Navigate(_path.Text.Trim()); e.Handled = true; } };
            _filter = UI.Input("", 150, "Filter");
            _filter.Height = 32;
            _filter.TextChanged += (s, e) => _view?.Refresh();
            var nav = new DockPanel { Margin = new Thickness(12, 0, 12, 8) };
            var navLeft = UI.H(2,
                FlatButton.Tool(Icons.Back, null, "Back (Alt+Left)", GoBack),
                FlatButton.Tool(Icons.Up, null, "Up a folder (Backspace)", GoUp),
                FlatButton.Tool(Icons.Home, null, "Home folder", GoHome),
                FlatButton.Tool(Icons.Refresh, null, "Refresh (Ctrl+R)", Refresh),
                FlatButton.Tool(Icons.FolderPlus, null, "New folder (F7)", NewFolder));
            navLeft.Margin = new Thickness(0, 0, 8, 0);
            _filter.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(navLeft, Dock.Left);
            DockPanel.SetDock(_filter, Dock.Right);
            nav.Children.Add(navLeft);
            nav.Children.Add(_filter);
            nav.Children.Add(_path);

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
            _overlayRetry = FlatButton.Secondary("Try again", Refresh);
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
            _status.Margin = new Thickness(14, 6, 14, 10);

            _accentBar = new Border { Height = 2, Background = Brushes.Transparent };

            var root = new DockPanel();
            DockPanel.SetDock(_accentBar, Dock.Top);
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(nav, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_accentBar);
            root.Children.Add(top);
            root.Children.Add(nav);
            root.Children.Add(_status);
            root.Children.Add(listHost);
            Child = root;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); Refresh(); };

            SiteStore.Changed += FillSources;
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

        public void Close() => Detach();

        // ---------------- Navigation ----------------

        public void Navigate(string path, bool remember = true)
        {
            if (Fs == null) return;
            if (path == null) return;
            var fs = Fs;
            string target = path;
            int ticket = ++_loadTicket;
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
                _items = t.Result;
                Bind();
                if (keep.Count > 0) foreach (var it in _items.Where(i => keep.Contains(i.Name))) _list.SelectedItems.Add(it);
                HideOverlay();
                RememberLocation();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void RememberLocation()
        {
            string src = Fs?.Site?.Id ?? "local";
            if (Side == "left") { Settings.Current.LeftSource = src; Settings.Current.LeftPath = CurrentPath; }
            else { Settings.Current.RightSource = src; Settings.Current.RightPath = CurrentPath; }
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
            if (_view == null) { _status.Text = ""; return; }
            int count = _items.Count(i => !i.IsParent);
            var sel = Selected();
            long bytes = sel.Where(i => !i.IsDir).Sum(i => Math.Max(0, i.Size));
            _status.Text = sel.Count > 0
                ? string.Format("{0} of {1} selected{2}", sel.Count, count, bytes > 0 ? ", " + Fmt.Size(bytes) : "")
                : count + (count == 1 ? " item" : " items");
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
            if (Other.Fs is LocalFs && string.IsNullOrEmpty(Other.CurrentPath) && !(Other.Fs is SmbFs)) { why = "Open a folder on the other side first (not the drive list)."; return false; }
            return true;
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
            if (Fs == null || (Fs is LocalFs && string.IsNullOrEmpty(CurrentPath) && !(Fs is SmbFs))) return;
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
            Item("Compare and sync with other side...", null, () => RequestSync?.Invoke(this));
            if (Fs?.Site != null && Fs.Site.IsSsh) Item("Open terminal here", null, () => RequestTerminal?.Invoke(this));
            Item("Refresh", "Ctrl+R", Refresh);
            return m;
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
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
            bool driveList = Fs is LocalFs && !(Fs is SmbFs) && string.IsNullOrEmpty(target);
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
            if (Fs is LocalFs && !(Fs is SmbFs) && string.IsNullOrEmpty(target)) return;
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
            var ok = FlatButton.Primary("Apply", () => { DialogResult = true; });
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
