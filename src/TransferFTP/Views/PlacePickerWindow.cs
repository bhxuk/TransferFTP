using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TransferFTP
{
    public enum PlaceKind { Local, Site, QuickLink, Duplicate, NewSite }

    /// <summary>One choice in the picker: This PC, a saved site, a quick link, or "new connection".</summary>
    public class PlaceChoice
    {
        public PlaceKind Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Group { get; set; }
        public string Icon { get; set; }
        public Site Site { get; set; }
        public string Path { get; set; }
        public string Source { get; set; } = "local";
        public bool Connected { get; set; }
    }

    /// <summary>
    /// A searchable list of places to open: used for a new tab ("where should it open?")
    /// and for the terminal ("which server?").
    /// </summary>
    public class PlacePickerWindow : ThemedWindow
    {
        public PlaceChoice Picked { get; private set; }

        private readonly List<PlaceChoice> _all;
        private readonly ListBox _list;
        private readonly SearchBox _search;
        private readonly TextBlock _empty;

        public PlacePickerWindow(string title, string subtitle, List<PlaceChoice> choices, string openLabel, PlaceChoice preselect = null)
        {
            Title = title;
            Width = 560;
            Height = 600;
            MinHeight = 380;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            _all = choices;

            _search = new SearchBox("Search sites and folders", double.NaN) { Height = 38 };
            _search.Box.Height = 36;
            _search.Box.TextChanged += (s, e) => Fill();
            _search.Box.PreviewKeyDown += (s, e) =>
            {
                // Arrow keys move through the list while typing.
                if (e.Key == Key.Down) { Move(1); e.Handled = true; }
                else if (e.Key == Key.Up) { Move(-1); e.Handled = true; }
                else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            };

            _list = new ListBox { Margin = new Thickness(0, 12, 0, 0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.MouseDoubleClick += (s, e) => { if (_list.SelectedItem is PlaceChoice) Accept(); };
            _list.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };

            _empty = UI.Text("Nothing matches. Try another name, or add a new site.", 13, Theme.Muted);
            _empty.Margin = new Thickness(4, 16, 4, 0);
            _empty.Visibility = Visibility.Collapsed;

            var open = FlatButton.Primary(openLabel, Accept, openLabel + "|Open the place selected in the list (Enter)");
            open.IsDefault = false;
            var cancel = FlatButton.Secondary("Cancel", Close, "Cancel|Close without opening anything (Esc)");
            cancel.IsCancel = true;
            var buttons = UI.H(10, cancel, open);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 14, 0, 0);

            var head = UI.V(4, UI.Text(title, 20, Theme.Text, FontWeights.SemiBold, true), UI.Text(subtitle, 13, Theme.Muted));
            head.Margin = new Thickness(0, 0, 0, 14);
            var root = new DockPanel { Margin = new Thickness(22) };
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(_search, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(_empty, Dock.Top);
            root.Children.Add(head);
            root.Children.Add(_search);
            root.Children.Add(buttons);
            root.Children.Add(_empty);
            root.Children.Add(_list);
            Content = root;

            Fill(preselect);
            Loaded += (s, e) => _search.Box.Focus();
        }

        private void Fill(PlaceChoice select = null)
        {
            string q = (_search.Box.Text ?? "").Trim();
            var shown = string.IsNullOrEmpty(q)
                ? _all
                : _all.Where(c => c.Kind == PlaceKind.NewSite ||
                                  (c.Title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  (c.Detail ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _list.Items.Clear();
            string group = null;
            foreach (var c in shown)
            {
                if (c.Group != group && !string.IsNullOrEmpty(c.Group))
                {
                    group = c.Group;
                    _list.Items.Add(new ListBoxItem
                    {
                        Content = UI.Label(group),
                        IsEnabled = false,
                        Focusable = false,
                        Margin = new Thickness(0, _list.Items.Count == 0 ? 0 : 10, 0, 0)
                    });
                }
                _list.Items.Add(Row(c));
            }
            _empty.Visibility = shown.All(c => c.Kind == PlaceKind.NewSite) && shown.Count < _all.Count ? Visibility.Visible : Visibility.Collapsed;
            var target = (select != null ? Find(select) : null) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is PlaceChoice);
            if (target != null)
            {
                _list.SelectedItem = target;
                _list.ScrollIntoView(target);
            }
        }

        private ListBoxItem Find(PlaceChoice c) => _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == c);

        private ListBoxItem Row(PlaceChoice c)
        {
            bool isNew = c.Kind == PlaceKind.NewSite;
            var iconBox = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
                Background = isNew ? Theme.Subtle : c.Kind == PlaceKind.Site ? Theme.TealTint : Theme.AccentTint,
                Child = UI.Icon(c.Icon ?? Icons.Folder, 16, isNew ? Theme.Text : c.Kind == PlaceKind.Site ? Theme.Teal : Theme.Accent),
                VerticalAlignment = VerticalAlignment.Center
            };
            ((FrameworkElement)iconBox.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((FrameworkElement)iconBox.Child).VerticalAlignment = VerticalAlignment.Center;
            var title = UI.Text(c.Title, 14, Theme.Text, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            var text = UI.V(2, title);
            if (!string.IsNullOrEmpty(c.Detail))
            {
                var d = UI.Text(c.Detail, 12, Theme.Muted);
                d.TextTrimming = TextTrimming.CharacterEllipsis;
                d.TextWrapping = TextWrapping.NoWrap;
                text.Children.Add(d);
            }
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(12, 0, 0, 0);
            var row = new DockPanel();
            DockPanel.SetDock(iconBox, Dock.Left);
            row.Children.Add(iconBox);
            if (c.Connected)
            {
                var dot = new Border
                {
                    Background = Theme.Brush(Color.FromArgb(0x29, Theme.GreenColor.R, Theme.GreenColor.G, Theme.GreenColor.B)),
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
                    Child = UI.Text("Connected", 11, Theme.Green, FontWeights.SemiBold)
                };
                DockPanel.SetDock(dot, Dock.Right);
                row.Children.Add(dot);
            }
            row.Children.Add(text);
            var item = new ListBoxItem { Content = row, Tag = c };
            if (!string.IsNullOrEmpty(c.Detail)) Tips.Set(item, c.Title + "|" + c.Detail);
            return item;
        }

        private void Move(int dir)
        {
            var items = _list.Items.OfType<ListBoxItem>().Where(i => i.Tag is PlaceChoice).ToList();
            if (items.Count == 0) return;
            int i = items.IndexOf(_list.SelectedItem as ListBoxItem);
            i = Math.Max(0, Math.Min(items.Count - 1, i + dir));
            _list.SelectedItem = items[i];
            _list.ScrollIntoView(items[i]);
        }

        private void Accept()
        {
            if (!((_list.SelectedItem as ListBoxItem)?.Tag is PlaceChoice c)) return;
            Picked = c;
            DialogResult = true;
        }

        /// <summary>Saved sites, newest first, marked when they're open in a tab.</summary>
        public static IEnumerable<PlaceChoice> Sites(Func<Site, bool> filter, ISet<string> connectedIds, string group)
        {
            foreach (var s in SiteStore.All.Where(filter).OrderByDescending(x => x.LastUsed).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return new PlaceChoice
                {
                    Kind = PlaceKind.Site,
                    Title = s.Name,
                    Detail = s.Summary,
                    Group = group,
                    Icon = Icons.Server,
                    Site = s,
                    Source = s.Id,
                    Connected = connectedIds != null && connectedIds.Contains(s.Id)
                };
            }
        }
    }
}
