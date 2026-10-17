namespace PdfPecker.Fonts;

// The GPOS lookup types that adjust positions with value records: single and pair positioning. Attachments are in
// OpenTypeLayout.Attachment.cs, contextual positioning in OpenTypeLayout.Contextual.cs.
internal sealed partial class OpenTypeLayout
{
    private const int SinglePositioning = 1, PairPositioning = 2, CursivePositioning = 3;
    private const int MarkToBasePositioning = 4, MarkToLigaturePositioning = 5, MarkToMarkPositioning = 6;
    private const int ContextPositioning = 7, ChainedContextPositioning = 8;

    // Single positioning: one value record for every covered glyph (format 1), or one per coverage index (format 2).
    private const int SingleValueFormat = 1, SingleValueListFormat = 2;
    private const int SingleValueFormatField = 4, SingleValueField = 6, SingleValueCountField = 6, SingleValuesField = 8;

    // Pair positioning: a pair set per first glyph (format 1), or a matrix of glyph classes (format 2), with the value
    // formats of the first and second glyph's records.
    private const int PairSetFormat = 1, ClassPairFormat = 2;
    private const int ValueFormat1Field = 4, ValueFormat2Field = 6;
    private const int PairSetCountField = 8, PairSetOffsetsField = 10, PairValueRecordsField = 2, SecondGlyphLength = 2;
    private const int ClassDef1Field = 8, ClassDef2Field = 10, Class1CountField = 12, Class2CountField = 14, ClassRecordsField = 16;

    // The value record field ValueRecord has no name for, as kerning does not read it.
    private const int YAdvanceValue = 0x8;

    private bool ApplyGpos(LookupState c, int type, int sub, ref int i)
    {
        switch (type)
        {
            case SinglePositioning: return SinglePos(c, sub, ref i);
            case PairPositioning: return PairPos(c, sub, ref i);
            case CursivePositioning: return CursivePos(c, sub, ref i);
            case MarkToBasePositioning:
            case MarkToLigaturePositioning:
            case MarkToMarkPositioning: return MarkPos(c, type, sub, ref i);
            case ContextPositioning:
            case ChainedContextPositioning: return ContextLookup(c, sub, type == ChainedContextPositioning, ref i);
            default: return false;
        }
    }

    private bool SinglePos(LookupState c, int sub, ref int i)
    {
        int index = CoverageOf(sub, c.Buf[i].Gid);
        if (index < 0) return false;
        int format = _reader.U16(sub + FormatField), valueFormat = _reader.U16(sub + SingleValueFormatField);
        int record = format switch
        {
            SingleValueFormat => sub + SingleValueField,
            SingleValueListFormat when index < _reader.U16(sub + SingleValueCountField) => sub + SingleValuesField + index * ValueRecord.Size(valueFormat),
            _ => -1,
        };
        if (record < 0) return false;
        ApplyValue(c, i, valueFormat, record);
        i++;
        return true;
    }

    // Adds a value record's placement and advance to a glyph; device tables are not applied.
    private void ApplyValue(LookupState c, int i, int format, int record)
    {
        var buf = c.Buf;
        var g = buf[i];
        int field = record;
        if ((format & ValueRecord.XPlacement) != 0) { g.XOffset += _reader.S16(field); field += 2; }
        if ((format & ValueRecord.YPlacement) != 0) { g.YOffset += _reader.S16(field); field += 2; }
        if ((format & ValueRecord.XAdvance) != 0) { g.XAdvance += _reader.S16(field); field += 2; }
        if ((format & YAdvanceValue) != 0) g.YAdvance += _reader.S16(field);
        buf[i] = g;
    }

    // Adjusts the glyph and the next one the lookup does not skip. When the second glyph's value format is not empty,
    // the second glyph is consumed too.
    private bool PairPos(LookupState c, int sub, ref int i)
    {
        int index = CoverageOf(sub, c.Buf[i].Gid);
        if (index < 0) return false;
        int j = NextMatch(c, i);
        if (j < 0) return false;
        int format = _reader.U16(sub + FormatField);
        int format1 = _reader.U16(sub + ValueFormat1Field), format2 = _reader.U16(sub + ValueFormat2Field);
        int records = format switch
        {
            PairSetFormat => PairSetRecord(sub, index, format1, format2, c.Buf[j].Gid),
            ClassPairFormat => ClassPairRecord(sub, format1, format2, c.Buf[i].Gid, c.Buf[j].Gid),
            _ => -1,
        };
        if (records < 0) return false;
        ApplyValue(c, i, format1, records);
        ApplyValue(c, j, format2, records + ValueRecord.Size(format1));
        i = format2 != 0 ? j + 1 : j;
        return true;
    }

    // The value records of the pair in the first glyph's pair set, found by binary search on the second glyph, or -1.
    private int PairSetRecord(int sub, int index, int format1, int format2, int second)
    {
        if (index >= _reader.U16(sub + PairSetCountField)) return -1;
        int pairSet = sub + _reader.U16(sub + PairSetOffsetsField + index * 2);
        int recordLength = SecondGlyphLength + ValueRecord.Size(format1) + ValueRecord.Size(format2);
        int low = 0, high = _reader.U16(pairSet) - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            int record = pairSet + PairValueRecordsField + middle * recordLength;
            int glyph = _reader.U16(record);
            if (glyph < second) low = middle + 1;
            else if (glyph > second) high = middle - 1;
            else return record + SecondGlyphLength;
        }
        return -1;
    }

    // The value records of the pair's classes in the class matrix, or -1 when a class is past it.
    private int ClassPairRecord(int sub, int format1, int format2, int first, int second)
    {
        int class1 = ClassOf(sub + _reader.U16(sub + ClassDef1Field), first);
        int class2 = ClassOf(sub + _reader.U16(sub + ClassDef2Field), second);
        int class1Count = _reader.U16(sub + Class1CountField), class2Count = _reader.U16(sub + Class2CountField);
        if (class1 >= class1Count || class2 >= class2Count) return -1;
        return sub + ClassRecordsField + (class1 * class2Count + class2) * (ValueRecord.Size(format1) + ValueRecord.Size(format2));
    }
}
