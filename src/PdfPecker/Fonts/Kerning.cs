using System.Collections.Concurrent;

namespace PdfPecker.Fonts;

/// <summary>
/// Pair kerning of a font, as browsers apply it by default (font-kerning: auto): the GPOS 'kern' feature's pair
/// adjustments, or the legacy 'kern' table when the font has no GPOS kerning. Values are in font units and only
/// adjust the advance of the first glyph of a pair, which is all horizontal Latin, Greek and Cyrillic text needs.
/// Malformed tables give no kerning rather than an error.
/// </summary>
internal sealed class Kerning
{
    // GPOS lookup types.
    private const int PairPositioningLookup = 2;
    private const int ExtensionLookup = 9;

    // Pair positioning subtables: one pair set per first glyph (format 1), or a matrix of glyph classes (format 2).
    private const int PairSetFormat = 1, ClassPairFormat = 2;
    private const int FormatField = 0, CoverageField = 2, ValueFormat1Field = 4, ValueFormat2Field = 6;

    // Format 1: the pair sets are indexed by the first glyph's coverage index. A pair set holds its record count
    // and then records of the second glyph followed by the two value records.
    private const int PairSetCountField = 8, PairSetOffsetsField = 10;
    private const int PairSetRecordsField = 2, SecondGlyphLength = 2;

    // Format 2: the glyphs' classes, then a matrix with a row per class of the first glyph.
    private const int ClassDef1Field = 8, ClassDef2Field = 10, Class1CountField = 12, Class2CountField = 14;
    private const int ClassRecordsField = 16;

    // Legacy 'kern' table: a header, then subtables, each with a header of its own and sorted 6-byte pair records.
    private const int KernHeaderLength = 4, KernSubtableHeaderLength = 14, KernPairLength = 6;
    private const int KernCountField = 2, KernSubtableLengthField = 2, KernCoverageField = 4, KernPairCountField = 6;
    private const int KernValueField = 4;
    private const int KernMinimumSubtableLength = 6;
    // Coverage bits: horizontal, minimum values, cross-stream and the format in the high byte.
    private const int KernCoverageMask = 0xFF07, KernHorizontalFormat0 = 1;

    private static readonly uint[] KernFeature = [FontProgram.Tag("kern")];

    private readonly TableReader _reader;
    private readonly List<int> _pairPositioning = new(); // offsets of PairPos subtables, in lookup order
    private readonly int _kernPairsOffset = -1, _kernPairCount; // legacy kern table: offset of the first pair, and count
    private readonly ConcurrentDictionary<int, short> _cache = new();

    private Kerning(FontProgram font)
    {
        _reader = new TableReader(font.Data);
        if (font.Table("GPOS") is { } gpos) ReadGpos(gpos.Offset, gpos.Length);
        if (_pairPositioning.Count == 0 && font.Table("kern") is { } kern)
            (_kernPairsOffset, _kernPairCount) = ReadKern(kern.Offset, kern.Length);
    }

    /// <summary>The font's kerning, or null when it has none.</summary>
    public static Kerning? Load(FontProgram font)
    {
        try
        {
            var kerning = new Kerning(font);
            return kerning._pairPositioning.Count > 0 || kerning._kernPairCount > 0 ? kerning : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    /// <summary>The adjustment to the first glyph's advance, in font units (usually negative).</summary>
    public int Pair(int left, int right)
    {
        if ((left | right) > 0xFFFF || left == 0 || right == 0) return 0;
        int key = left << 16 | right;
        if (_cache.TryGetValue(key, out short cached)) return cached;
        int value;
        try { value = _pairPositioning.Count > 0 ? LookupGpos(left, right) : LookupKern(left, right); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { SwallowedErrors.Record(ex); value = 0; }
        short clamped = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        _cache.TryAdd(key, clamped);
        return clamped;
    }

    private void ReadGpos(int gpos, int length)
    {
        if (!LayoutLists.TryOpen(_reader, gpos, length, out var lists)) return;
        foreach (int index in lists.FindLookupIndexes(KernFeature))
        {
            if (lists.Lookup(index) is not { } lookup) continue;
            for (int i = 0; i < lookup.SubtableCount; i++)
            {
                var (type, subtable) = lookup.Subtable(i, ExtensionLookup);
                if (type == PairPositioningLookup && _reader.U16(subtable + FormatField) is PairSetFormat or ClassPairFormat)
                    _pairPositioning.Add(subtable);
            }
        }
    }

    private int LookupGpos(int left, int right)
    {
        // Each subtable that covers the pair adjusts it; real fonts list a pair once.
        foreach (int subtable in _pairPositioning)
        {
            int coverage = new CoverageTable(_reader, subtable + _reader.U16(subtable + CoverageField)).IndexOf(left);
            if (coverage < 0) continue;
            int format1 = _reader.U16(subtable + ValueFormat1Field), format2 = _reader.U16(subtable + ValueFormat2Field);
            bool found = _reader.U16(subtable + FormatField) == PairSetFormat
                ? TryLookupPairSet(subtable, coverage, format1, format2, right, out int value)
                : TryLookupClasses(subtable, format1, format2, left, right, out value);
            if (found) return value;
        }
        return 0;
    }

    private bool TryLookupPairSet(int subtable, int coverage, int format1, int format2, int right, out int value)
    {
        value = 0;
        if (coverage >= _reader.U16(subtable + PairSetCountField)) return false;
        int pairSet = subtable + _reader.U16(subtable + PairSetOffsetsField + coverage * 2);
        int recordLength = SecondGlyphLength + ValueRecord.Size(format1) + ValueRecord.Size(format2);
        int low = 0, high = _reader.U16(pairSet) - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            int record = pairSet + PairSetRecordsField + middle * recordLength;
            int secondGlyph = _reader.U16(record);
            if (secondGlyph == right)
            {
                value = ReadXAdvance(record + SecondGlyphLength, format1);
                return true;
            }
            if (secondGlyph < right) low = middle + 1; else high = middle - 1;
        }
        return false;
    }

    private bool TryLookupClasses(int subtable, int format1, int format2, int left, int right, out int value)
    {
        value = 0;
        int leftClass = new ClassDefTable(_reader, subtable + _reader.U16(subtable + ClassDef1Field)).ClassOf(left);
        int rightClass = new ClassDefTable(_reader, subtable + _reader.U16(subtable + ClassDef2Field)).ClassOf(right);
        int leftClassCount = _reader.U16(subtable + Class1CountField), rightClassCount = _reader.U16(subtable + Class2CountField);
        if (leftClass >= leftClassCount || rightClass >= rightClassCount) return false;
        int recordLength = ValueRecord.Size(format1) + ValueRecord.Size(format2);
        int record = subtable + ClassRecordsField + (leftClass * rightClassCount + rightClass) * recordLength;
        value = ReadXAdvance(record, format1);
        return true;
    }

    // The XAdvance field of a ValueRecord: XPlacement and YPlacement come first when present.
    private int ReadXAdvance(int record, int format)
    {
        if ((format & ValueRecord.XAdvance) == 0) return 0;
        int skipped = ((format & ValueRecord.XPlacement) != 0 ? 2 : 0) + ((format & ValueRecord.YPlacement) != 0 ? 2 : 0);
        return _reader.S16(record + skipped);
    }

    // The first horizontal format 0 subtable of a version 0 (Windows) kern table.
    private (int PairsOffset, int PairCount) ReadKern(int kern, int length)
    {
        if (length < KernHeaderLength || _reader.U16(kern) != 0) return (-1, 0);
        int end = kern + length, subtable = kern + KernHeaderLength;
        for (int i = 0, count = _reader.U16(kern + KernCountField); i < count && subtable + KernSubtableHeaderLength <= end; i++)
        {
            int subtableLength = _reader.U16(subtable + KernSubtableLengthField);
            int coverage = _reader.U16(subtable + KernCoverageField);
            // Horizontal, format 0, not minimum values and not cross-stream.
            if ((coverage & KernCoverageMask) == KernHorizontalFormat0)
            {
                int pairsOffset = subtable + KernSubtableHeaderLength;
                int pairCount = Math.Min(_reader.U16(subtable + KernPairCountField), (end - pairsOffset) / KernPairLength);
                return (pairsOffset, pairCount);
            }
            if (subtableLength < KernMinimumSubtableLength) break;
            subtable += subtableLength;
        }
        return (-1, 0);
    }

    private int LookupKern(int left, int right)
    {
        uint key = (uint)(left << 16 | right);
        int low = 0, high = _kernPairCount - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            int record = _kernPairsOffset + middle * KernPairLength;
            uint candidate = _reader.U32(record);
            if (candidate == key) return _reader.S16(record + KernValueField);
            if (candidate < key) low = middle + 1; else high = middle - 1;
        }
        return 0;
    }
}
