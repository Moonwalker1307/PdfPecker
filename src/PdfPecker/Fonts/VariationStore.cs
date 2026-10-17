namespace PdfPecker.Fonts;

/// <summary>
/// An ItemVariationStore evaluated at one instance's normalized coordinates. OpenType uses this one format for the
/// deltas of GPOS kerning and anchors (through GDEF), HVAR advance widths and CFF2 blends.
/// </summary>
internal sealed class VariationStore
{
    // Store header: format, offset of the region list, and the offsets of the ItemVariationData subtables.
    private const int SupportedFormat = 1;
    private const int HeaderLength = 8;
    private const int RegionListField = 2, DataCountField = 6, DataOffsetsField = 8;

    // Region list: the axis and region counts, then per region and axis a start, peak and end coordinate in F2Dot14.
    private const int RegionListHeaderLength = 4;
    private const int AxisCountField = 0, RegionCountField = 2;
    private const int AxisRecordLength = 6, PeakField = 2, EndField = 4;
    private const float F2Dot14One = 16384f;

    // ItemVariationData: item and region counts, the region indexes, then the rows of deltas.
    private const int DataHeaderLength = 6;
    private const int ItemCountField = 0, WordDeltaCountField = 2, RegionIndexCountField = 4;
    private const int WordDeltaCountMask = 0x7FFF, LongWordsFlag = 0x8000;

    // GDEF header: version, glyph class, attach list and ligature caret offsets, mark glyph sets, then this store.
    private const int GdefHeaderLength = 18;
    private const int GdefMinorVersionField = 2, GdefStoreOffsetField = 14;
    private const int GdefFirstVersionWithStore = 3;

    // DeltaSetIndexMap: formats 0 and 1 differ in the width of the entry count.
    private const int MapEntryFormatField = 1, MapShortCountField = 2, MapLongCountField = 2;
    private const int MapShortEntriesField = 4, MapLongEntriesField = 6;

    /// <summary>
    /// One ItemVariationData subtable: where its rows are and how they are laid out. It is read from the table when
    /// needed, because a store keeps only the offsets, which spares an allocation for every store.
    /// </summary>
    private readonly record struct ItemData(int Offset, int ItemCount, int WordCount, int RegionCount, bool LongWords)
    {
        public int RowLength => LongWords ? WordCount * 4 + (RegionCount - WordCount) * 2 : WordCount * 2 + (RegionCount - WordCount);

        public int RegionIndexesOffset => Offset + DataHeaderLength;

        public int RowsOffset => RegionIndexesOffset + RegionCount * 2;
    }

    private readonly TableReader _reader;
    private readonly int[] _data;      // absolute offsets of the ItemVariationData subtables
    private readonly float[] _scalars;  // how much each region applies at the instance

    public VariationStore(byte[] d, int start, int end, float[] coords)
    {
        _reader = new TableReader(d);
        if (start < 0 || start + HeaderLength > end || end > d.Length || _reader.U16(start) != SupportedFormat)
            throw new FormatException("Bad ItemVariationStore.");
        long regionList = start + (long)_reader.U32(start + RegionListField);
        int dataCount = _reader.U16(start + DataCountField);
        if (start + HeaderLength + dataCount * 4 > end || regionList + RegionListHeaderLength > end)
            throw new FormatException("Bad ItemVariationStore.");
        _scalars = ReadRegionScalars((int)regionList, end, coords);
        _data = new int[dataCount];
        for (int i = 0; i < dataCount; i++)
            _data[i] = ValidateItemData(start, i, end).Offset;
    }

    /// <summary>The ItemVariationStore of a font's GDEF table (version 1.3), or null.</summary>
    public static VariationStore? FromGdef(FontProgram p, float[] coords)
    {
        if (p.Table("GDEF") is not { Length: >= GdefHeaderLength } gdef
            || p.U16(gdef.Offset) != 1 || p.U16(gdef.Offset + GdefMinorVersionField) < GdefFirstVersionWithStore) return null;
        uint offset = p.U32(gdef.Offset + GdefStoreOffsetField);
        if (offset == 0 || offset >= gdef.Length) return null;
        return new VariationStore(p.Data, gdef.Offset + (int)offset, gdef.Offset + gdef.Length, coords);
    }

    public int SubtableCount => _data.Length;

    /// <summary>The delta of one item at the instance (0 for an index the store does not have).</summary>
    public float Delta(int outer, int inner)
    {
        if ((uint)outer >= (uint)_data.Length) return 0;
        var data = ItemDataAt(_data[outer]);
        if ((uint)inner >= (uint)data.ItemCount) return 0;
        int row = data.RowsOffset + inner * data.RowLength;
        float delta = 0;
        for (int k = 0; k < data.RegionCount; k++)
        {
            int value;
            if (k < data.WordCount)
            {
                value = data.LongWords ? (int)_reader.U32(row) : _reader.S16(row);
                row += data.LongWords ? 4 : 2;
            }
            else
            {
                value = data.LongWords ? _reader.S16(row) : _reader.S8(row);
                row += data.LongWords ? 2 : 1;
            }
            if (value != 0) delta += value * _scalars[_reader.U16(data.RegionIndexesOffset + k * 2)];
        }
        return delta;
    }

    /// <summary>The scalars of the regions one ItemVariationData subtable lists, in its order (CFF2 blends use these).</summary>
    public float[] RegionScalars(int outer)
    {
        if ((uint)outer >= (uint)_data.Length) throw new FormatException("Bad variation store index.");
        var data = ItemDataAt(_data[outer]);
        var scalars = new float[data.RegionCount];
        for (int k = 0; k < scalars.Length; k++) scalars[k] = _scalars[_reader.U16(data.RegionIndexesOffset + k * 2)];
        return scalars;
    }

    /// <summary>The (outer, inner) index a DeltaSetIndexMap gives an item, such as a glyph in HVAR.</summary>
    public static (int Outer, int Inner) MapIndex(byte[] d, int map, int end, int index)
    {
        var reader = new TableReader(d);
        int format = d[map], entryFormat = d[map + MapEntryFormatField];
        long count = format == 0 ? reader.U16(map + MapShortCountField) : reader.U32(map + MapLongCountField);
        int entries = map + (format == 0 ? MapShortEntriesField : MapLongEntriesField);
        // The entry format holds the entry size in bytes minus one and the inner index's width in bits minus one.
        int entrySize = ((entryFormat >> 4) & 3) + 1, innerBits = (entryFormat & 0x0F) + 1;
        if (count == 0) return (0, index);
        // An index past the last entry takes the last one's value, as the specification asks for glyphs.
        long entryIndex = Math.Min(index, count - 1);
        long entry = entries + entryIndex * entrySize;
        if (entry + entrySize > end) throw new FormatException("Bad DeltaSetIndexMap.");
        uint value = 0;
        for (int b = 0; b < entrySize; b++) value = value << 8 | d[entry + b];
        return ((int)(value >> innerBits), (int)(value & ((1u << innerBits) - 1)));
    }

    private float[] ReadRegionScalars(int regionList, int end, float[] coords)
    {
        int axisCount = _reader.U16(regionList + AxisCountField), regionCount = _reader.U16(regionList + RegionCountField);
        if (regionList + RegionListHeaderLength + (long)regionCount * axisCount * AxisRecordLength > end)
            throw new FormatException("Bad variation region list.");
        var scalars = new float[regionCount];
        for (int i = 0; i < regionCount; i++)
        {
            float scalar = 1;
            for (int axis = 0; axis < axisCount && scalar != 0; axis++)
            {
                int record = regionList + RegionListHeaderLength + (i * axisCount + axis) * AxisRecordLength;
                float coordinate = axis < coords.Length ? coords[axis] : 0;
                scalar *= AxisScalar(record, coordinate);
            }
            scalars[i] = scalar;
        }
        return scalars;
    }

    // How much one axis of a region applies at the coordinate: 1 at the peak, falling off linearly to 0 at the edges.
    // An axis with a damaged or never-varying range does not scale the region.
    private float AxisScalar(int record, float coordinate)
    {
        float start = _reader.S16(record) / F2Dot14One, peak = _reader.S16(record + PeakField) / F2Dot14One;
        float end = _reader.S16(record + EndField) / F2Dot14One;
        if (peak == 0 || start > peak || peak > end || start < 0 && end > 0 || coordinate == peak) return 1;
        if (coordinate < start || coordinate > end) return 0;
        return coordinate < peak ? (coordinate - start) / (peak - start) : (end - coordinate) / (end - peak);
    }

    private ItemData ItemDataAt(int offset)
    {
        int words = _reader.U16(offset + WordDeltaCountField);
        return new ItemData(
            offset,
            _reader.U16(offset + ItemCountField),
            words & WordDeltaCountMask,
            _reader.U16(offset + RegionIndexCountField),
            (words & LongWordsFlag) != 0);
    }

    private ItemData ValidateItemData(int start, int index, int end)
    {
        long offset = start + (long)_reader.U32(start + DataOffsetsField + index * 4);
        if (offset + DataHeaderLength > end) throw new FormatException("Bad ItemVariationData.");
        var data = ItemDataAt((int)offset);
        if (data.WordCount > data.RegionCount) throw new FormatException("Bad ItemVariationData.");
        long rowsEnd = data.RegionIndexesOffset + data.RegionCount * 2L + (long)data.ItemCount * data.RowLength;
        if (rowsEnd > end) throw new FormatException("Bad ItemVariationData.");
        for (int k = 0; k < data.RegionCount; k++)
            if (_reader.U16(data.RegionIndexesOffset + k * 2) >= _scalars.Length) throw new FormatException("Bad region index.");
        return data;
    }
}
