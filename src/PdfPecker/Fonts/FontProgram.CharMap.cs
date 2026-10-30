namespace PdfPecker.Fonts;

/// <summary>The character map: the cmap subtable the font is read through, and the widths by code point built from it.</summary>
internal sealed partial class FontProgram
{
    // Platforms and encodings, scored by how much of Unicode they cover; a Windows Symbol map scores SymbolMapScore.
    private const int UnicodePlatform = 0, MacintoshPlatform = 1, WindowsPlatform = 3;
    private const int UnicodeFullEncoding = 4, UnicodeVariationEncoding = 6;
    private const int WindowsSymbolEncoding = 0, WindowsUnicodeBmpEncoding = 1, WindowsUnicodeFullEncoding = 10;
    private const int MacintoshRomanEncoding = 0;
    private const int SymbolMapScore = 2;
    // Symbol fonts map their glyphs in the private use range at U+F020..U+F0FF.
    private const int SymbolCodeBase = 0xF000, SymbolFirstCode = 0x20, SymbolLastCode = 0xFF;

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
                        CmapTable.Read(Reader, subtable, ref map);
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

    // The subtable with the widest Unicode coverage, in a format whose codes are characters; -1 when there is none.
    // Format 2 is left out: its codes are those of a double-byte CJK encoding.
    private (int Offset, int Score) SelectCmapSubtable((int Offset, int Length) cmap)
    {
        int best = -1, bestScore = -1;
        foreach (var subtable in CmapTable.Subtables(Reader, cmap.Offset, cmap.Length))
        {
            if (subtable.Format is not (CmapTable.ByteEncodingFormat or CmapTable.SegmentMappingFormat or CmapTable.TrimmedTableFormat
                or CmapTable.TrimmedArrayFormat or CmapTable.SegmentedCoverageFormat or CmapTable.ManyToOneRangeFormat)) continue;
            int score = (subtable.Platform, subtable.Encoding) switch
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
                best = subtable.Offset;
            }
        }
        return (best, bestScore);
    }

    /// <summary>The map being built: the BMP as an array, the supplementary planes in a dictionary made on demand.</summary>
    private struct CharMap(ushort[] bmp, int numGlyphs) : ICmapSink
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
