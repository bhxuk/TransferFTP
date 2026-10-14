using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Renci.SshNet;

namespace TransferFTP
{
    /// <summary>An SSH terminal for a site: colours, cursor keys, and full-screen apps like nano, htop and vim.</summary>
    public class TerminalWindow : ThemedWindow
    {
        private readonly Site _site;
        private readonly string _startDir;
        private readonly TerminalView _view;
        private readonly TextBlock _state;
        private readonly DispatcherTimer _render, _blink, _resizeDebounce;
        private readonly object _gate = new object();
        private Decoder _decoder = Encoding.UTF8.GetDecoder();
        private SshClient _ssh;
        private ShellStream _shell;

        public TerminalWindow(Site site, string startDir)
        {
            _site = site;
            _startDir = startDir;
            Title = site.Name + " - Terminal";
            Width = 1000;
            Height = 640;
            MinWidth = 520;
            MinHeight = 320;
            Background = Theme.Stage;

            _view = new TerminalView();
            _view.Input += Send;
            _view.SizeInChars += (c, r) => { _resizeDebounce.Stop(); _resizeDebounce.Start(); };
            _view.Emu.Reply += Send;
            _view.Emu.TitleChanged += t => Dispatcher.BeginInvoke(new Action(() =>
            {
                Title = string.IsNullOrWhiteSpace(t) ? site.Name + " - Terminal" : t + " - " + site.Name;
            }));

            _state = UI.Text("Connecting...", 12, Theme.Muted);
            _state.VerticalAlignment = VerticalAlignment.Center;
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
            var buttons = UI.H(4,
                FlatButton.Tool(Icons.Close, "Ctrl+C", "Send Ctrl+C (stop the running command)", () => { Send("\x03"); _view.Focus(); }),
                FlatButton.Tool(Icons.Copy, "Paste", "Paste (Ctrl+V, or right-click)", () => { _view.Paste(); _view.Focus(); }),
                FlatButton.Tool(Icons.Plus, null, "Bigger text (Ctrl +)", () => { _view.FontSizePoints += 1; _view.Focus(); }),
                FlatButton.Tool(Icons.Minus, null, "Smaller text (Ctrl -)", () => { _view.FontSizePoints -= 1; _view.Focus(); }),
                FlatButton.Tool(Icons.Terminal, "Windows Terminal", "Open this server in Windows Terminal", OpenExternal),
                FlatButton.Tool(Icons.Refresh, "Reconnect", "Reconnect", Start));
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(UI.H(10, UI.Icon(Icons.Terminal, 16, Theme.Accent), UI.Text(site.User + "@" + site.Host, 13, Theme.Text, FontWeights.SemiBold), _state));

            var root = new DockPanel();
            var barHost = new Border { Background = Theme.Bg, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
            DockPanel.SetDock(barHost, Dock.Top);
            root.Children.Add(barHost);
            root.Children.Add(_view);
            Content = root;

            // Draw at most 60 times a second, only when something changed.
            _render = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _render.Tick += (s, e) =>
            {
                bool dirty;
                lock (_view.Gate) { dirty = _view.Emu.Dirty; _view.Emu.Dirty = false; }
                if (dirty) _view.InvalidateVisual();
            };
            _render.Start();
            _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            _blink.Tick += (s, e) => _view.Blink();
            _blink.Start();
            _resizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _resizeDebounce.Tick += (s, e) => { _resizeDebounce.Stop(); TellServerSize(); };

            Loaded += (s, e) => { _view.Focus(); Start(); };
            Activated += (s, e) => _view.Focus();
            Closed += (s, e) => { _render.Stop(); _blink.Stop(); _resizeDebounce.Stop(); Stop(); };
        }

        private void Start()
        {
            Stop();
            _state.Text = "Connecting...";
            _decoder = Encoding.UTF8.GetDecoder();
            int cols, rows;
            lock (_view.Gate) { cols = _view.Emu.Cols; rows = _view.Emu.Rows; }
            Task.Run(() =>
            {
                var ssh = new SshClient(SshAuth.Build(_site));
                SshAuth.Guard(ssh, _site);
                ssh.Connect();
                // "xterm-256color" tells the server we understand colours, so ls, the prompt and editors use them.
                var shell = ssh.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 1 << 16);
                shell.DataReceived += (o, e) =>
                {
                    var chars = new char[_decoder.GetCharCount(e.Data, 0, e.Data.Length)];
                    _decoder.GetChars(e.Data, 0, e.Data.Length, chars, 0);
                    lock (_view.Gate) _view.Emu.Feed(new string(chars));
                };
                shell.Closed += (o, e) => Dispatcher.BeginInvoke(new Action(() => _state.Text = "Session ended. Press Reconnect to start again."));
                lock (_gate) { _ssh = ssh; _shell = shell; }
                if (!string.IsNullOrEmpty(_startDir)) Send("cd '" + _startDir.Replace("'", "'\\''") + "' && clear\r");
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    string msg = TransferQueue.Friendly(t.Exception.GetBaseException());
                    _state.Text = "Not connected";
                    lock (_view.Gate) _view.Emu.Feed("\r\n\x1b[31mCouldn't connect: " + msg + "\x1b[0m\r\n");
                }
                else
                {
                    _state.Text = "Connected";
                    TellServerSize();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Lets the server know the window size so programs wrap and draw at the right width.</summary>
        private void TellServerSize()
        {
            ShellStream shell;
            lock (_gate) shell = _shell;
            if (shell == null) return;
            int cols, rows;
            lock (_view.Gate) { cols = _view.Emu.Cols; rows = _view.Emu.Rows; }
            try
            {
                // ChangeWindowSize is on newer SSH.NET versions; look it up so older ones still build.
                var m = shell.GetType().GetMethods().FirstOrDefault(x => x.Name == "ChangeWindowSize" && x.GetParameters().Length == 4);
                if (m != null)
                {
                    var p = m.GetParameters();
                    object Arg(int i, long v) => Convert.ChangeType(v, p[i].ParameterType);
                    m.Invoke(shell, new[] { Arg(0, cols), Arg(1, rows), Arg(2, 0), Arg(3, 0) });
                }
            }
            catch (Exception ex) { Log.Write("Terminal resize: " + ex.Message); }
        }

        private void Stop()
        {
            ShellStream shell;
            SshClient ssh;
            lock (_gate) { shell = _shell; ssh = _ssh; _shell = null; _ssh = null; }
            Task.Run(() =>
            {
                try { shell?.Dispose(); } catch { }
                try { ssh?.Disconnect(); } catch { }
                try { ssh?.Dispose(); } catch { }
            });
        }

        private void Send(string s)
        {
            ShellStream shell;
            lock (_gate) shell = _shell;
            if (shell == null || string.IsNullOrEmpty(s)) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                shell.Write(bytes, 0, bytes.Length);
                shell.Flush();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() => _state.Text = "Send failed: " + ex.Message));
            }
        }

        private void OpenExternal()
        {
            string target = (string.IsNullOrEmpty(_site.User) ? "" : _site.User + "@") + _site.Host;
            string args = "ssh " + (_site.Port != 22 ? "-p " + _site.Port + " " : "") +
                          (string.IsNullOrWhiteSpace(_site.KeyFile) ? "" : "-i \"" + _site.KeyFile + "\" ") + target;
            try { Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(new ProcessStartInfo("cmd.exe", "/k " + args) { UseShellExecute = true }); }
                catch (Exception ex) { Dialogs.Info("Couldn't open a terminal", ex.Message); }
            }
        }
    }
}
