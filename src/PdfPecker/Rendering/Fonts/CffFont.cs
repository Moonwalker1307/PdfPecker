using PdfPecker.Fonts.Cff;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>Parser for CFF (Compact Font Format) fonts, name- or CID-keyed, with a Type 2 charstring interpreter.</summary>
internal sealed class CffFont : IGlyphSource
{
    private readonly byte[] _d;
    private CffIndex _charStrings;
    private CffIndex _gsubrs;
    private CffIndex _subrs;
    private readonly List<CffIndex> _fdSubrs = new();
    private readonly List<(double DefaultWidth, double NominalWidth)> _fdWidths = new();
    private readonly List<Matrix?> _fdMatrix = new();
    private byte[]? _fdSelect;
    private double _defaultWidth, _nominalWidth;
    private string[] _strings = Array.Empty<string>();
    private int[] _charset = Array.Empty<int>(); // gid -> SID (or CID)
    private Dictionary<string, int>? _nameToGid;
    private Dictionary<int, int>? _cidToGid;
    private PathData?[] _cache = Array.Empty<PathData?>();
    private double[] _widths = Array.Empty<double>();

    public bool IsCid { get; private set; }
    public Matrix FontMatrix { get; private set; } = Matrix.Scale(0.001, 0.001);
    public int GlyphCount => _charStrings.Count;

    /// <summary>The font's built-in encoding: code to glyph id (0 when unmapped).</summary>
    public int[] BuiltinEncoding { get; private set; } = new int[256];

    private CffFont(byte[] d) => _d = d;

    public static CffFont? Parse(byte[] data)
    {
        try
        {
            var f = new CffFont(data);
            return f.Load() ? f : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool Load()
    {
        var d = _d;
        if (d.Length < 4 || d[0] != 1) return false; // CFF2 is not supported
        int pos = d[2];
        var names = CffIndex.Read(d, ref pos);
        var topDicts = CffIndex.Read(d, ref pos);
        var strings = CffIndex.Read(d, ref pos);
        _gsubrs = CffIndex.Read(d, ref pos);
        if (topDicts.Count < 1) return false;

        _strings = new string[strings.Count];
        for (int i = 0; i < _strings.Length; i++)
        {
            var (start, end) = strings.Range(i);
            _strings[i] = System.Text.Encoding.Latin1.GetString(d, start, end - start);
        }

        var (topStart, topEnd) = topDicts.Range(0);
        var top = CffDict.Parse(d, topStart, topEnd);
        if (!top.TryGetValue(17, out var cs) || cs.Length < 1) return false;
        int csPos = (int)cs[0];
        _charStrings = CffIndex.Read(d, ref csPos);
        int nGlyphs = _charStrings.Count;
        if (nGlyphs <= 0) return false;
        _cache = new PathData?[nGlyphs];
        _widths = new double[nGlyphs];
        Array.Fill(_widths, double.NaN);

        if (top.TryGetValue(OpFontMatrix, out var fm) && fm.Length >= 6)
            FontMatrix = new Matrix(fm[0], fm[1], fm[2], fm[3], fm[4], fm[5]);
        IsCid = top.ContainsKey(CffDict.Escape + 30); // ROS

        if (top.TryGetValue(18, out var priv) && priv.Length >= 2) ReadPrivate((int)priv[1], (int)priv[0], out _subrs, out _defaultWidth, out _nominalWidth);

        if (IsCid)
        {
            if (top.TryGetValue(CffDict.Escape + 36, out var fdArr) && fdArr.Length >= 1) // FDArray
            {
                int p = (int)fdArr[0];
                var fds = CffIndex.Read(d, ref p);
                for (int i = 0; i < fds.Count; i++)
                {
                    var (fdStart, fdEnd) = fds.Range(i);
                    var fd = CffDict.Parse(d, fdStart, fdEnd);
                    CffIndex subrs = default;
                    double dw = 0, nw = 0;
                    if (fd.TryGetValue(18, out var fp) && fp.Length >= 2) ReadPrivate((int)fp[1], (int)fp[0], out subrs, out dw, out nw);
                    _fdSubrs.Add(subrs);
                    _fdWidths.Add((dw, nw));
                    _fdMatrix.Add(fd.TryGetValue(OpFontMatrix, out var m) && m.Length >= 6 ? new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]) : null);
                }
                // A font-dict matrix scales glyph space; the top matrix then defaults to identity.
                if (_fdMatrix.Count > 0 && _fdMatrix[0] is Matrix fdm)
                    FontMatrix = top.ContainsKey(OpFontMatrix) ? fdm.Multiply(FontMatrix) : fdm;
            }
            if (top.TryGetValue(CffDict.Escape + 37, out var fdSel) && fdSel.Length >= 1) ReadFdSelect((int)fdSel[0], nGlyphs);
        }

        // Charset.
        _charset = new int[nGlyphs];
        int charsetOff = top.TryGetValue(15, out var cso) && cso.Length > 0 ? (int)cso[0] : 0;
        if (charsetOff == 0)
            for (int g = 0; g < nGlyphs; g++) _charset[g] = g; // ISOAdobe: SID = gid
        else if (charsetOff > 2) ReadCharset(charsetOff, nGlyphs);

        // Encoding (name-keyed fonts only).
        if (!IsCid)
        {
            int encOff = top.TryGetValue(16, out var eo) && eo.Length > 0 ? (int)eo[0] : 0;
            if (encOff == 0 || encOff == 1)
            {
                var names2 = Encodings.Standard;
                for (int c = 0; c < 256; c++)
                {
                    var n = names2[c];
                    if (n != null) BuiltinEncoding[c] = Math.Max(0, GlyphByName(n));
                }
            }
            else ReadEncoding(encOff);
        }
        return true;
    }

    private const int OpFontMatrix = CffDict.Escape + 7;

    private void ReadPrivate(int offset, int size, out CffIndex subrs, out double defaultWidth, out double nominalWidth)
    {
        subrs = default;
        defaultWidth = nominalWidth = 0;
        if (offset <= 0 || offset + size > _d.Length) return;
        var p = CffDict.Parse(_d, offset, offset + size);
        if (p.TryGetValue(20, out var dw) && dw.Length > 0) defaultWidth = dw[0];
        if (p.TryGetValue(21, out var nw) && nw.Length > 0) nominalWidth = nw[0];
        if (p.TryGetValue(19, out var so) && so.Length > 0)
        {
            int sp = offset + (int)so[0];
            if (sp > 0 && sp < _d.Length) subrs = CffIndex.Read(_d, ref sp);
        }
    }

    private void ReadFdSelect(int off, int nGlyphs)
    {
        var d = _d;
        var sel = new byte[nGlyphs];
        int format = d[off];
        if (format == 0)
        {
            for (int g = 0; g < nGlyphs; g++) sel[g] = d[off + 1 + g];
        }
        else if (format == 3)
        {
            int nRanges = d[off + 1] << 8 | d[off + 2];
            int p = off + 3;
            for (int r = 0; r < nRanges; r++)
            {
                int first = d[p] << 8 | d[p + 1];
                byte fd = d[p + 2];
                int next = d[p + 3] << 8 | d[p + 4];
                for (int g = first; g < next && g < nGlyphs; g++) sel[g] = fd;
                p += 3;
            }
        }
        _fdSelect = sel;
    }

    private void ReadCharset(int off, int nGlyphs)
    {
        var d = _d;
        int format = d[off];
        int p = off + 1;
        int g = 1;
        if (format == 0)
        {
            for (; g < nGlyphs; g++, p += 2) _charset[g] = d[p] << 8 | d[p + 1];
        }
        else
        {
            while (g < nGlyphs)
            {
                int first = d[p] << 8 | d[p + 1];
                int nLeft = format == 1 ? d[p + 2] : d[p + 2] << 8 | d[p + 3];
                p += format == 1 ? 3 : 4;
                for (int k = 0; k <= nLeft && g < nGlyphs; k++) _charset[g++] = first + k;
            }
        }
    }

    private void ReadEncoding(int off)
    {
        var d = _d;
        int format = d[off];
        int p = off + 1;
        if ((format & 0x7F) == 0)
        {
            int n = d[p++];
            for (int i = 1; i <= n; i++) BuiltinEncoding[d[p++]] = i;
        }
        else
        {
            int nRanges = d[p++];
            int g = 1;
            for (int r = 0; r < nRanges; r++)
            {
                int first = d[p], nLeft = d[p + 1];
                p += 2;
                for (int k = 0; k <= nLeft; k++) if (first + k < 256) BuiltinEncoding[first + k] = g++;
            }
        }
        if ((format & 0x80) != 0)
        {
            int nSups = d[p++];
            for (int s = 0; s < nSups; s++)
            {
                int code = d[p];
                int sid = d[p + 1] << 8 | d[p + 2];
                p += 3;
                int gid = Array.IndexOf(_charset, sid);
                if (gid > 0) BuiltinEncoding[code] = gid;
            }
        }
    }

    // ------------------------------------------------------------------ lookups

    public string? GlyphName(int gid) => IsCid || gid < 0 || gid >= _charset.Length ? null : Sid(_charset[gid]);

    private string? Sid(int sid)
    {
        if (sid < StandardStrings.Length) return StandardStrings[sid];
        sid -= StandardStrings.Length;
        return sid < _strings.Length ? _strings[sid] : null;
    }

    public int GlyphByName(string name)
    {
        if (IsCid) return -1;
        if (_nameToGid == null)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int g = 0; g < _charset.Length; g++)
            {
                var n = Sid(_charset[g]);
                if (n != null) map.TryAdd(n, g);
            }
            _nameToGid = map;
        }
        return _nameToGid.TryGetValue(name, out int gid) ? gid : -1;
    }

    public int GidForCid(int cid)
    {
        if (!IsCid) return cid;
        if (_cidToGid == null)
        {
            var map = new Dictionary<int, int>();
            for (int g = 0; g < _charset.Length; g++) map.TryAdd(_charset[g], g);
            _cidToGid = map;
        }
        return _cidToGid.TryGetValue(cid, out int gid) ? gid : 0;
    }

    public double Advance(int gid)
    {
        if (gid < 0 || gid >= _widths.Length) return -1;
        if (double.IsNaN(_widths[gid])) GetGlyph(gid);
        return _widths[gid];
    }

    // ------------------------------------------------------------------ Type 2 charstrings

    public PathData? GetGlyph(int gid)
    {
        if (gid < 0 || gid >= _cache.Length) return null;
        var cached = Volatile.Read(ref _cache[gid]);
        if (cached != null) return cached;
        var path = new PathData();
        var subrs = _subrs;
        double dw = _defaultWidth, nw = _nominalWidth;
        if (IsCid && _fdSelect != null && gid < _fdSelect.Length && _fdSelect[gid] < _fdSubrs.Count)
        {
            subrs = _fdSubrs[_fdSelect[gid]];
            (dw, nw) = _fdWidths[_fdSelect[gid]];
        }
        var st = new T2State { Path = path, Subrs = subrs, DefaultWidth = dw, NominalWidth = nw, Width = double.NaN };
        try
        {
            var (start, end) = _charStrings.Range(gid);
            Run(st, start, end, 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { }
        if (st.Open) path.ClosePath();
        _widths[gid] = double.IsNaN(st.Width) ? dw : st.Width;
        Volatile.Write(ref _cache[gid], path);
        return path;
    }

    private sealed class T2State
    {
        public PathData Path = null!;
        public CharstringBudget Budget = new(int.MaxValue); // guards against subroutine call explosions
        public CffIndex Subrs;
        public double DefaultWidth, NominalWidth, Width;
        public readonly Type2Stack Stack = new(96);

        public int Sp
        {
            get => Stack.Count;
            set => Stack.Count = value;
        }
        public double X, Y;
        public int Stems;
        public bool HaveWidth, Open, Ended;
        public readonly double[] Transient = new double[32];
    }

    private void Run(T2State s, int p, int end, int depth)
    {
        if (depth > Type2.MaxSubrDepth) return;
        ReadOnlySpan<byte> d = _d;
        var stack = s.Stack;
        var st = stack.Items;
        while (p < end && !s.Ended && s.Budget.Spend())
        {
            int b = d[p++];
            if (Type2.IsNumber(b))
            {
                stack.PushNumber(d, b, ref p); // a full stack drops the number
                continue;
            }
            switch (b)
            {
                case 1: case 3: case 18: case 23: // hstem vstem hstemhm vstemhm
                    TakeWidth(s, s.Sp % 2 == 1);
                    s.Stems += s.Sp / 2;
                    s.Sp = 0;
                    break;
                case 19: case 20: // hintmask cntrmask
                    TakeWidth(s, s.Sp % 2 == 1);
                    s.Stems += s.Sp / 2;
                    s.Sp = 0;
                    p += Type2.MaskBytes(s.Stems);
                    break;
                case 21: // rmoveto
                    TakeWidth(s, s.Sp > 2);
                    MoveTo(s, s.X + Arg(s, s.Sp - 2), s.Y + Arg(s, s.Sp - 1));
                    s.Sp = 0;
                    break;
                case 22: // hmoveto
                    TakeWidth(s, s.Sp > 1);
                    MoveTo(s, s.X + Arg(s, s.Sp - 1), s.Y);
                    s.Sp = 0;
                    break;
                case 4: // vmoveto
                    TakeWidth(s, s.Sp > 1);
                    MoveTo(s, s.X, s.Y + Arg(s, s.Sp - 1));
                    s.Sp = 0;
                    break;
                case 5: // rlineto
                    for (int i = 0; i + 1 < s.Sp; i += 2) LineTo(s, s.X + st[i], s.Y + st[i + 1]);
                    s.Sp = 0;
                    break;
                case 6: case 7: // hlineto vlineto
                {
                    bool horiz = b == 6;
                    for (int i = 0; i < s.Sp; i++, horiz = !horiz)
                        if (horiz) LineTo(s, s.X + st[i], s.Y);
                        else LineTo(s, s.X, s.Y + st[i]);
                    s.Sp = 0;
                    break;
                }
                case 8: // rrcurveto
                    for (int i = 0; i + 5 < s.Sp; i += 6) Curve(s, st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                    s.Sp = 0;
                    break;
                case 24: // rcurveline
                {
                    int i = 0;
                    for (; i + 5 < s.Sp - 2; i += 6) Curve(s, st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                    if (i + 1 < s.Sp) LineTo(s, s.X + st[i], s.Y + st[i + 1]);
                    s.Sp = 0;
                    break;
                }
                case 25: // rlinecurve
                {
                    int i = 0;
                    for (; i + 1 < s.Sp - 6; i += 2) LineTo(s, s.X + st[i], s.Y + st[i + 1]);
                    if (i + 5 < s.Sp) Curve(s, st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                    s.Sp = 0;
                    break;
                }
                case 26: // vvcurveto
                {
                    int i = 0;
                    double dx1 = 0;
                    if (s.Sp % 4 == 1) dx1 = st[i++];
                    for (; i + 3 < s.Sp; i += 4)
                    {
                        Curve(s, dx1, st[i], st[i + 1], st[i + 2], 0, st[i + 3]);
                        dx1 = 0;
                    }
                    s.Sp = 0;
                    break;
                }
                case 27: // hhcurveto
                {
                    int i = 0;
                    double dy1 = 0;
                    if (s.Sp % 4 == 1) dy1 = st[i++];
                    for (; i + 3 < s.Sp; i += 4)
                    {
                        Curve(s, st[i], dy1, st[i + 1], st[i + 2], st[i + 3], 0);
                        dy1 = 0;
                    }
                    s.Sp = 0;
                    break;
                }
                case 30: case 31: // vhcurveto hvcurveto
                {
                    bool h = b == 31;
                    int i = 0;
                    while (i + 3 < s.Sp)
                    {
                        bool last = s.Sp - i == 5;
                        if (h)
                            Curve(s, st[i], 0, st[i + 1], st[i + 2], last ? st[i + 4] : 0, st[i + 3]);
                        else
                            Curve(s, 0, st[i], st[i + 1], st[i + 2], st[i + 3], last ? st[i + 4] : 0);
                        i += last ? 5 : 4;
                        h = !h;
                    }
                    s.Sp = 0;
                    break;
                }
                case 10: // callsubr
                case 29: // callgsubr
                {
                    if (s.Sp < 1) return;
                    var subrs = b == 10 ? s.Subrs : _gsubrs;
                    int count = subrs.Count;
                    int idx = (int)stack.Pop() + Type2.SubrBias(count);
                    if (idx < 0 || idx >= count) break;
                    var (subrStart, subrEnd) = subrs.Range(idx);
                    Run(s, subrStart, subrEnd, depth + 1);
                    break;
                }
                case 11: // return
                    return;
                case 14: // endchar
                    TakeWidth(s, s.Sp == 1 || s.Sp == 5);
                    if (s.Sp >= 4)
                    {
                        // Accented character (seac): base and accent from the standard encoding.
                        double ady = st[s.Sp - 3], adx = st[s.Sp - 4];
                        int bchar = (int)st[s.Sp - 2], achar = (int)st[s.Sp - 1];
                        Seac(s, adx, ady, bchar, achar);
                    }
                    if (s.Open) { s.Path.ClosePath(); s.Open = false; }
                    s.Ended = true;
                    s.Sp = 0;
                    return;
                case 12:
                {
                    int b2 = d[p++];
                    Escape(s, b2);
                    break;
                }
                default:
                    s.Sp = 0;
                    break;
            }
        }
    }

    private void Seac(T2State s, double adx, double ady, int bchar, int achar)
    {
        if (bchar < 0 || bchar > 255 || achar < 0 || achar > 255) return;
        int bg = Encodings.Standard[bchar] is string bn ? GlyphByName(bn) : -1;
        int ag = Encodings.Standard[achar] is string an ? GlyphByName(an) : -1;
        if (bg > 0 && GetGlyph(bg) is PathData bp) bp.AppendTransformed(s.Path, Matrix.Identity);
        if (ag > 0 && GetGlyph(ag) is PathData ap) ap.AppendTransformed(s.Path, Matrix.Translation(adx, ady));
    }

    private void Escape(T2State s, int op)
    {
        var st = s.Stack.Items;
        switch (op)
        {
            case 35: // flex
                if (s.Sp >= 12)
                {
                    Curve(s, st[0], st[1], st[2], st[3], st[4], st[5]);
                    Curve(s, st[6], st[7], st[8], st[9], st[10], st[11]);
                }
                break;
            case 34: // hflex
                if (s.Sp >= 7)
                {
                    double y0 = s.Y;
                    Curve(s, st[0], 0, st[1], st[2], st[3], 0);
                    Curve(s, st[4], 0, st[5], y0 - s.Y, st[6], 0);
                }
                break;
            case 36: // hflex1
                if (s.Sp >= 9)
                {
                    Curve(s, st[0], st[1], st[2], st[3], st[4], 0);
                    Curve(s, st[5], 0, st[6], st[7], st[8], -(st[1] + st[3] + st[7]));
                }
                break;
            case 37: // flex1
                if (s.Sp >= 11)
                {
                    double x0 = s.X, y0 = s.Y;
                    double dx = st[0] + st[2] + st[4] + st[6] + st[8], dy = st[1] + st[3] + st[5] + st[7] + st[9];
                    Curve(s, st[0], st[1], st[2], st[3], st[4], st[5]);
                    if (Math.Abs(dx) > Math.Abs(dy)) Curve(s, st[6], st[7], st[8], st[9], st[10], y0 - (s.Y + st[7] + st[9]));
                    else Curve(s, st[6], st[7], st[8], st[9], x0 - (s.X + st[6] + st[8]), st[10]);
                }
                break;
            // Arithmetic operators (rarely used).
            case 3: if (s.Sp >= 2) { st[s.Sp - 2] = st[s.Sp - 2] != 0 && st[s.Sp - 1] != 0 ? 1 : 0; s.Sp--; } return;
            case 4: if (s.Sp >= 2) { st[s.Sp - 2] = st[s.Sp - 2] != 0 || st[s.Sp - 1] != 0 ? 1 : 0; s.Sp--; } return;
            case 5: if (s.Sp >= 1) st[s.Sp - 1] = st[s.Sp - 1] != 0 ? 0 : 1; return;
            case 9: if (s.Sp >= 1) st[s.Sp - 1] = Math.Abs(st[s.Sp - 1]); return;
            case 10: if (s.Sp >= 2) { st[s.Sp - 2] += st[s.Sp - 1]; s.Sp--; } return;
            case 11: if (s.Sp >= 2) { st[s.Sp - 2] -= st[s.Sp - 1]; s.Sp--; } return;
            case 12: if (s.Sp >= 2) { st[s.Sp - 2] = st[s.Sp - 1] == 0 ? 0 : st[s.Sp - 2] / st[s.Sp - 1]; s.Sp--; } return;
            case 14: if (s.Sp >= 1) st[s.Sp - 1] = -st[s.Sp - 1]; return;
            case 15: if (s.Sp >= 2) { st[s.Sp - 2] = st[s.Sp - 2] == st[s.Sp - 1] ? 1 : 0; s.Sp--; } return;
            case 18: if (s.Sp >= 1) s.Sp--; return;
            case 20: if (s.Sp >= 2) { int i = (int)st[s.Sp - 1]; if (i >= 0 && i < s.Transient.Length) s.Transient[i] = st[s.Sp - 2]; s.Sp -= 2; } return;
            case 21: if (s.Sp >= 1) { int i = (int)st[s.Sp - 1]; st[s.Sp - 1] = i >= 0 && i < s.Transient.Length ? s.Transient[i] : 0; } return;
            case 22: if (s.Sp >= 4) { st[s.Sp - 4] = st[s.Sp - 2] <= st[s.Sp - 1] ? st[s.Sp - 4] : st[s.Sp - 3]; s.Sp -= 3; } return;
            case 23: if (s.Sp < st.Length) st[s.Sp++] = 0.5; return;
            case 24: if (s.Sp >= 2) { st[s.Sp - 2] *= st[s.Sp - 1]; s.Sp--; } return;
            case 26: if (s.Sp >= 1) st[s.Sp - 1] = Math.Sqrt(Math.Abs(st[s.Sp - 1])); return;
            case 27: if (s.Sp >= 1 && s.Sp < st.Length) { st[s.Sp] = st[s.Sp - 1]; s.Sp++; } return;
            case 28: if (s.Sp >= 2) (st[s.Sp - 1], st[s.Sp - 2]) = (st[s.Sp - 2], st[s.Sp - 1]); return;
            case 29: if (s.Sp >= 1) { int i = (int)st[s.Sp - 1]; st[s.Sp - 1] = i >= 0 && i < s.Sp - 1 ? st[s.Sp - 2 - i] : 0; } return;
            case 30:
                if (s.Sp >= 2)
                {
                    int j = (int)st[s.Sp - 1], n = (int)st[s.Sp - 2];
                    s.Sp -= 2;
                    if (n > 0 && n <= s.Sp)
                    {
                        j = ((j % n) + n) % n;
                        var tmp = new double[n];
                        Array.Copy(st, s.Sp - n, tmp, 0, n);
                        for (int k = 0; k < n; k++) st[s.Sp - n + (k + j) % n] = tmp[k];
                    }
                }
                return;
        }
        s.Sp = 0;
    }

    private static double Arg(T2State s, int i) => i >= 0 && i < s.Sp ? s.Stack[i] : 0;

    private static void TakeWidth(T2State s, bool hasExtra)
    {
        if (s.HaveWidth) return;
        s.HaveWidth = true;
        if (hasExtra && s.Sp > 0)
        {
            s.Width = s.NominalWidth + s.Stack[0];
            Array.Copy(s.Stack.Items, 1, s.Stack.Items, 0, s.Sp - 1);
            s.Sp--;
        }
        else s.Width = s.DefaultWidth;
    }

    private static void MoveTo(T2State s, double x, double y)
    {
        if (s.Open) s.Path.ClosePath();
        s.X = x;
        s.Y = y;
        s.Path.Move(x, y);
        s.Open = true;
    }

    private static void LineTo(T2State s, double x, double y)
    {
        if (!s.Open) MoveTo(s, s.X, s.Y);
        s.X = x;
        s.Y = y;
        s.Path.Line(x, y);
    }

    private static void Curve(T2State s, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        if (!s.Open) MoveTo(s, s.X, s.Y);
        double x1 = s.X + dx1, y1 = s.Y + dy1, x2 = x1 + dx2, y2 = y1 + dy2, x3 = x2 + dx3, y3 = y2 + dy3;
        s.Path.Cubic(x1, y1, x2, y2, x3, y3);
        s.X = x3;
        s.Y = y3;
    }

    /// <summary>The 391 predefined CFF strings (SIDs 0-390).</summary>
    internal static readonly string[] StandardStrings =
    {
        ".notdef", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quoteright", "parenleft",
        "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five",
        "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D",
        "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "quoteleft", "a", "b", "c", "d", "e", "f", "g",
        "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar",
        "braceright", "asciitilde", "exclamdown", "cent", "sterling", "fraction", "yen", "florin", "section", "currency",
        "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft", "guilsinglright", "fi", "fl", "endash", "dagger",
        "daggerdbl", "periodcentered", "paragraph", "bullet", "quotesinglbase", "quotedblbase", "quotedblright",
        "guillemotright", "ellipsis", "perthousand", "questiondown", "grave", "acute", "circumflex", "tilde", "macron", "breve",
        "dotaccent", "dieresis", "ring", "cedilla", "hungarumlaut", "ogonek", "caron", "emdash", "AE", "ordfeminine", "Lslash",
        "Oslash", "OE", "ordmasculine", "ae", "dotlessi", "lslash", "oslash", "oe", "germandbls", "onesuperior", "logicalnot",
        "mu", "trademark", "Eth", "onehalf", "plusminus", "Thorn", "onequarter", "divide", "brokenbar", "degree", "thorn",
        "threequarters", "twosuperior", "registered", "minus", "eth", "multiply", "threesuperior", "copyright", "Aacute",
        "Acircumflex", "Adieresis", "Agrave", "Aring", "Atilde", "Ccedilla", "Eacute", "Ecircumflex", "Edieresis", "Egrave",
        "Iacute", "Icircumflex", "Idieresis", "Igrave", "Ntilde", "Oacute", "Ocircumflex", "Odieresis", "Ograve", "Otilde",
        "Scaron", "Uacute", "Ucircumflex", "Udieresis", "Ugrave", "Yacute", "Ydieresis", "Zcaron", "aacute", "acircumflex",
        "adieresis", "agrave", "aring", "atilde", "ccedilla", "eacute", "ecircumflex", "edieresis", "egrave", "iacute",
        "icircumflex", "idieresis", "igrave", "ntilde", "oacute", "ocircumflex", "odieresis", "ograve", "otilde", "scaron",
        "uacute", "ucircumflex", "udieresis", "ugrave", "yacute", "ydieresis", "zcaron", "exclamsmall", "Hungarumlautsmall",
        "dollaroldstyle", "dollarsuperior", "ampersandsmall", "Acutesmall", "parenleftsuperior", "parenrightsuperior",
        "twodotenleader", "onedotenleader", "zerooldstyle", "oneoldstyle", "twooldstyle", "threeoldstyle", "fouroldstyle",
        "fiveoldstyle", "sixoldstyle", "sevenoldstyle", "eightoldstyle", "nineoldstyle", "commasuperior",
        "threequartersemdash", "periodsuperior", "questionsmall", "asuperior", "bsuperior", "centsuperior", "dsuperior",
        "esuperior", "isuperior", "lsuperior", "msuperior", "nsuperior", "osuperior", "rsuperior", "ssuperior", "tsuperior",
        "ff", "ffi", "ffl", "parenleftinferior", "parenrightinferior", "Circumflexsmall", "hyphensuperior", "Gravesmall",
        "Asmall", "Bsmall", "Csmall", "Dsmall", "Esmall", "Fsmall", "Gsmall", "Hsmall", "Ismall", "Jsmall", "Ksmall", "Lsmall",
        "Msmall", "Nsmall", "Osmall", "Psmall", "Qsmall", "Rsmall", "Ssmall", "Tsmall", "Usmall", "Vsmall", "Wsmall", "Xsmall",
        "Ysmall", "Zsmall", "colonmonetary", "onefitted", "rupiah", "Tildesmall", "exclamdownsmall", "centoldstyle",
        "Lslashsmall", "Scaronsmall", "Zcaronsmall", "Dieresissmall", "Brevesmall", "Caronsmall", "Dotaccentsmall",
        "Macronsmall", "figuredash", "hypheninferior", "Ogoneksmall", "Ringsmall", "Cedillasmall", "questiondownsmall",
        "oneeighth", "threeeighths", "fiveeighths", "seveneighths", "onethird", "twothirds", "zerosuperior", "foursuperior",
        "fivesuperior", "sixsuperior", "sevensuperior", "eightsuperior", "ninesuperior", "zeroinferior", "oneinferior",
        "twoinferior", "threeinferior", "fourinferior", "fiveinferior", "sixinferior", "seveninferior", "eightinferior",
        "nineinferior", "centinferior", "dollarinferior", "periodinferior", "commainferior", "Agravesmall", "Aacutesmall",
        "Acircumflexsmall", "Atildesmall", "Adieresissmall", "Aringsmall", "AEsmall", "Ccedillasmall", "Egravesmall",
        "Eacutesmall", "Ecircumflexsmall", "Edieresissmall", "Igravesmall", "Iacutesmall", "Icircumflexsmall",
        "Idieresissmall", "Ethsmall", "Ntildesmall", "Ogravesmall", "Oacutesmall", "Ocircumflexsmall", "Otildesmall",
        "Odieresissmall", "OEsmall", "Oslashsmall", "Ugravesmall", "Uacutesmall", "Ucircumflexsmall", "Udieresissmall",
        "Yacutesmall", "Thornsmall", "Ydieresissmall", "001.000", "001.001", "001.002", "001.003", "Black", "Bold", "Book",
        "Light", "Medium", "Regular", "Roman", "Semibold",
    };
}
