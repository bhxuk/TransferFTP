using System;
using System.Collections.Generic;
using System.Text;

namespace TransferFTP
{
    /// <summary>One character on the terminal screen with its colours and style.</summary>
    public struct Cell
    {
        public char C;
        /// <summary>-1 = default, 0-255 = palette, otherwise 0x1000000 | RGB.</summary>
        public int Fg;
        public int Bg;
        public byte Flags;

        public const byte Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Strike = 32;

        public static Cell Blank(int bg) => new Cell { C = ' ', Fg = -1, Bg = bg };

        public bool SameStyle(in Cell o) => Fg == o.Fg && Bg == o.Bg && Flags == o.Flags;
    }

    /// <summary>
    /// An xterm-compatible screen: colours (16, 256 and true colour), cursor movement, scroll regions,
    /// the alternate screen used by nano, vim and htop, line drawing characters and scrollback.
    /// </summary>
    public class VtEmulator
    {
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        public int MaxScrollback { get; set; } = 5000;

        /// <summary>Main screen: scrollback followed by the visible rows.</summary>
        private readonly List<Cell[]> _main = new List<Cell[]>();
        private Cell[][] _alt;
        public bool AltScreen { get; private set; }

        public int CursorX { get; private set; }
        public int CursorY { get; private set; }
        public bool CursorVisible { get; private set; } = true;
        public bool AppCursorKeys { get; private set; }
        public bool BracketedPaste { get; private set; }
        public bool Dirty { get; set; } = true;

        /// <summary>Text the terminal must send back to the server (cursor position reports and so on).</summary>
        public event Action<string> Reply;
        public event Action<string> TitleChanged;

        private bool _wrapPending;
        private bool _autoWrap = true;
        private bool _originMode;
        private int _top, _bottom;
        private Cell _pen = Cell.Blank(-1);
        private int _savedX, _savedY;
        private Cell _savedPen;
        private bool _lineDrawing, _savedLineDrawing;

        private int _state;      // 0 text, 1 ESC, 2 CSI, 3 OSC, 4 charset, 5 ESC #
        private readonly StringBuilder _params = new StringBuilder();
        private readonly StringBuilder _osc = new StringBuilder();
        private bool _oscEsc;
        private char _charsetSlot;

        public VtEmulator(int cols, int rows)
        {
            Cols = Math.Max(2, cols);
            Rows = Math.Max(2, rows);
            for (int i = 0; i < Rows; i++) _main.Add(NewLine(Cols, -1));
            _bottom = Rows - 1;
        }

        private static Cell[] NewLine(int cols, int bg)
        {
            var l = new Cell[cols];
            for (int i = 0; i < cols; i++) l[i] = Cell.Blank(bg);
            return l;
        }

        // ---------------- Reading the screen ----------------

        /// <summary>Lines available for display: scrollback plus screen on the main buffer, just the screen on the alternate one.</summary>
        public int TotalLines => AltScreen ? Rows : _main.Count;
        public int ScrollbackLines => AltScreen ? 0 : _main.Count - Rows;

        /// <summary>Line by absolute index (0 = oldest scrollback line).</summary>
        public Cell[] Line(int index)
        {
            if (AltScreen) return index >= 0 && index < Rows ? _alt[index] : null;
            return index >= 0 && index < _main.Count ? _main[index] : null;
        }

        private Cell[] ScreenLine(int y) => AltScreen ? _alt[y] : _main[_main.Count - Rows + y];

        private void SetScreenLine(int y, Cell[] line)
        {
            if (AltScreen) _alt[y] = line; else _main[_main.Count - Rows + y] = line;
        }

        // ---------------- Resizing ----------------

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(2, cols);
            rows = Math.Max(2, rows);
            if (cols == Cols && rows == Rows) return;

            for (int i = 0; i < _main.Count; i++) _main[i] = Fit(_main[i], cols);
            if (rows > Rows)
            {
                // Pull lines back from scrollback first, like xterm, then add blanks.
                int need = rows - Rows;
                int fromScrollback = Math.Min(need, _main.Count - Rows);
                CursorY += fromScrollback;
                for (int i = 0; i < need - fromScrollback; i++) _main.Add(NewLine(cols, -1));
            }
            else if (rows < Rows)
            {
                // Drop blank lines below the cursor first, then push the rest into scrollback.
                int drop = Rows - rows;
                int removed = 0;
                while (removed < drop && CursorY < Rows - 1 - removed && IsBlank(_main[_main.Count - 1]))
                {
                    _main.RemoveAt(_main.Count - 1);
                    removed++;
                }
                CursorY -= drop - removed;
            }

            if (_alt != null)
            {
                var alt = new Cell[rows][];
                for (int i = 0; i < rows; i++) alt[i] = i < _alt.Length ? Fit(_alt[i], cols) : NewLine(cols, -1);
                _alt = alt;
            }

            Cols = cols;
            Rows = rows;
            while (_main.Count < Rows) _main.Insert(0, NewLine(cols, -1));
            _top = 0;
            _bottom = Rows - 1;
            CursorX = Math.Max(0, Math.Min(Cols - 1, CursorX));
            CursorY = Math.Max(0, Math.Min(Rows - 1, CursorY));
            _wrapPending = false;
            Dirty = true;
        }

        private static Cell[] Fit(Cell[] line, int cols)
        {
            if (line.Length == cols) return line;
            var n = new Cell[cols];
            for (int i = 0; i < cols; i++) n[i] = i < line.Length ? line[i] : Cell.Blank(-1);
            return n;
        }

        private static bool IsBlank(Cell[] line)
        {
            foreach (var c in line) if (c.C != ' ' || c.Bg != -1) return false;
            return true;
        }

        // ---------------- Input from the server ----------------

        public void Feed(string text)
        {
            foreach (char ch in text) Put(ch);
            Dirty = true;
        }

        private void Put(char ch)
        {
            switch (_state)
            {
                case 1: Escape(ch); return;
                case 2: CsiChar(ch); return;
                case 3: OscChar(ch); return;
                case 4:
                    if (_charsetSlot == '(') _lineDrawing = ch == '0';
                    _state = 0;
                    return;
                case 5:
                    if (ch == '8') FillWithE();
                    _state = 0;
                    return;
            }

            switch (ch)
            {
                case '\x1b': _state = 1; return;
                case '\r': CursorX = 0; _wrapPending = false; return;
                case '\n':
                case '\v':
                case '\f': Index(); return;
                case '\b':
                    if (CursorX > 0) CursorX--;
                    _wrapPending = false;
                    return;
                case '\t':
                    CursorX = Math.Min(Cols - 1, (CursorX / 8 + 1) * 8);
                    return;
                case '\a':
                case '\0':
                case '\x0e':
                case '\x0f':
                    return;
            }
            if (ch < ' ' || ch == '\x7f') return;
            Print(_lineDrawing ? LineDraw(ch) : ch);
        }

        private static char LineDraw(char c) => c switch
        {
            'j' => '┘', 'k' => '┐', 'l' => '┌', 'm' => '└', 'n' => '┼',
            'q' => '─', 't' => '├', 'u' => '┤', 'v' => '┴', 'w' => '┬',
            'x' => '│', 'a' => '▒', '`' => '◆', 'f' => '°', 'g' => '±',
            '~' => '·', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£',
            _ => c
        };

        private void Print(char ch)
        {
            if (_wrapPending && _autoWrap)
            {
                CursorX = 0;
                Index();
            }
            _wrapPending = false;
            var line = ScreenLine(CursorY);
            var cell = _pen;
            cell.C = ch;
            line[CursorX] = cell;
            if (CursorX == Cols - 1) _wrapPending = true;
            else CursorX++;
        }

        /// <summary>Line feed: move down, scrolling the region when at its bottom.</summary>
        private void Index()
        {
            _wrapPending = false;
            if (CursorY == _bottom) ScrollUp(1);
            else if (CursorY < Rows - 1) CursorY++;
        }

        private void ReverseIndex()
        {
            _wrapPending = false;
            if (CursorY == _top) ScrollDown(1);
            else if (CursorY > 0) CursorY--;
        }

        private void ScrollUp(int n)
        {
            for (int k = 0; k < n; k++)
            {
                if (!AltScreen && _top == 0 && _bottom == Rows - 1)
                {
                    // Whole screen scrolls: the top line goes into the scrollback.
                    _main.Add(NewLine(Cols, _pen.Bg));
                    if (_main.Count > Rows + MaxScrollback) _main.RemoveAt(0);
                }
                else
                {
                    for (int y = _top; y < _bottom; y++) SetScreenLine(y, ScreenLine(y + 1));
                    SetScreenLine(_bottom, NewLine(Cols, _pen.Bg));
                }
            }
        }

        private void ScrollDown(int n)
        {
            for (int k = 0; k < n; k++)
            {
                for (int y = _bottom; y > _top; y--) SetScreenLine(y, ScreenLine(y - 1));
                SetScreenLine(_top, NewLine(Cols, _pen.Bg));
            }
        }

        private void Escape(char ch)
        {
            _state = 0;
            switch (ch)
            {
                case '[': _state = 2; _params.Clear(); break;
                case ']': _state = 3; _osc.Clear(); _oscEsc = false; break;
                case '(':
                case ')':
                case '*':
                case '+': _state = 4; _charsetSlot = ch; break;
                case '#': _state = 5; break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': Index(); break;
                case 'E': CursorX = 0; Index(); break;
                case 'M': ReverseIndex(); break;
                case 'c': FullReset(); break;
                case '=':
                case '>':
                default: break;
            }
        }

        private void OscChar(char ch)
        {
            if (ch == '\a' || (_oscEsc && ch == '\\'))
            {
                _state = 0;
                string s = _osc.ToString();
                int semi = s.IndexOf(';');
                if (semi > 0 && (s.StartsWith("0;") || s.StartsWith("2;"))) TitleChanged?.Invoke(s.Substring(semi + 1));
                return;
            }
            if (ch == '\x1b') { _oscEsc = true; return; }
            _oscEsc = false;
            if (_osc.Length < 512) _osc.Append(ch);
        }

        private void CsiChar(char ch)
        {
            if (ch >= 0x20 && ch <= 0x3f) { if (_params.Length < 64) _params.Append(ch); return; }
            if (ch >= 0x40 && ch <= 0x7e) { _state = 0; Csi(ch, _params.ToString()); return; }
            if (ch == '\x1b') { _state = 1; return; }
            // Control characters inside a sequence still act.
            if (ch < ' ') { int keep = _state; _state = 0; Put(ch); _state = keep; }
        }

        private static List<int> Parse(string p, out bool priv, out bool gt)
        {
            priv = p.StartsWith("?");
            gt = p.StartsWith(">");
            var list = new List<int>();
            foreach (var part in p.TrimStart('?', '>', '=', '!').Split(';', ':'))
                list.Add(int.TryParse(part, out int v) ? v : -1);
            return list;
        }

        private static int A(List<int> a, int i, int def) => i < a.Count && a[i] > 0 ? a[i] : def;

        private void Csi(char cmd, string p)
        {
            var a = Parse(p, out bool priv, out bool gt);
            int n = A(a, 0, 1);
            switch (cmd)
            {
                case 'A': CursorY = Math.Max(CursorY >= _top ? _top : 0, CursorY - n); _wrapPending = false; break;
                case 'B': CursorY = Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + n); _wrapPending = false; break;
                case 'C': CursorX = Math.Min(Cols - 1, CursorX + n); _wrapPending = false; break;
                case 'D': CursorX = Math.Max(0, CursorX - n); _wrapPending = false; break;
                case 'E': CursorX = 0; CursorY = Math.Min(Rows - 1, CursorY + n); _wrapPending = false; break;
                case 'F': CursorX = 0; CursorY = Math.Max(0, CursorY - n); _wrapPending = false; break;
                case 'G':
                case '`': CursorX = Clamp(n - 1, Cols); _wrapPending = false; break;
                case 'd': CursorY = Clamp((_originMode ? _top : 0) + n - 1, Rows); _wrapPending = false; break;
                case 'H':
                case 'f':
                    CursorY = Clamp((_originMode ? _top : 0) + A(a, 0, 1) - 1, Rows);
                    CursorX = Clamp(A(a, 1, 1) - 1, Cols);
                    _wrapPending = false;
                    break;
                case 'J': EraseDisplay(a.Count > 0 && a[0] > 0 ? a[0] : 0); break;
                case 'K': EraseLine(a.Count > 0 && a[0] > 0 ? a[0] : 0); break;
                case 'L': if (CursorY >= _top && CursorY <= _bottom) { int t = _top; _top = CursorY; ScrollDown(n); _top = t; } break;
                case 'M': if (CursorY >= _top && CursorY <= _bottom) { int t = _top; _top = CursorY; ScrollUpRegionOnly(n); _top = t; } break;
                case 'P': DeleteChars(n); break;
                case '@': InsertChars(n); break;
                case 'X': EraseChars(n); break;
                case 'S': ScrollUpRegionOnly(n); break;
                case 'T': if (!gt) ScrollDown(n); break;
                case 'r':
                    _top = Clamp(A(a, 0, 1) - 1, Rows);
                    _bottom = Clamp(A(a, 1, Rows) - 1, Rows);
                    if (_bottom <= _top) { _top = 0; _bottom = Rows - 1; }
                    CursorX = 0;
                    CursorY = _originMode ? _top : 0;
                    _wrapPending = false;
                    break;
                case 'm': if (!gt) Sgr(a); break;
                case 'h': SetMode(a, priv, true); break;
                case 'l': SetMode(a, priv, false); break;
                case 's': if (!priv) SaveCursor(); break;
                case 'u': RestoreCursor(); break;
                case 'n':
                    if (A(a, 0, 0) == 6) Reply?.Invoke("\x1b[" + (CursorY + 1) + ";" + (CursorX + 1) + "R");
                    else if (A(a, 0, 0) == 5) Reply?.Invoke("\x1b[0n");
                    break;
                case 'c':
                    if (gt) Reply?.Invoke("\x1b[>0;276;0c");
                    else Reply?.Invoke("\x1b[?62;22c");
                    break;
                case 't':
                    if (A(a, 0, 0) == 18) Reply?.Invoke("\x1b[8;" + Rows + ";" + Cols + "t");
                    break;
            }
        }

        private static int Clamp(int v, int count) => Math.Max(0, Math.Min(count - 1, v));

        /// <summary>Scrolls just the region, never into scrollback (used by delete-line and CSI S).</summary>
        private void ScrollUpRegionOnly(int n)
        {
            for (int k = 0; k < n; k++)
            {
                for (int y = _top; y < _bottom; y++) SetScreenLine(y, ScreenLine(y + 1));
                SetScreenLine(_bottom, NewLine(Cols, _pen.Bg));
            }
        }

        private void EraseDisplay(int mode)
        {
            _wrapPending = false;
            if (mode == 0)
            {
                EraseLine(0);
                for (int y = CursorY + 1; y < Rows; y++) SetScreenLine(y, NewLine(Cols, _pen.Bg));
            }
            else if (mode == 1)
            {
                EraseLine(1);
                for (int y = 0; y < CursorY; y++) SetScreenLine(y, NewLine(Cols, _pen.Bg));
            }
            else if (mode == 2)
            {
                if (!AltScreen)
                {
                    // "clear": keep what was on screen by pushing it into the scrollback.
                    int last = Rows - 1;
                    while (last >= 0 && IsBlank(ScreenLine(last))) last--;
                    for (int i = 0; i <= last; i++) _main.Add(NewLine(Cols, _pen.Bg));
                    while (_main.Count > Rows + MaxScrollback) _main.RemoveAt(0);
                }
                for (int y = 0; y < Rows; y++) SetScreenLine(y, NewLine(Cols, _pen.Bg));
            }
            else if (mode == 3 && !AltScreen)
            {
                int drop = _main.Count - Rows;
                if (drop > 0) _main.RemoveRange(0, drop);
            }
        }

        private void EraseLine(int mode)
        {
            var line = ScreenLine(CursorY);
            int from = mode == 0 ? CursorX : 0;
            int to = mode == 1 ? CursorX : Cols - 1;
            for (int x = from; x <= to && x < Cols; x++) line[x] = Cell.Blank(_pen.Bg);
            _wrapPending = false;
        }

        private void DeleteChars(int n)
        {
            var line = ScreenLine(CursorY);
            n = Math.Min(n, Cols - CursorX);
            for (int x = CursorX; x < Cols; x++) line[x] = x + n < Cols ? line[x + n] : Cell.Blank(_pen.Bg);
            _wrapPending = false;
        }

        private void InsertChars(int n)
        {
            var line = ScreenLine(CursorY);
            n = Math.Min(n, Cols - CursorX);
            for (int x = Cols - 1; x >= CursorX; x--) line[x] = x - n >= CursorX ? line[x - n] : Cell.Blank(_pen.Bg);
            _wrapPending = false;
        }

        private void EraseChars(int n)
        {
            var line = ScreenLine(CursorY);
            for (int x = CursorX; x < Math.Min(Cols, CursorX + n); x++) line[x] = Cell.Blank(_pen.Bg);
            _wrapPending = false;
        }

        private void FillWithE()
        {
            for (int y = 0; y < Rows; y++)
            {
                var l = NewLine(Cols, -1);
                for (int x = 0; x < Cols; x++) l[x].C = 'E';
                SetScreenLine(y, l);
            }
        }

        private void SetMode(List<int> a, bool priv, bool on)
        {
            if (!priv)
            {
                return;   // insert mode (4) and others are rarely used by shells; ignored
            }
            foreach (int m in a)
            {
                switch (m)
                {
                    case 1: AppCursorKeys = on; break;
                    case 6: _originMode = on; CursorX = 0; CursorY = on ? _top : 0; break;
                    case 7: _autoWrap = on; break;
                    case 25: CursorVisible = on; break;
                    case 47:
                    case 1047: SwitchScreen(on, false); break;
                    case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
                    case 1049: SwitchScreen(on, true); break;
                    case 2004: BracketedPaste = on; break;
                }
            }
        }

        private void SwitchScreen(bool alt, bool saveCursor)
        {
            if (alt == AltScreen) return;
            if (alt)
            {
                if (saveCursor) SaveCursor();
                _alt = new Cell[Rows][];
                for (int i = 0; i < Rows; i++) _alt[i] = NewLine(Cols, -1);
                AltScreen = true;
            }
            else
            {
                AltScreen = false;
                _alt = null;
                if (saveCursor) RestoreCursor();
            }
            _top = 0;
            _bottom = Rows - 1;
            _wrapPending = false;
        }

        private void SaveCursor()
        {
            _savedX = CursorX;
            _savedY = CursorY;
            _savedPen = _pen;
            _savedLineDrawing = _lineDrawing;
        }

        private void RestoreCursor()
        {
            CursorX = Clamp(_savedX, Cols);
            CursorY = Clamp(_savedY, Rows);
            _pen = _savedPen;
            _lineDrawing = _savedLineDrawing;
            _wrapPending = false;
        }

        private void FullReset()
        {
            if (AltScreen) SwitchScreen(false, false);
            _pen = Cell.Blank(-1);
            _top = 0;
            _bottom = Rows - 1;
            _autoWrap = true;
            _originMode = false;
            AppCursorKeys = false;
            BracketedPaste = false;
            CursorVisible = true;
            _lineDrawing = false;
            EraseDisplay(2);
            CursorX = CursorY = 0;
        }

        private void Sgr(List<int> a)
        {
            if (a.Count == 0) { ResetPen(); return; }
            for (int i = 0; i < a.Count; i++)
            {
                int v = a[i] < 0 ? 0 : a[i];
                switch (v)
                {
                    case 0: ResetPen(); break;
                    case 1: _pen.Flags |= Cell.Bold; break;
                    case 2: _pen.Flags |= Cell.Dim; break;
                    case 3: _pen.Flags |= Cell.Italic; break;
                    case 4: _pen.Flags |= Cell.Underline; break;
                    case 7: _pen.Flags |= Cell.Inverse; break;
                    case 9: _pen.Flags |= Cell.Strike; break;
                    case 21:
                    case 22: _pen.Flags &= unchecked((byte)~(Cell.Bold | Cell.Dim)); break;
                    case 23: _pen.Flags &= unchecked((byte)~Cell.Italic); break;
                    case 24: _pen.Flags &= unchecked((byte)~Cell.Underline); break;
                    case 27: _pen.Flags &= unchecked((byte)~Cell.Inverse); break;
                    case 29: _pen.Flags &= unchecked((byte)~Cell.Strike); break;
                    case 39: _pen.Fg = -1; break;
                    case 49: _pen.Bg = -1; break;
                    case 38:
                    case 48:
                        int colour = ExtendedColour(a, ref i);
                        if (colour != int.MinValue) { if (v == 38) _pen.Fg = colour; else _pen.Bg = colour; }
                        break;
                    default:
                        if (v >= 30 && v <= 37) _pen.Fg = v - 30;
                        else if (v >= 40 && v <= 47) _pen.Bg = v - 40;
                        else if (v >= 90 && v <= 97) _pen.Fg = v - 90 + 8;
                        else if (v >= 100 && v <= 107) _pen.Bg = v - 100 + 8;
                        break;
                }
            }
        }

        private void ResetPen()
        {
            _pen.Fg = -1;
            _pen.Bg = -1;
            _pen.Flags = 0;
        }

        private static int ExtendedColour(List<int> a, ref int i)
        {
            if (i + 1 >= a.Count) return int.MinValue;
            int kind = a[i + 1];
            if (kind == 5 && i + 2 < a.Count)
            {
                int idx = Math.Max(0, Math.Min(255, a[i + 2]));
                i += 2;
                return idx;
            }
            if (kind == 2 && i + 4 < a.Count)
            {
                int r = Math.Max(0, a[i + 2]) & 255, g = Math.Max(0, a[i + 3]) & 255, b = Math.Max(0, a[i + 4]) & 255;
                i += 4;
                return 0x1000000 | (r << 16) | (g << 8) | b;
            }
            i = a.Count;
            return int.MinValue;
        }
    }
}
