using PdfPecker.Svg;

namespace PdfPecker.Fonts;

/// <summary>
/// The colour glyphs of a font's COLR and CPAL tables: version 0 layers of glyphs in palette colours, and version 1
/// paint graphs of solid colours, linear, radial and sweep gradients, transforms, glyph clips and composites. A glyph's
/// paint graph is read into a tree of <see cref="ColorPaint"/> nodes for the PDF writer, which draws it as a Type 3
/// glyph, as Chrome's print output does. Colours come from the first palette; variable paints take the deltas of the
/// font's instance. A damaged table, a cycle of glyphs painting each other or a graph past the size limits makes the
/// glyph draw nothing rather than an error.
/// </summary>
internal sealed class ColorGlyphs
{
    // COLR header: version 0 fields, then the offsets version 1 adds.
    private const int ColrV0HeaderLength = 14, ColrV1HeaderLength = 34;
    private const int NumBaseGlyphRecordsField = 2, BaseGlyphRecordsField = 4, LayerRecordsField = 8, NumLayerRecordsField = 12;
    private const int BaseGlyphListField = 14, LayerListField = 18, ClipListField = 22, VarIndexMapField = 26, VariationStoreField = 30;
    private const int BaseGlyphRecordLength = 6, LayerRecordLength = 4, BaseGlyphPaintRecordLength = 6;
    private const int ClipRecordLength = 7;

    // CPAL header: the entries per palette, the palette and colour record counts, the colour records and the index of
    // each palette's first record. A colour record is blue, green, red and alpha.
    private const int CpalHeaderLength = 12;
    private const int PaletteEntriesField = 2, NumPalettesField = 4, NumColorRecordsField = 6, ColorRecordsField = 8, FirstPaletteIndexField = 12;

    /// <summary>The palette index that stands for the text colour.</summary>
    public const int ForegroundIndex = 0xFFFF;

    // A glyph's paint graph is read to at most this depth and this many paints; a deeper or larger one draws nothing.
    // Shared layers make the tree a graph is read into larger than the graph, so the count bounds the tree.
    private const int MaxDepth = 64;
    private const int MaxPaints = 50_000;
    // Looking for the text colour in the whole table reads at most this many paints, and assumes it is used past that.
    private const int MaxScannedPaints = 1_000_000;

    private const float F2Dot14 = 1f / 16384, Fixed = 1f / 65536;
    private const uint NoVariation = 0xFFFFFFFF;

    private readonly byte[] _d;
    private readonly TableReader _r;
    private readonly int _colr, _end;
    private readonly int _baseRecords, _numBaseRecords, _layerRecords, _numLayerRecords;
    private readonly int _baseGlyphList, _layerList, _clipList, _varIndexMap;
    private readonly VariationStore? _store;
    private readonly uint[] _palette; // the first palette, 0xAARRGGBB
    private bool? _usesForeground;
    private readonly object _lock = new();

    private ColorGlyphs(FontProgram p, (int Offset, int Length) colr, (int Offset, int Length) cpal)
    {
        _d = p.Data;
        _r = new TableReader(_d);
        _colr = colr.Offset;
        _end = colr.Offset + colr.Length;
        Need(_colr, ColrV0HeaderLength);
        int version = _r.U16(_colr);
        _numBaseRecords = _r.U16(_colr + NumBaseGlyphRecordsField);
        _baseRecords = Sub(_colr, _r.U32(_colr + BaseGlyphRecordsField));
        _layerRecords = Sub(_colr, _r.U32(_colr + LayerRecordsField));
        _numLayerRecords = _r.U16(_colr + NumLayerRecordsField);
        if (_numBaseRecords > 0) Need(_baseRecords, _numBaseRecords * BaseGlyphRecordLength);
        if (_numLayerRecords > 0) Need(_layerRecords, _numLayerRecords * LayerRecordLength);
        if (version >= 1 && colr.Length >= ColrV1HeaderLength)
        {
            _baseGlyphList = Sub(_colr, _r.U32(_colr + BaseGlyphListField));
            _layerList = Sub(_colr, _r.U32(_colr + LayerListField));
            _clipList = Sub(_colr, _r.U32(_colr + ClipListField));
            _varIndexMap = Sub(_colr, _r.U32(_colr + VarIndexMapField));
            int store = Sub(_colr, _r.U32(_colr + VariationStoreField));
            if (_baseGlyphList != 0) Need(_baseGlyphList, 4 + (long)_r.U32(_baseGlyphList) * BaseGlyphPaintRecordLength);
            if (_layerList != 0) Need(_layerList, 4 + (long)_r.U32(_layerList) * 4);
            if (_clipList != 0) Need(_clipList, 5 + (long)_r.U32(_clipList + 1) * ClipRecordLength);
            if (store != 0 && p.InstanceCoordinates is { } coords) _store = new VariationStore(_d, store, _end, coords);
        }
        _palette = ReadPalette(cpal);
    }

    /// <summary>The colour glyphs of a font, or null when it has none (or its COLR or CPAL table is damaged).</summary>
    public static ColorGlyphs? Load(FontProgram p)
    {
        if (p.Table("COLR") is not { } colr || p.Table("CPAL") is not { } cpal) return null;
        try
        {
            var glyphs = new ColorGlyphs(p, colr, cpal);
            return glyphs._numBaseRecords > 0 || glyphs._baseGlyphList != 0 && glyphs._r.U32(glyphs._baseGlyphList) > 0 ? glyphs : null;
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    /// <summary>Whether a glyph has a colour drawing.</summary>
    public bool Has(int gid)
    {
        try
        {
            return BasePaint(gid) != 0 || BaseRecord(gid) != 0;
        }
        catch (FormatException)
        {
            // The glyph's record points outside the table: it is a colour glyph whose drawing is damaged, which
            // Paint reports as null, so it draws nothing.
            return true;
        }
    }

    /// <summary>
    /// Whether any paint uses the text colour: such a font draws differently in each colour, so the writer keeps a
    /// font for each.
    /// </summary>
    public bool UsesForeground
    {
        get
        {
            lock (_lock) return _usesForeground ??= ScanForeground();
        }
    }

    /// <summary>
    /// The paint tree of a glyph's colour drawing, inside its clip box when it has one. Null when the glyph's drawing
    /// is damaged, has a cycle or is too large: the glyph then draws nothing.
    /// </summary>
    public ColorPaint? Paint(int gid) => Paint(gid, out _);

    /// <summary>
    /// The colour drawing of a glyph, and in <paramref name="read"/> how many paints reading it took, which a glyph past
    /// the limits also spends before it draws nothing.
    /// </summary>
    public ColorPaint? Paint(int gid, out int read)
    {
        int budget = MaxPaints;
        try
        {
            // The glyphs whose paints are being read, outermost first, to stop a cycle of PaintColrGlyph references.
            var stack = new List<int>();
            var paint = Glyph(gid, stack, 0, ref budget);
            read = MaxPaints - budget;
            return paint;
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            read = Math.Clamp(MaxPaints - budget, 1, MaxPaints);
            return null;
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The colour drawing of a glyph, version 1 first, inside the glyph's clip box.</summary>
    private ColorPaint? Glyph(int gid, List<int> stack, int depth, ref int budget)
    {
        if (stack.Contains(gid)) throw new FormatException("A cycle of colour glyphs.");
        stack.Add(gid);
        ColorPaint? paint;
        int v1 = BasePaint(gid);
        if (v1 != 0) paint = Read(v1, stack, depth, ref budget);
        else if (BaseRecord(gid) is int record and not 0)
        {
            int first = _r.U16(record + 2), count = _r.U16(record + 4);
            if (first + count > _numLayerRecords) throw new FormatException("Bad COLR layer range.");
            var layers = new ColorPaint[count];
            for (int i = 0; i < count; i++)
            {
                int layer = _layerRecords + (first + i) * LayerRecordLength;
                Spend(ref budget, 2);
                layers[i] = new ColorGlyphClip(_r.U16(layer), Solid(_r.U16(layer + 2), 1));
            }
            paint = new ColorLayers(layers);
        }
        else paint = null;
        stack.RemoveAt(stack.Count - 1);
        if (paint != null && ClipBox(gid) is { } box) paint = new ColorClipBox(box.XMin, box.YMin, box.XMax, box.YMax, paint);
        return paint;
    }

    private ColorPaint Read(int p, List<int> stack, int depth, ref int budget)
    {
        if (depth > MaxDepth) throw new FormatException("COLR paints nested too deep.");
        Spend(ref budget, 1);
        Need(p, 1);
        int format = _d[p];
        switch (format)
        {
            case 1: // PaintColrLayers
            {
                Need(p, 6);
                int count = _d[p + 1];
                long first = _r.U32(p + 2);
                if (_layerList == 0 || first + count > _r.U32(_layerList)) throw new FormatException("Bad COLR layer range.");
                var layers = new ColorPaint[count];
                for (int i = 0; i < count; i++)
                    layers[i] = Read(Sub(_layerList, _r.U32(_layerList + 4 + (int)(first + i) * 4)), stack, depth + 1, ref budget);
                return new ColorLayers(layers);
            }
            case 2: // PaintSolid
            case 3:
            {
                Need(p, format == 2 ? 5 : 9);
                float alpha = _r.S16(p + 3) * F2Dot14 + Delta(format == 3 ? _r.U32(p + 5) : NoVariation, 0) * F2Dot14;
                return Solid(_r.U16(p + 1), alpha);
            }
            case 4: // PaintLinearGradient
            case 5:
            {
                Need(p, format == 4 ? 16 : 20);
                uint var = format == 5 ? _r.U32(p + 16) : NoVariation;
                var g = new ColorGradient { Kind = ColorGradientKind.Linear };
                ReadColorLine(Sub24(p, p + 1), format == 5, g);
                g.X0 = Coord(p + 4, var, 0); g.Y0 = Coord(p + 6, var, 1);
                g.X1 = Coord(p + 8, var, 2); g.Y1 = Coord(p + 10, var, 3);
                g.X2 = Coord(p + 12, var, 4); g.Y2 = Coord(p + 14, var, 5);
                return g;
            }
            case 6: // PaintRadialGradient
            case 7:
            {
                Need(p, format == 6 ? 16 : 20);
                uint var = format == 7 ? _r.U32(p + 16) : NoVariation;
                var g = new ColorGradient { Kind = ColorGradientKind.Radial };
                ReadColorLine(Sub24(p, p + 1), format == 7, g);
                g.X0 = Coord(p + 4, var, 0); g.Y0 = Coord(p + 6, var, 1); g.R0 = _r.U16(p + 8) + Delta(var, 2);
                g.X1 = Coord(p + 10, var, 3); g.Y1 = Coord(p + 12, var, 4); g.R1 = _r.U16(p + 14) + Delta(var, 5);
                return g;
            }
            case 8: // PaintSweepGradient
            case 9:
            {
                Need(p, format == 8 ? 12 : 16);
                uint var = format == 9 ? _r.U32(p + 12) : NoVariation;
                var g = new ColorGradient { Kind = ColorGradientKind.Sweep };
                ReadColorLine(Sub24(p, p + 1), format == 9, g);
                g.X0 = Coord(p + 4, var, 0); g.Y0 = Coord(p + 6, var, 1);
                // Angles are in half turns biased by one: 0 is -180 degrees and 1.0 is 0, so that -1 to 1 spans a turn.
                g.StartAngle = ((_r.S16(p + 8) + Delta(var, 2)) * F2Dot14 + 1) * 180;
                g.EndAngle = ((_r.S16(p + 10) + Delta(var, 3)) * F2Dot14 + 1) * 180;
                return g;
            }
            case 10: // PaintGlyph
            {
                Need(p, 6);
                return new ColorGlyphClip(_r.U16(p + 4), Read(Sub24(p, p + 1), stack, depth + 1, ref budget));
            }
            case 11: // PaintColrGlyph
            {
                Need(p, 3);
                return Glyph(_r.U16(p + 1), stack, depth + 1, ref budget) ?? new ColorLayers([]);
            }
            case 12: // PaintTransform
            case 13:
            {
                Need(p, 7);
                int t = Sub24(p, p + 4);
                Need(t, format == 12 ? 24 : 28);
                uint var = format == 13 ? _r.U32(t + 24) : NoVariation;
                float F(int field) => ((int)_r.U32(t + field * 4) + Delta(var, field)) * Fixed;
                var m = new Matrix(F(0), F(1), F(2), F(3), F(4), F(5));
                return new ColorTransform(m, Read(Sub24(p, p + 1), stack, depth + 1, ref budget));
            }
            case 14: // PaintTranslate
            case 15:
            {
                Need(p, format == 14 ? 8 : 12);
                uint var = format == 15 ? _r.U32(p + 8) : NoVariation;
                var m = Matrix.Translate(Coord(p + 4, var, 0), Coord(p + 6, var, 1));
                return new ColorTransform(m, Read(Sub24(p, p + 1), stack, depth + 1, ref budget));
            }
            case >= 16 and <= 31:
                return ReadScaleRotateSkew(p, format, stack, depth, ref budget);
            case 32: // PaintComposite
            {
                Need(p, 8);
                var source = Read(Sub24(p, p + 1), stack, depth + 1, ref budget);
                var backdrop = Read(Sub24(p, p + 5), stack, depth + 1, ref budget);
                return new ColorComposite(_d[p + 4], source, backdrop);
            }
            default:
                // A format this reader does not know draws nothing, as the specification asks.
                return new ColorLayers([]);
        }
    }

    /// <summary>
    /// The scale (16 to 23), rotate (24 to 27) and skew (28 to 31) paints: even formats are static, odd ones variable,
    /// and those that turn about a centre have its coordinates after their own values.
    /// </summary>
    private ColorTransform ReadScaleRotateSkew(int p, int format, List<int> stack, int depth, ref int budget)
    {
        bool variable = (format & 1) == 1;
        int kind = (format - 16) / 2; // 0 scale, 1 scale about a centre, 2 uniform scale, 3 uniform about a centre, 4 rotate, ...
        int values = kind switch { 0 or 1 => 2, 2 or 3 => 1, 4 or 5 => 1, _ => 2 };
        bool centred = kind is 1 or 3 or 5 or 7;
        int fields = values + (centred ? 2 : 0);
        int length = 4 + fields * 2;
        Need(p, length + (variable ? 4 : 0));
        uint var = variable ? _r.U32(p + length) : NoVariation;
        float Value(int i) => (_r.S16(p + 4 + i * 2) + Delta(var, i)) * F2Dot14;
        Matrix m;
        switch (kind)
        {
            case 0 or 1: m = Matrix.Scale(Value(0), Value(1)); break;
            case 2 or 3: m = Matrix.Scale(Value(0), Value(0)); break;
            case 4 or 5: m = Matrix.Rotate(Value(0) * 180); break;
            default:
            {
                // A positive x skew turns the y axis counter-clockwise, a positive y skew the x axis.
                float tx = MathF.Tan(Value(0) * MathF.PI), ty = MathF.Tan(Value(1) * MathF.PI);
                m = new Matrix(1, ty, -tx, 1, 0, 0);
                break;
            }
        }
        if (centred)
        {
            float cx = Coord(p + 4 + values * 2, var, values), cy = Coord(p + 6 + values * 2, var, values + 1);
            m = Matrix.Translate(-cx, -cy).Then(m).Then(Matrix.Translate(cx, cy));
        }
        return new ColorTransform(m, Read(Sub24(p, p + 1), stack, depth + 1, ref budget));
    }

    private void ReadColorLine(int line, bool variable, ColorGradient g)
    {
        Need(line, 3);
        int count = _r.U16(line + 1);
        int length = variable ? 10 : 6;
        Need(line + 3, count * length);
        g.Extend = _d[line] switch { 1 => ColorExtend.Repeat, 2 => ColorExtend.Reflect, _ => ColorExtend.Pad };
        g.Stops = new ColorStop[count];
        for (int i = 0; i < count; i++)
        {
            int s = line + 3 + i * length;
            uint var = variable ? _r.U32(s + 6) : NoVariation;
            float offset = (_r.S16(s) + Delta(var, 0)) * F2Dot14;
            float alpha = (_r.S16(s + 4) + Delta(var, 1)) * F2Dot14;
            var solid = Solid(_r.U16(s + 2), alpha);
            g.Stops[i] = new ColorStop(offset, solid.Color, solid.Foreground);
        }
    }

    /// <summary>A palette colour (or the text colour) with its alpha multiplied by <paramref name="alpha"/>.</summary>
    private ColorSolid Solid(int index, float alpha)
    {
        bool foreground = index == ForegroundIndex;
        // An index past the palette draws nothing, as browsers do.
        uint color = foreground ? 0xFF000000 : index < _palette.Length ? _palette[index] : 0;
        uint a = (uint)MathF.Round(Math.Clamp((color >> 24) * alpha, 0, 255));
        return new ColorSolid(a << 24 | color & 0xFFFFFF, foreground);
    }

    /// <summary>A coordinate in font units: an FWORD and its delta.</summary>
    private float Coord(int offset, uint var, int field) => _r.S16(offset) + Delta(var, field);

    /// <summary>The delta of one field of a variable paint at the font's instance, in the field's own units.</summary>
    private float Delta(uint varIndexBase, int field)
    {
        if (_store == null || varIndexBase == NoVariation) return 0;
        long index = varIndexBase + (long)field;
        if (index > 0xFFFFFFFE) return 0;
        var (outer, inner) = _varIndexMap != 0
            ? VariationStore.MapIndex(_d, _varIndexMap, _end, (int)Math.Min(index, int.MaxValue))
            : ((int)(index >> 16), (int)(index & 0xFFFF));
        return _store.Delta(outer, inner);
    }

    /// <summary>The offset of a glyph's version 1 paint, 0 when it has none.</summary>
    private int BasePaint(int gid)
    {
        if (_baseGlyphList == 0) return 0;
        int lo = 0, hi = (int)Math.Min(_r.U32(_baseGlyphList), int.MaxValue) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int record = _baseGlyphList + 4 + mid * BaseGlyphPaintRecordLength;
            int g = _r.U16(record);
            if (g == gid) return Sub(_baseGlyphList, _r.U32(record + 2));
            if (g < gid) lo = mid + 1; else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>The offset of a glyph's version 0 base glyph record, 0 when it has none.</summary>
    private int BaseRecord(int gid)
    {
        int lo = 0, hi = _numBaseRecords - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int record = _baseRecords + mid * BaseGlyphRecordLength;
            int g = _r.U16(record);
            if (g == gid) return record;
            if (g < gid) lo = mid + 1; else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>A glyph's clip box in font units, from the ClipList.</summary>
    private (float XMin, float YMin, float XMax, float YMax)? ClipBox(int gid)
    {
        if (_clipList == 0) return null;
        long count = _r.U32(_clipList + 1);
        int lo = 0, hi = (int)Math.Min(count, int.MaxValue) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int record = _clipList + 5 + mid * ClipRecordLength;
            int start = _r.U16(record), end = _r.U16(record + 2);
            if (gid < start) hi = mid - 1;
            else if (gid > end) lo = mid + 1;
            else
            {
                int box = Sub24(_clipList, record + 4);
                Need(box, 9);
                uint var = NoVariation;
                if (_d[box] == 2)
                {
                    Need(box, 13);
                    var = _r.U32(box + 9);
                }
                return (Coord(box + 1, var, 0), Coord(box + 3, var, 1), Coord(box + 5, var, 2), Coord(box + 7, var, 3));
            }
        }
        return null;
    }

    /// <summary>The first palette's colours, as 0xAARRGGBB.</summary>
    private uint[] ReadPalette((int Offset, int Length) cpal)
    {
        int start = cpal.Offset, end = cpal.Offset + cpal.Length;
        if (cpal.Length < CpalHeaderLength) throw new FormatException("Bad CPAL table.");
        int entries = _r.U16(start + PaletteEntriesField), palettes = _r.U16(start + NumPalettesField);
        int records = _r.U16(start + NumColorRecordsField);
        long array = start + (long)_r.U32(start + ColorRecordsField);
        if (palettes == 0 || entries == 0) return [];
        int first = _r.U16(start + FirstPaletteIndexField);
        if (first + entries > records || array + (long)records * 4 > end) throw new FormatException("Bad CPAL table.");
        var colors = new uint[entries];
        for (int i = 0; i < entries; i++)
        {
            int c = (int)array + (first + i) * 4;
            colors[i] = (uint)_d[c + 3] << 24 | (uint)_d[c + 2] << 16 | (uint)_d[c + 1] << 8 | _d[c];
        }
        return colors;
    }

    /// <summary>Whether any layer or paint of the table uses the text colour, each paint read once.</summary>
    private bool ScanForeground()
    {
        try
        {
            for (int i = 0; i < _numLayerRecords; i++)
                if (_r.U16(_layerRecords + i * LayerRecordLength + 2) == ForegroundIndex) return true;
            if (_baseGlyphList == 0) return false;
            var seen = new HashSet<int>();
            var pending = new Stack<int>();
            long bases = _r.U32(_baseGlyphList);
            for (int i = 0; i < bases; i++) pending.Push(Sub(_baseGlyphList, _r.U32(_baseGlyphList + 4 + i * BaseGlyphPaintRecordLength + 2)));
            if (_layerList != 0)
                for (long i = 0, n = _r.U32(_layerList); i < n; i++) pending.Push(Sub(_layerList, _r.U32(_layerList + 4 + (int)i * 4)));
            while (pending.Count > 0)
            {
                int p = pending.Pop();
                if (p == 0 || !seen.Add(p)) continue;
                if (seen.Count > MaxScannedPaints) return true;
                Need(p, 1);
                int format = _d[p];
                switch (format)
                {
                    case 2 or 3:
                        Need(p, 3);
                        if (_r.U16(p + 1) == ForegroundIndex) return true;
                        break;
                    case >= 4 and <= 9:
                    {
                        int line = Sub24(p, p + 1);
                        Need(line, 3);
                        int length = (format & 1) == 1 ? 10 : 6;
                        int count = _r.U16(line + 1);
                        Need(line + 3, count * length);
                        for (int i = 0; i < count; i++)
                            if (_r.U16(line + 3 + i * length + 2) == ForegroundIndex) return true;
                        break;
                    }
                    case 10 or 12 or 13 or (>= 14 and <= 31):
                        pending.Push(Sub24(p, p + 1));
                        break;
                    case 32:
                        Need(p, 8);
                        pending.Push(Sub24(p, p + 1));
                        pending.Push(Sub24(p, p + 5));
                        break;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return true;
        }
    }

    // ------------------------------------------------------------------ bounds

    private static void Spend(ref int budget, int paints)
    {
        budget -= paints;
        if (budget < 0) throw new FormatException("COLR glyph too large.");
    }

    /// <summary>An absolute offset from a 32-bit offset relative to <paramref name="from"/>; 0 stays 0 (no table).</summary>
    private int Sub(int from, uint offset)
    {
        if (offset == 0) return 0;
        long at = from + (long)offset;
        if (at >= _end) throw new FormatException("COLR offset out of range.");
        return (int)at;
    }

    /// <summary>The table a 24-bit offset at <paramref name="field"/> points to, relative to <paramref name="from"/>.</summary>
    private int Sub24(int from, int field)
    {
        Need(field, 3);
        int offset = _d[field] << 16 | _d[field + 1] << 8 | _d[field + 2];
        if (offset == 0) throw new FormatException("Null COLR paint offset.");
        return Sub(from, (uint)offset);
    }

    /// <summary>Throws unless <paramref name="length"/> bytes from <paramref name="offset"/> are inside the COLR table.</summary>
    private void Need(long offset, long length)
    {
        if (offset < _colr || length < 0 || offset + length > _end) throw new FormatException("COLR data out of range.");
    }
}

/// <summary>A node of a colour glyph's paint tree, in font units with y up.</summary>
internal abstract class ColorPaint;

/// <summary>Paints drawn one after the other.</summary>
internal sealed class ColorLayers(ColorPaint[] layers) : ColorPaint
{
    public readonly ColorPaint[] Layers = layers;
}

/// <summary>A colour, 0xAARRGGBB; for the text colour only its alpha counts.</summary>
internal sealed class ColorSolid(uint color, bool foreground) : ColorPaint
{
    public readonly uint Color = color;
    public readonly bool Foreground = foreground;

    /// <summary>The colour with the text colour put in for the foreground.</summary>
    public uint Resolve(uint foregroundColor) => Foreground ? Color & 0xFF000000 | foregroundColor & 0xFFFFFF : Color;
}

internal enum ColorGradientKind : byte { Linear, Radial, Sweep }

internal enum ColorExtend : byte { Pad, Repeat, Reflect }

/// <summary>A stop of a colour line: its offset, its colour (0xAARRGGBB) and whether that is the text colour.</summary>
internal readonly record struct ColorStop(float Offset, uint Color, bool Foreground);

/// <summary>
/// A gradient. Linear: along (X0, Y0) to (X1, Y1), turned so that its lines are parallel to (X0, Y0) to (X2, Y2).
/// Radial: between the circles (X0, Y0, R0) and (X1, Y1, R1). Sweep: around (X0, Y0) from StartAngle to EndAngle,
/// degrees counter-clockwise from the x axis.
/// </summary>
internal sealed class ColorGradient : ColorPaint
{
    public ColorGradientKind Kind;
    public ColorExtend Extend;
    public ColorStop[] Stops = [];
    public float X0, Y0, X1, Y1, X2, Y2, R0, R1, StartAngle, EndAngle;
}

/// <summary>A paint clipped to a glyph's outline.</summary>
internal sealed class ColorGlyphClip(int gid, ColorPaint paint) : ColorPaint
{
    public readonly int Gid = gid;
    public readonly ColorPaint Paint = paint;
}

/// <summary>A paint clipped to a glyph's clip box.</summary>
internal sealed class ColorClipBox(float xMin, float yMin, float xMax, float yMax, ColorPaint paint) : ColorPaint
{
    public readonly float XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax;
    public readonly ColorPaint Paint = paint;
}

/// <summary>A paint in a transformed space: <see cref="Matrix"/> maps it to the space around it.</summary>
internal sealed class ColorTransform(Matrix matrix, ColorPaint paint) : ColorPaint
{
    public readonly Matrix Matrix = matrix;
    public readonly ColorPaint Paint = paint;
}

/// <summary>A source paint composited onto a backdrop paint (the COLR composite modes, 0 clear to 27 luminosity).</summary>
internal sealed class ColorComposite(int mode, ColorPaint source, ColorPaint backdrop) : ColorPaint
{
    public const int Clear = 0, Src = 1, Dest = 2, SrcOver = 3, DestOver = 4, SrcIn = 5, DestIn = 6, SrcOut = 7, DestOut = 8,
        SrcAtop = 9, DestAtop = 10, Xor = 11, Plus = 12;

    public readonly int Mode = mode;
    public readonly ColorPaint Source = source, Backdrop = backdrop;
}
