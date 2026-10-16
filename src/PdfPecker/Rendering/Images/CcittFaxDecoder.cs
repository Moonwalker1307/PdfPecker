using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>CCITT Group 3 (1D and 2D) and Group 4 fax decoder for CCITTFaxDecode image streams.</summary>
internal sealed class CcittFaxDecoder
{
    private const int LookupBits = 13;

    // Lookup tables indexed by the next 13 bits: (code length << 16) | run length, or 0 for an invalid code.
    private static readonly string[] ExtendedMakeup =
    {
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
        "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    };

    private static readonly int[] WhiteTable = BuildTable(WhiteCodes());
    private static readonly int[] BlackTable = BuildTable(BlackCodes());

    private readonly byte[] _d;
    private long _bit;
    private readonly long _bitLen;

    private CcittFaxDecoder(byte[] d)
    {
        _d = d;
        _bitLen = (long)d.Length * 8;
    }

    /// <summary>Decodes to packed 1-bit rows where 1 is white (or black with BlackIs1), as DeviceGray samples expect.</summary>
    public static byte[]? Decode(byte[] data, PdfDict? parms, PdfFile file, int width, int height)
    {
        int k = file.GetInt(parms, "K", 0);
        int columns = file.GetInt(parms, "Columns", 1728);
        if (columns <= 0) columns = width > 0 ? width : 1728;
        int rows = file.GetInt(parms, "Rows", 0);
        if (rows <= 0) rows = height;
        bool byteAlign = file.GetBool(parms, "EncodedByteAlign", false);
        bool blackIs1 = file.GetBool(parms, "BlackIs1", false);
        if (rows <= 0 || (long)columns * rows > PdfImageDecoder.MaxPixels) return null;
        return DecodeCore(data, k, columns, rows, byteAlign, blackIs1, out _);
    }

    /// <summary>
    /// Decodes fax data of a TIFF strip or tile: K &lt; 0 for T.6, 0 for one-dimensional T.4, &gt; 0 for two-dimensional
    /// T.4. Returns packed rows where 1 is white (or black with <paramref name="blackIs1"/>).
    /// </summary>
    public static byte[] Decode(byte[] data, int k, int columns, int rows, bool byteAlign, bool blackIs1)
        => DecodeCore(data, k, columns, rows, byteAlign, blackIs1, out _);

    /// <summary>
    /// Decodes T.6 (MMR) data as JBIG2 uses it: packed rows with 1 = black, starting at <paramref name="start"/>. Sets
    /// <paramref name="end"/> to the byte after the data, skipping an end-of-block code.
    /// </summary>
    public static byte[] DecodeMmr(byte[] data, int start, int width, int height, out int end)
    {
        var slice = start == 0 ? data : data.AsSpan(Math.Min(start, data.Length)).ToArray();
        var output = DecodeCore(slice, -1, width, height, false, true, out long bits);
        end = start + (int)((bits + 7) >> 3);
        return output;
    }

    private static byte[] DecodeCore(byte[] data, int k, int columns, int rows, bool byteAlign, bool blackIs1, out long bitsUsed)
    {
        var dec = new CcittFaxDecoder(data);
        int rowBytes = (columns + 7) / 8;
        var output = new byte[rowBytes * rows];
        var refLine = new int[columns + 4];
        var codLine = new int[columns + 4];
        int refCount = 0;
        refLine[refCount++] = columns;
        refLine[refCount++] = columns;
        int decodedRows = 0;
        try
        {
            for (int row = 0; row < rows; row++)
            {
                bool twoD = k < 0;
                if (k >= 0)
                {
                    if (byteAlign) dec.Align();
                    dec.SkipEols();
                    if (k > 0) twoD = dec.Bits(1) == 0;
                }
                else if (byteAlign) dec.Align();
                if (dec._bit >= dec._bitLen) break;
                if (k < 0 && dec.Peek(24) == 0x001001) break; // end of block

                int codCount = twoD ? dec.Row2D(refLine, refCount, codLine, columns) : dec.Row1D(codLine, columns);
                if (codCount < 0) break;
                // Changes to pixels: start white, toggle at each change.
                int o = row * rowBytes;
                bool black = false;
                int pos = 0;
                for (int i = 0; i <= codCount; i++)
                {
                    int end = i < codCount ? Math.Min(codLine[i], columns) : columns;
                    if (!black)
                    {
                        // White pixels: 1 bits (unless BlackIs1).
                        if (!blackIs1) SetBits(output, o, pos, end);
                    }
                    else if (blackIs1) SetBits(output, o, pos, end);
                    pos = Math.Max(pos, end);
                    black = !black;
                }
                decodedRows = row + 1;
                Array.Copy(codLine, refLine, codCount);
                refCount = codCount;
                refLine[refCount] = columns;
                refLine[refCount + 1] = columns;
                refCount += 2;
            }
            if (k < 0 && dec.Peek(24) == 0x001001) dec._bit += 24;
        }
        catch (IndexOutOfRangeException)
        {
            // Corrupt data: keep the rows decoded so far.
        }
        bitsUsed = Math.Min(dec._bit, dec._bitLen);
        // Rows the data ran out before are still 0 bits, which is black unless BlackIs1; paper white is the honest
        // stand-in for missing scan lines.
        if (!blackIs1) output.AsSpan(decodedRows * rowBytes).Fill(0xFF);
        return output;
    }

    private static void SetBits(byte[] o, int rowOff, int from, int to)
    {
        for (int x = from; x < to; x++) o[rowOff + (x >> 3)] |= (byte)(0x80 >> (x & 7));
    }

    // ------------------------------------------------------------------ rows

    private int Row1D(int[] cod, int columns)
    {
        int n = 0, a0 = 0;
        bool white = true;
        while (a0 < columns)
        {
            int run = ReadRun(white);
            if (run < 0) return n == 0 ? -1 : n;
            a0 = Math.Min(columns, a0 + run);
            cod[n++] = a0;
            white = !white;
            if (n >= cod.Length - 2) break;
        }
        return n;
    }

    private int Row2D(int[] refLine, int refCount, int[] cod, int columns)
    {
        int n = 0;
        int a0 = -1;
        int color = 0; // 0 white, 1 black
        int bi = 0;
        while (a0 < columns)
        {
            // b1: first changing element on the reference line right of a0 with the opposite color.
            while (bi > 0 && refLine[bi - 1] > a0) bi--;
            while (bi < refCount && (refLine[bi] <= a0 || (bi & 1) != color)) bi++;
            int b1 = bi < refCount ? refLine[bi] : columns;
            int b2 = bi + 1 < refCount ? refLine[bi + 1] : columns;
            int mode = ReadMode();
            switch (mode)
            {
                case 0: // pass
                    a0 = b2;
                    break;
                case 1: // horizontal
                {
                    int r1 = ReadRun(color == 0), r2 = ReadRun(color != 0);
                    if (r1 < 0 || r2 < 0) return n == 0 ? -1 : n;
                    int start = Math.Max(a0, 0);
                    int a1 = Math.Min(columns, start + r1), a2 = Math.Min(columns, a1 + r2);
                    if (n < cod.Length - 2) cod[n++] = a1;
                    if (n < cod.Length - 2) cod[n++] = a2;
                    a0 = a2;
                    break;
                }
                case >= 2 and <= 8: // vertical: d = mode - 5 (-3..3)
                {
                    int a1 = Math.Clamp(b1 + (mode - 5), 0, columns);
                    if (n > 0 && a1 < cod[n - 1]) a1 = cod[n - 1];
                    if (n < cod.Length - 2) cod[n++] = a1;
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

    /// <summary>0 pass, 1 horizontal, 2..8 vertical (VL3..VR3), -1 error.</summary>
    private int ReadMode()
    {
        if (Bits(1) == 1) return 5; // V0
        int b = Bits(2);
        if (b == 0b11) return 6; // VR1 (011)
        if (b == 0b10) return 4; // VL1 (010)
        if (b == 0b01) return 1; // horizontal (001)
        // 000...
        if (Bits(1) == 1) return 0; // pass (0001)
        int c = Bits(2); // 0000xx
        if (c == 0b11) return 7; // VR2 (000011)
        if (c == 0b10) return 3; // VL2 (000010)
        int d = Bits(1); // 00000x
        if (c == 0b01 && d == 1) return 8; // VR3 (0000011)
        if (c == 0b01 && d == 0) return 2; // VL3 (0000010)
        return -1; // extensions and EOL in 2D data are not supported
    }

    private int ReadRun(bool white)
    {
        var table = white ? WhiteTable : BlackTable;
        int total = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            int e = table[Peek(LookupBits)];
            if (e == 0) return -1;
            int len = e >> 16, run = e & 0xFFFF;
            _bit += len;
            total += run;
            if (run < 64) return total;
        }
        return -1;
    }

    private void SkipEols()
    {
        // EOL: at least 11 zeros followed by a 1.
        for (int guard = 0; guard < 8; guard++)
        {
            if (Peek(12) == 1) { _bit += 12; continue; }
            // Fill bits before an EOL.
            long save = _bit;
            int zeros = 0;
            while (_bit < _bitLen && Peek(1) == 0 && zeros < 64) { _bit++; zeros++; }
            if (zeros >= 11 && Peek(1) == 1) { _bit++; continue; }
            _bit = save;
            break;
        }
    }

    private void Align() => _bit = (_bit + 7) & ~7L;

    private int Bits(int n)
    {
        int v = Peek(n);
        _bit += n;
        return v;
    }

    private int Peek(int n)
    {
        int v = 0;
        long b = _bit;
        for (int i = 0; i < n; i++, b++)
        {
            int bit = b < _bitLen ? (_d[b >> 3] >> (7 - (int)(b & 7))) & 1 : 0;
            v = v << 1 | bit;
        }
        return v;
    }

    // ------------------------------------------------------------------ code tables (ITU-T T.4)

    private static int[] BuildTable(List<(string Code, int Run)> codes)
    {
        var t = new int[1 << LookupBits];
        foreach (var (code, run) in codes)
        {
            int len = code.Length;
            int v = Convert.ToInt32(code, 2);
            int shift = LookupBits - len;
            int start = v << shift;
            for (int i = 0; i < 1 << shift; i++) t[start + i] = len << 16 | run;
        }
        return t;
    }


    internal static List<(string Code, int Run)> WhiteCodes()
    {
        string[] term =
        {
            "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000",
            "000011", "110100", "110101", "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
            "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010", "00011011", "00010010",
            "00010011", "00010100", "00010101", "00010110", "00010111", "00101000", "00101001", "00101010", "00101011",
            "00101100", "00101101", "00000100", "00000101", "00001010", "00001011", "01010010", "01010011", "01010100",
            "01010101", "00100100", "00100101", "01011000", "01011001", "01011010", "01011011", "01001010", "01001011",
            "00110010", "00110011", "00110100",
        };
        string[] makeup =
        {
            "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111",
            "011001100", "011001101", "011010010", "011010011", "011010100", "011010101", "011010110", "011010111",
            "011011000", "011011001", "011011010", "011011011", "010011000", "010011001", "010011010", "011000", "010011011",
        };
        var list = new List<(string, int)>();
        for (int i = 0; i < term.Length; i++) list.Add((term[i], i));
        for (int i = 0; i < makeup.Length; i++) list.Add((makeup[i], 64 * (i + 1)));
        for (int i = 0; i < ExtendedMakeup.Length; i++) list.Add((ExtendedMakeup[i], 1792 + 64 * i));
        return list;
    }

    internal static List<(string Code, int Run)> BlackCodes()
    {
        string[] term =
        {
            "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101",
            "0000111", "00000100", "00000111", "000011000", "0000010111", "0000011000", "0000001000", "00001100111",
            "00001101000", "00001101100", "00000110111", "00000101000", "00000010111", "00000011000", "000011001010",
            "000011001011", "000011001100", "000011001101", "000001101000", "000001101001", "000001101010", "000001101011",
            "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100",
            "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
            "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000",
            "000000100111", "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010",
            "000001100110", "000001100111",
        };
        string[] makeup =
        {
            "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101",
            "0000001101100", "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101",
            "0000001110010", "0000001110011", "0000001110100", "0000001110101", "0000001110110", "0000001110111",
            "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010", "0000001011011",
            "0000001100100", "0000001100101",
        };
        var list = new List<(string, int)>();
        for (int i = 0; i < term.Length; i++) list.Add((term[i], i));
        for (int i = 0; i < makeup.Length; i++) list.Add((makeup[i], 64 * (i + 1)));
        for (int i = 0; i < ExtendedMakeup.Length; i++) list.Add((ExtendedMakeup[i], 1792 + 64 * i));
        return list;
    }
}
