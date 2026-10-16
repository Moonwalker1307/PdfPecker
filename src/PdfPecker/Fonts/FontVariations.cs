using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>A design axis of a variable font (fvar): weight, width, slant, optical size or a custom one.</summary>
internal readonly record struct VariationAxis(uint Tag, float Min, float Default, float Max)
{
    public string Name => new(new[] { (char)(Tag >> 24), (char)(Tag >> 16 & 0xFF), (char)(Tag >> 8 & 0xFF), (char)(Tag & 0xFF) });
}

/// <summary>
/// Makes static instances of variable fonts: the outlines, advance widths and kerning at one point of the design
/// space, as a plain font the rest of the library embeds and measures like any other. PDF has no variable fonts, so
/// this is what a browser's print output does too. TrueType outlines get their gvar deltas; CFF2 outlines become a
/// static CFF table (see <see cref="Cff2Instancer"/>).
/// </summary>
internal static class FontVariations
{
    public const uint Wght = 0x77676874, Wdth = 0x77647468, Ital = 0x6974616C, Slnt = 0x736C6E74, Opsz = 0x6F70737A;

    // Tables that describe variations or hinting of the default instance: an instance has no use for them.
    private static readonly HashSet<uint> Dropped = new[]
    {
        "fvar", "gvar", "avar", "CFF2", "HVAR", "VVAR", "MVAR", "STAT", "cvar", "fpgm", "prep", "cvt ", "hdmx", "LTSH", "VDMX", "DSIG", "gasp",
    }.Select(FontProgram.Tag).ToHashSet();

    /// <summary>The axes of a variable font, or an empty array.</summary>
    public static VariationAxis[] ReadAxes(FontProgram p)
    {
        if (p.Table("fvar") is not { } fvar || fvar.Length < 16) return Array.Empty<VariationAxis>();
        int axesOffset = p.U16(fvar.Offset + 4), count = p.U16(fvar.Offset + 8), size = p.U16(fvar.Offset + 10);
        if (size < 20 || axesOffset + (long)count * size > fvar.Length) return Array.Empty<VariationAxis>();
        var axes = new VariationAxis[count];
        for (int i = 0; i < count; i++)
        {
            int o = fvar.Offset + axesOffset + i * size;
            float min = Fixed(p, o + 4), def = Fixed(p, o + 8), max = Fixed(p, o + 12);
            if (min > def) min = def;
            if (max < def) max = def;
            axes[i] = new VariationAxis(p.U32(o), min, def, max);
        }
        return axes;
    }

    private static float Fixed(FontProgram p, int o) => (int)p.U32(o) / 65536f;

    /// <summary>
    /// The normalized coordinates (-1 to 1, after avar) of a point in the design space, rounded to F2Dot14 as the
    /// spec requires. Axes not given keep their default.
    /// </summary>
    public static float[] Normalize(FontProgram p, VariationAxis[] axes, IReadOnlyDictionary<uint, float> values)
    {
        var coords = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            var a = axes[i];
            float v = values.TryGetValue(a.Tag, out var given) ? Math.Clamp(given, a.Min, a.Max) : a.Default;
            float n = v < a.Default ? (a.Default == a.Min ? 0 : -(a.Default - v) / (a.Default - a.Min))
                : v > a.Default ? (a.Max == a.Default ? 0 : (v - a.Default) / (a.Max - a.Default)) : 0;
            coords[i] = F2Dot14(Math.Clamp(n, -1, 1));
        }
        ApplyAvar(p, coords);
        return coords;
    }

    private static float F2Dot14(float v) => MathF.Round(v * 16384) / 16384;

    private static void ApplyAvar(FontProgram p, float[] coords)
    {
        if (p.Table("avar") is not { } avar || avar.Length < 8) return;
        int end = avar.Offset + avar.Length;
        int o = avar.Offset + 8;
        int axisCount = p.U16(avar.Offset + 6);
        for (int i = 0; i < axisCount && i < coords.Length; i++)
        {
            if (o + 2 > end) return;
            int maps = p.U16(o);
            o += 2;
            if (o + maps * 4 > end) return;
            float v = coords[i];
            float prevFrom = -1, prevTo = -1;
            for (int k = 0; k < maps; k++)
            {
                float from = p.S16(o + k * 4) / 16384f, to = p.S16(o + k * 4 + 2) / 16384f;
                if (v == from) { coords[i] = to; break; }
                if (v < from)
                {
                    if (k > 0 && from > prevFrom) coords[i] = F2Dot14(prevTo + (to - prevTo) * (v - prevFrom) / (from - prevFrom));
                    break;
                }
                prevFrom = from;
                prevTo = to;
            }
            o += maps * 4;
        }
    }

    /// <summary>True when the font's outlines can be instanced: glyf outlines with gvar variations, or CFF2 outlines.</summary>
    public static bool CanInstance(FontProgram p)
        => p.Table("fvar") != null
            && (p.Table("CFF2") != null || !p.IsCff && p.Table("gvar") != null && p.Table("glyf") != null && p.Table("loca") != null);

    /// <summary>A static font at the normalized coordinates, as sfnt bytes.</summary>
    public static byte[] Instance(FontProgram p, float[] coords, IReadOnlyDictionary<uint, float> userValues, VariationAxis[] axes)
    {
        int n = p.NumGlyphs;
        int[] advances;
        short[] lsbs;
        (short XMin, short YMin, short XMax, short YMax)?[] bboxes;
        var replaced = new Dictionary<uint, byte[]>();
        uint sfntVersion = 0x00010000;
        if (p.Table("CFF2") != null)
        {
            var cff = Cff2Instancer.Instance(p, coords, p.PostScriptName);
            (advances, lsbs, bboxes) = (cff.Advances, cff.Lsbs, cff.Boxes);
            replaced[0x43464620] = cff.Cff; // 'CFF '
            sfntVersion = 0x4F54544F;
        }
        else
        {
            var head = p.Table("head")!.Value;
            bool longLoca = p.S16(head.Offset + 50) != 0;
            var loca = p.Table("loca")!.Value;
            var glyf = p.Table("glyf")!.Value;
            if (loca.Length < (n + 1) * (longLoca ? 4 : 2)) throw new FormatException("The font's loca table is too short.");
            int GlyphStart(int g) => longLoca ? (int)p.U32(loca.Offset + g * 4) : p.U16(loca.Offset + g * 2) * 2;

            var gvar = new Gvar(p, coords);
            var glyphs = new byte[n][];
            advances = new int[n];
            lsbs = new short[n];
            bboxes = new (short XMin, short YMin, short XMax, short YMax)?[n];
            for (int g = 0; g < n; g++)
            {
                int start = GlyphStart(g), end = GlyphStart(g + 1);
                if (start < 0 || start > glyf.Length) start = 0;
                if (end < start || end > glyf.Length) end = start;
                int advance = p.Advance(g);
                short lsb = p.LeftSideBearing(g);
                if (end - start < 10)
                {
                    // An empty glyph (a space): only its advance can vary.
                    var phantom = new float[8] { -lsb, 0, advance - lsb, 0, 0, 0, 0, 0 };
                    gvar.Apply(g, null, null, phantom, 0);
                    glyphs[g] = Array.Empty<byte>();
                    advances[g] = Math.Max(0, (int)MathF.Round(phantom[2] - phantom[0]));
                    lsbs[g] = 0;
                    continue;
                }
                var (data, adv, l, box) = InstanceGlyph(p, glyf.Offset + start, end - start, g, advance, lsb, gvar);
                glyphs[g] = data;
                advances[g] = adv;
                lsbs[g] = l;
                bboxes[g] = box;
            }

            // Composite glyphs got the bounding boxes of their components only roughly: the boxes matter little in a PDF.
            var glyfOut = new MemoryStream();
            var locaOut = new byte[(n + 1) * 4];
            for (int g = 0; g < n; g++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(g * 4), (uint)glyfOut.Length);
                glyfOut.Write(glyphs[g]);
                while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(n * 4), (uint)glyfOut.Length);
            replaced[0x676C7966] = glyfOut.ToArray(); // glyf
            replaced[0x6C6F6361] = locaOut;           // loca
        }

        var tables = new List<(uint Tag, byte[] Data)>();
        var gdefStore = VariationStore.FromGdef(p, coords);

        var hmtx = new byte[n * 4];
        int maxAdvance = 0;
        short minLsb = short.MaxValue, xMinAll = short.MaxValue, yMinAll = short.MaxValue, xMaxAll = short.MinValue, yMaxAll = short.MinValue;
        for (int g = 0; g < n; g++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), (ushort)Math.Clamp(advances[g], 0, 65535));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(g * 4 + 2), lsbs[g]);
            maxAdvance = Math.Max(maxAdvance, advances[g]);
            if (bboxes[g] is { } b)
            {
                minLsb = Math.Min(minLsb, lsbs[g]);
                xMinAll = Math.Min(xMinAll, b.XMin);
                yMinAll = Math.Min(yMinAll, b.YMin);
                xMaxAll = Math.Max(xMaxAll, b.XMax);
                yMaxAll = Math.Max(yMaxAll, b.YMax);
            }
        }

        foreach (var (tag, offset, length) in p.Tables)
        {
            if (Dropped.Contains(tag)) continue;
            var data = p.Data.AsSpan(offset, length).ToArray();
            if (replaced.Remove(tag, out var instanced)) data = instanced;
            switch (tag)
            {
                case 0x686D7478: data = hmtx; break;               // hmtx
                case 0x47504F53 when gdefStore != null:            // GPOS: kerning and anchors of the instance
                    data = GposVariations.Instance(data, gdefStore);
                    break;
                case 0x68656164 when data.Length >= 54:            // head: long loca offsets, new bounding box
                    if (sfntVersion != 0x4F54544F) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(50), 1);
                    if (xMinAll <= xMaxAll)
                    {
                        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(36), xMinAll);
                        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(38), yMinAll);
                        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(40), xMaxAll);
                        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(42), yMaxAll);
                    }
                    break;
                case 0x68686561 when data.Length >= 36:            // hhea: every glyph has its own metric
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), (ushort)Math.Clamp(maxAdvance, 0, 65535));
                    if (minLsb != short.MaxValue) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(12), minLsb);
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(34), (ushort)n);
                    break;
                case 0x4F532F32 when data.Length >= 6:             // OS/2: the instance's weight and width classes
                    if (userValues.TryGetValue(Wght, out float w) && axes.Any(a => a.Tag == Wght))
                        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)Math.Clamp((int)MathF.Round(w), 1, 1000));
                    if (userValues.TryGetValue(Wdth, out float wd) && axes.Any(a => a.Tag == Wdth))
                        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), (ushort)WidthClass(wd));
                    break;
            }
            tables.Add((tag, data));
        }
        foreach (var (tag, data) in replaced) tables.Add((tag, data));
        return SfntBuilder.Build(sfntVersion, tables);
    }

    private static int WidthClass(float percent) => percent switch
    {
        <= 56.25f => 1, <= 68.75f => 2, <= 81.25f => 3, <= 93.75f => 4, <= 106.25f => 5, <= 118.75f => 6, <= 137.5f => 7, <= 175f => 8, _ => 9,
    };

    /// <summary>One glyph at the instance: its new glyf data, advance, left side bearing and bounding box.</summary>
    private static (byte[] Data, int Advance, short Lsb, (short, short, short, short) Box) InstanceGlyph(
        FontProgram p, int o, int length, int gid, int advance, short lsb, Gvar gvar)
    {
        var d = p.Data;
        int contours = p.S16(o);
        short xMin = p.S16(o + 2), yMin = p.S16(o + 4), xMax = p.S16(o + 6), yMax = p.S16(o + 8);
        float originX = xMin - lsb;
        if (contours >= 0)
        {
            // Simple glyph: decode the points, add the deltas, encode again without instructions.
            int pos = o + 10;
            var endPts = new int[contours];
            for (int c = 0; c < contours; c++) endPts[c] = p.U16(pos + c * 2);
            pos += contours * 2;
            int points = contours == 0 ? 0 : endPts[^1] + 1;
            int instrLen = p.U16(pos);
            pos += 2 + instrLen;
            var flags = new byte[points];
            for (int i = 0; i < points;)
            {
                byte f = d[pos++];
                flags[i++] = f;
                if ((f & 8) != 0)
                {
                    int repeat = d[pos++];
                    for (int r = 0; r < repeat && i < points; r++) flags[i++] = f;
                }
            }
            var xy = new float[(points + 4) * 2];
            int v = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0) { int dx = d[pos++]; v += (f & 16) != 0 ? dx : -dx; }
                else if ((f & 16) == 0) { v += (short)(d[pos] << 8 | d[pos + 1]); pos += 2; }
                xy[i * 2] = v;
            }
            v = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0) { int dy = d[pos++]; v += (f & 32) != 0 ? dy : -dy; }
                else if ((f & 32) == 0) { v += (short)(d[pos] << 8 | d[pos + 1]); pos += 2; }
                xy[i * 2 + 1] = v;
            }
            int ph = points * 2;
            xy[ph] = originX; xy[ph + 2] = originX + advance;
            gvar.Apply(gid, endPts, flags, xy, points);

            var ix = new int[points];
            var iy = new int[points];
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
            for (int i = 0; i < points; i++)
            {
                ix[i] = (int)MathF.Round(xy[i * 2]);
                iy[i] = (int)MathF.Round(xy[i * 2 + 1]);
                bx0 = Math.Min(bx0, ix[i]); bx1 = Math.Max(bx1, ix[i]);
                by0 = Math.Min(by0, iy[i]); by1 = Math.Max(by1, iy[i]);
            }
            if (points == 0) bx0 = by0 = bx1 = by1 = 0;
            int newOrigin = (int)MathF.Round(xy[ph]);
            int newAdvance = Math.Max(0, (int)MathF.Round(xy[ph + 2]) - newOrigin);
            var box = ((short)bx0, (short)by0, (short)bx1, (short)by1);

            var outBuf = new MemoryStream(length + points * 2);
            void W16(int value) { outBuf.WriteByte((byte)(value >> 8)); outBuf.WriteByte((byte)value); }
            W16(contours); W16(bx0); W16(by0); W16(bx1); W16(by1);
            foreach (var e in endPts) W16(e);
            W16(0); // no instructions
            var xs = new MemoryStream();
            var ys = new MemoryStream();
            int px = 0, py = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = (byte)(flags[i] & 1 | (i == 0 ? flags[0] & 0x40 : 0));
                int dx = ix[i] - px, dy = iy[i] - py;
                px = ix[i]; py = iy[i];
                if (dx == 0) f |= 16;
                else if (dx > -256 && dx < 256) { f |= (byte)(2 | (dx > 0 ? 16 : 0)); xs.WriteByte((byte)Math.Abs(dx)); }
                else { xs.WriteByte((byte)(dx >> 8)); xs.WriteByte((byte)dx); }
                if (dy == 0) f |= 32;
                else if (dy > -256 && dy < 256) { f |= (byte)(4 | (dy > 0 ? 32 : 0)); ys.WriteByte((byte)Math.Abs(dy)); }
                else { ys.WriteByte((byte)(dy >> 8)); ys.WriteByte((byte)dy); }
                outBuf.WriteByte(f);
            }
            xs.WriteTo(outBuf);
            ys.WriteTo(outBuf);
            return (outBuf.ToArray(), newAdvance, (short)Math.Clamp(bx0 - newOrigin, short.MinValue, short.MaxValue), box);
        }
        else
        {
            // Composite glyph: its points are the component offsets, which the deltas move.
            var comps = new List<(int Flags, int Glyph, int Arg1, int Arg2, byte[] Transform)>();
            int pos = o + 10;
            while (true)
            {
                int flags = p.U16(pos), glyph = p.U16(pos + 2);
                pos += 4;
                int a1, a2;
                bool xyValues = (flags & 2) != 0;
                if ((flags & 1) != 0)
                {
                    a1 = xyValues ? p.S16(pos) : p.U16(pos);
                    a2 = xyValues ? p.S16(pos + 2) : p.U16(pos + 2);
                    pos += 4;
                }
                else
                {
                    a1 = xyValues ? (sbyte)d[pos] : d[pos];
                    a2 = xyValues ? (sbyte)d[pos + 1] : d[pos + 1];
                    pos += 2;
                }
                int tLen = (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                comps.Add((flags, glyph, a1, a2, d.AsSpan(pos, tLen).ToArray()));
                pos += tLen;
                if ((flags & 0x20) == 0) break;
            }
            int points = comps.Count;
            var xy = new float[(points + 4) * 2];
            for (int i = 0; i < points; i++)
            {
                xy[i * 2] = comps[i].Arg1;
                xy[i * 2 + 1] = comps[i].Arg2;
            }
            int ph = points * 2;
            xy[ph] = originX; xy[ph + 2] = originX + advance;
            gvar.Apply(gid, null, null, xy, points);
            int newOrigin = (int)MathF.Round(xy[ph]);
            int newAdvance = Math.Max(0, (int)MathF.Round(xy[ph + 2]) - newOrigin);

            var outBuf = new MemoryStream(length + points * 2);
            void W16(int value) { outBuf.WriteByte((byte)(value >> 8)); outBuf.WriteByte((byte)value); }
            W16(contours); W16(xMin); W16(yMin); W16(xMax); W16(yMax);
            for (int i = 0; i < points; i++)
            {
                var c = comps[i];
                bool xyValues = (c.Flags & 2) != 0;
                // Arguments as words, no instructions.
                int flags = (c.Flags | 1) & ~0x100;
                if (i < points - 1) flags |= 0x20; else flags &= ~0x20;
                W16(flags); W16(c.Glyph);
                W16(xyValues ? (int)MathF.Round(xy[i * 2]) : c.Arg1);
                W16(xyValues ? (int)MathF.Round(xy[i * 2 + 1]) : c.Arg2);
                outBuf.Write(c.Transform);
            }
            return (outBuf.ToArray(), newAdvance, (short)Math.Clamp(xMin - newOrigin, short.MinValue, short.MaxValue), (xMin, yMin, xMax, yMax));
        }
    }

    /// <summary>The gvar table: per glyph, the tuple variations whose deltas move its points.</summary>
    private sealed class Gvar
    {
        private readonly FontProgram _p;
        private readonly float[] _coords;
        private readonly int _start, _end, _axisCount, _sharedTuples, _sharedCount, _glyphCount, _dataStart;
        private readonly bool _longOffsets;

        public Gvar(FontProgram p, float[] coords)
        {
            _p = p;
            _coords = coords;
            var t = p.Table("gvar")!.Value;
            _start = t.Offset;
            _end = t.Offset + t.Length;
            _axisCount = p.U16(_start + 4);
            _sharedCount = p.U16(_start + 6);
            _sharedTuples = _start + (int)p.U32(_start + 8);
            _glyphCount = p.U16(_start + 12);
            _longOffsets = (p.U16(_start + 14) & 1) != 0;
            long dataStart = p.U32(_start + 16);
            if (_axisCount == 0 || dataStart > t.Length || 8 + (long)_sharedCount * _axisCount * 2 > t.Length) throw new FormatException("Bad gvar header.");
            _dataStart = _start + (int)dataStart;
        }

        private int Offset(int g)
            => _longOffsets ? (int)_p.U32(_start + 20 + g * 4) : _p.U16(_start + 20 + g * 2) * 2;

        /// <summary>How much a tuple applies at the instance's coordinates: 0 to 1.</summary>
        private float Scalar(int peak, int start, int end)
        {
            float s = 1;
            for (int a = 0; a < _axisCount; a++)
            {
                float pk = _p.S16(peak + a * 2) / 16384f;
                if (pk == 0) continue;
                float c = a < _coords.Length ? _coords[a] : 0;
                if (c == pk) continue;
                if (c == 0) return 0;
                if (start >= 0)
                {
                    float st = _p.S16(start + a * 2) / 16384f, en = _p.S16(end + a * 2) / 16384f;
                    if (st > pk || pk > en || st < 0 && en > 0) continue; // an invalid region does not restrict the axis
                    if (c < st || c > en) return 0;
                    s *= c < pk ? (c - st) / (pk - st) : (en - c) / (en - pk);
                }
                else
                {
                    if (c < Math.Min(0, pk) || c > Math.Max(0, pk)) return 0;
                    s *= c / pk;
                }
            }
            return s;
        }

        /// <summary>
        /// Adds the glyph's deltas to its points (x, y pairs, then the four phantom points). Points a tuple does not
        /// list are inferred from their neighbours on the contour (IUP) for simple glyphs.
        /// </summary>
        public void Apply(int gid, int[]? endPts, byte[]? flags, float[] xy, int points)
        {
            if (gid >= _glyphCount) return;
            int dataOff = Offset(gid), dataEnd = Offset(gid + 1);
            if (dataEnd <= dataOff) return;
            long o64 = (long)_dataStart + dataOff;
            if (dataOff < 0 || o64 + 4 > _end) throw new FormatException("Glyph variation data outside gvar.");
            int o = (int)o64;
            int total = points + 4;
            int tupleCount = _p.U16(o) & 0x0FFF;
            bool sharedPoints = (_p.U16(o) & 0x8000) != 0;
            int serial = o + _p.U16(o + 2);
            int header = o + 4;
            int[]? shared = null;
            if (sharedPoints) shared = ReadPoints(ref serial, total);
            var orig = (float[])xy.Clone();
            for (int t = 0; t < tupleCount; t++)
            {
                int size = _p.U16(header), index = _p.U16(header + 2);
                header += 4;
                int peak;
                if ((index & 0x8000) != 0) { peak = header; header += _axisCount * 2; }
                else
                {
                    int k = index & 0x0FFF;
                    if (k >= _sharedCount) { serial += size; continue; }
                    peak = _sharedTuples + k * _axisCount * 2;
                }
                int st = -1, en = -1;
                if ((index & 0x4000) != 0) { st = header; en = header + _axisCount * 2; header += _axisCount * 4; }
                int next = serial + size;
                float scalar = Scalar(peak, st, en);
                if (scalar != 0)
                {
                    int pos = serial;
                    int[]? pts = (index & 0x2000) != 0 ? ReadPoints(ref pos, total) : shared;
                    int count = pts?.Length ?? total;
                    var dx = ReadDeltas(ref pos, count);
                    var dy = ReadDeltas(ref pos, count);
                    if (pts == null)
                    {
                        for (int i = 0; i < total && i < count; i++) { xy[i * 2] += dx[i] * scalar; xy[i * 2 + 1] += dy[i] * scalar; }
                    }
                    else
                    {
                        var deltas = new float[total * 2];
                        var touched = new bool[total];
                        for (int i = 0; i < pts.Length; i++)
                        {
                            int pt = pts[i];
                            if ((uint)pt >= (uint)total) continue;
                            deltas[pt * 2] += dx[i];
                            deltas[pt * 2 + 1] += dy[i];
                            touched[pt] = true;
                        }
                        if (endPts != null) Interpolate(endPts, orig, deltas, touched);
                        for (int i = 0; i < total; i++) { xy[i * 2] += deltas[i * 2] * scalar; xy[i * 2 + 1] += deltas[i * 2 + 1] * scalar; }
                    }
                }
                serial = next;
            }
        }

        /// <summary>Infers the deltas of untouched points of each contour from the touched points around them.</summary>
        private static void Interpolate(int[] endPts, float[] orig, float[] deltas, bool[] touched)
        {
            int first = 0;
            foreach (int last in endPts)
            {
                if (last < first || last * 2 + 1 >= orig.Length) break;
                int firstTouched = -1;
                for (int i = first; i <= last; i++) if (touched[i]) { firstTouched = i; break; }
                if (firstTouched >= 0)
                {
                    int prev = firstTouched;
                    int i = firstTouched;
                    do
                    {
                        i = i == last ? first : i + 1;
                        if (touched[i] || i == firstTouched)
                        {
                            // Fill the points strictly between prev and i.
                            for (int j = prev == last ? first : prev + 1; j != i; j = j == last ? first : j + 1)
                                for (int axis = 0; axis < 2; axis++)
                                    deltas[j * 2 + axis] = Infer(orig[j * 2 + axis], orig[prev * 2 + axis], orig[i * 2 + axis],
                                        deltas[prev * 2 + axis], deltas[i * 2 + axis]);
                            prev = i;
                        }
                    } while (i != firstTouched);
                }
                first = last + 1;
            }
        }

        private static float Infer(float v, float a, float b, float da, float db)
        {
            if (a == b) return da == db ? da : 0;
            if (a > b) { (a, b) = (b, a); (da, db) = (db, da); }
            if (v <= a) return da;
            if (v >= b) return db;
            return da + (v - a) * (db - da) / (b - a);
        }

        private int[]? ReadPoints(ref int o, int total)
        {
            if (o >= _end) return Array.Empty<int>();
            int count = _p.Data[o++];
            if (count == 0) return null; // all points
            if ((count & 0x80) != 0) count = (count & 0x7F) << 8 | _p.Data[o++];
            var pts = new int[count];
            int i = 0, v = 0;
            while (i < count && o < _end)
            {
                int run = _p.Data[o++];
                bool words = (run & 0x80) != 0;
                int n = (run & 0x7F) + 1;
                for (int k = 0; k < n && i < count; k++)
                {
                    if (words) { v += _p.U16(o); o += 2; }
                    else v += _p.Data[o++];
                    pts[i++] = v;
                }
            }
            return pts;
        }

        private float[] ReadDeltas(ref int o, int count)
        {
            var deltas = new float[count];
            int i = 0;
            while (i < count && o < _end)
            {
                int run = _p.Data[o++];
                int n = (run & 0x3F) + 1;
                int kind = run & 0xC0;
                for (int k = 0; k < n && i < count; k++)
                {
                    switch (kind)
                    {
                        case 0x80: deltas[i++] = 0; break;
                        case 0x40: deltas[i++] = _p.S16(o); o += 2; break;
                        case 0xC0: deltas[i++] = (int)_p.U32(o); o += 4; break;
                        default: deltas[i++] = (sbyte)_p.Data[o++]; break;
                    }
                }
            }
            return deltas;
        }
    }
}
