using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace TransferFTP
{
    /// <summary>The colour palette, shared with Arrakis. Light or dark and the accent are read once at start-up.</summary>
    public static class Theme
    {
        public static readonly bool IsLight = ResolveLight();
        public static readonly Color AccentColor = AccentFor(Settings.Current.Accent);

        public static readonly string[] AccentNames = { "Amber", "Teal", "Blue", "Pink", "Green", "Purple" };
        public static readonly string[] ThemeNames = { "Dark", "Light", "System" };

        public static Color AccentFor(string name) => name switch
        {
            "Teal" => Color.FromRgb(0x3F, 0xC4, 0xB2),
            "Blue" => Color.FromRgb(0x5B, 0x8C, 0xFF),
            "Pink" => Color.FromRgb(0xF0, 0x6A, 0x9A),
            "Green" => Color.FromRgb(0x7C, 0xC8, 0x5A),
            "Purple" => Color.FromRgb(0xA0, 0x7C, 0xF0),
            _ => Color.FromRgb(0xFF, 0xB5, 0x47)
        };

        private static bool ResolveLight()
        {
            string t = Settings.Current.Theme ?? "Dark";
            if (t == "Light") return true;
            if (t == "System")
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
                }
                catch { }
            }
            return false;
        }

        public static readonly Color BgColor = IsLight ? Color.FromRgb(0xF6, 0xF5, 0xF2) : Color.FromRgb(0x0F, 0x11, 0x15);
        public static readonly Color PanelColor = IsLight ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x18, 0x1B, 0x22);
        public static readonly Color SideColor = IsLight ? Color.FromRgb(0xEE, 0xEC, 0xE8) : Color.FromRgb(0x14, 0x17, 0x1D);
        public static readonly Color TextColor = IsLight ? Color.FromRgb(0x17, 0x19, 0x1F) : Color.FromRgb(0xF3, 0xF1, 0xEC);
        public static readonly Color MutedColor = IsLight ? Color.FromRgb(0x5B, 0x60, 0x6B) : Color.FromRgb(0xA7, 0xAB, 0xB5);
        public static readonly Color TealColor = IsLight ? Color.FromRgb(0x1F, 0xA3, 0x92) : Color.FromRgb(0x5E, 0xD3, 0xC3);
        public static readonly Color GreenColor = IsLight ? Color.FromRgb(0x4E, 0xA0, 0x3A) : Color.FromRgb(0x8F, 0xD1, 0x6A);
        public static readonly Color RedColor = Color.FromRgb(0xE2, 0x58, 0x5F);
        public static readonly Color LineColor = IsLight ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        public static readonly Color SubtleColor = IsLight ? Color.FromArgb(0x0D, 0, 0, 0) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
        public static readonly Color OffColor = IsLight ? Color.FromRgb(0xC4, 0xC8, 0xD0) : Color.FromRgb(0x3A, 0x3F, 0x4B);
        public static readonly Color PopupColor = IsLight ? Color.FromArgb(0xFA, 0xFB, 0xFA, 0xF8) : Color.FromArgb(0xF7, 0x16, 0x19, 0x1F);
        public static readonly Color AccentTintColor = Color.FromArgb(IsLight ? (byte)0x30 : (byte)0x29, AccentColor.R, AccentColor.G, AccentColor.B);
        public static readonly Color DarkColor = Color.FromRgb(0x1A, 0x12, 0x06);

        public static readonly SolidColorBrush Bg = B(BgColor);
        public static readonly SolidColorBrush Panel = B(PanelColor);
        public static readonly SolidColorBrush Side = B(SideColor);
        public static readonly SolidColorBrush Text = B(TextColor);
        public static readonly SolidColorBrush Muted = B(MutedColor);
        public static readonly SolidColorBrush Accent = B(AccentColor);
        public static readonly SolidColorBrush Teal = B(TealColor);
        public static readonly SolidColorBrush Green = B(GreenColor);
        public static readonly SolidColorBrush Red = B(RedColor);
        public static readonly SolidColorBrush Dark = B(DarkColor);
        public static readonly SolidColorBrush Line = B(LineColor);
        public static readonly SolidColorBrush Subtle = B(SubtleColor);
        public static readonly SolidColorBrush Off = B(OffColor);
        public static readonly SolidColorBrush Popup = B(PopupColor);
        public static readonly SolidColorBrush AccentTint = B(AccentTintColor);
        public static readonly SolidColorBrush TealTint = B(Color.FromArgb(0x29, TealColor.R, TealColor.G, TealColor.B));
        public static readonly SolidColorBrush RedTint = B(Color.FromArgb(0x29, 0xE2, 0x58, 0x5F));
        public static readonly SolidColorBrush Stage = B(IsLight ? Color.FromRgb(0xE4, 0xE2, 0xDE) : Color.FromRgb(0x0B, 0x0D, 0x11));

        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");

        public static string Hex(Color c) => string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);

        private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static SolidColorBrush Brush(Color c) => B(c);

        public static DropShadowEffect Shadow(double blur = 30, double opacity = 0.35, double depth = 10)
            => new DropShadowEffect { BlurRadius = blur, Opacity = opacity, ShadowDepth = depth, Direction = 270, Color = Colors.Black };

        private static BitmapImage _icon;

        public static BitmapImage AppIcon()
        {
            if (_icon != null) return _icon;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/Assets/transferftp.ico", UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            _icon = img;
            return img;
        }
    }

    /// <summary>Small factory helpers so the code-built UI stays readable.</summary>
    public static class UI
    {
        public static TextBlock Text(string text, double size = 14, Brush fg = null, FontWeight? weight = null, bool display = false)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = fg ?? Theme.Text,
                FontWeight = weight ?? FontWeights.Normal,
                FontFamily = display ? Theme.Display : Theme.Font,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static TextBlock Label(string text)
        {
            var t = Text(text.ToUpperInvariant(), 11, Theme.Muted, FontWeights.SemiBold);
            t.TextWrapping = TextWrapping.NoWrap;
            return t;
        }

        public static Border Card(UIElement child, double padding = 20, Brush bg = null, double radius = 16)
        {
            return new Border
            {
                Background = bg ?? Theme.Panel,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(radius),
                Padding = new Thickness(padding),
                Child = child
            };
        }

        public static StackPanel V(double gap, params UIElement[] children) => Stack(Orientation.Vertical, gap, children);
        public static StackPanel H(double gap, params UIElement[] children) => Stack(Orientation.Horizontal, gap, children);

        public static StackPanel Stack(Orientation o, double gap, params UIElement[] children)
        {
            var sp = new GapStack { Orientation = o, Gap = gap };
            foreach (var c in children) if (c != null) sp.Children.Add(c);
            return sp;
        }

        /// <summary>A 24x24-grid line icon drawn at the given size.</summary>
        public static FrameworkElement Icon(string data, double size = 18, Brush stroke = null, double thickness = 1.8)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = stroke ?? Theme.Text,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
        }

        public static Border Divider() => new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 4, 0, 4) };

        public static TextBox Input(string value = "", double width = double.NaN, string hint = null)
        {
            var t = new TextBox { Text = value ?? "", Width = width, Height = 34, FontSize = 13 };
            if (!string.IsNullOrEmpty(hint)) Hint.Set(t, hint);
            return t;
        }

        public static PasswordBox Password(string value = "")
        {
            return new PasswordBox { Password = value ?? "", Height = 34, FontSize = 13 };
        }

        public static ComboBox Combo(string[] options, string selected, double width = double.NaN)
        {
            var cb = new ComboBox { Width = width, FontSize = 13 };
            foreach (var o in options) cb.Items.Add(o);
            cb.SelectedItem = Array.IndexOf(options, selected) >= 0 ? selected : options[0];
            return cb;
        }

        /// <summary>A label above a control, used in forms.</summary>
        public static StackPanel Field(string label, UIElement control)
        {
            var l = Text(label, 12, Theme.Muted, FontWeights.SemiBold);
            return V(6, l, control);
        }

        /// <summary>Wraps a DataTemplate body (XAML) so GridView columns can be built from code.</summary>
        public static DataTemplate Template(string body)
        {
            string xaml = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                          "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + body + "</DataTemplate>";
            return (DataTemplate)XamlReader.Parse(Styles.Fill(xaml));
        }

        public static GridViewColumn Column(string header, double width, string body)
            => new GridViewColumn { Header = header, Width = width, CellTemplate = Template(body) };
    }

    /// <summary>Grey placeholder text shown in an empty text box (drawn by the TextBox template from Tag).</summary>
    public static class Hint
    {
        public static void Set(TextBox box, string hint) => box.Tag = hint;
    }

    /// <summary>
    /// Hover help. A tip is written "Title|What it does (Shortcut)": the title shows in bold,
    /// the rest underneath, and the shortcut as a key cap.
    /// </summary>
    public static class Tips
    {
        public static object Make(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            string title = spec, detail = null, keys = null;
            int bar = spec.IndexOf('|');
            if (bar >= 0) { title = spec.Substring(0, bar).Trim(); detail = spec.Substring(bar + 1).Trim(); }
            // A shortcut in brackets at the end of the title or detail becomes a key cap.
            string ExtractKeys(ref string s)
            {
                if (s == null || !s.EndsWith(")")) return null;
                int open = s.LastIndexOf('(');
                if (open < 0) return null;
                string inner = s.Substring(open + 1, s.Length - open - 2);
                bool looksLikeKey = inner.Length <= 22 && (inner.Contains("Ctrl") || inner.Contains("Alt") || inner.Contains("Shift") ||
                    (inner.Length <= 3 && inner.StartsWith("F")) || inner == "Backspace" || inner == "Del" || inner == "Enter" || inner == "Tab");
                if (!looksLikeKey) return null;
                s = s.Substring(0, open).TrimEnd();
                return inner;
            }
            keys = ExtractKeys(ref title) ?? ExtractKeys(ref detail);

            var head = new DockPanel();
            if (keys != null)
            {
                var cap = new Border
                {
                    Background = Theme.Subtle, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = keys, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text }
                };
                DockPanel.SetDock(cap, Dock.Right);
                head.Children.Add(cap);
            }
            head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap });
            if (string.IsNullOrEmpty(detail)) return head;
            var body = new TextBlock { Text = detail, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(body);
            return stack;
        }

        public static T Set<T>(T element, string spec) where T : FrameworkElement
        {
            element.ToolTip = Make(spec);
            if (element.ToolTip != null)
            {
                int bar = spec.IndexOf('|');
                System.Windows.Automation.AutomationProperties.SetHelpText(element, spec.Replace("|", ". "));
                if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(element)))
                    System.Windows.Automation.AutomationProperties.SetName(element, bar > 0 ? spec.Substring(0, bar) : spec);
            }
            return element;
        }
    }

    /// <summary>A search-style box: magnifier icon, placeholder and a clear button, with room to breathe.</summary>
    public class SearchBox : Border
    {
        public TextBox Box { get; }
        private readonly FlatButton _clear;

        public SearchBox(string hint, double width)
        {
            Width = width;
            Height = 32;
            Background = Theme.Subtle;
            BorderBrush = Theme.Line;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Cursor = Cursors.IBeam;
            var icon = UI.Icon(Icons.Search, 14, Theme.Muted);
            icon.Margin = new Thickness(10, 0, 2, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            Box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 4, 0),
                Height = 30,
                FontSize = 13,
                Tag = hint,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _clear = new FlatButton
            {
                Content = UI.Icon(Icons.Close, 10, Theme.Muted, 2.2),
                Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0),
                Background = Brushes.Transparent, CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 5, 0), Visibility = Visibility.Collapsed, Focusable = false
            };
            Tips.Set(_clear, "Clear the filter|Show every file again (Esc)");
            _clear.Click += (s, e) => { Box.Text = ""; Box.Focus(); };
            Box.TextChanged += (s, e) => _clear.Visibility = Box.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Box.KeyDown += (s, e) => { if (e.Key == Key.Escape && Box.Text.Length > 0) { Box.Text = ""; e.Handled = true; } };
            Box.GotKeyboardFocus += (s, e) => BorderBrush = Theme.Accent;
            Box.LostKeyboardFocus += (s, e) => BorderBrush = Theme.Line;
            MouseLeftButtonDown += (s, e) => { Box.Focus(); };
            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(_clear, Dock.Right);
            dock.Children.Add(icon);
            dock.Children.Add(_clear);
            dock.Children.Add(Box);
            Child = dock;
        }
    }

    /// <summary>StackPanel with a gap between children.</summary>
    public class GapStack : StackPanel
    {
        public double Gap { get; set; }

        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
            for (int i = 0; i < Children.Count; i++)
            {
                if (!(Children[i] is FrameworkElement fe)) continue;
                var m = fe.Margin;
                double g = i == 0 ? 0 : Gap;
                fe.Margin = Orientation == Orientation.Vertical
                    ? new Thickness(m.Left, g, m.Right, m.Bottom)
                    : new Thickness(g, m.Top, m.Right, m.Bottom);
            }
        }
    }

    /// <summary>A flat button with rounded corners and a soft hover.</summary>
    public class FlatButton : Button
    {
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(FlatButton), new PropertyMetadata(new CornerRadius(10)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        private static ControlTemplate _template;

        private static ControlTemplate BuildTemplate()
        {
            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' " +
                "BorderThickness='{TemplateBinding BorderThickness}' " +
                "CornerRadius='{Binding CornerRadius, RelativeSource={RelativeSource TemplatedParent}}' SnapsToDevicePixels='True'>" +
                "<ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' " +
                "VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='False'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.86'/></Trigger>" +
                "<Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.7'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>" +
                "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='" + Theme.Hex(Theme.AccentColor) + "'/></Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        public FlatButton()
        {
            if (_template == null) _template = BuildTemplate();
            Template = _template;
            Background = Theme.Subtle;
            Foreground = Theme.Text;
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(1);
            Padding = new Thickness(14, 0, 14, 0);
            MinHeight = 34;
            FontSize = 13;
            FontWeight = FontWeights.SemiBold;
            FontFamily = Theme.Font;
            Cursor = Cursors.Hand;
            HorizontalContentAlignment = HorizontalAlignment.Center;
            VerticalContentAlignment = VerticalAlignment.Center;
            FocusVisualStyle = null;
        }

        public static FlatButton Primary(string text, Action onClick = null, string tip = null)
        {
            var b = new FlatButton { Content = text, Background = Theme.Accent, Foreground = Theme.Dark, MinHeight = 38, Padding = new Thickness(18, 0, 18, 0) };
            if (tip != null) Tips.Set(b, tip);
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }

        public static FlatButton Secondary(string text, Action onClick = null, string tip = null)
        {
            var b = new FlatButton
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderBrush = Theme.Brush(Theme.IsLight ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                MinHeight = 38
            };
            if (tip != null) Tips.Set(b, tip);
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>Icon with an optional text label next to it.</summary>
        public static FlatButton Tool(string iconData, string text, string tooltip, Action onClick = null)
        {
            UIElement content = string.IsNullOrEmpty(text)
                ? UI.Icon(iconData, 16, Theme.Text)
                : UI.H(8, UI.Icon(iconData, 16, Theme.Text), UI.Text(text, 13, Theme.Text, FontWeights.SemiBold));
            var b = new FlatButton
            {
                Content = content,
                Background = Brushes.Transparent,
                Padding = string.IsNullOrEmpty(text) ? new Thickness(0) : new Thickness(10, 0, 12, 0),
                MinWidth = 34,
                Height = 34
            };
            System.Windows.Automation.AutomationProperties.SetName(b, text ?? (tooltip ?? "").Split('|')[0]);
            Tips.Set(b, tooltip);
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }
    }

    public static class Icons
    {
        public const string Folder = "M3 7a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z";
        public const string FolderPlus = "M3 7a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1zM12 11v5M9.5 13.5h5";
        public const string File = "M6 3h9l4 4v14H6zM14 3v5h5";
        public const string Up = "M12 19V5M6 11l6-6 6 6";
        public const string Back = "M19 12H5M11 6l-6 6 6 6";
        public const string Home = "M3 11l9-7 9 7M5 10v10h14V10";
        public const string Refresh = "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7";
        public const string Server = "M4 4h16v6H4zM4 14h16v6H4zM8 7h.01M8 17h.01";
        public const string Plug = "M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4";
        public const string Unplug = "M6 8h12v3a6 6 0 0 1-12 0zM12 17v4M3 3l18 18";
        public const string Sync = "M4 12a8 8 0 0 1 14-5.3M20 4v5h-5M20 12a8 8 0 0 1-14 5.3M4 20v-5h5";
        public const string Terminal = "M4 5h16v14H4zM7 9l3 3-3 3M12 15h5";
        public const string Settings = "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1";
        public const string Trash = "M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13";
        public const string Close = "M6 6l12 12M18 6L6 18";
        public const string Play = "M7 4l13 8-13 8z";
        public const string Pause = "M8 5v14M16 5v14";
        public const string Check = "M5 12l5 5 9-10";
        public const string Plus = "M12 5v14M5 12h14";
        public const string Edit = "M4 20h4L19 9l-4-4L4 16zM14 6l4 4";
        public const string Right = "M5 12h14M13 6l6 6-6 6";
        public const string Left = "M19 12H5M11 6l-6 6 6 6";
        public const string Search = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14M20 20l-4-4";
        public const string Lock = "M6 11h12v9H6zM8 11V7a4 4 0 0 1 8 0v4";
        public const string Copy = "M8 8h12v12H8zM4 16V4h12";
        public const string Eye = "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6";
        public const string Download = "M12 4v11M7 10l5 5 5-5M5 20h14";
        public const string Upload = "M12 20V9M7 14l5-5 5 5M5 4h14";
        public const string Retry = "M4 4v6h6M4.5 15a8 8 0 1 0 1.9-8.3L4 10";
        public const string Key = "M8 14a4 4 0 1 1 0-8a4 4 0 0 1 0 8zM11 10h10M18 10v3M21 10v4";
        public const string Minus = "M5 12h14";
        public const string Star = "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z";
        public const string Window = "M3 5h18v14H3zM3 9h18";
        public const string Tab = "M3 19V7h7l2 3h9v9zM3 7V5h7v2";
        public const string Lines = "M5 6h14M5 11h14M5 16h9";
    }
}
