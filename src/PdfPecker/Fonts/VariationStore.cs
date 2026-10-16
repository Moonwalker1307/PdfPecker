namespace PdfPecker.Fonts;

/// <summary>
/// An ItemVariationStore evaluated at one instance's normalized coordinates. OpenType uses this one format for the
/// deltas of GPOS kerning and anchors (through GDEF), HVAR advance widths and CFF2 blends.
/// </summary>
internal sealed class VariationStore
{
    private readonly byte[] _d;
    private readonly int[] _data;       // absolute offsets of the ItemVariationData subtables
    private readonly float[] _scalars;  // how much each region applies at the instance

    public VariationStore(byte[] d, int start, int end, float[] coords)
    {
        _d = d;
        if (start < 0 || start + 8 > end || end > d.Length || U16(start) != 1) throw new FormatException("Bad ItemVariationStore.");
        long regions = start + (long)U32(start + 2);
        int count = U16(start + 6);
        if (start + 8 + count * 4 > end || regions + 4 > end) throw new FormatException("Bad ItemVariationStore.");
        int r = (int)regions;
        int axisCount = U16(r), regionCount = U16(r + 2);
        if (r + 4 + (long)regionCount * axisCount * 6 > end) throw new FormatException("Bad variation region list.");
        _scalars = new float[regionCount];
        for (int i = 0; i < regionCount; i++)
        {
            float s = 1;
            for (int a = 0; a < axisCount && s != 0; a++)
            {
                int o = r + 4 + (i * axisCount + a) * 6;
                float st = S16(o) / 16384f, peak = S16(o + 2) / 16384f, en = S16(o + 4) / 16384f;
                float c = a < coords.Length ? coords[a] : 0;
                if (peak == 0 || st > peak || peak > en || st < 0 && en > 0 || c == peak) continue;
                if (c < st || c > en) s = 0;
                else s *= c < peak ? (c - st) / (peak - st) : (en - c) / (en - peak);
            }
            _scalars[i] = s;
        }
        _data = new int[count];
        for (int i = 0; i < count; i++)
        {
            long o = start + (long)U32(start + 8 + i * 4);
            if (o + 6 > end) throw new FormatException("Bad ItemVariationData.");
            int sub = (int)o;
            int items = U16(sub), words = U16(sub + 2) & 0x7FFF, regionIndexes = U16(sub + 4);
            bool longWords = (U16(sub + 2) & 0x8000) != 0;
            if (words > regionIndexes) throw new FormatException("Bad ItemVariationData.");
            long rows = sub + 6 + regionIndexes * 2L + (long)items * RowSize(longWords, words, regionIndexes);
            if (rows > end) throw new FormatException("Bad ItemVariationData.");
            for (int k = 0; k < regionIndexes; k++)
                if (U16(sub + 6 + k * 2) >= regionCount) throw new FormatException("Bad region index.");
            _data[i] = sub;
        }
    }

    /// <summary>The ItemVariationStore of a font's GDEF table (version 1.3), or null.</summary>
    public static VariationStore? FromGdef(FontProgram p, float[] coords)
    {
        if (p.Table("GDEF") is not { Length: >= 18 } gdef || p.U16(gdef.Offset) != 1 || p.U16(gdef.Offset + 2) < 3) return null;
        uint offset = p.U32(gdef.Offset + 14);
        if (offset == 0 || offset >= gdef.Length) return null;
        return new VariationStore(p.Data, gdef.Offset + (int)offset, gdef.Offset + gdef.Length, coords);
    }

    private static int RowSize(bool longWords, int words, int regions)
        => longWords ? words * 4 + (regions - words) * 2 : words * 2 + (regions - words);

    private int U16(int o) => _d[o] << 8 | _d[o + 1];
    private short S16(int o) => (short)(_d[o] << 8 | _d[o + 1]);
    private uint U32(int o) => (uint)(_d[o] << 24 | _d[o + 1] << 16 | _d[o + 2] << 8 | _d[o + 3]);

    public int SubtableCount => _data.Length;

    /// <summary>The delta of one item at the instance (0 for an index the store does not have).</summary>
    public float Delta(int outer, int inner)
    {
        if ((uint)outer >= (uint)_data.Length) return 0;
        int sub = _data[outer];
        int items = U16(sub), words = U16(sub + 2) & 0x7FFF, regions = U16(sub + 4);
        bool longWords = (U16(sub + 2) & 0x8000) != 0;
        if ((uint)inner >= (uint)items) return 0;
        int row = sub + 6 + regions * 2 + inner * RowSize(longWords, words, regions);
        float delta = 0;
        for (int k = 0; k < regions; k++)
        {
            int value;
            if (k < words)
            {
                value = longWords ? (int)U32(row) : S16(row);
                row += longWords ? 4 : 2;
            }
            else
            {
                value = longWords ? S16(row) : (sbyte)_d[row];
                row += longWords ? 2 : 1;
            }
            if (value != 0) delta += value * _scalars[U16(sub + 6 + k * 2)];
        }
        return delta;
    }

    /// <summary>The scalars of the regions one ItemVariationData subtable lists, in its order (CFF2 blends use these).</summary>
    public float[] RegionScalars(int outer)
    {
        if ((uint)outer >= (uint)_data.Length) throw new FormatException("Bad variation store index.");
        int sub = _data[outer];
        var s = new float[U16(sub + 4)];
        for (int k = 0; k < s.Length; k++) s[k] = _scalars[U16(sub + 6 + k * 2)];
        return s;
    }

    /// <summary>The (outer, inner) index a DeltaSetIndexMap gives an item, such as a glyph in HVAR.</summary>
    public static (int Outer, int Inner) MapIndex(byte[] d, int map, int end, int index)
    {
        int format = d[map], entryFormat = d[map + 1];
        long count = format == 0 ? d[map + 2] << 8 | d[map + 3] : (uint)(d[map + 2] << 24 | d[map + 3] << 16 | d[map + 4] << 8 | d[map + 5]);
        int data = map + (format == 0 ? 4 : 6);
        int size = ((entryFormat >> 4) & 3) + 1, innerBits = (entryFormat & 0x0F) + 1;
        if (count == 0) return (0, index);
        long i = Math.Min(index, count - 1);
        long at = data + i * size;
        if (at + size > end) throw new FormatException("Bad DeltaSetIndexMap.");
        uint v = 0;
        for (int b = 0; b < size; b++) v = v << 8 | d[at + b];
        return ((int)(v >> innerBits), (int)(v & ((1u << innerBits) - 1)));
    }
}
