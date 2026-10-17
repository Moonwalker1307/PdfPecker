namespace PdfPecker.Fonts;

/// <summary>The character map: the cmap subtable the font is read through, and the widths by code point built from it.</summary>
internal sealed partial class FontProgram
{
    // cmap header: the number of encoding records, each a platform, an encoding and a subtable offset.
    private const int CmapNumTablesField = 2, CmapRecordsField = 4, CmapRecordLength = 8;
    private const int CmapRecordEncodingField = 2, CmapRecordOffsetField = 4;

    // Platforms and encodings, scored by how much of Unicode they cover; a Windows Symbol map scores SymbolMapScore.
    private const int UnicodePlatform = 0, MacintoshPlatform = 1, WindowsPlatform = 3;
    private const int UnicodeFullEncoding = 4, UnicodeVariationEncoding = 6;
    private const int WindowsSymbolEncoding = 0, WindowsUnicodeBmpEncoding = 1, WindowsUnicodeFullEncoding = 10;
    private const int MacintoshRomanEncoding = 0;
    private const int SymbolMapScore = 2;
    // Symbol fonts map their glyphs in the private use range at U+F020..U+F0FF.
    private const int SymbolCodeBase = 0xF000, SymbolFirstCode = 0x20, SymbolLastCode = 0xFF;

    // Subtable formats: byte encoding, segment mapping to delta values, trimmed table and segmented coverage.
    private const int ByteEncodingFormat = 0, SegmentMappingFormat = 4, TrimmedTableFormat = 6, SegmentedCoverageFormat = 12;
    private const int ByteEncodingGlyphsField = 6, ByteEncodingCodes = 256;
    private const int TrimmedTableFirstCodeField = 6, TrimmedTableCountField = 8, TrimmedTableGlyphsField = 10;
    // Format 4: segCountX2, then four parallel arrays (end codes, a reserved pad, start codes, deltas, range offsets).
    private const int SegmentCountX2Field = 6, EndCodesField = 14, ReservedPadLength = 2;
    private const int LastSegmentEndCode = 0xFFFF;
    // Format 12: the group count, then groups of a start code, an end code and the glyph of the start code.
    private const int GroupCountField = 12, GroupsField = 16, GroupLength = 12;
    private const int GroupEndCodeField = 4, GroupStartGlyphField = 8;

    private const int MaxCodePoint = 0x10FFFF;

    /// <summary>Advance widths in 1/1000 em indexed by BMP code point.</summary>
    public short[] CharWidths => _charWidths ?? BuildCharWidths();

    private short[] BuildCharWidths()
    {
        lock (_lock)
        {
            if (_charWidths != null) return _charWidths;
            var bmp = _bmp ?? BuildCharMap();
            var glyphWidths = new short[Math.Max(1, NumGlyphs)];
            for (int g = 0; g < glyphWidths.Length; g++) glyphWidths[g] = (short)Math.Clamp(Advance1000(g), short.MinValue, short.MaxValue);
            var widths = new short[65536];
            for (int c = 0; c < widths.Length; c++) widths[c] = glyphWidths[bmp[c]];
            _charWidths = widths;
            return widths;
        }
    }

    private ushort[] BuildCharMap()
    {
        lock (_lock)
        {
            if (_bmp != null) return _bmp;
            var map = new CharMap(new ushort[65536], NumGlyphs);
            if (Table("cmap") is { } cmap)
                try
                {
                    var (subtable, score) = SelectCmapSubtable(cmap);
                    if (subtable >= 0)
                    {
                        ReadCmapSubtable(subtable, ref map);
                        if (score == SymbolMapScore) map.MapSymbolsToLatin1();
                    }
                }
                catch (Exception ex) when (IsFormatError(ex))
                {
                    // A damaged character map: keep what was read before the damage.
                }
            _astral = map.Astral;
            _bmp = map.Bmp;
            return map.Bmp;
        }
    }

    // The subtable with the widest Unicode coverage, in a format this reader knows; -1 when there is none.
    private (int Offset, int Score) SelectCmapSubtable((int Offset, int Length) cmap)
    {
        int best = -1, bestScore = -1;
        int count = Reader.U16(cmap.Offset + CmapNumTablesField);
        for (int i = 0; i < count; i++)
        {
            int record = cmap.Offset + CmapRecordsField + i * CmapRecordLength;
            int platform = Reader.U16(record), encoding = Reader.U16(record + CmapRecordEncodingField);
            int subtable = cmap.Offset + (int)Reader.U32(record + CmapRecordOffsetField);
            if (subtable + 2 > cmap.Offset + cmap.Length) continue;
            int format = Reader.U16(subtable);
            if (format is not (ByteEncodingFormat or SegmentMappingFormat or TrimmedTableFormat or SegmentedCoverageFormat)) continue;
            int score = (platform, encoding) switch
            {
                (WindowsPlatform, WindowsUnicodeFullEncoding) => 6,
                (UnicodePlatform, UnicodeFullEncoding) or (UnicodePlatform, UnicodeVariationEncoding) => 5,
                (WindowsPlatform, WindowsUnicodeBmpEncoding) => 4,
                (UnicodePlatform, _) => 3,
                (WindowsPlatform, WindowsSymbolEncoding) => SymbolMapScore,
                (MacintoshPlatform, MacintoshRomanEncoding) => 1,
                _ => -1,
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = subtable;
            }
        }
        return (best, bestScore);
    }

    private void ReadCmapSubtable(int subtable, ref CharMap map)
    {
        switch (Reader.U16(subtable))
        {
            case ByteEncodingFormat: ReadByteEncoding(subtable, ref map); break;
            case TrimmedTableFormat: ReadTrimmedTable(subtable, ref map); break;
            case SegmentMappingFormat: ReadSegmentMapping(subtable, ref map); break;
            case SegmentedCoverageFormat: ReadSegmentedCoverage(subtable, ref map); break;
        }
    }

    private void ReadByteEncoding(int subtable, ref CharMap map)
    {
        for (int code = 0; code < ByteEncodingCodes; code++) map.Map(code, Data[subtable + ByteEncodingGlyphsField + code]);
    }

    private void ReadTrimmedTable(int subtable, ref CharMap map)
    {
        int first = Reader.U16(subtable + TrimmedTableFirstCodeField), count = Reader.U16(subtable + TrimmedTableCountField);
        for (int i = 0; i < count; i++) map.Map(first + i, Reader.U16(subtable + TrimmedTableGlyphsField + i * 2));
    }

    // Each segment maps a code range either by adding a delta or through the glyph id array that follows the range
    // offsets, which a range offset addresses relative to its own position.
    private void ReadSegmentMapping(int subtable, ref CharMap map)
    {
        int segmentsX2 = Reader.U16(subtable + SegmentCountX2Field);
        int endCodes = subtable + EndCodesField, startCodes = endCodes + segmentsX2 + ReservedPadLength;
        int deltas = startCodes + segmentsX2, rangeOffsets = deltas + segmentsX2;
        for (int s = 0; s < segmentsX2; s += 2)
        {
            int end = Reader.U16(endCodes + s), start = Reader.U16(startCodes + s);
            int delta = Reader.S16(deltas + s), rangeOffset = Reader.U16(rangeOffsets + s);
            if (start > end) continue;
            for (int code = start; code <= end && code != LastSegmentEndCode; code++)
            {
                int glyph;
                if (rangeOffset == 0) glyph = (code + delta) & 0xFFFF;
                else
                {
                    int glyphAddress = rangeOffsets + s + rangeOffset + (code - start) * 2;
                    if (glyphAddress + 2 > Data.Length) break;
                    glyph = Reader.U16(glyphAddress);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                map.Map(code, glyph);
            }
        }
    }

    private void ReadSegmentedCoverage(int subtable, ref CharMap map)
    {
        long groups = Reader.U32(subtable + GroupCountField);
        for (long i = 0; i < groups; i++)
        {
            int group = subtable + GroupsField + (int)i * GroupLength;
            long start = Reader.U32(group), end = Reader.U32(group + GroupEndCodeField), startGlyph = Reader.U32(group + GroupStartGlyphField);
            if (end > MaxCodePoint || start > end) continue;
            for (long code = start; code <= end; code++) map.Map((int)code, (int)(startGlyph + code - start));
        }
    }

    /// <summary>The map being built: the BMP as an array, the supplementary planes in a dictionary made on demand.</summary>
    private struct CharMap(ushort[] bmp, int numGlyphs)
    {
        public readonly ushort[] Bmp => bmp;
        public Dictionary<int, ushort>? Astral { get; private set; }

        /// <summary>Maps a code point to a glyph; .notdef, glyphs the font does not have and codes outside Unicode are ignored.</summary>
        public void Map(int code, int glyph)
        {
            if (glyph <= 0 || glyph >= numGlyphs) return;
            if (code < 0x10000)
            {
                if (code >= 0) bmp[code] = (ushort)glyph;
            }
            else if (code <= MaxCodePoint) (Astral ??= new Dictionary<int, ushort>())[code] = (ushort)glyph;
        }

        /// <summary>Makes the glyphs of a symbol font reachable from plain ASCII and Latin-1 too.</summary>
        public readonly void MapSymbolsToLatin1()
        {
            for (int code = SymbolFirstCode; code <= SymbolLastCode; code++)
                if (bmp[code] == 0) bmp[code] = bmp[SymbolCodeBase + code];
        }
    }
}
