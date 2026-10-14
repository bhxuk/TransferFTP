using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Renci.SshNet;

namespace TransferFTP
{
    /// <summary>
    /// A small terminal: enough of VT100 for a shell prompt, line editing, tab completion and command output.
    /// Full-screen programs (vim, htop) are better in Windows Terminal, which the toolbar can open.
    /// </summary>
    public class TerminalScreen
    {
        private readonly List<StringBuilder> _lines = new List<StringBuilder> { new StringBuilder() };
        private int _row, _col;
        private int _state;          // 0 normal, 1 after ESC, 2 CSI, 3 OSC, 4 skip one char
        private readonly StringBuilder _params = new StringBuilder();
        private bool _oscEsc;
        public int Rows { get; set; } = 40;
        public int MaxLines { get; set; } = 5000;
        public bool Dirty { get; set; }

        public int CursorRow => _row;
        public int CursorCol => _col;
        public int LineCount => _lines.Count;

        private int ScreenTop => Math.Max(0, _lines.Count - Rows);

        public void Feed(string text)
        {
            foreach (char ch in text) Put(ch);
            Dirty = true;
        }

        private StringBuilder Line
        {
            get
            {
                while (_row >= _lines.Count) _lines.Add(new StringBuilder());
                return _lines[_row];
            }
        }

        private void Put(char ch)
        {
            switch (_state)
            {
                case 1:
                    if (ch == '[') { _state = 2; _params.Clear(); }
                    else if (ch == ']') { _state = 3; _oscEsc = false; }
                    else if (ch == '(' || ch == ')' || ch == '#') _state = 4;
                    else _state = 0;
                    return;
                case 2:
                    if ((ch >= '0' && ch <= '9') || ch == ';' || ch == '?' || ch == '>' || ch == '=') { _params.Append(ch); return; }
                    Csi(ch, _params.ToString());
                    _state = 0;
                    return;
                case 3:
                    if (ch == '\a') { _state = 0; return; }
                    if (_oscEsc && ch == '\\') { _state = 0; return; }
                    _oscEsc = ch == '\x1b';
                    return;
                case 4:
                    _state = 0;
                    return;
            }

            switch (ch)
            {
                case '\x1b': _state = 1; return;
                case '\r': _col = 0; return;
                case '\n': NewLine(); return;
                case '\b': if (_col > 0) _col--; return;
                case '\a': return;
                case '\t':
                    int next = (_col / 8 + 1) * 8;
                    while (_col < next) Write(' ');
                    return;
            }
            if (ch < ' ') return;
            Write(ch);
        }

        private void Write(char ch)
        {
            var line = Line;
            while (line.Length < _col) line.Append(' ');
            if (_col < line.Length) line[_col] = ch; else line.Append(ch);
            _col++;
        }

        private void NewLine()
        {
            _row++;
            while (_row >= _lines.Count) _lines.Add(new StringBuilder());
            if (_lines.Count > MaxLines)
            {
                int drop = _lines.Count - MaxLines;
                _lines.RemoveRange(0, drop);
                _row -= drop;
            }
        }

        private static int Arg(string[] a, int i, int def)
        {
            if (i >= a.Length || string.IsNullOrEmpty(a[i])) return def;
            return int.TryParse(a[i].TrimStart('?', '>', '='), out int v) ? v : def;
        }

        private void Csi(char cmd, string p)
        {
            var a = p.Split(';');
            int n = Arg(a, 0, 1);
            var line = Line;
            switch (cmd)
            {
                case 'A': _row = Math.Max(ScreenTop, _row - n); break;
                case 'B': _row += n; break;
                case 'C': _col += n; break;
                case 'D': _col = Math.Max(0, _col - n); break;
                case 'G': _col = Math.Max(0, n - 1); break;
                case 'H':
                case 'f':
                    _row = ScreenTop + Math.Max(0, Arg(a, 0, 1) - 1);
                    _col = Math.Max(0, Arg(a, 1, 1) - 1);
                    break;
                case 'K':
                    int mode = Arg(a, 0, 0);
                    if (mode == 0) { if (_col < line.Length) line.Length = _col; }
                    else if (mode == 1) { for (int i = 0; i < Math.Min(_col + 1, line.Length); i++) line[i] = ' '; }
                    else line.Clear();
                    break;
                case 'J':
                    int jm = Arg(a, 0, 0);
                    if (jm == 2 || jm == 3)
                    {
                        // Clear screen: push the old content up into the scrollback.
                        for (int i = 0; i < Rows; i++) _lines.Add(new StringBuilder());
                        _row = ScreenTop;
                        _col = 0;
                    }
                    else if (jm == 0)
                    {
                        if (_col < line.Length) line.Length = _col;
                        for (int i = _row + 1; i < _lines.Count; i++) _lines[i].Clear();
                    }
                    break;
                case 'P':
                    if (_col < line.Length) line.Remove(_col, Math.Min(n, line.Length - _col));
                    break;
                case '@':
                    if (_col <= line.Length) line.Insert(_col, new string(' ', n));
                    break;
                case 'X':
                    for (int i = _col; i < Math.Min(line.Length, _col + n); i++) line[i] = ' ';
                    break;
                default:
                    break;   // colours (m), modes (h/l) and the rest are ignored
            }
        }

        public string Text(out int caret)
        {
            var sb = new StringBuilder();
            caret = 0;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i == _row) caret = sb.Length + Math.Min(_col, _lines[i].Length);
                sb.Append(_lines[i]);
                if (i < _lines.Count - 1) sb.Append('\n');
            }
            Dirty = false;
            return sb.ToString();
        }
    }

    public class TerminalWindow : ThemedWindow
    {
        private readonly Site _site;
        private readonly string _startDir;
        private readonly TextBox _screenBox;
        private readonly TextBlock _state;
        private readonly TerminalScreen _screen = new TerminalScreen();
        private readonly DispatcherTimer _render;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly object _gate = new object();
        private SshClient _ssh;
        private ShellStream _shell;

        public TerminalWindow(Site site, string startDir)
        {
            _site = site;
            _startDir = startDir;
            Title = site.Name + " - Terminal";
            Width = 980;
            Height = 620;
            MinWidth = 520;
            MinHeight = 320;
            Background = Theme.Stage;

            _screenBox = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = Theme.Mono,
                FontSize = 14,
                Background = Theme.Stage,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 10, 14, 10),
                VerticalContentAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _screenBox.PreviewKeyDown += KeyDownHandler;
            _screenBox.PreviewTextInput += (s, e) => { if (!string.IsNullOrEmpty(e.Text) && e.Text[0] >= ' ') Send(e.Text); e.Handled = true; };
            _screenBox.MouseRightButtonUp += (s, e) => { PasteClipboard(); e.Handled = true; };
            CommandManager.AddPreviewExecutedHandler(_screenBox, (s, e) =>
            {
                if (e.Command == ApplicationCommands.Paste) { PasteClipboard(); e.Handled = true; }
            });

            _state = UI.Text("Connecting...", 12, Theme.Muted);
            _state.VerticalAlignment = VerticalAlignment.Center;
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
            var buttons = UI.H(4,
                FlatButton.Tool(Icons.Close, "Ctrl+C", "Send Ctrl+C (stop the running command)", () => Send("\x03")),
                FlatButton.Tool(Icons.Copy, "Paste", "Paste (or right-click)", PasteClipboard),
                FlatButton.Tool(Icons.Terminal, "Windows Terminal", "Open this server in Windows Terminal, for vim, htop and other full-screen apps", OpenExternal),
                FlatButton.Tool(Icons.Refresh, "Reconnect", "Reconnect", Start));
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(UI.H(10, UI.Icon(Icons.Terminal, 16, Theme.Accent), UI.Text(site.User + "@" + site.Host, 13, Theme.Text, FontWeights.SemiBold), _state));

            var root = new DockPanel();
            var barHost = new Border { Background = Theme.Bg, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
            DockPanel.SetDock(barHost, Dock.Top);
            root.Children.Add(barHost);
            root.Children.Add(_screenBox);
            Content = root;

            _render = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _render.Tick += (s, e) => Render();
            _render.Start();

            Loaded += (s, e) => { _screenBox.Focus(); Start(); };
            Closed += (s, e) => { _render.Stop(); Stop(); };
        }

        private void Start()
        {
            Stop();
            _state.Text = "Connecting...";
            Task.Run(() =>
            {
                var ssh = new SshClient(SshAuth.Build(_site));
                SshAuth.Guard(ssh, _site);
                ssh.Connect();
                var shell = ssh.CreateShellStream("xterm", 120, 40, 0, 0, 1 << 16);
                shell.DataReceived += (o, e) =>
                {
                    var chars = new char[_decoder.GetCharCount(e.Data, 0, e.Data.Length)];
                    _decoder.GetChars(e.Data, 0, e.Data.Length, chars, 0);
                    lock (_gate) _screen.Feed(new string(chars));
                };
                shell.Closed += (o, e) => Dispatcher.BeginInvoke(new Action(() => _state.Text = "Session ended"));
                lock (_gate) { _ssh = ssh; _shell = shell; }
                if (!string.IsNullOrEmpty(_startDir)) shell.Write("cd '" + _startDir.Replace("'", "'\\''") + "'\n");
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    string msg = TransferQueue.Friendly(t.Exception.GetBaseException());
                    _state.Text = "Not connected";
                    lock (_gate) _screen.Feed("\r\nCouldn't connect: " + msg + "\r\n");
                }
                else _state.Text = "Connected";
            }, TaskScheduler.FromCurrentSynchronizationContext());
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
            if (shell == null) return;
            try { shell.Write(s); shell.Flush(); }
            catch (Exception ex) { _state.Text = "Send failed: " + ex.Message; }
        }

        private void PasteClipboard()
        {
            try
            {
                if (Clipboard.ContainsText()) Send(Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r"));
            }
            catch { }
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

        private void KeyDownHandler(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            string seq = null;

            if (ctrl && key == Key.C && _screenBox.SelectionLength > 0) { _screenBox.Copy(); e.Handled = true; return; }
            if (ctrl && shift && key == Key.C) { _screenBox.Copy(); e.Handled = true; return; }
            if (ctrl && (key == Key.V || (shift && key == Key.V))) { PasteClipboard(); e.Handled = true; return; }

            switch (key)
            {
                case Key.Enter: seq = "\r"; break;
                case Key.Back: seq = "\x7f"; break;
                case Key.Tab: seq = "\t"; break;
                case Key.Escape: seq = "\x1b"; break;
                case Key.Up: seq = "\x1b[A"; break;
                case Key.Down: seq = "\x1b[B"; break;
                case Key.Right: seq = "\x1b[C"; break;
                case Key.Left: seq = "\x1b[D"; break;
                case Key.Home: seq = "\x1b[H"; break;
                case Key.End: seq = "\x1b[F"; break;
                case Key.Delete: seq = "\x1b[3~"; break;
                case Key.PageUp: if (!shift) seq = "\x1b[5~"; break;
                case Key.PageDown: if (!shift) seq = "\x1b[6~"; break;
                default:
                    if (ctrl && key >= Key.A && key <= Key.Z) seq = ((char)(key - Key.A + 1)).ToString();
                    break;
            }
            if (seq != null)
            {
                Send(seq);
                e.Handled = true;
            }
        }

        private void Render()
        {
            if (_screenBox.SelectionLength > 0) return;   // don't disturb a selection being copied
            string text;
            int caret;
            lock (_gate)
            {
                if (!_screen.Dirty) return;
                text = _screen.Text(out caret);
            }
            bool atBottom = _screenBox.VerticalOffset >= _screenBox.ExtentHeight - _screenBox.ViewportHeight - 4;
            _screenBox.Text = text;
            _screenBox.CaretIndex = Math.Min(caret, text.Length);
            if (atBottom) _screenBox.ScrollToEnd();
        }
    }
}
