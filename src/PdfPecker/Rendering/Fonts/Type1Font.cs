using System.Globalization;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>Parser for embedded Type 1 fonts (FontFile): eexec decryption and the Type 1 charstring interpreter.</summary>
internal sealed class Type1Font : IGlyphSource
{
    private readonly List<byte[]> _charStrings = new();
    private readonly List<byte[]> _subrs = new();
    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly List<string> _glyphNames = new();
    private PathData?[] _cache = Array.Empty<PathData?>();
    private double[] _widths = Array.Empty<double>();
    private int _lenIV = 4;

    public Matrix FontMatrix { get; private set; } = Matrix.Scale(0.001, 0.001);
    public int GlyphCount => _charStrings.Count;

    /// <summary>The font's built-in encoding as glyph names (null when unset).</summary>
    public string?[] BuiltinEncoding { get; } = new string?[256];

    public static Type1Font? Parse(byte[] data, int length1)
    {
        try
        {
            var f = new Type1Font();
            return f.Load(data, length1) ? f : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or FormatException)
        {
            return null;
        }
    }

    public int GlyphByName(string name) => _names.TryGetValue(name, out int g) ? g : -1;

    public string? GlyphName(int gid) => gid >= 0 && gid < _glyphNames.Count ? _glyphNames[gid] : null;

    public double Advance(int gid)
    {
        if (gid < 0 || gid >= _widths.Length) return -1;
        if (double.IsNaN(_widths[gid])) GetGlyph(gid);
        return _widths[gid];
    }

    private bool Load(byte[] data, int length1)
    {
        data = StripPfb(data, ref length1);
        // Locate the end of the cleartext part.
        int eexec = IndexOf(data, "eexec"u8, 0);
        if (length1 <= 0 || length1 > data.Length || (eexec >= 0 && eexec + 5 > length1 + 64)) length1 = eexec >= 0 ? eexec + 5 : data.Length;
        if (eexec >= 0 && eexec + 5 <= data.Length && length1 < eexec + 5) length1 = eexec + 5;
        ParseCleartext(data, Math.Min(length1, data.Length));

        int start = length1;
        while (start < data.Length && (data[start] == '\r' || data[start] == '\n' || data[start] == ' ' || data[start] == '\t')) start++;
        if (start >= data.Length) return false;
        var enc = data.AsSpan(start).ToArray();
        if (IsHex(enc)) enc = FromHex(enc);
        var priv = Decrypt(enc, 55665, 4);
        ParsePrivate(priv);
        _cache = new PathData?[_charStrings.Count];
        _widths = new double[_charStrings.Count];
        Array.Fill(_widths, double.NaN);
        return _charStrings.Count > 0;
    }

    private static byte[] StripPfb(byte[] d, ref int length1)
    {
        if (d.Length < 6 || d[0] != 0x80 || d[1] != 1) return d;
        var ms = new MemoryStream(d.Length);
        int p = 0, seg = 0;
        while (p + 6 <= d.Length && d[p] == 0x80 && (d[p + 1] == 1 || d[p + 1] == 2))
        {
            int len = d[p + 2] | d[p + 3] << 8 | d[p + 4] << 16 | d[p + 5] << 24;
            p += 6;
            len = Math.Min(len, d.Length - p);
            ms.Write(d, p, len);
            if (seg == 0) length1 = len;
            p += len;
            seg++;
        }
        return ms.ToArray();
    }

    private static int IndexOf(byte[] d, ReadOnlySpan<byte> s, int from)
    {
        int i = d.AsSpan(from).IndexOf(s);
        return i < 0 ? -1 : i + from;
    }

    private static bool IsHex(byte[] d)
    {
        int n = Math.Min(4, d.Length);
        for (int i = 0; i < n; i++) if (PdfLexer.HexVal(d[i]) < 0) return false;
        return true;
    }

    private static byte[] FromHex(byte[] d)
    {
        var o = new List<byte>(d.Length / 2);
        int hi = -1;
        foreach (byte b in d)
        {
            int v = PdfLexer.HexVal(b);
            if (v < 0) continue;
            if (hi < 0) hi = v;
            else { o.Add((byte)(hi << 4 | v)); hi = -1; }
        }
        return o.ToArray();
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> d, int r, int skip)
    {
        const int c1 = 52845, c2 = 22719;
        var o = new byte[Math.Max(0, d.Length - skip)];
        for (int i = 0; i < d.Length; i++)
        {
            int c = d[i];
            int p = c ^ (r >> 8);
            r = ((c + r) * c1 + c2) & 0xFFFF;
            if (i >= skip) o[i - skip] = (byte)p;
        }
        return o;
    }

    private void ParseCleartext(byte[] d, int end)
    {
        var lx = new PdfLexer(d, 0, end);
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof) break;
            if (k != TokenKind.Name) continue;
            if (lx.NameValue == "FontMatrix")
            {
                if (lx.Next() is TokenKind.ArrayStart or TokenKind.BraceOpen)
                {
                    var m = new double[6];
                    int n = 0;
                    while (n < 6 && lx.Next() is TokenKind.Integer or TokenKind.Real) m[n++] = lx.Number;
                    if (n == 6 && m[0] != 0) FontMatrix = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                }
            }
            else if (lx.NameValue == "Encoding")
            {
                int save = lx.Pos;
                var k2 = lx.Next();
                if (k2 == TokenKind.Keyword && lx.KeywordIs("StandardEncoding"))
                {
                    for (int c = 0; c < 256; c++) BuiltinEncoding[c] = Encodings.Standard[c];
                    continue;
                }
                lx.Pos = save;
                // "dup code /name put" entries until "readonly def" or "def".
                while (true)
                {
                    var t = lx.Next();
                    if (t == TokenKind.Eof) break;
                    if (t == TokenKind.Keyword && (lx.KeywordIs("def") || lx.KeywordIs("readonly"))) break;
                    if (t == TokenKind.Keyword && lx.KeywordIs("dup"))
                    {
                        if (lx.Next() != TokenKind.Integer) continue;
                        int code = (int)lx.Integer;
                        if (lx.Next() != TokenKind.Name) continue;
                        if (code >= 0 && code < 256) BuiltinEncoding[code] = lx.NameValue;
                    }
                }
            }
        }
    }

    private void ParsePrivate(byte[] d)
    {
        int lenIvPos = IndexOf(d, "/lenIV"u8, 0);
        if (lenIvPos >= 0)
        {
            var lx = new PdfLexer(d, lenIvPos + 6);
            if (lx.Next() == TokenKind.Integer) _lenIV = (int)lx.Integer;
        }
        // Subrs: "dup <i> <len> RD <bytes> NP"
        int subrsPos = IndexOf(d, "/Subrs"u8, 0);
        int charsPos = IndexOf(d, "/CharStrings"u8, 0);
        if (subrsPos >= 0 && (charsPos < 0 || subrsPos < charsPos))
        {
            int p = subrsPos + 6;
            int count = ReadInt(d, ref p);
            var subrs = new byte[Math.Clamp(count, 0, 65536)][];
            for (int i = 0; i < subrs.Length; i++)
            {
                int dup = IndexOf(d, "dup"u8, p);
                if (dup < 0 || (charsPos >= 0 && dup > charsPos)) break;
                p = dup + 3;
                int idx = ReadInt(d, ref p);
                int len = ReadInt(d, ref p);
                if (!SkipToken(d, ref p) || len < 0 || p + 1 + len > d.Length) break;
                p++; // single space after RD
                var cs = Decrypt(d.AsSpan(p, len), 4330, Math.Max(0, _lenIV));
                if (idx >= 0 && idx < subrs.Length) subrs[idx] = cs;
                p += len;
            }
            foreach (var s in subrs) _subrs.Add(s ?? Array.Empty<byte>());
        }
        if (charsPos < 0) return;
        {
            int p = charsPos + 12;
            int count = ReadInt(d, ref p);
            for (int i = 0; i < count && p < d.Length; i++)
            {
                int slash = Array.IndexOf(d, (byte)'/', p);
                if (slash < 0) break;
                int ns = slash + 1, ne = ns;
                while (ne < d.Length && PdfLexer.IsRegular(d[ne])) ne++;
                string name = System.Text.Encoding.Latin1.GetString(d, ns, ne - ns);
                p = ne;
                int len = ReadInt(d, ref p);
                if (!SkipToken(d, ref p) || len < 0 || p + 1 + len > d.Length) break;
                p++;
                var cs = Decrypt(d.AsSpan(p, len), 4330, Math.Max(0, _lenIV));
                p += len;
                if (_names.ContainsKey(name)) continue;
                _names[name] = _charStrings.Count;
                _glyphNames.Add(name);
                _charStrings.Add(cs);
            }
        }
    }

    private static int ReadInt(byte[] d, ref int p)
    {
        while (p < d.Length && !(d[p] >= '0' && d[p] <= '9') && d[p] != '-') p++;
        bool neg = p < d.Length && d[p] == '-';
        if (neg) p++;
        int v = 0;
        while (p < d.Length && d[p] >= '0' && d[p] <= '9') v = v * 10 + (d[p++] - '0');
        return neg ? -v : v;
    }

    /// <summary>Skips whitespace and one token (the RD or -| operator).</summary>
    private static bool SkipToken(byte[] d, ref int p)
    {
        while (p < d.Length && PdfLexer.IsWhite(d[p])) p++;
        int s = p;
        while (p < d.Length && !PdfLexer.IsWhite(d[p])) p++;
        return p > s && p < d.Length;
    }

    // ------------------------------------------------------------------ charstrings

    public PathData? GetGlyph(int gid)
    {
        if (gid < 0 || gid >= _cache.Length) return null;
        var cached = Volatile.Read(ref _cache[gid]);
        if (cached != null) return cached;
        var s = new T1State { Path = new PathData() };
        try { Run(s, _charStrings[gid], 0); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { }
        if (s.Open) s.Path.ClosePath();
        _widths[gid] = s.Width;
        Volatile.Write(ref _cache[gid], s.Path);
        return s.Path;
    }

    private sealed class T1State
    {
        public PathData Path = null!;
        public int Budget = 200_000; // charstring bytes per glyph: guards against subroutine call explosions
        public readonly double[] Stack = new double[64];
        public int Sp;
        public readonly Stack<double> Ps = new();
        public double X, Y, Sbx, Width;
        public bool Open, Ended, Flex;
        public readonly List<(double X, double Y)> FlexPts = new();
    }

    private void Run(T1State s, byte[] cs, int depth)
    {
        if (depth > 10) return;
        var st = s.Stack;
        int p = 0;
        while (p < cs.Length && !s.Ended && --s.Budget > 0)
        {
            int b = cs[p++];
            if (b >= 32)
            {
                double v;
                if (b <= 246) v = b - 139;
                else if (b <= 250) v = (b - 247) * 256 + cs[p++] + 108;
                else if (b <= 254) v = -(b - 251) * 256 - cs[p++] - 108;
                else { v = cs[p] << 24 | cs[p + 1] << 16 | cs[p + 2] << 8 | cs[p + 3]; p += 4; }
                if (s.Sp < st.Length) st[s.Sp++] = v;
                continue;
            }
            switch (b)
            {
                case 13: // hsbw
                    if (s.Sp >= 2) { s.Sbx = st[0]; s.Width = st[1]; s.X = st[0]; s.Y = 0; }
                    s.Sp = 0;
                    break;
                case 9: // closepath
                    if (s.Open) { s.Path.ClosePath(); s.Open = false; }
                    s.Sp = 0;
                    break;
                case 21: // rmoveto
                    if (s.Sp >= 2) Move(s, s.X + st[s.Sp - 2], s.Y + st[s.Sp - 1]);
                    s.Sp = 0;
                    break;
                case 22: // hmoveto
                    if (s.Sp >= 1) Move(s, s.X + st[s.Sp - 1], s.Y);
                    s.Sp = 0;
                    break;
                case 4: // vmoveto
                    if (s.Sp >= 1) Move(s, s.X, s.Y + st[s.Sp - 1]);
                    s.Sp = 0;
                    break;
                case 5: if (s.Sp >= 2) Line(s, s.X + st[0], s.Y + st[1]); s.Sp = 0; break;
                case 6: if (s.Sp >= 1) Line(s, s.X + st[0], s.Y); s.Sp = 0; break;
                case 7: if (s.Sp >= 1) Line(s, s.X, s.Y + st[0]); s.Sp = 0; break;
                case 8: // rrcurveto
                    if (s.Sp >= 6) Curve(s, st[0], st[1], st[2], st[3], st[4], st[5]);
                    s.Sp = 0;
                    break;
                case 30: // vhcurveto
                    if (s.Sp >= 4) Curve(s, 0, st[0], st[1], st[2], st[3], 0);
                    s.Sp = 0;
                    break;
                case 31: // hvcurveto
                    if (s.Sp >= 4) Curve(s, st[0], 0, st[1], st[2], 0, st[3]);
                    s.Sp = 0;
                    break;
                case 10: // callsubr
                {
                    if (s.Sp < 1) break;
                    int idx = (int)st[--s.Sp];
                    if (idx >= 0 && idx < _subrs.Count) Run(s, _subrs[idx], depth + 1);
                    break;
                }
                case 11: return;
                case 14: // endchar
                    if (s.Open) { s.Path.ClosePath(); s.Open = false; }
                    s.Ended = true;
                    return;
                case 1: case 3: s.Sp = 0; break; // hstem vstem
                case 12:
                {
                    int op = cs[p++];
                    switch (op)
                    {
                        case 7: // sbw
                            if (s.Sp >= 4) { s.Sbx = st[0]; s.X = st[0]; s.Y = st[1]; s.Width = st[2]; }
                            s.Sp = 0;
                            break;
                        case 6: // seac
                            if (s.Sp >= 5) Seac(s, st[0], st[1], st[2], (int)st[3], (int)st[4], depth);
                            s.Ended = true;
                            return;
                        case 12: // div
                            if (s.Sp >= 2) { st[s.Sp - 2] = st[s.Sp - 1] == 0 ? 0 : st[s.Sp - 2] / st[s.Sp - 1]; s.Sp--; }
                            break;
                        case 16: // callothersubr
                        {
                            if (s.Sp < 2) { s.Sp = 0; break; }
                            int other = (int)st[--s.Sp];
                            int n = (int)st[--s.Sp];
                            n = Math.Clamp(n, 0, s.Sp);
                            var args = new double[n];
                            for (int i = 0; i < n; i++) args[i] = st[s.Sp - n + i];
                            s.Sp -= n;
                            switch (other)
                            {
                                case 1:
                                    s.Flex = true;
                                    s.FlexPts.Clear();
                                    break;
                                case 2:
                                    break;
                                case 0:
                                    s.Flex = false;
                                    if (s.FlexPts.Count >= 7)
                                    {
                                        var f = s.FlexPts;
                                        CurveAbs(s, f[1], f[2], f[3]);
                                        CurveAbs(s, f[4], f[5], f[6]);
                                    }
                                    s.Ps.Push(s.Y);
                                    s.Ps.Push(s.X);
                                    break;
                                case 3:
                                    s.Ps.Push(3);
                                    break;
                                default:
                                    for (int i = 0; i < n; i++) s.Ps.Push(args[i]);
                                    break;
                            }
                            break;
                        }
                        case 17: // pop
                            if (s.Sp < st.Length) st[s.Sp++] = s.Ps.Count > 0 ? s.Ps.Pop() : 0;
                            break;
                        case 33: // setcurrentpoint
                            if (s.Sp >= 2) { s.X = st[0]; s.Y = st[1]; }
                            s.Sp = 0;
                            break;
                        default: // dotsection, vstem3, hstem3
                            s.Sp = 0;
                            break;
                    }
                    break;
                }
                default:
                    s.Sp = 0;
                    break;
            }
        }
    }

    private void Seac(T1State s, double asb, double adx, double ady, int bchar, int achar, int depth)
    {
        if (bchar is < 0 or > 255 || achar is < 0 or > 255) return;
        int bg = Encodings.Standard[bchar] is string bn ? GlyphByName(bn) : -1;
        int ag = Encodings.Standard[achar] is string an ? GlyphByName(an) : -1;
        if (bg >= 0 && bg < _charStrings.Count && GetGlyph(bg) is PathData bp) bp.AppendTransformed(s.Path, Matrix.Identity);
        if (ag >= 0 && ag < _charStrings.Count && GetGlyph(ag) is PathData ap)
            ap.AppendTransformed(s.Path, Matrix.Translation(adx - asb, ady));
    }

    private static void Move(T1State s, double x, double y)
    {
        s.X = x;
        s.Y = y;
        if (s.Flex) { s.FlexPts.Add((x, y)); return; }
        if (s.Open) s.Path.ClosePath();
        s.Path.Move(x, y);
        s.Open = true;
    }

    private static void Line(T1State s, double x, double y)
    {
        if (!s.Open) { s.Path.Move(s.X, s.Y); s.Open = true; }
        s.X = x;
        s.Y = y;
        s.Path.Line(x, y);
    }

    private static void Curve(T1State s, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        double x1 = s.X + dx1, y1 = s.Y + dy1, x2 = x1 + dx2, y2 = y1 + dy2, x3 = x2 + dx3, y3 = y2 + dy3;
        CurveAbs(s, (x1, y1), (x2, y2), (x3, y3));
    }

    private static void CurveAbs(T1State s, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        if (!s.Open) { s.Path.Move(s.X, s.Y); s.Open = true; }
        s.Path.Cubic(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        s.X = c.X;
        s.Y = c.Y;
    }
}
