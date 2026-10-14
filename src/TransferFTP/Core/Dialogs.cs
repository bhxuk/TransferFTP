using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TransferFTP
{
    public static class Native
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void StyleWindow(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = Theme.IsLight ? 0 : 1;
                DwmSetWindowAttribute(h, 20, ref on, sizeof(int));      // dark title bar
                int round = 2;
                DwmSetWindowAttribute(h, 33, ref round, sizeof(int));   // rounded corners
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class NetResource
        {
            public int Scope = 0;
            public int Type = 1;            // RESOURCETYPE_DISK
            public int DisplayType = 0;
            public int Usage = 0;
            public string LocalName = null;
            public string RemoteName;
            public string Comment = null;
            public string Provider = null;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);

        /// <summary>Signs in to a network share with the given account. Returns 0 on success.</summary>
        public static int ConnectShare(string unc, string user, string password)
        {
            var nr = new NetResource { RemoteName = unc };
            int r = WNetAddConnection2(nr, password, user, 0);
            if (r == 1219)
            {
                // Already connected with different credentials: drop and retry once.
                WNetCancelConnection2(unc, 0, true);
                r = WNetAddConnection2(nr, password, user, 0);
            }
            return r;
        }

        public static void DisconnectShare(string unc)
        {
            try { WNetCancelConnection2(unc, 0, true); } catch { }
        }
    }

    /// <summary>Base window with the app look: dark title bar, theme colours, icon.</summary>
    public class ThemedWindow : Window
    {
        public ThemedWindow()
        {
            Background = Theme.Bg;
            Foreground = Theme.Text;
            FontFamily = Theme.Font;
            FontSize = 13;
            UseLayoutRounding = true;
            try { Icon = Theme.AppIcon(); } catch { }
            SourceInitialized += (s, e) => Native.StyleWindow(this);
            var main = Application.Current?.MainWindow;
            if (main != null && main != this && main.IsLoaded)
            {
                Owner = main;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>Small modal dialogs. All of them are safe to call from any thread.</summary>
    public static class Dialogs
    {
        public static T OnUi<T>(Func<T> f)
        {
            var d = Application.Current.Dispatcher;
            return d.CheckAccess() ? f() : d.Invoke(f);
        }

        private static ThemedWindow Frame(string title, double width)
        {
            return new ThemedWindow
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
        }

        private static StackPanel Buttons(params FlatButton[] buttons)
        {
            var row = UI.H(10, buttons);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 8, 0, 0);
            return row;
        }

        public static void Info(string title, string message) => OnUi(() =>
        {
            var w = Frame(title, 460);
            var ok = FlatButton.Primary("OK", () => w.Close());
            ok.IsDefault = true;
            w.Content = new Border { Padding = new Thickness(24), Child = UI.V(14, UI.Text(title, 18, Theme.Text, FontWeights.SemiBold, true), UI.Text(message, 13, Theme.Muted), Buttons(ok)) };
            w.ShowDialog();
            return true;
        });

        public static bool Confirm(string title, string message, string yes = "Yes", bool danger = false) => OnUi(() =>
        {
            bool result = false;
            var w = Frame(title, 460);
            var y = FlatButton.Primary(yes, () => { result = true; w.Close(); });
            if (danger) { y.Background = Theme.Red; y.Foreground = Brushes.White; }
            var n = FlatButton.Secondary("Cancel", () => w.Close());
            n.IsCancel = true;
            y.IsDefault = true;
            w.Content = new Border { Padding = new Thickness(24), Child = UI.V(14, UI.Text(title, 18, Theme.Text, FontWeights.SemiBold, true), UI.Text(message, 13, Theme.Muted), Buttons(n, y)) };
            w.ShowDialog();
            return result;
        });

        /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
        public static string Prompt(string title, string message, string value = "", bool password = false) => OnUi(() =>
        {
            string result = null;
            var w = Frame(title, 480);
            var box = UI.Input(value);
            var pwd = UI.Password();
            Control input = password ? (Control)pwd : box;
            var ok = FlatButton.Primary("OK", () => { result = password ? pwd.Password : box.Text; w.Close(); });
            ok.IsDefault = true;
            var cancel = FlatButton.Secondary("Cancel", () => w.Close());
            cancel.IsCancel = true;
            w.Content = new Border { Padding = new Thickness(24), Child = UI.V(14, UI.Text(title, 18, Theme.Text, FontWeights.SemiBold, true), UI.Text(message, 13, Theme.Muted), input, Buttons(cancel, ok)) };
            w.Loaded += (s, e) =>
            {
                input.Focus();
                if (!password)
                {
                    // Select the name without its extension, like Explorer does.
                    int dot = box.Text.LastIndexOf('.');
                    if (dot > 0) box.Select(0, dot); else box.SelectAll();
                }
            };
            w.ShowDialog();
            return result;
        });

        /// <summary>Shows a list of choices as buttons. Returns the index picked, or -1. "remember" reports the tick box.</summary>
        public static int Choose(string title, string message, string[] choices, out bool remember, string rememberText = null)
        {
            bool rem = false;
            int pick = OnUi(() =>
            {
                int result = -1;
                var w = Frame(title, 540);
                var tick = rememberText == null ? null : new CheckBox { Content = rememberText };
                var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
                for (int i = 0; i < choices.Length; i++)
                {
                    int idx = i;
                    var b = i == 0 ? FlatButton.Primary(choices[i]) : FlatButton.Secondary(choices[i]);
                    b.Margin = new Thickness(8, 0, 0, 8);
                    b.Click += (s, e) => { result = idx; w.Close(); };
                    row.Children.Add(b);
                }
                var body = UI.V(14, UI.Text(title, 18, Theme.Text, FontWeights.SemiBold, true), UI.Text(message, 13, Theme.Muted));
                if (tick != null) body.Children.Add(tick);
                body.Children.Add(row);
                w.Content = new Border { Padding = new Thickness(24), Child = body };
                w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
                w.ShowDialog();
                rem = tick?.IsChecked == true;
                return result;
            });
            remember = rem;
            return pick;
        }
    }
}
