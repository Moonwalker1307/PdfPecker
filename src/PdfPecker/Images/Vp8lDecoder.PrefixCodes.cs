namespace PdfPecker.Images;

internal ref partial struct Vp8lDecoder
{
    // A normal code sends the lengths of its symbols' codes prefix coded themselves: up to 19 code length code
    // lengths of 3 bits, in this order, then the lengths, where 16 repeats the previous non-zero length and 17 and 18
    // repeat a zero, each a number of times given by extra bits.
    private static readonly byte[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    private const int NumCodeLengthCodes = 19;
    private const int MinCodeLengthCodes = 4;
    private const int RepeatPreviousCode = 16;
    private const int RepeatZerosLongCode = 18;
    private const int InitialPreviousLength = 8;

    /// <summary>Reads a simple or normal prefix code over the first <paramref name="alphabetSize"/> symbols, or returns null.</summary>
    private PrefixCode? ReadPrefixCode(int alphabetSize)
    {
        var lengths = codeLengths ??= new int[NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits)];
        // A simple code may name any of the first 256 symbols, even in a smaller alphabet.
        Array.Clear(lengths, 0, Math.Max(alphabetSize, NumLiteralCodes));
        bool valid = reader.ReadBit() ? ReadSimpleCodeLengths(lengths) : ReadNormalCodeLengths(lengths, alphabetSize);
        if (!valid || reader.Overrun) return null;
        return PrefixCode.Build(lengths.AsSpan(0, alphabetSize));
    }

    /// <summary>A simple code: one or two symbols, each with a code of one bit.</summary>
    private bool ReadSimpleCodeLengths(int[] lengths)
    {
        bool twoSymbols = reader.ReadBit();
        int first = (int)reader.Read(reader.ReadBit() ? 8 : 1);
        lengths[first] = 1;
        if (twoSymbols) lengths[reader.Read(8)] = 1;
        return true;
    }

    private bool ReadNormalCodeLengths(int[] lengths, int alphabetSize)
    {
        Span<int> codeLengthLengths = stackalloc int[NumCodeLengthCodes];
        codeLengthLengths.Clear();
        int count = (int)reader.Read(4) + MinCodeLengthCodes;
        for (int i = 0; i < count; i++) codeLengthLengths[CodeLengthCodeOrder[i]] = (int)reader.Read(3);
        var lengthCode = PrefixCode.Build(codeLengthLengths);
        if (lengthCode == null) return false;

        // The stream may stop sending lengths early; the remaining symbols are unused.
        int lengthsLeft = alphabetSize;
        if (reader.ReadBit())
        {
            int bits = 2 + 2 * (int)reader.Read(3);
            lengthsLeft = 2 + (int)reader.Read(bits);
            if (lengthsLeft > alphabetSize) return false;
        }
        int symbol = 0, previous = InitialPreviousLength;
        while (symbol < alphabetSize && lengthsLeft-- > 0)
        {
            int length = lengthCode.Read(ref reader);
            if (length < RepeatPreviousCode)
            {
                lengths[symbol++] = length;
                if (length != 0) previous = length;
                continue;
            }
            int repeat = length switch
            {
                RepeatPreviousCode => (int)reader.Read(2) + 3,
                RepeatZerosLongCode => (int)reader.Read(7) + 11,
                _ => (int)reader.Read(3) + 3,
            };
            if (symbol + repeat > alphabetSize) return false;
            lengths.AsSpan(symbol, repeat).Fill(length == RepeatPreviousCode ? previous : 0);
            symbol += repeat;
        }
        return true;
    }

    /// <summary>The five prefix codes of a group: green, length or cache index; red; blue; alpha; and distance.</summary>
    private sealed record PrefixCodeGroup(PrefixCode Green, PrefixCode Red, PrefixCode Blue, PrefixCode Alpha, PrefixCode Distance);

    /// <summary>A canonical prefix code, read through a lookup table on its first bits.</summary>
    private sealed class PrefixCode
    {
        private const int MaxCodeLength = 15;
        private const int LookupBits = 9;

        // An entry of the lookup table holds the symbol above the length of its code. Codes longer than the table
        // has no entry for, and are walked one bit at a time.
        private const int EntryLengthBits = 4;
        private const int EntryLengthMask = (1 << EntryLengthBits) - 1;
        private const int LongCode = -1;

        private readonly int _singleSymbol = -1;
        private readonly int _lookupBits;
        private readonly int[] _lookup = Array.Empty<int>();
        private readonly int[] _lengthCounts = Array.Empty<int>();
        private readonly int[] _sortedSymbols = Array.Empty<int>();

        /// <summary>A code of one symbol, which takes no bits.</summary>
        private PrefixCode(int symbol) => _singleSymbol = symbol;

        private PrefixCode(int lookupBits, int[] lookup, int[] lengthCounts, int[] sortedSymbols)
        {
            _lookupBits = lookupBits;
            _lookup = lookup;
            _lengthCounts = lengthCounts;
            _sortedSymbols = sortedSymbols;
        }

        /// <summary>The number of lookup table entries the code keeps.</summary>
        public int TableSize => _lookup.Length;

        /// <summary>The code with these lengths per symbol (zero for an unused symbol), or null when they are not a complete code.</summary>
        public static PrefixCode? Build(ReadOnlySpan<int> lengths)
        {
            Span<int> counts = stackalloc int[MaxCodeLength + 1];
            counts.Clear();
            int used = 0, last = 0, maxLength = 0;
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0) continue;
                if (length > MaxCodeLength) return null;
                counts[length]++;
                used++;
                last = symbol;
                maxLength = Math.Max(maxLength, length);
            }
            if (used == 0) return null;
            if (used == 1) return new PrefixCode(last);
            if (!IsComplete(counts)) return null;

            // The symbols sorted by code length, and by value within a length, as canonical codes are assigned.
            Span<int> offsets = stackalloc int[MaxCodeLength + 1];
            offsets.Clear();
            for (int length = 1; length < MaxCodeLength; length++) offsets[length + 1] = offsets[length] + counts[length];
            var sorted = new int[used];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] != 0) sorted[offsets[lengths[symbol]]++] = symbol;

            // The lookup table is indexed by the next bits of the stream, which come least significant first, so each
            // code fills every entry that starts with its bits reversed.
            int lookupBits = Math.Min(maxLength, LookupBits);
            var lookup = new int[1 << lookupBits];
            lookup.AsSpan().Fill(LongCode);
            int code = 0, index = 0;
            for (int length = 1; length <= lookupBits; length++)
            {
                for (int k = 0; k < counts[length]; k++, index++, code++)
                {
                    int entry = sorted[index] << EntryLengthBits | length;
                    for (int r = ReverseBits(code, length); r < lookup.Length; r += 1 << length) lookup[r] = entry;
                }
                code <<= 1;
            }
            return new PrefixCode(lookupBits, lookup, counts.ToArray(), sorted);
        }

        /// <summary>Whether the lengths use every code: neither over-subscribed nor leaving codes unused.</summary>
        private static bool IsComplete(ReadOnlySpan<int> counts)
        {
            int left = 1;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                left = (left << 1) - counts[length];
                if (left < 0) return false;
            }
            return left == 0;
        }

        private static int ReverseBits(int code, int length)
        {
            int reversed = 0;
            for (int i = 0; i < length; i++)
            {
                reversed = reversed << 1 | (code & 1);
                code >>= 1;
            }
            return reversed;
        }

        public int Read(ref BitReader reader)
        {
            if (_singleSymbol >= 0) return _singleSymbol;
            int entry = _lookup[reader.Peek(_lookupBits)];
            if (entry != LongCode)
            {
                reader.Skip(entry & EntryLengthMask);
                return entry >> EntryLengthBits;
            }
            return ReadLongCode(ref reader);
        }

        /// <summary>Walks a code longer than the lookup table one bit at a time, from its first bit.</summary>
        private int ReadLongCode(ref BitReader reader)
        {
            int code = 0, first = 0, index = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                code |= (int)reader.Read(1);
                int count = _lengthCounts[length];
                if (code - first < count) return _sortedSymbols[index + code - first];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new InvalidDataException("Bad prefix code.");
        }
    }

    /// <summary>
    /// Reads bits least significant first. Reading past the end yields zeros and sets <see cref="Overrun"/>, which the
    /// decoder checks after each pixel and each prefix code rather than on every read.
    /// </summary>
    private ref struct BitReader
    {
        // A byte is added while the buffer holds no more than this many bits, so it never holds more than 64.
        private const int RefillLimit = 56;

        private readonly ReadOnlySpan<byte> data;
        private int position;
        private ulong bits;
        private int count;

        public BitReader(ReadOnlySpan<byte> data) => this.data = data;

        public bool Overrun { get; private set; }

        public uint Peek(int n)
        {
            if (count < n) Fill();
            return (uint)(bits & ((1UL << n) - 1));
        }

        public void Skip(int n)
        {
            if (n > count)
            {
                Overrun = true;
                bits = 0;
                count = 0;
                return;
            }
            bits >>= n;
            count -= n;
        }

        public uint Read(int n)
        {
            if (n == 0) return 0;
            uint value = Peek(n);
            Skip(n);
            return value;
        }

        public bool ReadBit() => Read(1) == 1;

        private void Fill()
        {
            while (count <= RefillLimit && position < data.Length)
            {
                bits |= (ulong)data[position++] << count;
                count += 8;
            }
        }
    }
}
