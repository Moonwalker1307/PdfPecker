using System.Numerics;

namespace PdfPecker.Fonts;

/// <summary>
/// The script, feature and lookup lists that open a GPOS or GSUB table, as far as simple pair kerning and ligatures
/// need them: which lookups the default features of the Latin script (or else the default script) use, and where
/// their subtables are. Every offset is absolute in the font data.
/// </summary>
internal readonly struct LayoutLists
{
    private const int HeaderLength = 10;
    private const int ScriptListField = 4, FeatureListField = 6, LookupListField = 8;
    private const int ScriptRecordLength = 6, FeatureRecordLength = 6;
    private const int RecordOffsetField = 4;
    private const int SupportedVersion = 1;

    // Language system table: after the lookup order and the required feature come the feature indexes.
    private const int FeatureIndexCountField = 4, FeatureIndexesField = 6;

    // Feature table: after the feature parameters come the lookup indexes.
    private const int LookupIndexCountField = 2, LookupIndexesField = 4;

    private static readonly uint LatinScript = FontProgram.Tag("latn");
    private static readonly uint DefaultScript = FontProgram.Tag("DFLT");

    private readonly TableReader _reader;
    private readonly int _scripts, _features, _lookups;

    private LayoutLists(TableReader reader, int scripts, int features, int lookups)
    {
        _reader = reader;
        _scripts = scripts;
        _features = features;
        _lookups = lookups;
    }

    /// <summary>Opens the lists of a version 1 table that is long enough to hold its header.</summary>
    public static bool TryOpen(TableReader reader, int table, int length, out LayoutLists lists)
    {
        lists = default;
        if (length < HeaderLength || reader.U16(table) != SupportedVersion) return false;
        lists = new LayoutLists(
            reader,
            table + reader.U16(table + ScriptListField),
            table + reader.U16(table + FeatureListField),
            table + reader.U16(table + LookupListField));
        return true;
    }

    /// <summary>
    /// The indexes of the lookups that the features with one of the tags use in the default language system, in
    /// ascending order. They are not checked against the lookup list: <see cref="Lookup"/> does that.
    /// </summary>
    public SortedSet<int> FindLookupIndexes(ReadOnlySpan<uint> featureTags)
    {
        int languageSystem = FindLanguageSystem();
        if (languageSystem < 0) return new SortedSet<int>();
        var indexes = ReadLookupIndexes(languageSystem, featureTags);
        // A lookup list outside the table makes the whole table damaged, even when no lookup is used, as it always has.
        _ = _reader.U16(_lookups);
        return indexes;
    }

    /// <summary>The lookup with the index, or null for an index past the lookup list, which is a damaged reference.</summary>
    public LayoutLookup? Lookup(int index)
    {
        if (index >= _reader.U16(_lookups)) return null;
        return new LayoutLookup(_reader, _lookups + _reader.U16(_lookups + 2 + index * 2));
    }

    // The Latin script's default language system, or the one of the default script, or of the first script that has one.
    private int FindLanguageSystem()
    {
        int latin = -1, fallback = -1;
        for (int i = 0, scriptCount = _reader.U16(_scripts); i < scriptCount; i++)
        {
            int record = _scripts + 2 + i * ScriptRecordLength;
            uint tag = _reader.U32(record);
            int script = _scripts + _reader.U16(record + RecordOffsetField);
            int defaultLanguageSystem = _reader.U16(script);
            if (defaultLanguageSystem == 0) continue;
            if (tag == LatinScript) latin = script + defaultLanguageSystem;
            else if (tag == DefaultScript || fallback < 0) fallback = script + defaultLanguageSystem;
        }
        return latin >= 0 ? latin : fallback;
    }

    private SortedSet<int> ReadLookupIndexes(int languageSystem, ReadOnlySpan<uint> featureTags)
    {
        var lookupIndexes = new SortedSet<int>();
        int featureCount = _reader.U16(_features);
        for (int i = 0, n = _reader.U16(languageSystem + FeatureIndexCountField); i < n; i++)
        {
            int featureIndex = _reader.U16(languageSystem + FeatureIndexesField + i * 2);
            if (featureIndex >= featureCount) continue;
            int record = _features + 2 + featureIndex * FeatureRecordLength;
            if (!featureTags.Contains(_reader.U32(record))) continue;
            int feature = _features + _reader.U16(record + RecordOffsetField);
            for (int j = 0, m = _reader.U16(feature + LookupIndexCountField); j < m; j++)
                lookupIndexes.Add(_reader.U16(feature + LookupIndexesField + j * 2));
        }
        return lookupIndexes;
    }
}

/// <summary>A lookup of a GPOS or GSUB table: a type and the subtables that implement it.</summary>
internal readonly struct LayoutLookup
{
    private const int SubtableCountField = 4, SubtableOffsetsField = 6;

    // An extension subtable holds the real subtable's type and a 32-bit offset to it.
    private const int ExtensionTypeField = 2, ExtensionOffsetField = 4;

    private readonly TableReader _reader;
    private readonly int _offset;

    public LayoutLookup(TableReader reader, int offset)
    {
        _reader = reader;
        _offset = offset;
        Type = reader.U16(offset);
        SubtableCount = reader.U16(offset + SubtableCountField);
    }

    public int Type { get; }

    public int SubtableCount { get; }

    /// <summary>
    /// The type and offset of a subtable. When the lookup is of the extension type, they are those of the subtable
    /// the extension points to.
    /// </summary>
    public (int Type, int Offset) Subtable(int index, int extensionType)
    {
        int subtable = _offset + _reader.U16(_offset + SubtableOffsetsField + index * 2);
        if (Type != extensionType) return (Type, subtable);
        return (_reader.U16(subtable + ExtensionTypeField), subtable + (int)_reader.U32(subtable + ExtensionOffsetField));
    }
}

/// <summary>A coverage table: the glyphs a subtable applies to, each with the index that picks its data.</summary>
internal readonly struct CoverageTable(TableReader reader, int offset)
{
    private const int ListFormat = 1, RangeFormat = 2;
    private const int CountField = 2, EntriesField = 4;
    private const int RangeRecordLength = 6, RangeEndField = 2, RangeStartIndexField = 4;

    /// <summary>The coverage index of the glyph, or -1 when the table does not cover it.</summary>
    public int IndexOf(int glyph)
    {
        int format = reader.U16(offset), count = reader.U16(offset + CountField);
        int low = 0, high = count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (format == ListFormat)
            {
                int candidate = reader.U16(offset + EntriesField + middle * 2);
                if (candidate == glyph) return middle;
                if (candidate < glyph) low = middle + 1; else high = middle - 1;
            }
            else if (format == RangeFormat)
            {
                int record = offset + EntriesField + middle * RangeRecordLength;
                int start = reader.U16(record), end = reader.U16(record + RangeEndField);
                if (glyph < start) high = middle - 1;
                else if (glyph > end) low = middle + 1;
                else return reader.U16(record + RangeStartIndexField) + glyph - start;
            }
            else return -1;
        }
        return -1;
    }

    /// <summary>Every covered glyph with its coverage index; a table of an unknown format covers nothing.</summary>
    public EntryEnumerator Entries() => new(reader, offset);

    /// <summary>Walks a coverage table without allocating, which matters because every font's ligatures walk one.</summary>
    public struct EntryEnumerator
    {
        private readonly TableReader _reader;
        private readonly int _offset, _format, _count;
        private int _entry, _glyph, _end, _startIndex;

        internal EntryEnumerator(TableReader reader, int offset)
        {
            _reader = reader;
            _offset = offset;
            _format = reader.U16(offset);
            _count = reader.U16(offset + CountField);
            _entry = -1;
            _glyph = 0;
            _end = -1;
            Current = default;
        }

        /// <summary>The glyph and coverage index the last <see cref="MoveNext"/> reached.</summary>
        public (int Glyph, int Index) Current { get; private set; }

        /// <summary>Lets the enumerator be used in a foreach loop.</summary>
        public readonly EntryEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next covered glyph.</summary>
        public bool MoveNext()
        {
            if (_format == ListFormat)
            {
                if (++_entry >= _count) return false;
                Current = (_reader.U16(_offset + EntriesField + _entry * 2), _entry);
                return true;
            }
            if (_format != RangeFormat) return false;
            // Within a range the glyphs follow each other; at its end the next record is read.
            while (_glyph > _end)
            {
                if (++_entry >= _count) return false;
                int record = _offset + EntriesField + _entry * RangeRecordLength;
                _glyph = _reader.U16(record);
                _end = _reader.U16(record + RangeEndField);
                _startIndex = _reader.U16(record + RangeStartIndexField) - _glyph;
            }
            Current = (_glyph, _startIndex + _glyph);
            _glyph++;
            return true;
        }
    }
}

/// <summary>A class definition table: the class of each glyph, 0 for glyphs it does not list.</summary>
internal readonly struct ClassDefTable(TableReader reader, int offset)
{
    private const int ArrayFormat = 1, RangeFormat = 2;
    private const int StartGlyphField = 2, ArrayCountField = 4, ArrayField = 6;
    private const int RangeCountField = 2, RangesField = 4;
    private const int RangeRecordLength = 6, RangeEndField = 2, RangeClassField = 4;

    public int ClassOf(int glyph)
    {
        int format = reader.U16(offset);
        if (format == ArrayFormat)
        {
            int start = reader.U16(offset + StartGlyphField), count = reader.U16(offset + ArrayCountField);
            return glyph >= start && glyph < start + count ? reader.U16(offset + ArrayField + (glyph - start) * 2) : 0;
        }
        if (format == RangeFormat)
        {
            int low = 0, high = reader.U16(offset + RangeCountField) - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int record = offset + RangesField + middle * RangeRecordLength;
                if (glyph < reader.U16(record)) high = middle - 1;
                else if (glyph > reader.U16(record + RangeEndField)) low = middle + 1;
                else return reader.U16(record + RangeClassField);
            }
        }
        return 0;
    }
}

/// <summary>The size of the ValueRecords that a GPOS subtable's value formats describe.</summary>
internal static class ValueRecord
{
    public const int XPlacement = 0x1, YPlacement = 0x2, XAdvance = 0x4;

    /// <summary>The bits for the device tables of the four value fields.</summary>
    public const int DeviceTables = 0xF0;

    /// <summary>The formats' bits that stand for a field of two bytes: the four values and the four device offsets.</summary>
    private const int FieldBits = 0xFF;

    public static int Size(int format) => 2 * BitOperations.PopCount((uint)(format & FieldBits));
}
