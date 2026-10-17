namespace PdfPecker.Rendering;

internal static partial class WebPEncoder
{
    private const int MaxCodeLength = 15;

    // A normal code sends the lengths of its symbols' codes prefix coded themselves: up to 19 code length code
    // lengths of 3 bits, in this order, then the lengths, where 16 repeats the previous non-zero length 3 to 6 times
    // and 17 and 18 repeat a zero 3 to 10 and 11 to 138 times.
    private static readonly byte[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    private const int NumCodeLengthCodes = 19;
    private const int MinCodeLengthCodes = 4;
    private const int MaxCodeLengthCodeLength = 7;
    private const int RepeatPreviousCode = 16;
    private const int RepeatZerosCode = 17;
    private const int RepeatZerosLongCode = 18;
    private const int InitialPreviousLength = 8;

    // A simple code has one or two symbols below 256, the first sent in 1 or 8 bits, the second in 8.
    private const int MaxSimpleSymbol = 256;

    /// <summary>The codes of one alphabet, ready to emit; a code of a single symbol takes no bits.</summary>
    private sealed class PrefixCode
    {
        public readonly uint[] Bits;
        public readonly byte[] Lengths;

        public PrefixCode(int alphabet)
        {
            Bits = new uint[alphabet];
            Lengths = new byte[alphabet];
        }

        public void Put(BitWriter writer, int symbol) => writer.Write(Bits[symbol], Lengths[symbol]);
    }

    /// <summary>Builds a code from a histogram, writes its description and returns it.</summary>
    private static PrefixCode WriteCode(BitWriter writer, ReadOnlySpan<int> histogram)
    {
        var code = new PrefixCode(histogram.Length);
        int used = 0, first = -1, second = -1;
        for (int i = 0; i < histogram.Length; i++)
            if (histogram[i] > 0)
            {
                if (used == 0) first = i;
                else if (used == 1) second = i;
                used++;
            }
        if (used == 0) first = 0;
        if (used <= 2 && first < MaxSimpleSymbol && second < MaxSimpleSymbol)
        {
            WriteSimpleCode(writer, code, used == 2, first, second);
            return code;
        }

        var lengths = new int[histogram.Length];
        BuildLengths(histogram, MaxCodeLength, lengths);
        WriteLengths(writer, lengths);
        if (used > 1) AssignCanonicalCodes(lengths, code);
        return code;
    }

    /// <summary>A simple code: one symbol, which takes no bits, or two, which take one bit each.</summary>
    private static void WriteSimpleCode(BitWriter writer, PrefixCode code, bool twoSymbols, int first, int second)
    {
        writer.Write(1, 1);
        writer.Write(twoSymbols ? 1u : 0u, 1);
        if (first < 2)
        {
            writer.Write(0, 1);
            writer.Write((uint)first, 1);
        }
        else
        {
            writer.Write(1, 1);
            writer.Write((uint)first, 8);
        }
        if (!twoSymbols) return;
        writer.Write((uint)second, 8);
        code.Bits[second] = 1; // the larger symbol takes code 1
        code.Lengths[first] = code.Lengths[second] = 1;
    }

    /// <summary>Writes code lengths with the run-length codes 16 (repeat previous), 17 and 18 (zeros).</summary>
    private static void WriteLengths(BitWriter writer, int[] lengths)
    {
        var symbols = RunLengthCode(lengths);

        var histogram = new int[NumCodeLengthCodes];
        foreach (var (symbol, _) in symbols) histogram[symbol]++;
        var codeLengthLengths = new int[NumCodeLengthCodes];
        BuildLengths(histogram, MaxCodeLengthCodeLength, codeLengthLengths);
        var codeLengthCode = new PrefixCode(NumCodeLengthCodes);
        if (CountUsed(histogram) > 1) AssignCanonicalCodes(codeLengthLengths, codeLengthCode);

        // Trailing zero lengths in the send order are left out, but at least four are sent.
        int count = NumCodeLengthCodes;
        while (count > MinCodeLengthCodes && codeLengthLengths[CodeLengthCodeOrder[count - 1]] == 0) count--;
        writer.Write(0, 1); // normal code
        writer.Write((uint)(count - MinCodeLengthCodes), 4);
        for (int i = 0; i < count; i++) writer.Write((uint)codeLengthLengths[CodeLengthCodeOrder[i]], 3);
        writer.Write(0, 1); // the lengths cover the whole alphabet
        foreach (var (symbol, extra) in symbols)
        {
            codeLengthCode.Put(writer, symbol);
            if (symbol == RepeatPreviousCode) writer.Write((uint)extra, 2);
            else if (symbol == RepeatZerosCode) writer.Write((uint)extra, 3);
            else if (symbol == RepeatZerosLongCode) writer.Write((uint)extra, 7);
        }
    }

    /// <summary>The code lengths as code length symbols, each with the value of its extra bits.</summary>
    private static List<(int Symbol, int Extra)> RunLengthCode(int[] lengths)
    {
        int n = lengths.Length;
        var symbols = new List<(int Symbol, int Extra)>(n / 2 + 16);
        int previous = InitialPreviousLength;
        for (int i = 0; i < n;)
        {
            int length = lengths[i], run = 1;
            while (i + run < n && lengths[i + run] == length) run++;
            i += run;
            if (length == 0)
            {
                while (run >= 3)
                {
                    int repeat = Math.Min(run, run >= 11 ? 138 : 10);
                    symbols.Add(run >= 11 ? (RepeatZerosLongCode, repeat - 11) : (RepeatZerosCode, repeat - 3));
                    run -= repeat;
                }
            }
            else
            {
                if (length != previous)
                {
                    symbols.Add((length, 0));
                    previous = length;
                    run--;
                }
                while (run >= 3)
                {
                    int repeat = Math.Min(run, 6);
                    symbols.Add((RepeatPreviousCode, repeat - 3));
                    run -= repeat;
                }
            }
            for (; run > 0; run--) symbols.Add((length, 0));
        }
        return symbols;
    }

    /// <summary>Canonical codes from lengths, bit-reversed for least-significant-first output.</summary>
    private static void AssignCanonicalCodes(int[] lengths, PrefixCode code)
    {
        Span<int> count = stackalloc int[MaxCodeLength + 1];
        count.Clear();
        foreach (int length in lengths) count[length]++;
        count[0] = 0;
        Span<int> next = stackalloc int[MaxCodeLength + 1];
        next.Clear();
        int c = 0;
        for (int length = 1; length <= MaxCodeLength; length++)
        {
            c = (c + count[length - 1]) << 1;
            next[length] = c;
        }
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length == 0) continue;
            code.Bits[symbol] = Reverse(next[length]++, length);
            code.Lengths[symbol] = (byte)length;
        }
    }

    private static uint Reverse(int value, int bits)
    {
        int reversed = 0;
        for (int k = 0; k < bits; k++)
        {
            reversed = reversed << 1 | (value & 1);
            value >>= 1;
        }
        return (uint)reversed;
    }

    private static int CountUsed(ReadOnlySpan<int> histogram)
    {
        int used = 0;
        foreach (int count in histogram)
            if (count > 0) used++;
        return used;
    }

    /// <summary>Huffman code lengths limited to <paramref name="maxLength"/>, flattening rare counts until they fit.</summary>
    private static void BuildLengths(ReadOnlySpan<int> histogram, int maxLength, int[] lengths)
    {
        Array.Clear(lengths);
        int m = CountUsed(histogram);
        if (m <= 1)
        {
            // A code of one symbol, symbol 0 when none is used; the counts are never negative.
            lengths[m == 0 ? 0 : histogram.IndexOfAnyExcept(0)] = 1;
            return;
        }
        var symbols = new int[m];
        for (int i = 0, k = 0; i < histogram.Length; i++)
            if (histogram[i] > 0) symbols[k++] = i;
        var weight = new long[2 * m - 1];
        var parent = new int[2 * m - 1];
        var depth = new int[2 * m - 1];
        var sortedSymbols = new int[m];
        var keys = new long[m];
        for (long floor = 1; ; floor *= 2)
        {
            for (int i = 0; i < m; i++)
            {
                keys[i] = Math.Max(histogram[symbols[i]], floor);
                sortedSymbols[i] = symbols[i];
            }
            Array.Sort(keys, sortedSymbols);
            keys.CopyTo(weight, 0);
            // Two-queue Huffman: leaves in order, internal nodes are created in non-decreasing weight.
            int leaf = 0, inner = m, next = m;
            for (int k = 0; k < m - 1; k++)
            {
                int a = leaf < m && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                int b = leaf < m && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                weight[next] = weight[a] + weight[b];
                parent[a] = parent[b] = next;
                next++;
            }
            int root = 2 * m - 2, maxDepth = 0;
            depth[root] = 0;
            for (int node = root - 1; node >= 0; node--)
            {
                depth[node] = depth[parent[node]] + 1;
                if (node < m && depth[node] > maxDepth) maxDepth = depth[node];
            }
            if (maxDepth <= maxLength)
            {
                for (int i = 0; i < m; i++) lengths[sortedSymbols[i]] = depth[i];
                return;
            }
        }
    }
}
