using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TransferFTP
{
    /// <summary>Draws a VtEmulator screen in colour and turns keys into what a Linux shell expects.</summary>
    public class TerminalView : FrameworkElement
    {
        public VtEmulator Emu { get; private set; }
        public readonly object Gate = new object();

        /// <summary>Text typed or pasted, to be sent to the server.</summary>
        public event Action<string> Input;

        /// <summary>The visible size in characters changed.</summary>
        public event Action<int, int> SizeInChars;

        private readonly Typeface _regular, _bold, _italic, _boldItalic;
        private double _fontSize = 14;
        private double _cellW = 8, _cellH = 18, _baseline = 14;
        private double _dip = 1;
        private int _scroll;                 // lines scrolled back from the bottom
        private bool _skipSpace;
        private bool _focused;
        private bool _blinkOn = true;

        // Selection, in absolute line index and column.
        private bool _selecting;
        private (int line, int col) _selA, _selB;
        private bool _hasSelection;

        private static readonly Color[] Base16 =
        {
            Color.FromRgb(0x1D, 0x20, 0x27), Color.FromRgb(0xE2, 0x58, 0x5F), Color.FromRgb(0x8F, 0xD1, 0x6A), Color.FromRgb(0xFF, 0xB5, 0x47),
            Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0xC0, 0x7C, 0xF0), Color.FromRgb(0x5E, 0xD3, 0xC3), Color.FromRgb(0xD8, 0xD6, 0xD0),
            Color.FromRgb(0x6B, 0x70, 0x7C), Color.FromRgb(0xFF, 0x7A, 0x80), Color.FromRgb(0xB4, 0xEC, 0x8C), Color.FromRgb(0xFF, 0xD0, 0x80),
            Color.FromRgb(0x8C, 0xAE, 0xFF), Color.FromRgb(0xDB, 0xA6, 0xFF), Color.FromRgb(0x8E, 0xEC, 0xDF), Color.FromRgb(0xFF, 0xFF, 0xFF)
        };

        private static readonly Color DefaultFg = Color.FromRgb(0xE8, 0xE6, 0xE1);
        private static readonly Color DefaultBg = Color.FromRgb(0x0B, 0x0D, 0x11);
        private static readonly SolidColorBrush[] PaletteBrushes = BuildPalette();
        private static readonly SolidColorBrush DefaultFgBrush = Freeze(new SolidColorBrush(DefaultFg));
        private static readonly SolidColorBrush DefaultBgBrush = Freeze(new SolidColorBrush(DefaultBg));
        private static readonly SolidColorBrush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xB5, 0x47)));

        public TerminalView()
        {
            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.IBeam;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
            KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
            InputMethod.SetIsInputMethodEnabled(this, true);
            var family = Theme.Mono;
            _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            _italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
            _boldItalic = new Typeface(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
            Emu = new VtEmulator(120, 40);
            Measure();
            SizeChanged += (s, e) => FitToSize();
            GotKeyboardFocus += (s, e) => { _focused = true; InvalidateVisual(); };
            LostKeyboardFocus += (s, e) => { _focused = false; InvalidateVisual(); };
        }

        private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        private static SolidColorBrush[] BuildPalette()
        {
            var p = new SolidColorBrush[256];
            for (int i = 0; i < 16; i++) p[i] = Freeze(new SolidColorBrush(Base16[i]));
            int[] steps = { 0, 95, 135, 175, 215, 255 };
            for (int i = 16; i < 232; i++)
            {
                int v = i - 16;
                p[i] = Freeze(new SolidColorBrush(Color.FromRgb((byte)steps[v / 36], (byte)steps[v / 6 % 6], (byte)steps[v % 6])));
            }
            for (int i = 232; i < 256; i++)
            {
                byte g = (byte)(8 + (i - 232) * 10);
                p[i] = Freeze(new SolidColorBrush(Color.FromRgb(g, g, g)));
            }
            return p;
        }

        public double FontSizePoints
        {
            get => _fontSize;
            set { _fontSize = Math.Max(8, Math.Min(32, value)); Measure(); FitToSize(); InvalidateVisual(); }
        }

        private void Measure()
        {
            try { _dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { _dip = 1; }
            var ft = Text("MMMMMMMMMM", _regular, DefaultFgBrush);
            _cellW = ft.WidthIncludingTrailingWhitespace / 10.0;
            _cellH = Math.Ceiling(ft.Height * 1.08);
            _baseline = ft.Baseline + (_cellH - ft.Height) / 2;
        }

        private FormattedText Text(string s, Typeface face, Brush brush)
            => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _fontSize, brush, _dip);

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Measure();
            FitToSize();
        }

        private const double Pad = 10;

        private void FitToSize()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            int cols = Math.Max(20, (int)((ActualWidth - Pad * 2) / _cellW));
            int rows = Math.Max(5, (int)((ActualHeight - Pad * 2) / _cellH));
            bool changed;
            lock (Gate)
            {
                changed = cols != Emu.Cols || rows != Emu.Rows;
                if (changed) Emu.Resize(cols, rows);
            }
            if (changed) SizeInChars?.Invoke(cols, rows);
            InvalidateVisual();
        }

        public void Blink()
        {
            _blinkOn = !_blinkOn;
            if (_focused) InvalidateVisual();
        }

        // ---------------- Drawing ----------------

        private Brush Fore(in Cell c)
        {
            int fg = c.Fg;
            // Bold text in one of the first 8 colours is shown in the bright version, like most terminals.
            if ((c.Flags & Cell.Bold) != 0 && fg >= 0 && fg < 8) fg += 8;
            return ColourBrush(fg, DefaultFgBrush);
        }

        private static Brush ColourBrush(int v, SolidColorBrush fallback)
        {
            if (v < 0) return fallback;
            if (v < 256) return PaletteBrushes[v];
            var b = new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
            b.Freeze();
            return b;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(DefaultBgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
            lock (Gate)
            {
                var emu = Emu;
                int total = emu.TotalLines;
                _scroll = Math.Max(0, Math.Min(_scroll, emu.ScrollbackLines));
                int first = total - emu.Rows - _scroll;
                var sb = new StringBuilder();
                for (int r = 0; r < emu.Rows; r++)
                {
                    var line = emu.Line(first + r);
                    if (line == null) continue;
                    double y = Pad + r * _cellH;
                    int x = 0;
                    while (x < line.Length)
                    {
                        int start = x;
                        var style = line[x];
                        sb.Clear();
                        while (x < line.Length && line[x].SameStyle(style)) { sb.Append(line[x].C == '\0' ? ' ' : line[x].C); x++; }
                        DrawRun(dc, style, sb.ToString(), Pad + start * _cellW, y);
                    }
                    if (_hasSelection) DrawSelection(dc, first + r, y, line.Length);
                }

                // Cursor: a solid block when focused, an outline when not.
                if (_scroll == 0 && emu.CursorVisible)
                {
                    var rect = new Rect(Pad + emu.CursorX * _cellW, Pad + emu.CursorY * _cellH, _cellW, _cellH);
                    if (_focused && _blinkOn)
                    {
                        dc.DrawRectangle(Theme.Accent, null, rect);
                        var line = emu.Line(total - emu.Rows + emu.CursorY);
                        if (line != null && emu.CursorX < line.Length && line[emu.CursorX].C != ' ')
                            dc.DrawText(Text(line[emu.CursorX].C.ToString(), _regular, Theme.Dark), new Point(rect.X, rect.Y + _baseline - Baseline()));
                    }
                    else if (!_focused)
                    {
                        dc.DrawRectangle(null, new Pen(Theme.Accent, 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
                    }
                }

                if (_scroll > 0)
                {
                    var tag = Text("  " + _scroll + " lines up  ·  scroll down or type to return  ", _regular, Theme.Dark);
                    var r = new Rect(ActualWidth - tag.Width - 16, 8, tag.Width + 8, _cellH + 4);
                    dc.DrawRoundedRectangle(Theme.Accent, null, r, 6, 6);
                    dc.DrawText(tag, new Point(r.X + 4, r.Y + 2));
                }
            }
        }

        private double _baselineOfFont = -1;

        private double Baseline()
        {
            if (_baselineOfFont < 0) _baselineOfFont = Text("M", _regular, DefaultFgBrush).Baseline;
            return _baselineOfFont;
        }

        private void DrawRun(DrawingContext dc, in Cell style, string text, double x, double y)
        {
            Brush fg = Fore(style);
            Brush bg = style.Bg < 0 ? null : ColourBrush(style.Bg, DefaultBgBrush);
            if ((style.Flags & Cell.Inverse) != 0)
            {
                var f = fg;
                fg = bg ?? DefaultBgBrush;
                bg = f;
            }
            double w = text.Length * _cellW;
            if (bg != null) dc.DrawRectangle(bg, null, new Rect(x, y, w + 0.5, _cellH + 0.5));
            if (text.Trim().Length == 0 && (style.Flags & Cell.Underline) == 0) return;

            bool bold = (style.Flags & Cell.Bold) != 0, italic = (style.Flags & Cell.Italic) != 0;
            var face = bold ? (italic ? _boldItalic : _bold) : italic ? _italic : _regular;
            if ((style.Flags & Cell.Dim) != 0) dc.PushOpacity(0.6);

            // Draw each character in its own cell so columns stay lined up even when a fallback font is used.
            var ft = Text(text, face, fg);
            if (Math.Abs(ft.WidthIncludingTrailingWhitespace - w) < 0.5)
                dc.DrawText(ft, new Point(x, y + _baseline - ft.Baseline));
            else
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == ' ') continue;
                    var one = Text(text[i].ToString(), face, fg);
                    dc.DrawText(one, new Point(x + i * _cellW, y + _baseline - one.Baseline));
                }

            if ((style.Flags & Cell.Underline) != 0)
                dc.DrawRectangle(fg, null, new Rect(x, y + _cellH - 2, w, 1));
            if ((style.Flags & Cell.Strike) != 0)
                dc.DrawRectangle(fg, null, new Rect(x, y + _cellH / 2, w, 1));
            if ((style.Flags & Cell.Dim) != 0) dc.Pop();
        }

        private void DrawSelection(DrawingContext dc, int absLine, double y, int cols)
        {
            var (s, e) = Ordered();
            if (absLine < s.line || absLine > e.line) return;
            int from = absLine == s.line ? s.col : 0;
            int to = absLine == e.line ? e.col : cols;
            if (to <= from) return;
            dc.DrawRectangle(SelectionBrush, null, new Rect(Pad + from * _cellW, y, (to - from) * _cellW, _cellH));
        }

        private ((int line, int col), (int line, int col)) Ordered()
        {
            var a = _selA;
            var b = _selB;
            return a.line < b.line || (a.line == b.line && a.col <= b.col) ? (a, b) : (b, a);
        }

        // ---------------- Selection and copy ----------------

        private (int line, int col) CellAt(Point p)
        {
            lock (Gate)
            {
                int row = Math.Max(0, Math.Min(Emu.Rows - 1, (int)((p.Y - Pad) / _cellH)));
                int col = Math.Max(0, Math.Min(Emu.Cols, (int)Math.Round((p.X - Pad) / _cellW)));
                int first = Emu.TotalLines - Emu.Rows - _scroll;
                return (first + row, col);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            if (e.ClickCount == 2) { SelectWord(CellAt(e.GetPosition(this))); e.Handled = true; return; }
            _selA = _selB = CellAt(e.GetPosition(this));
            _selecting = true;
            _hasSelection = false;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_selecting) return;
            var p = e.GetPosition(this);
            // Dragging past the top or bottom scrolls the scrollback.
            if (p.Y < 0) Scroll(1);
            else if (p.Y > ActualHeight) Scroll(-1);
            _selB = CellAt(p);
            _hasSelection = _selA != _selB;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_selecting) { _selecting = false; ReleaseMouseCapture(); }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            // Like PuTTY: right-click copies a selection, or pastes when nothing is selected.
            if (_hasSelection) CopySelection(); else Paste();
            e.Handled = true;
        }

        private void SelectWord((int line, int col) at)
        {
            lock (Gate)
            {
                var line = Emu.Line(at.line);
                if (line == null) return;
                int c = Math.Min(at.col, line.Length - 1);
                bool Word(char ch) => !char.IsWhiteSpace(ch) && ch != '\0';
                if (c < 0 || !Word(line[c].C)) return;
                int s = c, e = c;
                while (s > 0 && Word(line[s - 1].C)) s--;
                while (e < line.Length - 1 && Word(line[e + 1].C)) e++;
                _selA = (at.line, s);
                _selB = (at.line, e + 1);
                _hasSelection = true;
            }
            InvalidateVisual();
        }

        public bool HasSelection => _hasSelection;

        public void CopySelection()
        {
            if (!_hasSelection) return;
            var sb = new StringBuilder();
            lock (Gate)
            {
                var (s, e) = Ordered();
                for (int l = s.line; l <= e.line; l++)
                {
                    var line = Emu.Line(l);
                    if (line == null) continue;
                    int from = l == s.line ? s.col : 0;
                    int to = l == e.line ? Math.Min(e.col, line.Length) : line.Length;
                    var row = new StringBuilder();
                    for (int x = from; x < to; x++) row.Append(line[x].C == '\0' ? ' ' : line[x].C);
                    sb.Append(row.ToString().TrimEnd());
                    if (l < e.line) sb.Append(Environment.NewLine);
                }
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
            _hasSelection = false;
            InvalidateVisual();
        }

        public void Paste()
        {
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { text = null; }
            if (string.IsNullOrEmpty(text)) return;
            text = text.Replace("\r\n", "\r").Replace("\n", "\r");
            bool bracketed;
            lock (Gate) bracketed = Emu.BracketedPaste;
            Send(bracketed ? "\x1b[200~" + text + "\x1b[201~" : text);
        }

        // ---------------- Scrolling ----------------

        private void Scroll(int lines)
        {
            lock (Gate) _scroll = Math.Max(0, Math.Min(Emu.ScrollbackLines, _scroll + lines));
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            int lines = e.Delta / 40;
            bool alt;
            lock (Gate) alt = Emu.AltScreen;
            if (alt)
            {
                // Full-screen apps (less, nano, htop) get arrow keys instead.
                string key = lines > 0 ? Arrow('A') : Arrow('B');
                for (int i = 0; i < Math.Abs(lines); i++) Input?.Invoke(key);
            }
            else Scroll(lines);
            e.Handled = true;
        }

        // ---------------- Keyboard ----------------

        private void Send(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (_scroll != 0) { _scroll = 0; InvalidateVisual(); }
            if (_hasSelection) { _hasSelection = false; InvalidateVisual(); }
            _blinkOn = true;
            Input?.Invoke(s);
        }

        private string Arrow(char c)
        {
            bool app;
            lock (Gate) app = Emu.AppCursorKeys;
            return (app ? "\x1bO" : "\x1b[") + c;
        }

        private static int Mod(ModifierKeys m)
        {
            int v = 1;
            if ((m & ModifierKeys.Shift) != 0) v += 1;
            if ((m & ModifierKeys.Alt) != 0) v += 2;
            if ((m & ModifierKeys.Control) != 0) v += 4;
            return v;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            _skipSpace = false;
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int m = Mod(mods);
            string seq = null;

            // Copy, paste and font size.
            if (ctrl && shift && key == Key.C) { CopySelection(); e.Handled = true; return; }
            if (ctrl && key == Key.C && _hasSelection) { CopySelection(); e.Handled = true; return; }
            if ((ctrl && shift && key == Key.V) || (shift && key == Key.Insert) || (ctrl && key == Key.V)) { Paste(); e.Handled = true; return; }
            if (ctrl && (key == Key.OemPlus || key == Key.Add)) { FontSizePoints += 1; e.Handled = true; return; }
            if (ctrl && (key == Key.OemMinus || key == Key.Subtract)) { FontSizePoints -= 1; e.Handled = true; return; }
            if (ctrl && (key == Key.D0 || key == Key.NumPad0)) { FontSizePoints = 14; e.Handled = true; return; }
            if (shift && key == Key.PageUp) { lock (Gate) Scroll(Emu.Rows - 1); e.Handled = true; return; }
            if (shift && key == Key.PageDown) { lock (Gate) Scroll(-(Emu.Rows - 1)); e.Handled = true; return; }

            string Csi(string plain, char final) => m > 1 ? "\x1b[1;" + m + final : plain;
            string Tilde(int code) => m > 1 ? "\x1b[" + code + ";" + m + "~" : "\x1b[" + code + "~";

            switch (key)
            {
                case Key.Space:
                    // A Windows text box swallows Space before it becomes text, so send it from here,
                    // and ignore the text event if one follows.
                    if (!alt) { seq = ctrl ? "\0" : " "; _skipSpace = !ctrl; }
                    else seq = "\x1b ";
                    break;
                case Key.Enter: seq = alt ? "\x1b\r" : "\r"; break;
                case Key.Back: seq = ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f"; break;
                case Key.Tab: seq = shift ? "\x1b[Z" : "\t"; break;
                case Key.Escape: seq = "\x1b"; break;
                case Key.Up: seq = m > 1 ? Csi(null, 'A') : Arrow('A'); break;
                case Key.Down: seq = m > 1 ? Csi(null, 'B') : Arrow('B'); break;
                case Key.Right: seq = m > 1 ? Csi(null, 'C') : Arrow('C'); break;
                case Key.Left: seq = m > 1 ? Csi(null, 'D') : Arrow('D'); break;
                case Key.Home: seq = m > 1 ? Csi(null, 'H') : Arrow('H'); break;
                case Key.End: seq = m > 1 ? Csi(null, 'F') : Arrow('F'); break;
                case Key.Insert: seq = Tilde(2); break;
                case Key.Delete: seq = Tilde(3); break;
                case Key.PageUp: seq = Tilde(5); break;
                case Key.PageDown: seq = Tilde(6); break;
                case Key.F1: seq = m > 1 ? Csi(null, 'P') : "\x1bOP"; break;
                case Key.F2: seq = m > 1 ? Csi(null, 'Q') : "\x1bOQ"; break;
                case Key.F3: seq = m > 1 ? Csi(null, 'R') : "\x1bOR"; break;
                case Key.F4: seq = m > 1 ? Csi(null, 'S') : "\x1bOS"; break;
                case Key.F5: seq = Tilde(15); break;
                case Key.F6: seq = Tilde(17); break;
                case Key.F7: seq = Tilde(18); break;
                case Key.F8: seq = Tilde(19); break;
                case Key.F9: seq = Tilde(20); break;
                case Key.F10: seq = Tilde(21); break;
                case Key.F11: seq = Tilde(23); break;
                case Key.F12: seq = Tilde(24); break;
                default:
                    if (ctrl && !alt && key >= Key.A && key <= Key.Z) seq = ((char)(key - Key.A + 1)).ToString();
                    else if (ctrl && key == Key.OemOpenBrackets) seq = "\x1b";
                    else if (ctrl && key == Key.Oem5) seq = "\x1c";
                    else if (ctrl && key == Key.OemCloseBrackets) seq = "\x1d";
                    else if (ctrl && key == Key.D6) seq = "\x1e";
                    else if (ctrl && key == Key.OemMinus) seq = "\x1f";
                    else if (alt && !ctrl && key >= Key.A && key <= Key.Z)
                    {
                        char c = (char)('a' + (key - Key.A));
                        seq = "\x1b" + (shift ? char.ToUpperInvariant(c) : c);
                    }
                    else if (alt && !ctrl && key >= Key.D0 && key <= Key.D9 && !shift)
                        seq = "\x1b" + (char)('0' + (key - Key.D0));
                    break;
            }
            if (seq != null)
            {
                Send(seq);
                e.Handled = true;
            }
        }

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            string t = e.Text;
            if (string.IsNullOrEmpty(t)) return;
            if (_skipSpace && t == " ") { _skipSpace = false; e.Handled = true; return; }
            _skipSpace = false;
            var sb = new StringBuilder();
            foreach (char c in t) if (c >= ' ' && c != '\x7f') sb.Append(c);
            if (sb.Length > 0) Send(sb.ToString());
            e.Handled = true;
        }
    }
}
