using System.Numerics;

namespace PdfPecker.Rendering;

internal static partial class JpegEncoder
{
    // AC symbols are a run of zeros in the high nibble and the size of the next coefficient in the low one. Two have
    // no coefficient: end of block (the rest are zero) and ZRL, a run of sixteen zeros.
    private const int EndOfBlock = 0x00;
    private const int ZeroRunLength = 0xF0;
    private const int MaxRun = 15;

    /// <summary>The four Huffman tables of the scan.</summary>
    private sealed record EntropyTables(HuffmanTable LumaDc, HuffmanTable LumaAc, HuffmanTable? ChromaDc, HuffmanTable? ChromaAc);

    /// <summary>
    /// The quantised blocks in scan order (MCU by MCU, the Y blocks row by row, then Cb, then Cr), coded twice: once to
    /// count the symbols for optimal tables, then to write them.
    /// </summary>
    private sealed class Scan
    {
        private const int Y = 0, Cb = 1, Cr = 2;

        private readonly short[]?[] coefficients;
        private readonly int lumaBlocksX, chromaBlocksX;
        private readonly Frame frame;
        private readonly bool gray;

        /// <summary>A scan of Y, Cb and Cr, or of Y alone when <paramref name="cb"/> and <paramref name="cr"/> are null.</summary>
        public Scan(short[] y, short[]? cb, short[]? cr, Frame frame)
        {
            coefficients = new[] { y, cb, cr };
            gray = cb == null;
            this.frame = frame;
            lumaBlocksX = frame.LumaWidth / BlockSize;
            chromaBlocksX = frame.ChromaWidth / BlockSize;
        }

        /// <summary>Huffman tables built from the frequencies of the scan's symbols.</summary>
        public EntropyTables OptimalTables()
        {
            var counter = new SymbolCounter();
            Code(ref counter);
            if (gray) return new EntropyTables(HuffmanTable.FromFrequencies(counter.LumaDc), HuffmanTable.FromFrequencies(counter.LumaAc), null, null);
            return new EntropyTables(
                HuffmanTable.FromFrequencies(counter.LumaDc), HuffmanTable.FromFrequencies(counter.LumaAc),
                HuffmanTable.FromFrequencies(counter.ChromaDc), HuffmanTable.FromFrequencies(counter.ChromaAc));
        }

        public void Write(JpegWriter output, EntropyTables tables)
        {
            var writer = new SymbolWriter(new BitWriter(output), tables);
            Code(ref writer);
            writer.Bits.Flush();
        }

        /// <summary>Runs every block through the entropy coder in scan order, with DC prediction per component.</summary>
        private void Code<TSink>(ref TSink sink) where TSink : struct, ISymbolSink
        {
            Span<int> predictions = stackalloc int[3];
            predictions.Clear();
            int lumaSide = frame.LumaBlocksPerMcuSide;
            for (int mcuY = 0; mcuY < frame.McusY; mcuY++)
                for (int mcuX = 0; mcuX < frame.McusX; mcuX++)
                {
                    for (int v = 0; v < lumaSide; v++)
                        for (int h = 0; h < lumaSide; h++)
                        {
                            int block = (mcuY * lumaSide + v) * lumaBlocksX + mcuX * lumaSide + h;
                            CodeBlock(ref sink, Y, block, ref predictions[Y]);
                        }
                    if (gray) continue;
                    int chromaBlock = mcuY * chromaBlocksX + mcuX;
                    CodeBlock(ref sink, Cb, chromaBlock, ref predictions[Cb]);
                    CodeBlock(ref sink, Cr, chromaBlock, ref predictions[Cr]);
                }
        }

        /// <summary>The DC difference from the block before, then the AC coefficients as runs of zeros and values.</summary>
        private void CodeBlock<TSink>(ref TSink sink, int component, int block, ref int prediction) where TSink : struct, ISymbolSink
        {
            var coefficients = this.coefficients[component]!.AsSpan(block * BlockLength, BlockLength);
            bool luma = component == Y;
            int difference = coefficients[0] - prediction;
            prediction = coefficients[0];
            sink.Dc(luma, BitLength(difference), difference);
            int run = 0;
            for (int k = 1; k < BlockLength; k++)
            {
                int value = coefficients[k];
                if (value == 0)
                {
                    run++;
                    continue;
                }
                for (; run > MaxRun; run -= MaxRun + 1) sink.Ac(luma, ZeroRunLength, 0, 0);
                int size = BitLength(value);
                sink.Ac(luma, run << 4 | size, size, value);
                run = 0;
            }
            if (run > 0) sink.Ac(luma, EndOfBlock, 0, 0);
        }

        /// <summary>The number of bits of a value's magnitude, which is its size category.</summary>
        private static int BitLength(int value) => 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));
    }

    /// <summary>What the entropy coder does with each symbol: count it, or write it with its extra bits.</summary>
    private interface ISymbolSink
    {
        void Dc(bool luma, int size, int difference);

        void Ac(bool luma, int symbol, int size, int value);
    }

    private struct SymbolCounter : ISymbolSink
    {
        // Each table has room for the 256 symbols and the reserved code point that HuffmanTable adds.
        public readonly long[] LumaDc = new long[HuffmanTable.FrequencyCount];
        public readonly long[] LumaAc = new long[HuffmanTable.FrequencyCount];
        public readonly long[] ChromaDc = new long[HuffmanTable.FrequencyCount];
        public readonly long[] ChromaAc = new long[HuffmanTable.FrequencyCount];

        public SymbolCounter()
        {
        }

        public readonly void Dc(bool luma, int size, int difference) => (luma ? LumaDc : ChromaDc)[size]++;

        public readonly void Ac(bool luma, int symbol, int size, int value) => (luma ? LumaAc : ChromaAc)[symbol]++;
    }

    private readonly struct SymbolWriter(BitWriter bits, EntropyTables tables) : ISymbolSink
    {
        public BitWriter Bits { get; } = bits;

        public void Dc(bool luma, int size, int difference)
        {
            var table = luma ? tables.LumaDc : tables.ChromaDc!;
            Bits.Write(table.Code[size], table.Size[size]);
            if (size > 0) Bits.Write(Magnitude(difference, size), size);
        }

        public void Ac(bool luma, int symbol, int size, int value)
        {
            var table = luma ? tables.LumaAc : tables.ChromaAc!;
            Bits.Write(table.Code[symbol], table.Size[symbol]);
            if (size > 0) Bits.Write(Magnitude(value, size), size);
        }

        /// <summary>The low bits of a value, with negative values one less (T.81 F.1.2.1).</summary>
        private static uint Magnitude(int value, int size) => (uint)(value < 0 ? value - 1 : value) & ((1u << size) - 1);
    }

    /// <summary>A Huffman table built from symbol frequencies (ITU-T T.81 Annex K.2), limited to 16-bit codes.</summary>
    private sealed class HuffmanTable
    {
        public const int MaxCodeLength = 16;

        // Symbol 256 is a reserved code point that keeps any real symbol from getting the code of all ones.
        public const int FrequencyCount = 257;
        private const int ReservedSymbol = 256;
        private const int MaxUnlimitedLength = 32;

        /// <summary>The number of codes of each length, 1 to 16.</summary>
        public readonly byte[] Counts = new byte[MaxCodeLength + 1];

        /// <summary>The symbols in order of code length.</summary>
        public byte[] Values { get; private set; } = Array.Empty<byte>();

        public readonly uint[] Code = new uint[256];
        public readonly byte[] Size = new byte[256];

        public static HuffmanTable FromFrequencies(long[] frequencies)
        {
            var codeSize = CodeSizes(frequencies);
            var counts = CountsByLength(codeSize);
            var table = new HuffmanTable();
            for (int length = 1; length <= MaxCodeLength; length++) table.Counts[length] = (byte)counts[length];
            table.Values = SymbolsByCodeSize(codeSize);
            table.AssignCanonicalCodes();
            return table;
        }

        /// <summary>
        /// The code size of each symbol (Annex K.2 figure K.1): the two least frequent nodes are merged until one is
        /// left, and every symbol in a merged chain gets one bit longer.
        /// </summary>
        private static int[] CodeSizes(long[] frequencies)
        {
            var frequency = (long[])frequencies.Clone();
            frequency[ReservedSymbol] = 1;
            // A table needs at least one symbol.
            if (frequency.AsSpan(0, ReservedSymbol).IndexOfAnyExcept(0L) < 0) frequency[0] = 1;
            var codeSize = new int[FrequencyCount];
            var next = new int[FrequencyCount]; // the next symbol in the chain merged into each one, or -1
            Array.Fill(next, -1);
            while (true)
            {
                int least = -1, second = -1;
                long leastFrequency = long.MaxValue, secondFrequency = long.MaxValue;
                // Ties go to the highest symbol, as in the figure.
                for (int i = 0; i < FrequencyCount; i++)
                    if (frequency[i] != 0 && frequency[i] <= leastFrequency)
                    {
                        leastFrequency = frequency[i];
                        least = i;
                    }
                for (int i = 0; i < FrequencyCount; i++)
                    if (frequency[i] != 0 && frequency[i] <= secondFrequency && i != least)
                    {
                        secondFrequency = frequency[i];
                        second = i;
                    }
                if (second < 0) break;
                frequency[least] += frequency[second];
                frequency[second] = 0;
                codeSize[least]++;
                while (next[least] >= 0)
                {
                    least = next[least];
                    codeSize[least]++;
                }
                next[least] = second;
                codeSize[second]++;
                while (next[second] >= 0)
                {
                    second = next[second];
                    codeSize[second]++;
                }
            }
            return codeSize;
        }

        /// <summary>The number of codes of each length, limited to 16 bits (Annex K.3 figure K.3), without the reserved one.</summary>
        private static int[] CountsByLength(int[] codeSize)
        {
            var counts = new int[MaxUnlimitedLength + 1];
            foreach (int size in codeSize)
                if (size > 0) counts[size]++;
            // A code longer than 16 bits and its sibling are moved up a level, and a shorter code is split to make room.
            for (int length = MaxUnlimitedLength; length > MaxCodeLength; length--)
            {
                while (counts[length] > 0)
                {
                    int shorter = length - 2;
                    while (counts[shorter] == 0) shorter--;
                    counts[length] -= 2;
                    counts[length - 1]++;
                    counts[shorter + 1] += 2;
                    counts[shorter]--;
                }
            }
            int longest = MaxCodeLength;
            while (counts[longest] == 0) longest--;
            counts[longest]--; // drop the reserved code point, which has the longest code
            return counts;
        }

        /// <summary>The real symbols sorted by code size, then by value (Annex K.2 figure K.4).</summary>
        private static byte[] SymbolsByCodeSize(int[] codeSize)
        {
            int count = 0;
            for (int symbol = 0; symbol < ReservedSymbol; symbol++)
                if (codeSize[symbol] > 0) count++;
            var values = new byte[count];
            int k = 0;
            for (int length = 1; length <= MaxUnlimitedLength; length++)
                for (int symbol = 0; symbol < ReservedSymbol; symbol++)
                    if (codeSize[symbol] == length) values[k++] = (byte)symbol;
            return values;
        }

        private void AssignCanonicalCodes()
        {
            uint code = 0;
            int k = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                for (int n = 0; n < Counts[length]; n++, k++)
                {
                    Code[Values[k]] = code++;
                    Size[Values[k]] = (byte)length;
                }
                code <<= 1;
            }
        }
    }

    /// <summary>The bytes of the file, in a buffer that doubles when full.</summary>
    private sealed class JpegWriter
    {
        private const int MinCapacity = 4096;
        private byte[] buffer;
        private int length;

        public JpegWriter(int capacity) => buffer = new byte[Math.Max(capacity, MinCapacity)];

        public void WriteByte(byte value)
        {
            if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            buffer[length++] = value;
        }

        public void WriteUInt16(int value)
        {
            WriteByte((byte)(value >> 8));
            WriteByte((byte)value);
        }

        public void WriteMarker(Marker marker)
        {
            WriteByte(0xFF);
            WriteByte((byte)marker);
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes) WriteByte(b);
        }

        public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>Most-significant-bit-first entropy-coded data, with a zero byte stuffed after every 0xFF.</summary>
    private sealed class BitWriter(JpegWriter output)
    {
        private ulong accumulator;
        private int count;

        public void Write(uint code, int size)
        {
            accumulator = (accumulator << size) | code;
            count += size;
            while (count >= 8)
            {
                count -= 8;
                byte b = (byte)(accumulator >> count);
                output.WriteByte(b);
                if (b == 0xFF) output.WriteByte(0);
            }
        }

        /// <summary>Pads the last byte with one bits.</summary>
        public void Flush()
        {
            if (count > 0) Write((1u << (8 - count)) - 1, 8 - count);
        }
    }
}
