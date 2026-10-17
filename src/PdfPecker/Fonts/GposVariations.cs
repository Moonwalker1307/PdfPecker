namespace PdfPecker.Fonts;

/// <summary>
/// Writes an instance's GPOS values into a copy of the table. A variable font keeps the default instance's kerning
/// and anchor values in GPOS and their variations in GDEF's ItemVariationStore, referenced by VariationIndex device
/// tables; adding the deltas in place lets every reader of GPOS (kerning, mark positioning) see the instance without
/// knowing about variations. Only numbers change, so the table keeps its layout.
/// </summary>
internal readonly ref struct GposVariations
{
    private const int SupportedVersion = 1, HeaderLength = 10;
    private const int LookupListField = 8;

    // GPOS lookup types.
    private const int SinglePositioning = 1, PairPositioning = 2, CursivePositioning = 3;
    private const int MarkToBase = 4, MarkToLigature = 5, MarkToMark = 6;
    private const int ExtensionLookup = 9;

    private const int FormatField = 0;
    private const int SingleValueFormatField = 4, SingleFormat1Record = 6;
    private const int SingleFormat2CountField = 6, SingleFormat2Records = 8;

    // Pair positioning: the value formats come first in both formats.
    private const int ValueFormat1Field = 4, ValueFormat2Field = 6;
    private const int PairSetCountField = 8, PairSetOffsetsField = 10;
    private const int PairSetRecordsField = 2, SecondGlyphLength = 2;
    private const int Class1CountField = 12, Class2CountField = 14, ClassRecordsField = 16;

    // Cursive positioning: an entry and an exit anchor offset for each covered glyph.
    private const int EntryExitCountField = 4, EntryExitOffsetsField = 6;

    // Mark attachment: the number of mark classes, the mark array, and the array of base, ligature or mark2 anchors.
    private const int MarkClassCountField = 6, MarkArrayField = 8, AttachArrayField = 10;
    private const int MarkRecordLength = 4, MarkAnchorField = 2;

    // Anchor tables. Format 3 adds a device table offset for each coordinate.
    private const int AnchorWithDevices = 3;
    private const int AnchorXField = 2, AnchorYField = 4, AnchorXDeviceField = 6, AnchorYDeviceField = 8;

    // Device tables: a VariationIndex table holds the delta set's outer and inner index and the format 0x8000.
    private const int VariationIndexFormat = 0x8000;
    private const int DeviceOuterField = 0, DeviceInnerField = 2, DeviceFormatField = 4;

    private readonly byte[] _data;
    private readonly TableReader _reader;
    private readonly VariationStore _store;

    // Values already moved, as the offset of the field, and pair sets already walked, as the negated offset of the
    // set. Subtables, anchors and pair sets may be shared, and one set instead of two saves an allocation.
    private readonly HashSet<int> _done = new();

    private GposVariations(byte[] data, VariationStore store)
    {
        _data = data;
        _reader = new TableReader(data);
        _store = store;
    }

    /// <summary>A copy of the GPOS table with the store's deltas added, or the table itself when it is not version 1.</summary>
    public static byte[] Instance(byte[] gpos, VariationStore store)
    {
        var data = (byte[])gpos.Clone();
        if (data.Length < HeaderLength || new TableReader(data).U16(0) != SupportedVersion) return gpos;
        return new GposVariations(data, store).VaryLookups();
    }

    private byte[] VaryLookups()
    {
        int lookups = _reader.U16(LookupListField);
        for (int i = 0, n = _reader.U16(lookups); i < n; i++)
        {
            var lookup = new LayoutLookup(_reader, lookups + _reader.U16(lookups + 2 + i * 2));
            for (int s = 0; s < lookup.SubtableCount; s++)
            {
                var (type, subtable) = lookup.Subtable(s, ExtensionLookup);
                VarySubtable(type, subtable);
            }
        }
        return _data;
    }

    private void VarySubtable(int type, int subtable)
    {
        int format = _reader.U16(subtable + FormatField);
        switch (type)
        {
            case SinglePositioning when format == 1:
                VaryValue(subtable, subtable + SingleFormat1Record, _reader.U16(subtable + SingleValueFormatField));
                break;
            case SinglePositioning when format == 2:
                VarySingleArray(subtable);
                break;
            case PairPositioning when format == 1:
                VaryPairSets(subtable);
                break;
            case PairPositioning when format == 2:
                VaryPairClasses(subtable);
                break;
            case CursivePositioning when format == 1:
                for (int k = 0, n = _reader.U16(subtable + EntryExitCountField) * 2; k < n; k++)
                    VaryAnchorAt(subtable, subtable + EntryExitOffsetsField + k * 2);
                break;
            case MarkToBase or MarkToLigature or MarkToMark when format == 1:
                VaryMarkAttachment(type, subtable);
                break;
        }
    }

    private void VarySingleArray(int subtable)
    {
        int valueFormat = _reader.U16(subtable + SingleValueFormatField), recordLength = ValueRecord.Size(valueFormat);
        for (int k = 0, n = _reader.U16(subtable + SingleFormat2CountField); k < n; k++)
            VaryValue(subtable, subtable + SingleFormat2Records + k * recordLength, valueFormat);
    }

    private void VaryPairSets(int subtable)
    {
        int format1 = _reader.U16(subtable + ValueFormat1Field), format2 = _reader.U16(subtable + ValueFormat2Field);
        int length1 = ValueRecord.Size(format1), length2 = ValueRecord.Size(format2);
        if (!HasDeviceTables(format1, format2)) return;
        for (int k = 0, n = _reader.U16(subtable + PairSetCountField); k < n; k++)
        {
            int pairSet = subtable + _reader.U16(subtable + PairSetOffsetsField + k * 2);
            if (!_done.Add(-pairSet)) continue;
            for (int r = 0, count = _reader.U16(pairSet); r < count; r++)
            {
                int values = pairSet + PairSetRecordsField + r * (SecondGlyphLength + length1 + length2) + SecondGlyphLength;
                VaryValue(subtable, values, format1);
                VaryValue(subtable, values + length1, format2);
            }
        }
    }

    private void VaryPairClasses(int subtable)
    {
        int format1 = _reader.U16(subtable + ValueFormat1Field), format2 = _reader.U16(subtable + ValueFormat2Field);
        int length1 = ValueRecord.Size(format1), length2 = ValueRecord.Size(format2);
        if (!HasDeviceTables(format1, format2)) return;
        int leftClasses = _reader.U16(subtable + Class1CountField), rightClasses = _reader.U16(subtable + Class2CountField);
        for (int i = 0; i < leftClasses * rightClasses; i++)
        {
            int values = subtable + ClassRecordsField + i * (length1 + length2);
            VaryValue(subtable, values, format1);
            VaryValue(subtable, values + length1, format2);
        }
    }

    // Without device tables in either format, no value of the subtable varies.
    private static bool HasDeviceTables(int format1, int format2) => ((format1 | format2) & ValueRecord.DeviceTables) != 0;

    private void VaryMarkAttachment(int type, int subtable)
    {
        int classes = _reader.U16(subtable + MarkClassCountField);
        int marks = subtable + _reader.U16(subtable + MarkArrayField);
        for (int k = 0, n = _reader.U16(marks); k < n; k++)
            VaryAnchorAt(marks, marks + 2 + k * MarkRecordLength + MarkAnchorField);
        int array = subtable + _reader.U16(subtable + AttachArrayField);
        if (type == MarkToLigature)
        {
            // Ligature attach tables hold a row of anchors for each component of the ligature.
            for (int l = 0, n = _reader.U16(array); l < n; l++)
            {
                int attach = array + _reader.U16(array + 2 + l * 2);
                for (int k = 0, count = _reader.U16(attach) * classes; k < count; k++)
                    VaryAnchorAt(attach, attach + 2 + k * 2);
            }
        }
        else
        {
            for (int k = 0, count = _reader.U16(array) * classes; k < count; k++)
                VaryAnchorAt(array, array + 2 + k * 2);
        }
    }

    // An anchor offset, relative to its parent table, where 0 means there is no anchor.
    private void VaryAnchorAt(int parent, int offsetField)
    {
        if (_reader.U16(offsetField) is int offset and > 0) VaryAnchor(parent + offset);
    }

    /// <summary>A ValueRecord: placement and advance fields, then the offsets of their device tables.</summary>
    private void VaryValue(int parent, int record, int format)
    {
        if ((format & ValueRecord.DeviceTables) == 0) return;
        Span<int> fields = stackalloc int[4];
        int position = record;
        for (int bit = 0; bit < 4; bit++)
        {
            fields[bit] = -1;
            if ((format & 1 << bit) != 0) { fields[bit] = position; position += 2; }
        }
        for (int bit = 4; bit < 8; bit++)
        {
            if ((format & 1 << bit) == 0) continue;
            int device = _reader.U16(position);
            position += 2;
            if (device != 0 && fields[bit - 4] >= 0) VaryField(fields[bit - 4], parent + device);
        }
    }

    /// <summary>A format 3 anchor: x and y, with a device table for each.</summary>
    private void VaryAnchor(int anchor)
    {
        if (_reader.U16(anchor + FormatField) != AnchorWithDevices) return;
        if (_reader.U16(anchor + AnchorXDeviceField) is int x and > 0) VaryField(anchor + AnchorXField, anchor + x);
        if (_reader.U16(anchor + AnchorYDeviceField) is int y and > 0) VaryField(anchor + AnchorYField, anchor + y);
    }

    // Adds the delta a VariationIndex device table names to the 16-bit value at the field.
    private void VaryField(int field, int device)
    {
        if (_reader.U16(device + DeviceFormatField) != VariationIndexFormat || !_done.Add(field)) return; // other device formats are hinting adjustments
        float delta = _store.Delta(_reader.U16(device + DeviceOuterField), _reader.U16(device + DeviceInnerField));
        if (delta == 0) return;
        int value = Math.Clamp((int)MathF.Round(_reader.S16(field) + delta), short.MinValue, short.MaxValue);
        _data[field] = (byte)(value >> 8);
        _data[field + 1] = (byte)value;
    }
}
