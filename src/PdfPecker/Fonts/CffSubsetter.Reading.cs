using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>Reading the parts of the font that the subset copies or rewrites.</summary>
internal static partial class CffSubsetter
{
    // The header: major version and header size. Only CFF version 1 is subset; CFF2 is embedded whole.
    private const int MinHeaderLength = 4, CffMajorVersion = 1;
    private const int MajorVersionField = 0, HeaderSizeField = 2;

    // Charset offsets up to this one stand for the predefined ISOAdobe, Expert and ExpertSubset charsets.
    private const int LastPredefinedCharset = 2;

    // A charset of format 0 has a string id for every glyph after .notdef; formats 1 and 2 have ranges of a first
    // string id and the number of glyphs that follow it, in one or two bytes, until every glyph is covered.
    private const int CharsetPerGlyph = 0, CharsetRanges8 = 1, CharsetRanges16 = 2;
    private const int CharsetFormatLength = 1, StringIdLength = 2, RangeCountField = 2;
    private const int Range8Length = 3, Range16Length = 4;

    // An FDSelect of format 0 has a Font DICT number for every glyph; format 3 has ranges, each a first glyph and a
    // Font DICT number, closed by a sentinel glyph.
    private const int FdSelectPerGlyph = 0, FdSelectRanges = 3;
    private const int FdSelectFormatLength = 1, FdRangeCountLength = 2, FdRangeLength = 3, FdRangeFdField = 2;
    private const int FdSentinelLength = 2;

    /// <summary>
    /// The font's INDEXes and DICTs. The charset and FDSelect are the bytes they are encoded as, and a CID-keyed font
    /// has its Font DICTs; every font has a Private DICT block for each Font DICT, or one for the Top DICT.
    /// </summary>
    private sealed class SourceFont
    {
        private SourceFont(byte[] data, CffIndex names, CffIndex strings, CffIndex globalSubrs, Dict top, CffIndex charStrings)
        {
            Data = data;
            Names = names;
            Strings = strings;
            GlobalSubrs = globalSubrs;
            Top = top;
            CharStrings = charStrings;
        }

        public byte[] Data { get; }
        public int HeaderLength => Data[HeaderSizeField];
        public CffIndex Names { get; }
        public CffIndex Strings { get; }
        public CffIndex GlobalSubrs { get; }
        public Dict Top { get; }
        public CffIndex CharStrings { get; }

        /// <summary>A charset of its own, or null for none or a predefined one.</summary>
        public ReadOnlyMemory<byte>? Charset { get; private set; }

        public ReadOnlyMemory<byte>? FdSelect { get; private set; }

        /// <summary>The Font DICTs of a CID-keyed font; null for a name-keyed one.</summary>
        public List<Dict>? FontDicts { get; private set; }

        public bool IsCidKeyed => FontDicts != null;

        public List<PrivateBlock> Privates { get; } = new();

        /// <summary>The CFF table's parts, or null when it is not a CFF table this subsets.</summary>
        public static SourceFont? Read(byte[] data)
        {
            if (data.Length < MinHeaderLength || data[MajorVersionField] != CffMajorVersion) return null;
            int pos = data[HeaderSizeField];
            var names = CffIndex.Read(data, ref pos);
            var topDicts = CffIndex.Read(data, ref pos);
            var strings = CffIndex.Read(data, ref pos);
            var globalSubrs = CffIndex.Read(data, ref pos);
            if (topDicts.Count != 1) return null;
            var top = Dict.Parse(data, topDicts.CheckedRange(0));

            int charStringsAt = top.Operand(CharStringsOperator);
            if (charStringsAt <= 0) return null;
            var charStrings = CffIndex.Read(data, ref charStringsAt);

            var font = new SourceFont(data, names, strings, globalSubrs, top, charStrings);
            int charsetAt = top.Operand(CharsetOperator);
            if (charsetAt > LastPredefinedCharset) font.Charset = data.AsMemory(charsetAt, CharsetLength(data, charsetAt, charStrings.Count));
            // The FDArray makes a font CID-keyed, whether or not it has an offset.
            if (top.Contains(FdArrayOperator)) font.ReadFontDicts();
            else font.Privates.Add(PrivateBlock.Read(data, top));
            return font;
        }

        private void ReadFontDicts()
        {
            int fdSelectAt = Top.Operand(FdSelectOperator);
            FdSelect = Data.AsMemory(fdSelectAt, FdSelectLength(Data, fdSelectAt, CharStrings.Count));
            int fdArrayAt = Top.Operand(FdArrayOperator);
            var fdArray = CffIndex.Read(Data, ref fdArrayAt);
            FontDicts = new List<Dict>(fdArray.Count);
            for (int i = 0; i < fdArray.Count; i++)
            {
                var fontDict = Dict.Parse(Data, fdArray.CheckedRange(i));
                FontDicts.Add(fontDict);
                Privates.Add(PrivateBlock.Read(Data, fontDict));
            }
        }

        /// <summary>The Font DICT of a glyph; 0 for a name-keyed font and for a glyph that no range covers.</summary>
        public int FontDictOf(int glyph)
        {
            if (FdSelect is not { } memory) return 0;
            var fdSelect = memory.Span;
            if (fdSelect[0] == FdSelectPerGlyph) return fdSelect[FdSelectFormatLength + glyph];
            int ranges = fdSelect[FdSelectFormatLength] << 8 | fdSelect[FdSelectFormatLength + 1];
            for (int i = 0; i < ranges; i++)
            {
                int range = FdSelectFormatLength + FdRangeCountLength + i * FdRangeLength;
                int first = fdSelect[range] << 8 | fdSelect[range + 1];
                // The next range's first glyph, or the sentinel, ends this one.
                int next = fdSelect[range + FdRangeLength] << 8 | fdSelect[range + FdRangeLength + 1];
                if (glyph >= first && glyph < next) return fdSelect[range + FdRangeFdField];
            }
            return 0;
        }

        private static int CharsetLength(byte[] data, int offset, int glyphCount)
        {
            int format = data[offset];
            if (format == CharsetPerGlyph) return CharsetFormatLength + (glyphCount - 1) * StringIdLength;
            if (format is not (CharsetRanges8 or CharsetRanges16)) throw new FormatException("Unknown CFF charset format.");
            var reader = new TableReader(data);
            int pos = offset + CharsetFormatLength;
            // .notdef is not in the charset, so the ranges start with the second glyph.
            for (int covered = 1; covered < glyphCount;)
            {
                int more = format == CharsetRanges8 ? data[pos + RangeCountField] : reader.U16(pos + RangeCountField);
                pos += format == CharsetRanges8 ? Range8Length : Range16Length;
                covered += more + 1;
            }
            return pos - offset;
        }

        private static int FdSelectLength(byte[] data, int offset, int glyphCount)
        {
            int format = data[offset];
            if (format == FdSelectPerGlyph) return FdSelectFormatLength + glyphCount;
            if (format == FdSelectRanges)
                return FdSelectFormatLength + FdRangeCountLength + new TableReader(data).U16(offset + FdSelectFormatLength) * FdRangeLength + FdSentinelLength;
            throw new FormatException("Unknown CFF FDSelect format.");
        }
    }

    /// <summary>
    /// A Private DICT: its size, the bytes from its start up to its local subroutines (which keep their offset from
    /// the DICT), and the subroutines. Without subroutines the bytes are the DICT alone.
    /// </summary>
    private readonly record struct PrivateBlock(int DictSize, int PrefixStart, int PrefixLength, CffIndex? Subrs)
    {
        /// <summary>The Private DICT a Top or Font DICT points at; only its first Private entry counts.</summary>
        public static PrivateBlock Read(byte[] data, Dict dict)
        {
            if (dict.Find(PrivateOperator) is not { Operands.Length: >= 2 } entry) return default;
            int size = (int)entry.Operands[0], offset = (int)entry.Operands[1];
            if ((uint)offset > (uint)data.Length || (uint)size > (uint)(data.Length - offset)) throw new FormatException("Damaged CFF Private DICT.");
            var privateDict = Dict.Parse(data, (offset, offset + size));
            // The subroutines' offset counts from the Private DICT; one inside the DICT is not taken as subroutines.
            int subrsOffset = privateDict.Operand(SubrsOperator);
            if (subrsOffset < size) return new PrivateBlock(size, offset, size, null);
            int subrsAt = offset + subrsOffset;
            var subrs = CffIndex.Read(data, ref subrsAt);
            return new PrivateBlock(size, offset, subrsOffset, subrs);
        }

        public ReadOnlySpan<byte> Prefix(byte[] data) => data.AsSpan(PrefixStart, PrefixLength);
    }

    /// <summary>An operator, its integer operands (a real one reads as 0) and the bytes it was encoded as.</summary>
    private readonly record struct DictEntry(int Operator, double[] Operands, int Start, int End)
    {
        public ReadOnlySpan<byte> Encoded(byte[] data) => data.AsSpan(Start, End - Start);
    }

    /// <summary>
    /// A DICT's entries in order, each with the bytes it was encoded as, so that the entries this does not rewrite are
    /// copied exactly. None of the operators it rewrites or looks up take a real number.
    /// </summary>
    private readonly struct Dict(List<DictEntry> entries)
    {
        public List<DictEntry> Entries => entries;

        public static Dict Parse(byte[] data, (int Start, int End) range)
        {
            var (start, end) = range;
            var entries = new List<DictEntry>();
            var operands = new List<double>();
            var reader = new CffDictReader(data, start, end, cff2: false);
            int entryStart = start;
            while (reader.Next())
            {
                // A token cut off by the end of the DICT, or a reserved byte, is a damaged font.
                if (reader.Position > end || reader.Kind == CffDictToken.Reserved) throw new FormatException("Damaged CFF DICT.");
                if (reader.Kind == CffDictToken.Operand)
                {
                    operands.Add(reader.IsReal ? 0 : reader.Operand);
                    continue;
                }
                entries.Add(new DictEntry(reader.Operator, operands.ToArray(), entryStart, reader.Position));
                operands.Clear();
                entryStart = reader.Position;
            }
            return new Dict(entries);
        }

        public bool Contains(int op) => Find(op) != null;

        /// <summary>The first entry of the operator, whatever its operands.</summary>
        public DictEntry? Find(int op)
        {
            foreach (var entry in entries)
                if (entry.Operator == op) return entry;
            return null;
        }

        /// <summary>The first operand of the first entry of the operator that has one, or -1.</summary>
        public int Operand(int op)
        {
            foreach (var entry in entries)
                if (entry.Operator == op && entry.Operands.Length > 0) return (int)entry.Operands[0];
            return -1;
        }
    }
}
