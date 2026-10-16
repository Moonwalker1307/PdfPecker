using System.Buffers.Binary;
using System.Globalization;
using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>
/// Turns a CFF2 (PostScript-flavoured variable) font into a static CFF table at one instance. Every glyph's
/// charstring is run with its blends resolved and subroutines expanded, and written again as plain moveto, lineto and
/// curveto operators in a CID-keyed CFF (version 1) font, which PDF readers and the rest of this library understand;
/// few PDF readers can draw CFF2. Hints are left out, as the TrueType instances leave out their instructions.
/// </summary>
internal static class Cff2Instancer
{
    public sealed record Result(byte[] Cff, int[] Advances, short[] Lsbs, (short XMin, short YMin, short XMax, short YMax)?[] Boxes);

    public static Result Instance(FontProgram p, float[] coords, string fontName)
    {
        var t = p.Table("CFF2")!.Value;
        var d = p.Data.AsSpan(t.Offset, t.Length).ToArray();
        if (d.Length < 5 || d[0] != 2) throw new FormatException("Not a CFF2 table.");
        int headerSize = d[2], topLength = d[3] << 8 | d[4];
        if (headerSize + topLength > d.Length) throw new FormatException("Bad CFF2 header.");
        var top = CffDict.ParseCff2(d, headerSize, headerSize + topLength, null);
        int gsubrPos = headerSize + topLength;
        var gsubrs = CffIndex.ReadCff2(d, ref gsubrPos);

        VariationStore? store = null;
        if (top.TryGetValue(24, out var vs) && vs.Length > 0)
        {
            int o = (int)vs[0];
            if (o + 2 > d.Length) throw new FormatException("Bad CFF2 vstore.");
            store = new VariationStore(d, o + 2, Math.Min(d.Length, o + 2 + (d[o] << 8 | d[o + 1])), coords);
        }

        if (!top.TryGetValue(17, out var cs) || cs.Length == 0) throw new FormatException("CFF2 without CharStrings.");
        int csPos = (int)cs[0];
        var charStrings = CffIndex.ReadCff2(d, ref csPos);
        int n = charStrings.Count;

        // Font DICTs: each has a Private DICT with its local subroutines and default vsindex.
        var fds = new List<(CffIndex Subrs, int VsIndex)>();
        if (top.TryGetValue(CffDict.Escape + 36, out var fdArrayOff) && fdArrayOff.Length > 0)
        {
            int pos = (int)fdArrayOff[0];
            var fdArray = CffIndex.ReadCff2(d, ref pos);
            for (int i = 0; i < fdArray.Count; i++)
            {
                var (s, e) = fdArray.Range(i);
                var fd = CffDict.ParseCff2(d, s, e, null);
                CffIndex subrs = default;
                int vsindex = 0;
                if (fd.TryGetValue(18, out var priv) && priv.Length >= 2)
                {
                    int size = (int)priv[0], off = (int)priv[1];
                    if (off < 0 || size < 0 || off + size > d.Length) throw new FormatException("Bad CFF2 Private DICT.");
                    var pd = CffDict.ParseCff2(d, off, off + size, store);
                    if (pd.TryGetValue(22, out var v) && v.Length > 0) vsindex = (int)v[0];
                    if (pd.TryGetValue(19, out var sub) && sub.Length > 0)
                    {
                        int sp = off + (int)sub[0];
                        subrs = CffIndex.ReadCff2(d, ref sp);
                    }
                }
                fds.Add((subrs, vsindex));
            }
        }
        if (fds.Count == 0) fds.Add((default, 0));
        var fdOf = new byte[n];
        if (top.TryGetValue(CffDict.Escape + 37, out var fdSelect) && fdSelect.Length > 0) ReadFdSelect(d, (int)fdSelect[0], fdOf, fds.Count);

        // HVAR moves the advance widths; the left side bearing is the outline's own.
        Func<int, float>? advanceDelta = null;
        if (p.Table("HVAR") is { Length: >= 20 } hvar)
        {
            uint storeOff = p.U32(hvar.Offset + 4), mapOff = p.U32(hvar.Offset + 8);
            if (storeOff > 0 && storeOff < hvar.Length)
            {
                var hstore = new VariationStore(p.Data, hvar.Offset + (int)storeOff, hvar.Offset + hvar.Length, coords);
                advanceDelta = mapOff > 0 && mapOff < hvar.Length
                    ? g => { var (o, i) = VariationStore.MapIndex(p.Data, hvar.Offset + (int)mapOff, hvar.Offset + hvar.Length, g); return hstore.Delta(o, i); }
                    : g => hstore.Delta(0, g);
            }
        }

        var glyphs = new byte[n][];
        var advances = new int[n];
        var lsbs = new short[n];
        var boxes = new (short, short, short, short)?[n];
        var run = new Charstring(d, gsubrs, store);
        for (int g = 0; g < n; g++)
        {
            int advance = (int)MathF.Round(p.Advance(g) + (advanceDelta?.Invoke(g) ?? 0));
            advances[g] = Math.Max(0, advance);
            var fd = fds[fdOf[g] < fds.Count ? fdOf[g] : 0];
            var (s, e) = charStrings.Range(g);
            run.Run(s, e, fd.Subrs, fd.VsIndex);
            glyphs[g] = run.Encode(advances[g], out var box);
            boxes[g] = box;
            lsbs[g] = box?.Item1 ?? 0;
        }

        double[]? matrix = top.TryGetValue(CffDict.Escape + 7, out var fm) && fm.Length == 6 ? fm : null;
        return new Result(WriteCff(fontName, glyphs, matrix), advances, lsbs, boxes);
    }

    // ---- Reading CFF2 ----

    private static void ReadFdSelect(byte[] d, int o, byte[] fdOf, int fdCount)
    {
        int n = fdOf.Length;
        int format = d[o];
        if (format == 0)
        {
            for (int g = 0; g < n; g++) fdOf[g] = d[o + 1 + g];
        }
        else if (format is 3 or 4)
        {
            bool wide = format == 4;
            int ranges = wide ? (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o + 1)) : d[o + 1] << 8 | d[o + 2];
            int r = o + (wide ? 5 : 3), size = wide ? 6 : 3;
            for (int i = 0; i < ranges; i++)
            {
                int first = wide ? (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(r + i * size)) : d[r + i * size] << 8 | d[r + i * size + 1];
                int fd = wide ? d[r + i * size + 4] << 8 | d[r + i * size + 5] : d[r + i * size + 2];
                int next = wide ? (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(r + (i + 1) * size)) : d[r + (i + 1) * size] << 8 | d[r + (i + 1) * size + 1];
                for (int g = Math.Max(0, first); g < Math.Min(n, next); g++) fdOf[g] = (byte)Math.Min(fd, 255);
            }
        }
        else throw new FormatException("Unknown FDSelect format.");
    }

    // ---- Running charstrings ----

    /// <summary>Runs CFF2 charstrings into an outline in absolute coordinates, then writes it as a CFF charstring.</summary>
    private sealed class Charstring
    {
        private readonly byte[] _d;
        private readonly CffIndex _gsubrs;
        private readonly VariationStore? _store;
        private readonly Dictionary<int, float[]> _scalars = new();
        private readonly Type2Stack _stack = new(513); // the CFF2 limit
        private readonly List<(byte Op, double[] P)> _path = new(); // 'M', 'L' or 'C' with absolute points
        private CffIndex _subrs;
        private float[] _blend = Array.Empty<float>();
        private double _x, _y;
        private int _stems, _depth;

        // A font past the budget is treated as damaged and left as it is.
        private CharstringBudget _budget = new(100_000_000);

        public Charstring(byte[] d, CffIndex gsubrs, VariationStore? store)
        {
            _d = d;
            _gsubrs = gsubrs;
            _store = store;
        }

        private float[] Scalars(int vsindex)
        {
            if (_store == null) return Array.Empty<float>();
            if (!_scalars.TryGetValue(vsindex, out var s)) _scalars[vsindex] = s = _store.RegionScalars(vsindex);
            return s;
        }

        public void Run(int start, int end, CffIndex subrs, int vsindex)
        {
            _subrs = subrs;
            _blend = Scalars(vsindex);
            _stack.Clear();
            _path.Clear();
            _x = _y = 0;
            _stems = 0;
            _depth = 0;
            _budget.StartGlyph();
            Execute(start, end);
        }

        private void Execute(int pos, int end)
        {
            // The glyph's own charstring counts as the first level here.
            if (++_depth > Type2.MaxSubrDepth) throw new FormatException("CFF2 subroutines nest too deep.");
            var s = _stack;
            ReadOnlySpan<byte> d = _d;
            while (pos < end)
            {
                if (!_budget.Spend()) throw new FormatException("CFF2 charstrings call too many subroutines.");
                int b = d[pos++];
                if (Type2.IsNumber(b))
                {
                    if (!s.PushNumber(d, b, ref pos)) throw new FormatException("CFF2 stack overflow.");
                    continue;
                }
                switch (b)
                {
                    case 1: case 3: case 18: case 23: // stems
                        _stems += s.Count / 2;
                        s.Clear();
                        break;
                    case 19: case 20: // hintmask, cntrmask: arguments left on the stack are vertical stems
                        _stems += s.Count / 2;
                        s.Clear();
                        pos += Type2.MaskBytes(_stems);
                        break;
                    case 21: Move(s[^2], s[^1]); s.Clear(); break;
                    case 22: Move(s[^1], 0); s.Clear(); break;
                    case 4: Move(0, s[^1]); s.Clear(); break;
                    case 5:
                        for (int i = 0; i + 1 < s.Count; i += 2) Line(s[i], s[i + 1]);
                        s.Clear();
                        break;
                    case 6: case 7:
                    {
                        bool horizontal = b == 6;
                        for (int i = 0; i < s.Count; i++)
                        {
                            if (horizontal) Line(s[i], 0); else Line(0, s[i]);
                            horizontal = !horizontal;
                        }
                        s.Clear();
                        break;
                    }
                    case 8:
                        for (int i = 0; i + 5 < s.Count; i += 6) Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                        s.Clear();
                        break;
                    case 24: // rcurveline
                    {
                        int i = 0;
                        for (; i + 5 < s.Count - 2; i += 6) Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                        if (i + 1 < s.Count) Line(s[i], s[i + 1]);
                        s.Clear();
                        break;
                    }
                    case 25: // rlinecurve
                    {
                        int i = 0;
                        for (; i + 1 < s.Count - 6; i += 2) Line(s[i], s[i + 1]);
                        if (i + 5 < s.Count) Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                        s.Clear();
                        break;
                    }
                    case 26: // vvcurveto
                    {
                        int i = 0;
                        double dx1 = 0;
                        if (s.Count % 4 == 1) dx1 = s[i++];
                        for (; i + 3 < s.Count; i += 4) { Curve(dx1, s[i], s[i + 1], s[i + 2], 0, s[i + 3]); dx1 = 0; }
                        s.Clear();
                        break;
                    }
                    case 27: // hhcurveto
                    {
                        int i = 0;
                        double dy1 = 0;
                        if (s.Count % 4 == 1) dy1 = s[i++];
                        for (; i + 3 < s.Count; i += 4) { Curve(s[i], dy1, s[i + 1], s[i + 2], s[i + 3], 0); dy1 = 0; }
                        s.Clear();
                        break;
                    }
                    case 30: case 31: // vhcurveto, hvcurveto
                    {
                        bool horizontal = b == 31;
                        for (int i = 0; i + 3 < s.Count; i += 4)
                        {
                            double last = s.Count - i == 5 ? s[i + 4] : 0;
                            if (horizontal) Curve(s[i], 0, s[i + 1], s[i + 2], last, s[i + 3]);
                            else Curve(0, s[i], s[i + 1], s[i + 2], s[i + 3], last);
                            horizontal = !horizontal;
                        }
                        s.Clear();
                        break;
                    }
                    case 10: case 29: // callsubr, callgsubr
                    {
                        var index = b == 10 ? _subrs : _gsubrs;
                        int i = (int)s.Pop() + Type2.SubrBias(index.Count);
                        if ((uint)i >= (uint)index.Count) throw new FormatException("Bad CFF2 subroutine.");
                        var (ss, se) = index.Range(i);
                        Execute(ss, se);
                        break;
                    }
                    case 11: _depth--; return; // return (not in CFF2, but harmless)
                    case 14: s.Clear(); pos = end; break; // endchar (not in CFF2)
                    case 15: // vsindex
                        _blend = Scalars((int)s[^1]);
                        s.Clear();
                        break;
                    case 16: // blend
                    {
                        int count = (int)s.Pop();
                        int k = _blend.Length, first = s.Count - count * (k + 1);
                        if (count < 0 || first < 0) throw new FormatException("Bad CFF2 blend.");
                        for (int i = 0; i < count; i++)
                            for (int j = 0; j < k; j++)
                                s[first + i] += s[first + count + i * k + j] * _blend[j];
                        s.Count = first + count;
                        break;
                    }
                    case 12:
                    {
                        int op = d[pos++];
                        switch (op)
                        {
                            case 35 when s.Count >= 12: // flex
                                Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                                Curve(s[6], s[7], s[8], s[9], s[10], s[11]);
                                break;
                            case 34 when s.Count >= 7: // hflex
                                Curve(s[0], 0, s[1], s[2], s[3], 0);
                                Curve(s[4], 0, s[5], -s[2], s[6], 0);
                                break;
                            case 36 when s.Count >= 9: // hflex1
                                Curve(s[0], s[1], s[2], s[3], s[4], 0);
                                Curve(s[5], 0, s[6], s[7], s[8], -(s[1] + s[3] + s[7]));
                                break;
                            case 37 when s.Count >= 11: // flex1
                            {
                                double dx = s[0] + s[2] + s[4] + s[6] + s[8], dy = s[1] + s[3] + s[5] + s[7] + s[9];
                                Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                                if (Math.Abs(dx) > Math.Abs(dy)) Curve(s[6], s[7], s[8], s[9], s[10], -dy);
                                else Curve(s[6], s[7], s[8], s[9], -dx, s[10]);
                                break;
                            }
                        }
                        s.Clear();
                        break;
                    }
                    default:
                        s.Clear();
                        break;
                }
            }
            _depth--;
        }

        private void Move(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _path.Add(((byte)'M', new[] { _x, _y }));
        }

        private void Line(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _path.Add(((byte)'L', new[] { _x, _y }));
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            _path.Add(((byte)'C', new[] { x1, y1, x2, y2, _x, _y }));
        }

        /// <summary>The outline as a Type 2 charstring with the width first, and its bounding box.</summary>
        public byte[] Encode(int width, out (short, short, short, short)? box)
        {
            var o = new List<byte>();
            if (width != 0) Number(o, width);
            int cx = 0, cy = 0;
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            bool started = false;
            foreach (var (op, pts) in _path)
            {
                if (!started && op != 'M') { Number(o, 0); Number(o, 0); o.Add(21); } // a path must start with a moveto
                started = true;
                for (int i = 0; i < pts.Length; i += 2)
                {
                    // Points are rounded where they are, so the rounding does not add up along the path.
                    int x = (int)Math.Round(pts[i]), y = (int)Math.Round(pts[i + 1]);
                    Number(o, x - cx);
                    Number(o, y - cy);
                    cx = x;
                    cy = y;
                    if (op != 'M')
                    {
                        xMin = Math.Min(xMin, x); yMin = Math.Min(yMin, y);
                        xMax = Math.Max(xMax, x); yMax = Math.Max(yMax, y);
                    }
                }
                o.Add(op switch { (byte)'M' => 21, (byte)'L' => 5, _ => 8 });
            }
            o.Add(14);
            box = xMin <= xMax ? ((short)xMin, (short)yMin, (short)xMax, (short)yMax) : null;
            return o.ToArray();
        }

        private static void Number(List<byte> o, int v)
        {
            if (v >= -107 && v <= 107) o.Add((byte)(v + 139));
            else if (v >= 108 && v <= 1131) { v -= 108; o.Add((byte)(247 + (v >> 8))); o.Add((byte)v); }
            else if (v >= -1131 && v <= -108) { v = -v - 108; o.Add((byte)(251 + (v >> 8))); o.Add((byte)v); }
            else { v = Math.Clamp(v, short.MinValue, short.MaxValue); o.Add(28); o.Add((byte)(v >> 8)); o.Add((byte)v); }
        }
    }

    // ---- Writing CFF ----

    /// <summary>A CID-keyed CFF font (Adobe-Identity-0, CID = glyph id) with one Font DICT and no subroutines.</summary>
    private static byte[] WriteCff(string name, byte[][] glyphs, double[]? matrix)
    {
        int n = glyphs.Length;
        var header = new byte[] { 1, 0, 4, 4 };
        var nameIndex = CffIndex.Write(new[] { System.Text.Encoding.ASCII.GetBytes(name) });
        var strings = CffIndex.Write(new[] { "Adobe"u8.ToArray(), "Identity"u8.ToArray() });
        var gsubrs = CffIndex.Write(Array.Empty<byte[]>());
        var charset = n > 1 ? new byte[] { 2, 0, 1, (byte)((n - 2) >> 8), (byte)(n - 2) } : new byte[] { 0 };
        var fdSelect = new byte[] { 3, 0, 1, 0, 0, 0, (byte)(n >> 8), (byte)n };
        var charStrings = CffIndex.Write(glyphs);
        var privateDict = new byte[] { 139, 20, 139, 21 }; // defaultWidthX 0, nominalWidthX 0

        byte[] TopDict(int charsetOff, int fdSelectOff, int charStringsOff, int fdArrayOff)
        {
            var t = new List<byte>();
            Int(t, 391); Int(t, 392); Int(t, 0); t.Add(12); t.Add(30);     // ROS Adobe-Identity-0
            Int(t, n); t.Add(12); t.Add(34);                                 // CIDCount
            if (matrix != null)
            {
                foreach (var v in matrix) Real(t, v);
                t.Add(12); t.Add(7);
            }
            CffDict.WriteInt32(t, charsetOff); t.Add(15);
            CffDict.WriteInt32(t, fdSelectOff); t.Add(12); t.Add(37);
            CffDict.WriteInt32(t, charStringsOff); t.Add(17);
            CffDict.WriteInt32(t, fdArrayOff); t.Add(12); t.Add(36);
            return t.ToArray();
        }
        byte[] FontDict(int privateOff)
        {
            var t = new List<byte>();
            CffDict.WriteInt32(t, privateDict.Length); CffDict.WriteInt32(t, privateOff); t.Add(18);
            return t.ToArray();
        }

        // Offsets are written as 5-byte numbers, so the DICTs have the same size before and after they are known.
        int topSize = CffIndex.Write(new[] { TopDict(0, 0, 0, 0) }).Length;
        int fdArraySize = CffIndex.Write(new[] { FontDict(0) }).Length;
        int charsetOff = header.Length + nameIndex.Length + topSize + strings.Length + gsubrs.Length;
        int fdSelectOff = charsetOff + charset.Length;
        int charStringsOff = fdSelectOff + fdSelect.Length;
        int fdArrayOff = charStringsOff + charStrings.Length;
        int privateOff = fdArrayOff + fdArraySize;

        var o = new MemoryStream();
        o.Write(header);
        o.Write(nameIndex);
        o.Write(CffIndex.Write(new[] { TopDict(charsetOff, fdSelectOff, charStringsOff, fdArrayOff) }));
        o.Write(strings);
        o.Write(gsubrs);
        o.Write(charset);
        o.Write(fdSelect);
        o.Write(charStrings);
        o.Write(CffIndex.Write(new[] { FontDict(privateOff) }));
        o.Write(privateDict);
        return o.ToArray();
    }

    private static void Int(List<byte> o, int v)
    {
        if (v >= -107 && v <= 107) o.Add((byte)(v + 139));
        else CffDict.WriteInt32(o, v);
    }

    private static void Real(List<byte> o, double v)
    {
        o.Add(30);
        var s = v.ToString("R", CultureInfo.InvariantCulture).Replace("E-", "c").Replace("E+", "b").Replace("E", "b");
        var nibbles = new List<int>();
        foreach (char c in s)
            nibbles.Add(c switch { '.' => 0xA, 'b' => 0xB, 'c' => 0xC, '-' => 0xE, _ => c - '0' });
        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
        for (int i = 0; i < nibbles.Count; i += 2) o.Add((byte)(nibbles[i] << 4 | nibbles[i + 1]));
    }
}
