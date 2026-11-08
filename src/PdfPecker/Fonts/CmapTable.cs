namespace PdfPecker.Fonts;

/// <summary>Receives the mappings of a cmap subtable, one character code and its glyph at a time.</summary>
internal interface ICmapSink
{
    /// <summary>Maps a code to a glyph other than .notdef; a later mapping of the same code replaces an earlier one.</summary>
    void Map(int code, int glyph);
}

/// <summary>
/// The cmap table, read the same way for HTML to PDF (<see cref="FontProgram"/>) and for the renderer's embedded fonts:
/// the directory of encoding records, and the subtable formats 0, 2, 4, 6, 10, 12 and 13. Which subtable to read is
/// each caller's choice. Format 14, the variation sequences, is read apart (see <see cref="ReadVariationSequences"/>). Reads go through <see cref="TableReader"/>, so a subtable cut off by the end of the file
/// throws <see cref="IndexOutOfRangeException"/> after passing on what came before the damage.
/// </summary>
internal static class CmapTable
{
    // The header: a version and the number of encoding records, each a platform, an encoding and a subtable offset.
    private const int HeaderLength = 4, CountField = 2, RecordsField = 4, RecordLength = 8;
    private const int RecordEncodingField = 2, RecordOffsetField = 4;
    private const int FormatFieldLength = 2;

    /// <summary>Byte encoding: one glyph for each of 256 single-byte codes.</summary>
    public const int ByteEncodingFormat = 0;
    /// <summary>High-byte mapping through sub-headers, for the double-byte encodings of CJK fonts.</summary>
    public const int HighByteMappingFormat = 2;
    /// <summary>Segment mapping to delta values: the usual Unicode BMP subtable.</summary>
    public const int SegmentMappingFormat = 4;
    /// <summary>Trimmed table: a dense run of 16-bit codes.</summary>
    public const int TrimmedTableFormat = 6;
    /// <summary>Trimmed array: a dense run of 32-bit codes.</summary>
    public const int TrimmedArrayFormat = 10;
    /// <summary>Segmented coverage: ranges of 32-bit codes mapped to consecutive glyphs.</summary>
    public const int SegmentedCoverageFormat = 12;
    /// <summary>Many-to-one range mappings: ranges of 32-bit codes that each share one glyph, as in last-resort fonts.</summary>
    public const int ManyToOneRangeFormat = 13;
    /// <summary>Unicode variation sequences: the glyphs of a character followed by a variation selector.</summary>
    public const int VariationSequencesFormat = 14;

    private const int ByteEncodingGlyphsField = 6, ByteEncodingCodes = 256;
    // Format 2: 256 sub-header keys (a sub-header's index times 8), then the sub-headers of a first code, an entry count,
    // a delta and a range offset that counts from its own field into the glyph id array.
    private const int SubHeaderKeysField = 6, SubHeaderKeyCount = 256, SubHeaderLength = 8;
    private const int SubHeaderEntryCountField = 2, SubHeaderDeltaField = 4, SubHeaderRangeOffsetField = 6;
    // Format 4: segCountX2, then four parallel arrays (end codes, a reserved pad, start codes, deltas, range offsets).
    private const int SegmentCountX2Field = 6, EndCodesField = 14, ReservedPadLength = 2;
    private const int LastSegmentEndCode = 0xFFFF;
    private const int TrimmedTableFirstCodeField = 6, TrimmedTableCountField = 8, TrimmedTableGlyphsField = 10;
    private const int TrimmedArrayFirstCodeField = 12, TrimmedArrayCountField = 16, TrimmedArrayGlyphsField = 20;
    // Formats 12 and 13: the group count, then groups of a start code, an end code and a glyph.
    private const int GroupCountField = 12, GroupsField = 16, GroupLength = 12;
    private const int GroupEndCodeField = 4, GroupGlyphField = 8;

    // Format 14: the subtable's length and the selector record count, then records of a 24-bit selector and the offsets
    // of its default table (ranges of a 24-bit start and a count of further characters) and its non-default table
    // (mappings of a 24-bit character and a glyph), counted from the subtable's start.
    private const int VariationLengthField = 2, SelectorCountField = 6, SelectorRecordsField = 10, SelectorRecordLength = 11;
    private const int DefaultOffsetField = 3, NonDefaultOffsetField = 7;
    private const int RangeLength = 4, RangeCountField = 3, MappingLength = 5, MappingGlyphField = 3;

    private const int MaxCodePoint = 0x10FFFF, MaxGlyphId = 0xFFFF;

    /// <summary>
    /// A subtable reads at most this many codes, so overlapping segments or groups in a damaged font cannot loop over
    /// Unicode again and again; a font that maps every character once stays far below it.
    /// </summary>
    private const int MaxCodes = 200_000;

    /// <summary>An encoding record and the format of its subtable, which starts at <see cref="Offset"/> in the font.</summary>
    public readonly record struct Subtable(int Platform, int Encoding, int Format, int Offset);

    /// <summary>
    /// The encoding records of the cmap table at <paramref name="offset"/>, in the font's order. A record past the end
    /// of the table, or whose subtable starts in the header or past the end, is left out.
    /// </summary>
    public static List<Subtable> Subtables(TableReader font, int offset, int length)
    {
        var subtables = new List<Subtable>();
        if (length < HeaderLength) return subtables;
        int count = font.U16(offset + CountField);
        for (int i = 0; i < count; i++)
        {
            int record = offset + RecordsField + i * RecordLength;
            if (record + RecordLength > offset + length) break;
            uint at = font.U32(record + RecordOffsetField);
            if (at < HeaderLength || at > length - FormatFieldLength) continue;
            int subtable = offset + (int)at;
            subtables.Add(new Subtable(font.U16(record), font.U16(record + RecordEncodingField), font.U16(subtable), subtable));
        }
        return subtables;
    }

    /// <summary>
    /// Passes every code the subtable at <paramref name="subtable"/> maps to a glyph to <paramref name="sink"/>; false
    /// when this reader does not know its format.
    /// </summary>
    public static bool Read<TSink>(TableReader font, int subtable, ref TSink sink) where TSink : struct, ICmapSink
    {
        var codes = new Budget<TSink>(sink);
        try
        {
            switch (font.U16(subtable))
            {
                case ByteEncodingFormat: ReadByteEncoding(font, subtable, ref codes); return true;
                case HighByteMappingFormat: ReadHighByteMapping(font, subtable, ref codes); return true;
                case SegmentMappingFormat: ReadSegmentMapping(font, subtable, ref codes); return true;
                case TrimmedTableFormat: ReadTrimmedTable(font, subtable, ref codes); return true;
                case TrimmedArrayFormat: ReadTrimmedArray(font, subtable, ref codes); return true;
                case SegmentedCoverageFormat: ReadGroups(font, subtable, sequential: true, ref codes); return true;
                case ManyToOneRangeFormat: ReadGroups(font, subtable, sequential: false, ref codes); return true;
                default: return false;
            }
        }
        finally
        {
            // The sink keeps what was read before a damaged part of the subtable threw.
            sink = codes.Sink;
        }
    }

    /// <summary>The sink and how many more codes the subtable may read.</summary>
    private struct Budget<TSink>(TSink sink) where TSink : struct, ICmapSink
    {
        public TSink Sink = sink;
        private int _left = MaxCodes;

        /// <summary>Maps a code to a glyph unless the glyph is .notdef; false once the subtable has read too many codes.</summary>
        public bool Map(long code, long glyph)
        {
            if (--_left < 0) return false;
            if (glyph is > 0 and <= MaxGlyphId) Sink.Map((int)code, (int)glyph);
            return true;
        }
    }

    private static void ReadByteEncoding<TSink>(TableReader font, int subtable, ref Budget<TSink> codes) where TSink : struct, ICmapSink
    {
        for (int code = 0; code < ByteEncodingCodes; code++)
            if (!codes.Map(code, font.Data[subtable + ByteEncodingGlyphsField + code])) return;
    }

    // Single bytes go through sub-header 0, whose codes are the same for every high byte that selects it, so it is read
    // once; the other sub-headers map the codes of their high byte.
    private static void ReadHighByteMapping<TSink>(TableReader font, int subtable, ref Budget<TSink> codes) where TSink : struct, ICmapSink
    {
        int keys = subtable + SubHeaderKeysField, subHeaders = keys + SubHeaderKeyCount * 2;
        bool singleBytesRead = false;
        for (int high = 0; high < SubHeaderKeyCount; high++)
        {
            int index = font.U16(keys + high * 2) / SubHeaderLength;
            if (index == 0)
            {
                if (singleBytesRead) continue;
                singleBytesRead = true;
            }
            int subHeader = subHeaders + index * SubHeaderLength;
            int firstCode = font.U16(subHeader), entryCount = font.U16(subHeader + SubHeaderEntryCountField);
            int delta = font.S16(subHeader + SubHeaderDeltaField), rangeOffset = font.U16(subHeader + SubHeaderRangeOffsetField);
            int glyphs = subHeader + SubHeaderRangeOffsetField + rangeOffset;
            for (int i = 0; i < entryCount; i++)
            {
                int address = glyphs + i * 2;
                if (address + 2 > font.Data.Length) break;
                int glyph = font.U16(address);
                if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                if (!codes.Map(index == 0 ? firstCode + i : high << 8 | (firstCode + i), glyph)) return;
            }
        }
    }

    // Each segment maps a code range either by adding a delta or through the glyph id array that follows the range
    // offsets, which a range offset addresses relative to its own position.
    private static void ReadSegmentMapping<TSink>(TableReader font, int subtable, ref Budget<TSink> codes) where TSink : struct, ICmapSink
    {
        int segmentsX2 = font.U16(subtable + SegmentCountX2Field);
        int endCodes = subtable + EndCodesField, startCodes = endCodes + segmentsX2 + ReservedPadLength;
        int deltas = startCodes + segmentsX2, rangeOffsets = deltas + segmentsX2;
        for (int s = 0; s < segmentsX2; s += 2)
        {
            int end = font.U16(endCodes + s), start = font.U16(startCodes + s);
            int delta = font.S16(deltas + s), rangeOffset = font.U16(rangeOffsets + s);
            if (start > end) continue;
            for (int code = start; code <= end && code != LastSegmentEndCode; code++)
            {
                int glyph;
                if (rangeOffset == 0) glyph = (code + delta) & 0xFFFF;
                else
                {
                    int glyphAddress = rangeOffsets + s + rangeOffset + (code - start) * 2;
                    if (glyphAddress + 2 > font.Data.Length) break;
                    glyph = font.U16(glyphAddress);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (!codes.Map(code, glyph)) return;
            }
        }
    }

    private static void ReadTrimmedTable<TSink>(TableReader font, int subtable, ref Budget<TSink> codes) where TSink : struct, ICmapSink
    {
        int first = font.U16(subtable + TrimmedTableFirstCodeField), count = font.U16(subtable + TrimmedTableCountField);
        for (int i = 0; i < count; i++)
            if (!codes.Map(first + i, font.U16(subtable + TrimmedTableGlyphsField + i * 2))) return;
    }

    private static void ReadTrimmedArray<TSink>(TableReader font, int subtable, ref Budget<TSink> codes) where TSink : struct, ICmapSink
    {
        long first = font.U32(subtable + TrimmedArrayFirstCodeField), count = font.U32(subtable + TrimmedArrayCountField);
        for (long i = 0; i < count && first + i <= MaxCodePoint; i++)
            if (!codes.Map(first + i, font.U16(subtable + TrimmedArrayGlyphsField + (int)i * 2))) return;
    }

    // Format 12 gives each group's codes consecutive glyphs from the group's glyph on, format 13 gives them all that one.
    private static void ReadGroups<TSink>(TableReader font, int subtable, bool sequential, ref Budget<TSink> codes) where TSink : struct, ICmapSink
    {
        long groups = font.U32(subtable + GroupCountField);
        for (long i = 0; i < groups; i++)
        {
            int group = subtable + GroupsField + (int)i * GroupLength;
            long start = font.U32(group), end = font.U32(group + GroupEndCodeField), glyph = font.U32(group + GroupGlyphField);
            if (end > MaxCodePoint || start > end) continue;
            for (long code = start; code <= end; code++)
                if (!codes.Map(code, sequential ? glyph + code - start : glyph)) return;
        }
    }

    /// <summary>
    /// The variation sequences of the format 14 subtable at <paramref name="subtable"/>: for each selector, the characters
    /// it gives a glyph of their own (non-default) and those it leaves with their usual glyph (default). Records and
    /// tables outside the subtable's length are left out, glyphs the font does not have are ignored, and no more than
    /// <see cref="MaxCodes"/> mappings and ranges are read in all.
    /// </summary>
    public static VariationSequences ReadVariationSequences(TableReader font, int subtable, int numGlyphs)
    {
        var sequences = new VariationSequences();
        long length = Math.Min(font.U32(subtable + VariationLengthField), font.Data.Length - (long)subtable);
        long selectors = Math.Min(font.U32(subtable + SelectorCountField), (length - SelectorRecordsField) / SelectorRecordLength);
        int left = MaxCodes;
        for (int i = 0; i < selectors; i++)
        {
            int record = subtable + SelectorRecordsField + i * SelectorRecordLength;
            int selector = U24(font, record);
            uint defaults = font.U32(record + DefaultOffsetField), nonDefaults = font.U32(record + NonDefaultOffsetField);
            if (defaults != 0 && defaults <= length - 4)
            {
                int table = subtable + (int)defaults;
                long ranges = Math.Min(font.U32(table), (length - defaults - 4) / RangeLength);
                for (int r = 0; r < ranges && left-- > 0; r++)
                {
                    int range = table + 4 + r * RangeLength, start = U24(font, range);
                    sequences.AddDefault(selector, start, start + font.Data[range + RangeCountField]);
                }
            }
            if (nonDefaults != 0 && nonDefaults <= length - 4)
            {
                int table = subtable + (int)nonDefaults;
                long mappings = Math.Min(font.U32(table), (length - nonDefaults - 4) / MappingLength);
                for (int m = 0; m < mappings && left-- > 0; m++)
                {
                    int mapping = table + 4 + m * MappingLength, glyph = font.U16(mapping + MappingGlyphField);
                    if (glyph > 0 && glyph < numGlyphs) sequences.AddGlyph(selector, U24(font, mapping), glyph);
                }
            }
        }
        sequences.Seal();
        return sequences;
    }

    private static int U24(TableReader font, int offset) => font.Data[offset] << 16 | font.U16(offset + 1);
}
