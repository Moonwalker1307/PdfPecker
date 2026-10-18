using System.Runtime.InteropServices;

namespace PdfPecker.Rendering.Images;

// Huffman tables (Annex B): the standard tables, table segments (7.4.13), the symbol ID codes of Huffman-coded text
// regions (7.4.3.1.7) and the bit reader for Huffman-coded data.
internal sealed partial class Jbig2Decoder
{
    /// <summary>What a table line codes: a range above its low value, the lower or upper open range, or OOB.</summary>
    private enum HuffmanRange : byte
    {
        Normal,
        Lower,
        Upper,
        OutOfBand,
    }

    /// <summary>One line of a Huffman table (B.2): a prefix length, and the range of values its suffix bits select.</summary>
    private readonly record struct HuffmanLine(int PrefixLength, int RangeLength, int RangeLow, HuffmanRange Kind = HuffmanRange.Normal);

    private static HuffmanLine Line(int low, int prefixLength, int rangeLength) => new(prefixLength, rangeLength, low);

    // The open ranges and OOB lines of the standard tables, and of table segments, always take 32 suffix bits and none.
    private static HuffmanLine Lower(int low, int prefixLength) => new(prefixLength, 32, low, HuffmanRange.Lower);

    private static HuffmanLine Upper(int low, int prefixLength) => new(prefixLength, 32, low, HuffmanRange.Upper);

    private static HuffmanLine Oob(int prefixLength) => new(prefixLength, 0, 0, HuffmanRange.OutOfBand);

    private sealed class HuffTable
    {
        private readonly Dictionary<long, HuffmanLine> _byCode = new();
        private readonly int _maxLen;

        public HuffTable(ReadOnlySpan<HuffmanLine> lines)
        {
            // Canonical prefix codes in table order (B.3).
            int maxLen = 0;
            foreach (var l in lines) maxLen = Math.Max(maxLen, l.PrefixLength);
            var count = new int[maxLen + 2];
            foreach (var l in lines) if (l.PrefixLength > 0) count[l.PrefixLength]++;
            int code = 0;
            for (int len = 1; len <= maxLen; len++)
            {
                code = (code + count[len - 1]) << 1;
                if (len == 1) code = 0;
                int c = code;
                foreach (var l in lines)
                {
                    if (l.PrefixLength != len) continue;
                    _byCode[Key(len, c++)] = l;
                }
            }
            _maxLen = maxLen;
        }

        private static long Key(int length, int code) => (long)length << 32 | (uint)code;

        /// <summary>Decodes a value; returns false for OOB.</summary>
        public bool Decode(BitReader br, out int value)
        {
            int code = 0;
            for (int len = 1; len <= _maxLen; len++)
            {
                code = code << 1 | br.ReadBit();
                if (!_byCode.TryGetValue(Key(len, code), out var l)) continue;
                if (l.Kind == HuffmanRange.OutOfBand)
                {
                    value = 0;
                    return false;
                }
                long v = l.Kind == HuffmanRange.Lower
                    ? l.RangeLow - (long)br.ReadBits(l.RangeLength)
                    : l.RangeLow + (long)br.ReadBits(l.RangeLength);
                value = (int)Math.Clamp(v, int.MinValue, int.MaxValue);
                return true;
            }
            throw new InvalidDataException("Bad JBIG2 Huffman code.");
        }
    }

    private static HuffTable Table(params ReadOnlySpan<HuffmanLine> lines) => new(lines);

    // Tables B.1 to B.15, by their number.
    private static readonly Lazy<HuffTable[]> Standard = new(() => new[]
    {
        null!,
        // B.1
        Table(Line(0, 1, 4), Line(16, 2, 8), Line(272, 3, 16), Upper(65808, 3)),
        // B.2
        Table(Line(0, 1, 0), Line(1, 2, 0), Line(2, 3, 0), Line(3, 4, 3), Line(11, 5, 6), Upper(75, 6), Oob(6)),
        // B.3
        Table(Line(-256, 8, 8), Line(0, 1, 0), Line(1, 2, 0), Line(2, 3, 0), Line(3, 4, 3), Line(11, 5, 6),
            Lower(-257, 8), Upper(75, 7), Oob(6)),
        // B.4
        Table(Line(1, 1, 0), Line(2, 2, 0), Line(3, 3, 0), Line(4, 4, 3), Line(12, 5, 6), Upper(76, 5)),
        // B.5
        Table(Line(-255, 7, 8), Line(1, 1, 0), Line(2, 2, 0), Line(3, 3, 0), Line(4, 4, 3), Line(12, 5, 6),
            Lower(-256, 7), Upper(76, 6)),
        // B.6
        Table(Line(-2048, 5, 10), Line(-1024, 4, 9), Line(-512, 4, 8), Line(-256, 4, 7), Line(-128, 5, 6),
            Line(-64, 5, 5), Line(-32, 4, 5), Line(0, 2, 7), Line(128, 3, 7), Line(256, 3, 8), Line(512, 4, 9),
            Line(1024, 4, 10), Lower(-2049, 6), Upper(2048, 6)),
        // B.7
        Table(Line(-1024, 4, 9), Line(-512, 3, 8), Line(-256, 4, 7), Line(-128, 5, 6), Line(-64, 5, 5),
            Line(-32, 4, 5), Line(0, 4, 5), Line(32, 5, 5), Line(64, 5, 6), Line(128, 4, 7), Line(256, 3, 8),
            Line(512, 3, 9), Line(1024, 3, 10), Lower(-1025, 5), Upper(2048, 5)),
        // B.8
        Table(Line(-15, 8, 3), Line(-7, 9, 1), Line(-5, 8, 1), Line(-3, 9, 0), Line(-2, 7, 0), Line(-1, 4, 0),
            Line(0, 2, 1), Line(2, 5, 0), Line(3, 6, 0), Line(4, 3, 4), Line(20, 6, 1), Line(22, 4, 4),
            Line(38, 4, 5), Line(70, 5, 6), Line(134, 5, 7), Line(262, 6, 7), Line(390, 7, 8), Line(646, 6, 10),
            Lower(-16, 9), Upper(1670, 9), Oob(2)),
        // B.9
        Table(Line(-31, 8, 4), Line(-15, 9, 2), Line(-11, 8, 2), Line(-7, 9, 1), Line(-5, 7, 1), Line(-3, 4, 1),
            Line(-1, 3, 1), Line(1, 3, 1), Line(3, 5, 1), Line(5, 6, 1), Line(7, 3, 5), Line(39, 6, 2),
            Line(43, 4, 5), Line(75, 4, 6), Line(139, 5, 7), Line(267, 5, 8), Line(523, 6, 8), Line(779, 7, 9),
            Line(1291, 6, 11), Lower(-32, 9), Upper(3339, 9), Oob(2)),
        // B.10
        Table(Line(-21, 7, 4), Line(-5, 8, 0), Line(-4, 7, 0), Line(-3, 5, 0), Line(-2, 2, 2), Line(2, 5, 0),
            Line(3, 6, 0), Line(4, 7, 0), Line(5, 8, 0), Line(6, 2, 6), Line(70, 5, 5), Line(102, 6, 5),
            Line(134, 6, 6), Line(198, 6, 7), Line(326, 6, 8), Line(582, 6, 9), Line(1094, 6, 10), Line(2118, 7, 11),
            Lower(-22, 8), Upper(4166, 8), Oob(2)),
        // B.11
        Table(Line(1, 1, 0), Line(2, 2, 1), Line(4, 4, 0), Line(5, 4, 1), Line(7, 5, 1), Line(9, 5, 2),
            Line(13, 6, 2), Line(17, 7, 2), Line(21, 7, 3), Line(29, 7, 4), Line(45, 7, 5), Line(77, 7, 6),
            Upper(141, 7)),
        // B.12
        Table(Line(1, 1, 0), Line(2, 2, 0), Line(3, 3, 1), Line(5, 5, 0), Line(6, 5, 1), Line(8, 6, 1),
            Line(10, 7, 0), Line(11, 7, 1), Line(13, 7, 2), Line(17, 7, 3), Line(25, 7, 4), Line(41, 8, 5),
            Upper(73, 8)),
        // B.13
        Table(Line(1, 1, 0), Line(2, 3, 0), Line(3, 4, 0), Line(4, 5, 0), Line(5, 4, 1), Line(7, 3, 3),
            Line(15, 6, 1), Line(17, 6, 2), Line(21, 6, 3), Line(29, 6, 4), Line(45, 6, 5), Line(77, 7, 6),
            Upper(141, 7)),
        // B.14
        Table(Line(-2, 3, 0), Line(-1, 3, 0), Line(0, 1, 0), Line(1, 3, 0), Line(2, 3, 0)),
        // B.15
        Table(Line(-24, 7, 4), Line(-8, 6, 2), Line(-4, 5, 1), Line(-2, 4, 0), Line(-1, 3, 0), Line(0, 1, 0),
            Line(1, 3, 0), Line(2, 4, 0), Line(3, 5, 1), Line(5, 6, 2), Line(9, 7, 4), Lower(-25, 7), Upper(25, 7)),
    });

    private static HuffTable Std(int n) => Standard.Value[n];

    /// <summary>
    /// The standard table that <paramref name="selector"/> names among <paramref name="standard"/>, or for a selector
    /// past them the next custom table.
    /// </summary>
    private static HuffTable SelectTable(int selector, CustomTables custom, params ReadOnlySpan<int> standard) =>
        selector < standard.Length ? Std(standard[selector]) : custom.Next();

    /// <summary>The table segments a segment refers to, which its selectors for custom tables use up in order.</summary>
    private sealed class CustomTables(List<HuffTable> tables)
    {
        private int _next;

        public HuffTable Next() => _next < tables.Count ? tables[_next++] : throw new InvalidDataException("Missing JBIG2 custom table.");
    }

    // ------------------------------------------------------------------ table segments (7.4.13)

    private const int TableSegmentHeaderLength = 9;
    private const int MaxTableLines = 1 << 16;

    /// <summary>The flags byte of a table segment (7.4.13.1): OOB, and the bit sizes of the prefix and range lengths.</summary>
    private readonly record struct TableFlags(int Bits)
    {
        public bool HasOutOfBand => (Bits & 1) != 0;
        public int PrefixBits => ((Bits >> 1) & 7) + 1;
        public int RangeBits => ((Bits >> 4) & 7) + 1;
    }

    private static HuffTable ReadTableSegment(Segment seg)
    {
        if (seg.Length < TableSegmentHeaderLength) throw new InvalidDataException("Short JBIG2 table segment.");
        var reader = seg.Reader();
        var flags = new TableFlags(reader.U8());
        int low = reader.S32(), high = reader.S32();
        var br = new BitReader(seg.Data, reader.Position, seg.End);
        var lines = new List<HuffmanLine>();
        long cur = low;
        while (cur < high)
        {
            if (lines.Count > MaxTableLines || br.AtEnd) throw new InvalidDataException("Bad JBIG2 table segment.");
            int prefixLength = (int)br.ReadBits(flags.PrefixBits), rangeLength = (int)br.ReadBits(flags.RangeBits);
            lines.Add(Line((int)cur, prefixLength, rangeLength));
            cur += 1L << rangeLength;
        }
        lines.Add(Lower(low - 1, (int)br.ReadBits(flags.PrefixBits)));
        lines.Add(Upper(high, (int)br.ReadBits(flags.PrefixBits)));
        if (flags.HasOutOfBand) lines.Add(Oob((int)br.ReadBits(flags.PrefixBits)));
        return new HuffTable(CollectionsMarshal.AsSpan(lines));
    }

    // ------------------------------------------------------------------ symbol ID codes (7.4.3.1.7)

    private const int RunCodeCount = 35;
    private const int RunCodePrefixBits = 4;
    private const int RepeatPreviousCode = 32, ShortZeroRunCode = 33; // 34 is the long run of zeros

    private static HuffTable ReadSymbolCodes(BitReader br, int numSyms)
    {
        if (numSyms > MaxSymbols * 4) throw new InvalidDataException("Too many JBIG2 symbols.");
        // The code lengths are themselves coded with a table of 35 run codes: lengths 0 to 31 and three repeats.
        var runLines = new HuffmanLine[RunCodeCount];
        for (int i = 0; i < RunCodeCount; i++) runLines[i] = Line(i, (int)br.ReadBits(RunCodePrefixBits), 0);
        var runTable = new HuffTable(runLines);
        var lens = new int[numSyms];
        int n = 0;
        while (n < numSyms)
        {
            if (!runTable.Decode(br, out int code)) throw new InvalidDataException("Bad JBIG2 symbol code lengths.");
            if (code < RepeatPreviousCode)
            {
                lens[n++] = code;
                continue;
            }
            int value, repeat;
            if (code == RepeatPreviousCode)
            {
                if (n == 0) throw new InvalidDataException("Bad JBIG2 run code.");
                value = lens[n - 1];
                repeat = 3 + (int)br.ReadBits(2);
            }
            else if (code == ShortZeroRunCode)
            {
                value = 0;
                repeat = 3 + (int)br.ReadBits(3);
            }
            else
            {
                value = 0;
                repeat = 11 + (int)br.ReadBits(7);
            }
            for (int i = 0; i < repeat && n < numSyms; i++) lens[n++] = value;
        }
        var lines = new HuffmanLine[numSyms];
        for (int i = 0; i < numSyms; i++) lines[i] = Line(i, lens[i], 0);
        return new HuffTable(lines);
    }

    // ------------------------------------------------------------------ bits

    /// <summary>MSB-first bit reader for Huffman-coded data. Past its end it reads zeros.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;

        public BitReader(byte[] data, int position, int end)
        {
            _data = data;
            Position = position;
            End = Math.Min(end, data.Length);
        }

        /// <summary>The byte being read. Skipping coded bitmaps moves it directly.</summary>
        public int Position { get; set; }

        public int End { get; }

        public bool AtEnd => Position >= End;

        public int ReadBit()
        {
            int b = Position < End ? (_data[Position] >> (7 - _bit)) & 1 : 0;
            if (++_bit == 8)
            {
                _bit = 0;
                Position++;
            }
            return b;
        }

        public uint ReadBits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++) v = v << 1 | (uint)ReadBit();
            return v;
        }

        public void Align()
        {
            if (_bit != 0)
            {
                _bit = 0;
                Position++;
            }
        }
    }
}
