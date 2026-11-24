using System.Globalization;

namespace TarkovOverlay.Maps.Svg;

/// <summary>SVG path "d" → absolute segments. Handles every command (MLHVCSQTAZ, upper/lower case), implicit
/// repeats, "1.5.5" number runs, compact arc flags and exponents. Smooth curves reflect the previous control point.</summary>
public static class PathData
{
    public static List<Seg> Parse(string d)
    {
        var res = new List<Seg>();
        var t = new Tok(d);
        char cmd = '\0';
        double x = 0, y = 0, sx = 0, sy = 0;       // current point, subpath start
        double cx = 0, cy = 0;                     // last control point (for S/T)
        char prev = '\0';
        while (true)
        {
            t.SkipSep();
            if (t.End) break;
            char c = t.Peek;
            if (char.IsLetter(c) && c is not ('e' or 'E')) { cmd = c; t.Advance(); }
            else if (cmd == '\0') break; // garbage before first command
            bool rel = char.IsLower(cmd);
            char up = char.ToUpperInvariant(cmd);
            switch (up)
            {
                case 'M':
                {
                    if (!t.Num(out var a) || !t.Num(out var b)) return res;
                    x = rel ? x + a : a; y = rel ? y + b : b; sx = x; sy = y;
                    res.Add(new Seg(SegKind.Move, x, y));
                    cmd = rel ? 'l' : 'L'; // subsequent pairs are line-tos
                    prev = 'M';
                    continue;
                }
                case 'L':
                {
                    if (!t.Num(out var a) || !t.Num(out var b)) return res;
                    x = rel ? x + a : a; y = rel ? y + b : b;
                    res.Add(new Seg(SegKind.Line, x, y)); break;
                }
                case 'H':
                {
                    if (!t.Num(out var a)) return res;
                    x = rel ? x + a : a; res.Add(new Seg(SegKind.Line, x, y)); break;
                }
                case 'V':
                {
                    if (!t.Num(out var a)) return res;
                    y = rel ? y + a : a; res.Add(new Seg(SegKind.Line, x, y)); break;
                }
                case 'C':
                {
                    if (!t.Num(out var x1) || !t.Num(out var y1) || !t.Num(out var x2) || !t.Num(out var y2) || !t.Num(out var ex) || !t.Num(out var ey)) return res;
                    if (rel) { x1 += x; y1 += y; x2 += x; y2 += y; ex += x; ey += y; }
                    res.Add(new Seg(SegKind.Cubic, ex, ey, x1, y1, x2, y2));
                    cx = x2; cy = y2; x = ex; y = ey; prev = 'C'; continue;
                }
                case 'S':
                {
                    if (!t.Num(out var x2) || !t.Num(out var y2) || !t.Num(out var ex) || !t.Num(out var ey)) return res;
                    if (rel) { x2 += x; y2 += y; ex += x; ey += y; }
                    double x1 = prev == 'C' ? 2 * x - cx : x, y1 = prev == 'C' ? 2 * y - cy : y;
                    res.Add(new Seg(SegKind.Cubic, ex, ey, x1, y1, x2, y2));
                    cx = x2; cy = y2; x = ex; y = ey; prev = 'C'; continue;
                }
                case 'Q':
                {
                    if (!t.Num(out var x1) || !t.Num(out var y1) || !t.Num(out var ex) || !t.Num(out var ey)) return res;
                    if (rel) { x1 += x; y1 += y; ex += x; ey += y; }
                    res.Add(new Seg(SegKind.Quad, ex, ey, x1, y1));
                    cx = x1; cy = y1; x = ex; y = ey; prev = 'Q'; continue;
                }
                case 'T':
                {
                    if (!t.Num(out var ex) || !t.Num(out var ey)) return res;
                    if (rel) { ex += x; ey += y; }
                    double x1 = prev == 'Q' ? 2 * x - cx : x, y1 = prev == 'Q' ? 2 * y - cy : y;
                    res.Add(new Seg(SegKind.Quad, ex, ey, x1, y1));
                    cx = x1; cy = y1; x = ex; y = ey; prev = 'Q'; continue;
                }
                case 'A':
                {
                    if (!t.Num(out var rx) || !t.Num(out var ry) || !t.Num(out var rot) || !t.Flag(out var large) || !t.Flag(out var sweep) || !t.Num(out var ex) || !t.Num(out var ey)) return res;
                    if (rel) { ex += x; ey += y; }
                    res.Add(new Seg(SegKind.Arc, ex, ey, Math.Abs(rx), Math.Abs(ry), rot, (large ? 1 : 0) | (sweep ? 2 : 0)));
                    x = ex; y = ey; break;
                }
                case 'Z':
                    res.Add(new Seg(SegKind.Close, sx, sy));
                    x = sx; y = sy; prev = 'Z';
                    // a number right after Z without a command is invalid; stop treating it as repeat
                    cmd = '\0';
                    t.SkipSep();
                    if (!t.End && !char.IsLetter(t.Peek)) return res;
                    continue;
                default:
                    return res; // unknown command
            }
            prev = up;
        }
        return res;
    }

    private ref struct Tok
    {
        private readonly ReadOnlySpan<char> _s;
        private int _i;
        public Tok(string s) { _s = s.AsSpan(); _i = 0; }
        public bool End => _i >= _s.Length;
        public char Peek => _s[_i];
        public void Advance() => _i++;
        public void SkipSep() { while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++; }

        public bool Flag(out bool v)
        {
            SkipSep();
            v = false;
            if (End) return false;
            if (_s[_i] == '0' || _s[_i] == '1') { v = _s[_i] == '1'; _i++; return true; }
            return false;
        }

        public bool Num(out double v)
        {
            SkipSep();
            v = 0;
            int st = _i;
            if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
            bool digits = false, dot = false;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsDigit(c)) { digits = true; _i++; }
                else if (c == '.' && !dot) { dot = true; _i++; }
                else break;
            }
            if (!digits) { _i = st; return false; }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                int save = _i; _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                int ds = _i;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i == ds) _i = save;
            }
            return double.TryParse(_s[st.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
