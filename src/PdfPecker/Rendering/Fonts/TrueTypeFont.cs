using System.Buffers.Binary;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>A source of glyph outlines in glyph space (y up).</summary>
internal interface IGlyphSource
{
    /// <summary>Scale from glyph units to text space (1/unitsPerEm for TrueType, the FontMatrix for CFF/Type 1).</summary>
    Matrix FontMatrix { get; }

    int GlyphCount { get; }

    PathData? GetGlyph(int gid);

    /// <summary>Advance width in glyph units, or -1 if unknown.</summary>
    double Advance(int gid);
}

/// <summary>Parser for TrueType outlines (glyf/loca), character maps, metrics and glyph names.</summary>
internal sealed class TrueTypeFont : IGlyphSource
{
    private readonly byte[] _d;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private int[] _loca = Array.Empty<int>();
    private int _glyf = -1, _glyfLen;
    private int _numHMetrics, _hmtx = -1;
    private readonly List<(int Platform, int Encoding, int Offset)> _cmaps = new();
    private readonly Dictionary<int, Dictionary<int, int>> _cmapCache = new();
    private Dictionary<string, int>? _postNames;
    private bool _postLoaded;
    private PathData?[] _glyphCache = Array.Empty<PathData?>();
    private readonly object _lock = new();

    public int UnitsPerEm { get; private set; } = 1000;
    public int GlyphCount { get; private set; }
    public Matrix FontMatrix => Matrix.Scale(1.0 / UnitsPerEm, 1.0 / UnitsPerEm);
    public int Ascender { get; private set; }
    public int Descender { get; private set; }

    /// <summary>The raw CFF table of an OpenType font with PostScript outlines, if any.</summary>
    public byte[]? CffTable { get; private set; }

    private TrueTypeFont(byte[] data) => _d = data;

    public static TrueTypeFont? Parse(byte[] data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static TrueTypeFont? ParseCore(byte[] d)
    {
        if (d.Length < 12) return null;
        int dirOffset = 0;
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(d);
        if (tag == 0x74746366) // 'ttcf': first font of a collection
            dirOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
        int numTables = U16(d, dirOffset + 4);
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            int rec = dirOffset + 12 + i * 16;
            if (rec + 16 > d.Length) break;
            string name = System.Text.Encoding.ASCII.GetString(d, rec, 4);
            int off = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 8));
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 12));
            if (off < 0 || off > d.Length) continue;
            len = Math.Min(len, d.Length - off);
            tables[name] = (off, len);
        }
        int numGlyphs = 0;
        if (tables.TryGetValue("maxp", out var maxp) && maxp.Item2 >= 6) numGlyphs = U16(d, maxp.Item1 + 4);
        var f = new TrueTypeFont(d);
        foreach (var kv in tables) f._tables[kv.Key] = kv.Value;
        f.GlyphCount = numGlyphs;

        if (tables.TryGetValue("head", out var head) && head.Item2 >= 54)
        {
            int upem = U16(d, head.Item1 + 18);
            f.UnitsPerEm = upem is >= 16 and <= 16384 ? upem : 1000;
        }
        if (tables.TryGetValue("hhea", out var hhea) && hhea.Item2 >= 36)
        {
            f.Ascender = S16(d, hhea.Item1 + 4);
            f.Descender = S16(d, hhea.Item1 + 6);
            f._numHMetrics = U16(d, hhea.Item1 + 34);
        }
        if (tables.TryGetValue("hmtx", out var hmtx)) f._hmtx = hmtx.Item1;
        if (tables.TryGetValue("CFF ", out var cff)) f.CffTable = d.AsSpan(cff.Item1, cff.Item2).ToArray();

        if (tables.TryGetValue("glyf", out var glyf) && tables.TryGetValue("loca", out var loca) && tables.TryGetValue("head", out head))
        {
            f._glyf = glyf.Item1;
            f._glyfLen = glyf.Item2;
            bool longFormat = S16(d, head.Item1 + 50) != 0;
            int entries = longFormat ? loca.Item2 / 4 : loca.Item2 / 2;
            if (numGlyphs == 0) numGlyphs = Math.Max(0, entries - 1);
            int n = Math.Min(entries, numGlyphs + 1);
            // Some subset fonts have a short loca table; also guess the format when the declared one does not fit.
            if (!longFormat && loca.Item2 >= (numGlyphs + 1) * 4 && loca.Item2 < (numGlyphs + 1) * 2) longFormat = true;
            f._loca = new int[n];
            for (int i = 0; i < n; i++)
                f._loca[i] = longFormat ? (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(loca.Item1 + i * 4)) : U16(d, loca.Item1 + i * 2) * 2;
            if (f.GlyphCount == 0) f.GlyphCount = Math.Max(0, n - 1);
        }

        if (tables.TryGetValue("cmap", out var cmap) && cmap.Item2 >= 4)
        {
            int count = U16(d, cmap.Item1 + 2);
            for (int i = 0; i < count; i++)
            {
                int rec = cmap.Item1 + 4 + i * 8;
                if (rec + 8 > cmap.Item1 + cmap.Item2) break;
                int platform = U16(d, rec), encoding = U16(d, rec + 2);
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 4));
                if (off > 0 && off < cmap.Item2) f._cmaps.Add((platform, encoding, cmap.Item1 + off));
            }
        }
        f._glyphCache = new PathData?[Math.Max(0, f.GlyphCount)];
        return f;
    }

    public bool HasGlyf => _glyf >= 0 && _loca.Length > 1;

    private static int U16(byte[] d, int o) => d[o] << 8 | d[o + 1];
    private static int S16(byte[] d, int o) => (short)(d[o] << 8 | d[o + 1]);

    // ------------------------------------------------------------------ cmap

    public bool HasCmap(int platform, int encoding) => _cmaps.Any(c => c.Platform == platform && c.Encoding == encoding);

    public bool HasAnyCmap => _cmaps.Count > 0;

    /// <summary>Looks a code up in the (platform, encoding) subtable. Returns 0 if absent.</summary>
    public int Lookup(int platform, int encoding, int code)
    {
        var map = GetCmap(platform, encoding);
        return map != null && map.TryGetValue(code, out int gid) ? gid : 0;
    }

    public Dictionary<int, int>? GetCmap(int platform, int encoding)
    {
        lock (_lock)
        {
            int key = platform << 16 | encoding;
            if (_cmapCache.TryGetValue(key, out var cached)) return cached;
            Dictionary<int, int>? map = null;
            foreach (var c in _cmaps)
            {
                if (c.Platform != platform || c.Encoding != encoding) continue;
                try { map = ReadCmap(c.Offset); }
                catch (IndexOutOfRangeException) { map = null; }
                if (map != null) break;
            }
            _cmapCache[key] = map!;
            return map;
        }
    }

    /// <summary>The first usable subtable, for fonts whose cmap has an unusual platform.</summary>
    public Dictionary<int, int>? AnyCmap()
    {
        foreach (var c in _cmaps)
        {
            var m = GetCmap(c.Platform, c.Encoding);
            if (m != null && m.Count > 0) return m;
        }
        return null;
    }

    private Dictionary<int, int>? ReadCmap(int o)
    {
        var d = _d;
        int format = U16(d, o);
        var map = new Dictionary<int, int>();
        switch (format)
        {
            case 0:
                for (int i = 0; i < 256; i++) if (d[o + 6 + i] != 0) map[i] = d[o + 6 + i];
                break;
            case 4:
            {
                int segX2 = U16(d, o + 6);
                int ends = o + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                for (int s = 0; s < segX2 / 2; s++)
                {
                    int end = U16(d, ends + s * 2), start = U16(d, starts + s * 2);
                    int delta = S16(d, deltas + s * 2), rangeOff = U16(d, ranges + s * 2);
                    if (start > end) continue;
                    for (int c = start; c <= end && c < 0xFFFF; c++)
                    {
                        int gid;
                        if (rangeOff == 0) gid = (c + delta) & 0xFFFF;
                        else
                        {
                            int addr = ranges + s * 2 + rangeOff + (c - start) * 2;
                            if (addr + 1 >= d.Length) continue;
                            gid = U16(d, addr);
                            if (gid != 0) gid = (gid + delta) & 0xFFFF;
                        }
                        if (gid != 0) map[c] = gid;
                    }
                }
                break;
            }
            case 6:
            {
                int first = U16(d, o + 6), count = U16(d, o + 8);
                for (int i = 0; i < count; i++)
                {
                    int gid = U16(d, o + 10 + i * 2);
                    if (gid != 0) map[first + i] = gid;
                }
                break;
            }
            case 10:
            {
                int first = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o + 12));
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o + 16));
                for (int i = 0; i < count && i < 65536; i++)
                {
                    int gid = U16(d, o + 20 + i * 2);
                    if (gid != 0) map[first + i] = gid;
                }
                break;
            }
            case 12:
            case 13:
            {
                int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o + 12));
                int total = 0;
                for (int g = 0; g < groups; g++)
                {
                    int rec = o + 16 + g * 12;
                    int start = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec));
                    int end = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 4));
                    int gid = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(rec + 8));
                    if (end < start || end - start > 0x10FFFF) continue;
                    for (int c = start; c <= end; c++)
                    {
                        if (++total > 200_000) return map;
                        map[c] = format == 12 ? gid + (c - start) : gid;
                    }
                }
                break;
            }
            case 2:
            {
                // High-byte mapping through subHeaders (CJK): map single bytes and two-byte codes.
                int keys = o + 6;
                int subHeaders = keys + 512;
                for (int hi = 0; hi < 256; hi++)
                {
                    int k = U16(d, keys + hi * 2) / 8;
                    int sh = subHeaders + k * 8;
                    int firstCode = U16(d, sh), entryCount = U16(d, sh + 2), idDelta = S16(d, sh + 4), idRangeOffset = U16(d, sh + 6);
                    int arr = sh + 6 + idRangeOffset;
                    for (int i = 0; i < entryCount; i++)
                    {
                        int addr = arr + i * 2;
                        if (addr + 1 >= d.Length) break;
                        int gid = U16(d, addr);
                        if (gid != 0) gid = (gid + idDelta) & 0xFFFF;
                        if (gid == 0) continue;
                        int code = k == 0 ? firstCode + i : hi << 8 | (firstCode + i);
                        map[code] = gid;
                    }
                    if (k == 0 && hi > 0) continue;
                }
                break;
            }
            default:
                return null;
        }
        return map;
    }

    // ------------------------------------------------------------------ post table glyph names

    public int GlyphByName(string name)
    {
        lock (_lock)
        {
            if (!_postLoaded)
            {
                _postLoaded = true;
                try { _postNames = ReadPostNames(); }
                catch (IndexOutOfRangeException) { _postNames = null; }
            }
        }
        return _postNames != null && _postNames.TryGetValue(name, out int g) ? g : -1;
    }

    /// <summary>All glyph names from the post table (format 2), by glyph id.</summary>
    public Dictionary<string, int>? PostNames
    {
        get
        {
            GlyphByName("");
            return _postNames;
        }
    }

    private Dictionary<string, int>? ReadPostNames()
    {
        if (!_tables.TryGetValue("post", out var post) || post.Length < 34) return null;
        var d = _d;
        int o = post.Offset;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        if (version == 0x00010000)
        {
            for (int i = 0; i < MacGlyphNames.Length; i++) names.TryAdd(MacGlyphNames[i], i);
            return names;
        }
        if (version != 0x00020000) return null;
        int n = U16(d, o + 32);
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = U16(d, o + 34 + i * 2);
        var custom = new List<string>();
        int p = o + 34 + n * 2, end = o + post.Length;
        while (p < end)
        {
            int len = d[p++];
            if (p + len > end) break;
            custom.Add(System.Text.Encoding.Latin1.GetString(d, p, len));
            p += len;
        }
        for (int gid = 0; gid < n; gid++)
        {
            int idx = indices[gid];
            string? name = idx < 258 ? MacGlyphNames[idx] : idx - 258 < custom.Count ? custom[idx - 258] : null;
            if (name != null) names.TryAdd(name, gid);
        }
        return names;
    }

    // ------------------------------------------------------------------ metrics

    public double Advance(int gid)
    {
        if (_hmtx < 0 || _numHMetrics <= 0) return -1;
        int i = Math.Min(gid, _numHMetrics - 1);
        int o = _hmtx + i * 4;
        if (o + 2 > _d.Length) return -1;
        return U16(_d, o);
    }

    // ------------------------------------------------------------------ outlines

    public PathData? GetGlyph(int gid)
    {
        if (gid < 0 || gid >= _glyphCache.Length || !HasGlyf) return null;
        var cached = Volatile.Read(ref _glyphCache[gid]);
        if (cached != null) return cached;
        var path = new PathData();
        try
        {
            int budget = 256; // components per glyph: guards against exponential composite nesting
            AppendGlyph(path, gid, Matrix.Identity, 0, ref budget);
        }
        catch (IndexOutOfRangeException)
        {
            path.Clear();
        }
        Volatile.Write(ref _glyphCache[gid], path);
        return path;
    }

    /// <summary>Glyph contours as raw TrueType points (for the font pack generator).</summary>
    public bool TryGetRawContours(int gid, List<List<(int X, int Y, bool On)>> contours)
        => RawGlyph(gid, Matrix.Identity, 0, contours);

    private bool RawGlyph(int gid, Matrix m, int depth, List<List<(int X, int Y, bool On)>> output)
    {
        if (depth > 8 || gid < 0 || gid + 1 >= _loca.Length) return false;
        int start = _loca[gid], end = _loca[gid + 1];
        if (end <= start || start >= _glyfLen) return true;
        int o = _glyf + start;
        var d = _d;
        int nContours = S16(d, o);
        if (nContours >= 0)
        {
            ReadSimple(o, nContours, out var xs, out var ys, out var on, out var endPts);
            int p = 0;
            for (int c = 0; c < nContours; c++)
            {
                var list = new List<(int, int, bool)>();
                for (; p <= endPts[c] && p < xs.Length; p++)
                {
                    var (tx, ty) = m.Apply(xs[p], ys[p]);
                    list.Add(((int)Math.Round(tx), (int)Math.Round(ty), on[p]));
                }
                output.Add(list);
            }
            return true;
        }
        foreach (var (cg, cm) in Components(o))
            RawGlyph(cg, cm.Multiply(m), depth + 1, output);
        return true;
    }

    private void AppendGlyph(PathData path, int gid, Matrix m, int depth, ref int budget)
    {
        if (depth > 8 || --budget < 0 || gid < 0 || gid + 1 >= _loca.Length) return;
        int start = _loca[gid], end = _loca[gid + 1];
        if (end <= start || start >= _glyfLen) return;
        int o = _glyf + start;
        int nContours = S16(_d, o);
        if (nContours >= 0)
        {
            ReadSimple(o, nContours, out var xs, out var ys, out var on, out var endPts);
            int p = 0;
            for (int c = 0; c < nContours; c++)
            {
                int first = p, last = Math.Min(endPts[c], xs.Length - 1);
                if (last < first) continue; // malformed (non-increasing) end point: skip, never revisit points
                AddContour(path, xs, ys, on, first, last, m);
                p = last + 1;
            }
            return;
        }
        foreach (var (cg, cm) in Components(o)) AppendGlyph(path, cg, cm.Multiply(m), depth + 1, ref budget);
    }

    private IEnumerable<(int Gid, Matrix M)> Components(int o)
    {
        var d = _d;
        int p = o + 10;
        var result = new List<(int, Matrix)>();
        while (true)
        {
            int flags = U16(d, p), glyph = U16(d, p + 2);
            p += 4;
            double dx, dy;
            if ((flags & 1) != 0) { dx = S16(d, p); dy = S16(d, p + 2); p += 4; }
            else { dx = (sbyte)d[p]; dy = (sbyte)d[p + 1]; p += 2; }
            if ((flags & 2) == 0) { dx = 0; dy = 0; } // point matching: not supported, place at origin
            double a = 1, b = 0, c = 0, e = 1;
            if ((flags & 8) != 0) { a = e = F2Dot14(d, p); p += 2; }
            else if ((flags & 0x40) != 0) { a = F2Dot14(d, p); e = F2Dot14(d, p + 2); p += 4; }
            else if ((flags & 0x80) != 0) { a = F2Dot14(d, p); b = F2Dot14(d, p + 2); c = F2Dot14(d, p + 4); e = F2Dot14(d, p + 6); p += 8; }
            result.Add((glyph, new Matrix(a, b, c, e, dx, dy)));
            if ((flags & 0x20) == 0 || result.Count > 64) break;
        }
        return result;
    }

    private static double F2Dot14(byte[] d, int o) => S16(d, o) / 16384.0;

    private void ReadSimple(int o, int nContours, out int[] xs, out int[] ys, out bool[] on, out int[] endPts)
    {
        var d = _d;
        endPts = new int[nContours];
        int p = o + 10;
        for (int i = 0; i < nContours; i++) { endPts[i] = U16(d, p); p += 2; }
        int nPts = nContours == 0 ? 0 : endPts[nContours - 1] + 1;
        int instrLen = U16(d, p);
        p += 2 + instrLen;
        var flags = new byte[nPts];
        for (int i = 0; i < nPts;)
        {
            byte f = d[p++];
            flags[i++] = f;
            if ((f & 8) != 0)
            {
                int rep = d[p++];
                for (int k = 0; k < rep && i < nPts; k++) flags[i++] = f;
            }
        }
        xs = new int[nPts];
        ys = new int[nPts];
        on = new bool[nPts];
        int v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flags[i];
            if ((f & 2) != 0) { int dv = d[p++]; v += (f & 16) != 0 ? dv : -dv; }
            else if ((f & 16) == 0) { v += S16(d, p); p += 2; }
            xs[i] = v;
            on[i] = (f & 1) != 0;
        }
        v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flags[i];
            if ((f & 4) != 0) { int dv = d[p++]; v += (f & 32) != 0 ? dv : -dv; }
            else if ((f & 32) == 0) { v += S16(d, p); p += 2; }
            ys[i] = v;
        }
    }

    /// <summary>Adds one quadratic TrueType contour as cubic segments.</summary>
    public static void AddContour(PathData path, int[] xs, int[] ys, bool[] on, int first, int last, in Matrix m)
    {
        int n = last - first + 1;
        if (n < 2) return;
        int s = -1;
        for (int i = 0; i < n; i++) if (on[first + i]) { s = i; break; }
        double startX, startY;
        int walkFrom;
        if (s < 0)
        {
            // No on-curve point: start at the implied point between the first two control points.
            startX = (xs[first] + xs[first + 1]) / 2.0;
            startY = (ys[first] + ys[first + 1]) / 2.0;
            walkFrom = 1;
        }
        else
        {
            startX = xs[first + s];
            startY = ys[first + s];
            walkFrom = s + 1;
        }
        var (mx, my) = m.Apply(startX, startY);
        path.Move(mx, my);
        double curX = startX, curY = startY, ctrlX = 0, ctrlY = 0;
        bool pending = false;
        int steps = s < 0 ? n : n - 1;
        for (int k = 0; k < steps; k++)
        {
            int i = first + (walkFrom + k) % n;
            double x = xs[i], y = ys[i];
            if (on[i])
            {
                if (pending) Quad(path, curX, curY, ctrlX, ctrlY, x, y, m);
                else
                {
                    var (tx, ty) = m.Apply(x, y);
                    path.Line(tx, ty);
                }
                curX = x; curY = y;
                pending = false;
            }
            else
            {
                if (pending)
                {
                    double midX = (ctrlX + x) / 2, midY = (ctrlY + y) / 2;
                    Quad(path, curX, curY, ctrlX, ctrlY, midX, midY, m);
                    curX = midX; curY = midY;
                }
                ctrlX = x; ctrlY = y;
                pending = true;
            }
        }
        if (pending) Quad(path, curX, curY, ctrlX, ctrlY, startX, startY, m);
        path.ClosePath();
    }

    private static void Quad(PathData path, double x0, double y0, double qx, double qy, double x1, double y1, in Matrix m)
    {
        double c1x = x0 + 2.0 / 3 * (qx - x0), c1y = y0 + 2.0 / 3 * (qy - y0);
        double c2x = x1 + 2.0 / 3 * (qx - x1), c2y = y1 + 2.0 / 3 * (qy - y1);
        var (ax, ay) = m.Apply(c1x, c1y);
        var (bx, by) = m.Apply(c2x, c2y);
        var (ex, ey) = m.Apply(x1, y1);
        path.Cubic(ax, ay, bx, by, ex, ey);
    }

    /// <summary>The 258 standard Macintosh glyph names used by post table formats 1 and 2.</summary>
    internal static readonly string[] MacGlyphNames =
    {
        ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand",
        "quotesingle", "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two",
        "three", "four", "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y",
        "Z", "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d", "e", "f", "g",
        "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar",
        "braceright", "asciitilde", "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute",
        "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis",
        "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute", "ograve", "ocircumflex", "odieresis", "otilde",
        "uacute", "ugrave", "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph",
        "germandbls", "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash", "infinity",
        "plusminus", "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation", "product", "pi", "integral",
        "ordfeminine", "ordmasculine", "Omega", "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical", "florin",
        "approxequal", "Delta", "guillemotleft", "guillemotright", "ellipsis", "nonbreakingspace", "Agrave", "Atilde", "Otilde",
        "OE", "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge",
        "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl",
        "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute", "Edieresis",
        "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex", "apple", "Ograve", "Uacute",
        "Ucircumflex", "Ugrave", "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla",
        "hungarumlaut", "ogonek", "caron", "Lslash", "lslash", "Scaron", "scaron", "Zcaron", "zcaron", "brokenbar", "Eth", "eth",
        "Yacute", "yacute", "Thorn", "thorn", "minus", "multiply", "onesuperior", "twosuperior", "threesuperior", "onehalf",
        "onequarter", "threequarters", "franc", "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla", "Cacute", "cacute",
        "Ccaron", "ccaron", "dcroat",
    };
}
