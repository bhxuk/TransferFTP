using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TransferFTP
{
    /// <summary>One side of the window: a row of tabs, each its own file pane.</summary>
    public class PaneSide : DockPanel
    {
        public string Side { get; }
        public List<FilePane> Tabs { get; } = new List<FilePane>();
        public FilePane Current { get; private set; }

        /// <summary>The current pane on the opposite side, so F5 and drag know where to send files.</summary>
        public Func<FilePane> OtherCurrent;

        /// <summary>Called for each new pane so the window can hook up sync, terminal and the rest.</summary>
        public Action<FilePane> Wire;

        public event Action<FilePane> CurrentChanged;

        /// <summary>Shows the "where should the new tab open?" picker. Without it, new tabs copy the current one.</summary>
        public Action<PaneSide> NewTabPrompt;

        public void NewTab()
        {
            if (NewTabPrompt != null) NewTabPrompt(this);
            else Duplicate();
        }

        private readonly StackPanel _strip;
        private readonly ScrollViewer _stripScroll;
        private readonly Grid _host;

        public PaneSide(string side)
        {
            Side = side;
            _strip = new StackPanel { Orientation = Orientation.Horizontal };
            var add = FlatButton.Tool(Icons.Plus, null, "New tab|Choose This PC, a saved site or a quick link to open in a new tab (Ctrl+T)", NewTab);
            add.Height = 30;
            add.MinWidth = 30;
            add.Margin = new Thickness(2, 0, 0, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_strip);
            row.Children.Add(add);
            _stripScroll = new ScrollViewer
            {
                Content = row,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(4, 0, 4, 6)
            };
            // Mouse wheel scrolls the tab row sideways when there are lots of tabs.
            _stripScroll.PreviewMouseWheel += (s, e) => { _stripScroll.ScrollToHorizontalOffset(_stripScroll.HorizontalOffset - e.Delta / 2.0); e.Handled = true; };
            _host = new Grid();
            DockPanel.SetDock(_stripScroll, Dock.Top);
            Children.Add(_stripScroll);
            Children.Add(_host);
        }

        public FilePane AddTab(bool select = true)
        {
            var p = new FilePane(Side) { OtherProvider = () => OtherCurrent?.Invoke() };
            p.TitleChanged += x => RenderStrip();
            p.RequestNewTab = (source, path) =>
            {
                var t = AddTab();
                t.OpenPlace(source, path);
            };
            Wire?.Invoke(p);
            Tabs.Add(p);
            p.Visibility = Visibility.Collapsed;
            _host.Children.Add(p);
            if (select || Current == null) Select(p);
            else RenderStrip();
            return p;
        }

        public void Select(FilePane p)
        {
            if (p == null || !Tabs.Contains(p)) return;
            Current = p;
            foreach (var t in Tabs) t.Visibility = t == p ? Visibility.Visible : Visibility.Collapsed;
            RenderStrip();
            CurrentChanged?.Invoke(p);
        }

        /// <summary>Opens a new tab at the same place as the current one.</summary>
        public FilePane Duplicate()
        {
            var src = Current;
            var t = AddTab();
            if (src?.Fs?.Site != null) t.Connect(src.Fs.Site, src.CurrentPath);
            else t.OpenLocal(src == null ? null : (string.IsNullOrEmpty(src.CurrentPath) ? null : src.CurrentPath));
            t.Dispatcher.BeginInvoke(new Action(() => t.FocusList()), DispatcherPriority.Background);
            return t;
        }

        public void CloseTab(FilePane p)
        {
            if (p == null) return;
            if (Tabs.Count == 1)
            {
                // Closing the last tab just takes it back to This PC.
                p.OpenLocal(null);
                return;
            }
            int i = Tabs.IndexOf(p);
            Tabs.Remove(p);
            _host.Children.Remove(p);
            p.Close();
            if (Current == p) Select(Tabs[Math.Min(i, Tabs.Count - 1)]);
            else RenderStrip();
        }

        public void Cycle(int dir)
        {
            if (Tabs.Count < 2) return;
            int i = (Tabs.IndexOf(Current) + dir + Tabs.Count) % Tabs.Count;
            Select(Tabs[i]);
            Current.FocusList();
        }

        public void CloseAll()
        {
            foreach (var t in Tabs) t.Close();
        }

        public List<TabState> State() => Tabs.Select(t => new TabState { Source = t.SourceKey, Path = t.CurrentPath ?? "" }).ToList();

        public void Restore(List<TabState> tabs, int active)
        {
            if (tabs == null || tabs.Count == 0) tabs = new List<TabState> { new TabState() };
            foreach (var st in tabs.Take(20))
            {
                var t = AddTab(false);
                var site = st.Source == "local" ? null : SiteStore.Find(st.Source);
                if (site == null) t.OpenLocal(st.Source == "local" && !string.IsNullOrEmpty(st.Path) ? st.Path : null);
                else t.Connect(site, st.Path);
            }
            Select(Tabs[Math.Max(0, Math.Min(active, Tabs.Count - 1))]);
        }

        private void RenderStrip()
        {
            _strip.Children.Clear();
            foreach (var t in Tabs)
            {
                var tab = t;
                bool on = tab == Current;
                var title = UI.Text(tab.TabTitle, 12.5, on ? Theme.Text : Theme.Muted, on ? FontWeights.SemiBold : FontWeights.Normal);
                title.TextWrapping = TextWrapping.NoWrap;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.MaxWidth = 200;
                title.VerticalAlignment = VerticalAlignment.Center;
                var icon = UI.Icon(tab.Fs?.Site == null ? Icons.Folder : Icons.Server, 13, on ? Theme.Accent : Theme.Muted);
                var close = new FlatButton
                {
                    Content = UI.Icon(Icons.Close, 10, Theme.Muted, 2.2),
                    Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0),
                    Background = Brushes.Transparent, CornerRadius = new CornerRadius(10),
                    ToolTip = Tips.Make("Close tab (Ctrl+W)"),
                    Visibility = Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed
                };
                close.Click += (s, e) => { CloseTab(tab); e.Handled = true; };
                var content = UI.H(8, icon, title, close);
                var chip = new Border
                {
                    Background = on ? Theme.Panel : Brushes.Transparent,
                    BorderBrush = on ? Theme.Line : Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 4, 6, 4),
                    Margin = new Thickness(0, 0, 4, 0),
                    Height = 30,
                    Child = content,
                    Cursor = Cursors.Hand,
                    ToolTip = Tips.Make((tab.Fs?.Site == null ? "This PC" : tab.Fs.Site.Name) + "|" + (string.IsNullOrEmpty(tab.CurrentPath) ? "Drives and folders" : tab.CurrentPath) + "\nMiddle-click to close, right-click for more"),
                    AllowDrop = true
                };
                chip.MouseLeftButtonUp += (s, e) => { Select(tab); tab.FocusList(); };
                chip.MouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) { CloseTab(tab); e.Handled = true; } };
                // Dragging files over a tab switches to it, so you can drop into another tab.
                chip.DragEnter += (s, e) => { if (tab != Current) Select(tab); };
                chip.ContextMenu = TabMenu(tab);
                _strip.Children.Add(chip);
            }
        }

        private ContextMenu TabMenu(FilePane tab)
        {
            var m = new ContextMenu();
            void Item(string header, string gesture, Action a, bool enabled = true)
            {
                var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
            }
            Item("New tab...", "Ctrl+T", NewTab);
            Item("Duplicate tab", null, () => { Select(tab); Duplicate(); });
            Item("Close tab", "Ctrl+W", () => CloseTab(tab), Tabs.Count > 1);
            Item("Close other tabs", null, () => { foreach (var t in Tabs.Where(x => x != tab).ToList()) CloseTab(t); }, Tabs.Count > 1);
            m.Items.Add(new Separator());
            Item("Add folder to quick links", "Ctrl+D", () => { Select(tab); tab.AddQuickLink(); }, !string.IsNullOrEmpty(tab.CurrentPath));
            return m;
        }
    }
}
