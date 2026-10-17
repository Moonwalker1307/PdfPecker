using System.Buffers.Binary;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>CCITT Group 3 (1D and 2D) and Group 4 fax decoder for CCITTFaxDecode image streams.</summary>
/// <remarks>
/// A row is decoded to its changing elements, the columns where the colour changes, starting from white; a
/// two-dimensional row is coded against the changing elements of the row above it. Damaged data decodes as far as
/// it goes: reads past the end give zero bits, and the first row that cannot be decoded ends the image.
/// </remarks>
internal static partial class CcittFaxDecoder
{
    private const int DefaultColumns = 1728;

    /// <summary>Decodes to packed 1-bit rows where 1 is white (or black with BlackIs1), as DeviceGray samples expect.</summary>
    public static byte[]? Decode(byte[] data, PdfDict? parms, PdfFile file, int width, int height)
    {
        int k = file.GetInt(parms, "K", 0);
        int columns = file.GetInt(parms, "Columns", DefaultColumns);
        if (columns <= 0) columns = width > 0 ? width : DefaultColumns;
        int rows = file.GetInt(parms, "Rows", 0);
        if (rows <= 0) rows = height;
        bool byteAlign = file.GetBool(parms, "EncodedByteAlign", false);
        bool blackIs1 = file.GetBool(parms, "BlackIs1", false);
        if (rows <= 0 || (long)columns * rows > PdfImageDecoder.MaxPixels) return null;
        return DecodeRows(data, k, columns, rows, byteAlign, blackIs1, out _);
    }

    /// <summary>
    /// Decodes fax data of a TIFF strip or tile: K &lt; 0 for T.6, 0 for one-dimensional T.4, &gt; 0 for two-dimensional
    /// T.4. Returns packed rows where 1 is white (or black with <paramref name="blackIs1"/>).
    /// </summary>
    public static byte[] Decode(byte[] data, int k, int columns, int rows, bool byteAlign, bool blackIs1)
        => DecodeRows(data, k, columns, rows, byteAlign, blackIs1, out _);

    /// <summary>
    /// Decodes T.6 (MMR) data as JBIG2 uses it: packed rows with 1 = black, starting at <paramref name="start"/>. Sets
    /// <paramref name="end"/> to the byte after the data, skipping an end-of-block code.
    /// </summary>
    public static byte[] DecodeMmr(byte[] data, int start, int width, int height, out int end)
    {
        var output = DecodeRows(data.AsSpan(Math.Min(start, data.Length)), -1, width, height, false, true, out long bits);
        end = start + (int)((bits + 7) >> 3);
        return output;
    }

    /// <summary>
    /// Decodes up to <paramref name="rows"/> rows. Group 3 rows may be preceded by end-of-line codes (and, with
    /// <paramref name="k"/> &gt; 0, a bit that says whether the row is two-dimensional); Group 4 data ends early at an
    /// end-of-block code. Sets <paramref name="bitsUsed"/> to the bits read.
    /// </summary>
    private static byte[] DecodeRows(ReadOnlySpan<byte> data, int k, int columns, int rows, bool byteAlign, bool blackIs1, out long bitsUsed)
    {
        var reader = new BitReader(data);
        int rowBytes = (columns + 7) / 8;
        var output = new byte[rowBytes * rows];
        // The changing elements of the row above and of the row being decoded. Each list ends with two at the right
        // edge, which is where the two-dimensional modes find b1 and b2 once the row has no more changes.
        var reference = new int[columns + 4];
        var coding = new int[columns + 4];
        reference[0] = columns;
        reference[1] = columns;
        int referenceCount = 2;
        int decodedRows = 0;
        for (int row = 0; row < rows; row++)
        {
            bool twoDimensional = k < 0;
            if (k >= 0)
            {
                if (byteAlign) reader.AlignToByte();
                SkipEndOfLines(ref reader);
                if (k > 0) twoDimensional = reader.Read(1) == 0;
            }
            else if (byteAlign) reader.AlignToByte();
            if (reader.AtEnd) break;
            if (k < 0 && reader.Peek(EndOfBlockBits) == EndOfBlock) break;

            int count = twoDimensional ? ReadRow2D(ref reader, reference, referenceCount, coding, columns) : ReadRow1D(ref reader, coding, columns);
            if (count < 0) break;
            WriteRow(output.AsSpan(row * rowBytes, rowBytes), coding.AsSpan(0, count), columns, blackIs1);
            decodedRows = row + 1;
            Array.Copy(coding, reference, count);
            reference[count] = columns;
            reference[count + 1] = columns;
            referenceCount = count + 2;
        }
        if (k < 0 && reader.Peek(EndOfBlockBits) == EndOfBlock) reader.Skip(EndOfBlockBits);
        bitsUsed = reader.BitsUsed;
        // Rows the data ran out before are still 0 bits, which is black unless BlackIs1; paper white is the honest
        // stand-in for missing scan lines.
        if (!blackIs1) output.AsSpan(decodedRows * rowBytes).Fill(0xFF);
        return output;
    }

    /// <summary>Sets the bits of the runs that are 1 in the output: white ones, or black ones with BlackIs1.</summary>
    private static void WriteRow(Span<byte> row, ReadOnlySpan<int> changes, int columns, bool blackIs1)
    {
        bool black = false;
        int position = 0;
        for (int i = 0; i <= changes.Length; i++)
        {
            int end = i < changes.Length ? Math.Min(changes[i], columns) : columns;
            if (black == blackIs1) SetBits(row, position, end);
            position = Math.Max(position, end);
            black = !black;
        }
    }

    /// <summary>Sets the bits of columns <paramref name="from"/> to <paramref name="to"/> (exclusive) of a row that is still zero.</summary>
    private static void SetBits(Span<byte> row, int from, int to)
    {
        if (from >= to) return;
        int first = from >> 3, last = (to - 1) >> 3;
        byte head = (byte)(0xFF >> (from & 7)), tail = (byte)(0xFF << (7 - ((to - 1) & 7)));
        if (first == last)
        {
            row[first] |= (byte)(head & tail);
            return;
        }
        row[first] |= head;
        row[(first + 1)..last].Fill(0xFF);
        row[last] |= tail;
    }

    // ------------------------------------------------------------------ rows

    /// <summary>A one-dimensional row: alternating white and black runs. Returns the number of changes, or -1.</summary>
    private static int ReadRow1D(ref BitReader reader, int[] changes, int columns)
    {
        int n = 0, a0 = 0;
        bool white = true;
        while (a0 < columns)
        {
            int run = ReadRun(ref reader, white);
            if (run < 0) return n == 0 ? -1 : n;
            a0 = Math.Min(columns, a0 + run);
            changes[n++] = a0;
            white = !white;
            if (n >= changes.Length - 2) break;
        }
        return n;
    }

    /// <summary>
    /// A two-dimensional row (T.4 section 4.2.1.3): each change is coded relative to b1, the next change of the
    /// opposite colour on the reference row, or as a pair of runs. Returns the number of changes, or -1.
    /// </summary>
    private static int ReadRow2D(ref BitReader reader, int[] reference, int referenceCount, int[] changes, int columns)
    {
        int n = 0;
        int a0 = -1; // just before the first column, so that a change at column 0 is found
        int color = 0; // 0 white, 1 black
        int bi = 0;
        while (a0 < columns)
        {
            // b1: the first changing element on the reference line right of a0 with the opposite colour; changing
            // elements alternate colour, so its index has the parity of the colour of a0.
            while (bi > 0 && reference[bi - 1] > a0) bi--;
            while (bi < referenceCount && (reference[bi] <= a0 || (bi & 1) != color)) bi++;
            int b1 = bi < referenceCount ? reference[bi] : columns;
            int b2 = bi + 1 < referenceCount ? reference[bi + 1] : columns;
            var code = ReadMode(ref reader);
            switch (code.Mode)
            {
                case Mode.Pass:
                    a0 = b2;
                    break;
                case Mode.Horizontal:
                {
                    int r1 = ReadRun(ref reader, color == 0), r2 = ReadRun(ref reader, color != 0);
                    if (r1 < 0 || r2 < 0) return n == 0 ? -1 : n;
                    int start = Math.Max(a0, 0);
                    int a1 = Math.Min(columns, start + r1), a2 = Math.Min(columns, a1 + r2);
                    if (n < changes.Length - 2) changes[n++] = a1;
                    if (n < changes.Length - 2) changes[n++] = a2;
                    a0 = a2;
                    break;
                }
                case Mode.Vertical:
                {
                    int a1 = Math.Clamp(b1 + code.Offset, 0, columns);
                    if (n > 0 && a1 < changes[n - 1]) a1 = changes[n - 1];
                    if (n < changes.Length - 2) changes[n++] = a1;
                    a0 = a1;
                    color ^= 1;
                    break;
                }
                default:
                    return n == 0 ? -1 : n;
            }
            if (a0 >= columns) break;
        }
        return n;
    }

    /// <summary>A run length: make-up codes for multiples of 64, then a terminating code. Returns -1 for an invalid code.</summary>
    private static int ReadRun(ref BitReader reader, bool white)
    {
        var table = white ? WhiteRuns : BlackRuns;
        int total = 0;
        for (int codes = 0; codes < MaxCodesPerRun; codes++)
        {
            var entry = table[reader.Peek(RunLookupBits)];
            if (entry.Length == 0) return -1;
            reader.Skip(entry.Length);
            total += entry.Run;
            if (entry.Run < MakeupStep) return total;
        }
        return -1;
    }

    /// <summary>Skips end-of-line codes (eleven zeros and a one), with the fill bits that may come before them.</summary>
    private static void SkipEndOfLines(ref BitReader reader)
    {
        for (int codes = 0; codes < MaxEndOfLines; codes++)
        {
            if (reader.Peek(EndOfLineBits) == EndOfLine)
            {
                reader.Skip(EndOfLineBits);
                continue;
            }
            long start = reader.Position;
            int zeros = 0;
            while (!reader.AtEnd && reader.Peek(1) == 0 && zeros < MaxFillBits)
            {
                reader.Skip(1);
                zeros++;
            }
            if (zeros >= EndOfLineBits - 1 && reader.Peek(1) == 1)
            {
                reader.Skip(1);
                continue;
            }
            reader.Position = start;
            break;
        }
    }

    /// <summary>Reads the data most significant bit first, as a run of zero bits past its end.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly long _length; // in bits

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _length = (long)data.Length * 8;
        }

        public long Position { readonly get; set; }

        public readonly bool AtEnd => Position >= _length;

        public readonly long BitsUsed => Math.Min(Position, _length);

        /// <summary>The next <paramref name="count"/> bits (1 to 24) without reading them.</summary>
        public readonly int Peek(int count)
        {
            long index = Position >> 3;
            uint word;
            if (index + sizeof(uint) <= _data.Length) word = BinaryPrimitives.ReadUInt32BigEndian(_data[(int)index..]);
            else
            {
                word = 0;
                for (long at = index; at < index + sizeof(uint); at++) word = word << 8 | (at < _data.Length ? _data[(int)at] : 0u);
            }
            return (int)(word << (int)(Position & 7) >> (32 - count));
        }

        public int Read(int count)
        {
            int value = Peek(count);
            Position += count;
            return value;
        }

        public void Skip(int count) => Position += count;

        public void AlignToByte() => Position = (Position + 7) & ~7L;
    }
}
