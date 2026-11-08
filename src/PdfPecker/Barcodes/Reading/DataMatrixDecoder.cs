namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Reads a Data Matrix code (ECC 200, ISO/IEC 16022, with the rectangular extensions of ISO/IEC 21471) from its grid of modules: the data regions without their finder
/// and timing patterns, the codewords in the "utah" placement that <see cref="DataMatrixEncoder"/> writes, Reed-Solomon
/// correction of each block and every encodation (ASCII, C40, Text, X12, EDIFACT and Base 256).
/// </summary>
internal static class DataMatrixDecoder
{
    /// <summary>A symbol size: its modules, the size of each data region, and its codewords.</summary>
    internal readonly record struct SymbolSize(int Rows, int Columns, int RegionRows, int RegionColumns, int Data, int Ecc, int Blocks);

    /// <summary>The 24 square and 6 rectangular sizes of table 7 of the standard, and the 18 rectangular extensions.</summary>
    public static readonly SymbolSize[] Sizes = BuildSizes();

    private static SymbolSize[] BuildSizes()
    {
        var sizes = new List<SymbolSize>();
        foreach (var (size, region, data, ecc, blocks) in DataMatrixEncoder.Symbols)
            sizes.Add(new SymbolSize(size, size, region, region, data, ecc, blocks));
        sizes.Add(new SymbolSize(8, 18, 6, 16, 5, 7, 1));
        sizes.Add(new SymbolSize(8, 32, 6, 14, 10, 11, 1));
        sizes.Add(new SymbolSize(12, 26, 10, 24, 16, 14, 1));
        sizes.Add(new SymbolSize(12, 36, 10, 16, 22, 18, 1));
        sizes.Add(new SymbolSize(16, 36, 14, 16, 32, 24, 1));
        sizes.Add(new SymbolSize(16, 48, 14, 22, 49, 28, 1));
        // The rectangular extensions of ISO/IEC 21471 (DMRE), which fit long narrow labels.
        sizes.Add(new SymbolSize(8, 48, 6, 22, 18, 15, 1));
        sizes.Add(new SymbolSize(8, 64, 6, 14, 24, 18, 1));
        sizes.Add(new SymbolSize(8, 80, 6, 18, 32, 22, 1));
        sizes.Add(new SymbolSize(8, 96, 6, 22, 38, 28, 1));
        sizes.Add(new SymbolSize(8, 120, 6, 18, 49, 32, 1));
        sizes.Add(new SymbolSize(8, 144, 6, 22, 63, 36, 1));
        sizes.Add(new SymbolSize(12, 64, 10, 14, 43, 27, 1));
        sizes.Add(new SymbolSize(12, 88, 10, 20, 64, 36, 1));
        sizes.Add(new SymbolSize(16, 64, 14, 14, 62, 36, 1));
        sizes.Add(new SymbolSize(20, 36, 18, 16, 44, 28, 1));
        sizes.Add(new SymbolSize(20, 44, 18, 20, 56, 34, 1));
        sizes.Add(new SymbolSize(20, 64, 18, 14, 84, 42, 1));
        sizes.Add(new SymbolSize(22, 48, 20, 22, 72, 38, 1));
        sizes.Add(new SymbolSize(24, 48, 22, 22, 80, 41, 1));
        sizes.Add(new SymbolSize(24, 64, 22, 14, 108, 46, 1));
        sizes.Add(new SymbolSize(26, 40, 24, 18, 70, 38, 1));
        sizes.Add(new SymbolSize(26, 48, 24, 22, 90, 42, 1));
        sizes.Add(new SymbolSize(26, 64, 24, 14, 118, 50, 1));
        return sizes.ToArray();
    }

    // The bit maps of the sizes, made when first needed; a size's map never changes.
    private static readonly int[]?[] Maps = new int[Sizes.Length][];

    private const int Pad = 129, LatchC40 = 230, LatchBase256 = 231, Fnc1 = 232, StructuredAppend = 233, ReaderProgramming = 234,
        UpperShift = 235, Macro05 = 236, Macro06 = 237, LatchX12 = 238, LatchText = 239, LatchEdifact = 240, Eci = 241, Unlatch = 254;

    /// <summary>Decodes a grid of the modules of size <paramref name="sizeIndex"/> in <see cref="Sizes"/>, [y, x] and true for dark, finder L at the bottom left.</summary>
    public static DecodedContent? Decode(bool[,] grid, int sizeIndex)
    {
        var size = Sizes[sizeIndex];
        if (grid.GetLength(0) != size.Rows || grid.GetLength(1) != size.Columns) return null;
        int regionsY = size.Rows / (size.RegionRows + 2), regionsX = size.Columns / (size.RegionColumns + 2);
        int rows = regionsY * size.RegionRows, columns = regionsX * size.RegionColumns;
        var map = Maps[sizeIndex];
        if (map == null)
        {
            var placement = new DataMatrixEncoder.Placement(size.Data + size.Ecc, rows, columns);
            placement.Run();
            map = Maps[sizeIndex] = placement.Map;
        }

        var codewords = new int[size.Data + size.Ecc];
        for (int y = 0; y < size.Rows; y++)
        {
            int localY = y % (size.RegionRows + 2);
            if (localY == 0 || localY == size.RegionRows + 1) continue;
            int row = y / (size.RegionRows + 2) * size.RegionRows + localY - 1;
            for (int x = 0; x < size.Columns; x++)
            {
                int localX = x % (size.RegionColumns + 2);
                if (localX == 0 || localX == size.RegionColumns + 1 || !grid[y, x]) continue;
                int bit = map[row * columns + x / (size.RegionColumns + 2) * size.RegionColumns + localX - 1];
                if (bit >= 0) codewords[bit >> 3] |= 0x80 >> (bit & 7);
            }
        }

        // The codewords are dealt to the blocks in turn, the data and then the error correction (see the encoder).
        int blocks = size.Blocks, eccPerBlock = size.Ecc / blocks;
        var data = new byte[size.Data];
        for (int block = 0; block < blocks; block++)
        {
            int dataLength = (size.Data - block + blocks - 1) / blocks;
            var buffer = new int[dataLength + eccPerBlock];
            for (int i = 0; i < dataLength; i++) buffer[i] = codewords[block + i * blocks];
            for (int i = 0; i < eccPerBlock; i++) buffer[dataLength + i] = codewords[size.Data + block + i * blocks];
            if (!ReedSolomonDecoder.DataMatrix.Decode(buffer, eccPerBlock)) return null;
            for (int i = 0; i < dataLength; i++) data[block + i * blocks] = (byte)buffer[i];
        }
        return Parse(data);
    }

    private enum Mode { Ascii, C40, Text, X12, Edifact, Base256 }

    internal static DecodedContent? Parse(byte[] data)
    {
        var text = new CharacterSets.Builder(guessUtf8: false);
        var mode = Mode.Ascii;
        bool gs1 = false, upperShift = false;
        int shift = 0; // a C40 or Text shift still waiting for its value, which may come in the next pair
        string? trailer = null;
        int i = 0;
        while (i < data.Length)
        {
            switch (mode)
            {
                case Mode.Ascii:
                {
                    int c = data[i++];
                    if (c == 0) return null;
                    if (c <= 128)
                    {
                        text.Byte((byte)(c - 1 + (upperShift ? 128 : 0)));
                        upperShift = false;
                    }
                    else if (c == Pad)
                    {
                        i = data.Length;
                    }
                    else if (c <= 229)
                    {
                        int digits = c - 130;
                        text.Byte((byte)('0' + digits / 10));
                        text.Byte((byte)('0' + digits % 10));
                    }
                    else switch (c)
                    {
                        case LatchC40: mode = Mode.C40; break;
                        case LatchText: mode = Mode.Text; break;
                        case LatchX12: mode = Mode.X12; break;
                        case LatchEdifact: mode = Mode.Edifact; break;
                        case LatchBase256: mode = Mode.Base256; break;
                        case Fnc1:
                            // FNC1 first marks GS1 data; later it separates fields of variable length.
                            if (text.Length == 0) gs1 = true;
                            else text.Byte(0x1D);
                            break;
                        case StructuredAppend:
                            // The symbol's place in a sequence and the file identifier, which do not change the text.
                            i += 3;
                            break;
                        case ReaderProgramming:
                            break;
                        case UpperShift:
                            upperShift = true;
                            break;
                        case Macro05:
                        case Macro06:
                            foreach (var b in c == Macro05 ? "[)>\u001E05\u001D"u8 : "[)>\u001E06\u001D"u8) text.Byte(b);
                            trailer = "\u001E\u0004";
                            break;
                        case Eci:
                        {
                            if (i >= data.Length) return null;
                            int c1 = data[i++];
                            int eci;
                            if (c1 <= 127) eci = c1 - 1;
                            else if (c1 <= 191)
                            {
                                if (i >= data.Length) return null;
                                eci = (c1 - 128) * 254 + 127 + data[i++] - 1;
                            }
                            else
                            {
                                if (i + 1 >= data.Length) return null;
                                eci = (c1 - 192) * 64516 + 16383 + (data[i] - 1) * 254 + data[i + 1] - 1;
                                i += 2;
                            }
                            if (eci < 0) return null;
                            text.Eci(eci);
                            break;
                        }
                        default:
                            return null;
                    }
                    break;
                }
                case Mode.C40:
                case Mode.Text:
                case Mode.X12:
                {
                    if (data[i] == Unlatch)
                    {
                        i++;
                        mode = Mode.Ascii;
                        break;
                    }
                    // A single codeword left over is ASCII (the encoder may end a triplet mode that way).
                    if (i + 1 >= data.Length)
                    {
                        mode = Mode.Ascii;
                        break;
                    }
                    int value = data[i] * 256 + data[i + 1] - 1;
                    i += 2;
                    if (value < 0 || value >= 64000) return null;
                    Span<int> triple = [value / 1600, value / 40 % 40, value % 40];
                    if (mode == Mode.X12)
                    {
                        foreach (int v in triple)
                        {
                            int ch = v switch { 0 => '\r', 1 => '*', 2 => '>', 3 => ' ', < 14 => '0' + v - 4, _ => 'A' + v - 14 };
                            text.Byte((byte)ch);
                        }
                    }
                    else if (!C40Triple(triple, mode == Mode.Text, text, ref upperShift, ref gs1, ref shift)) return null;
                    break;
                }
                case Mode.Edifact:
                {
                    // Four 6-bit values in three codewords; 011111 returns to ASCII at the next codeword, and so do
                    // fewer than three codewords left at the end.
                    if (data.Length - i < 3)
                    {
                        mode = Mode.Ascii;
                        break;
                    }
                    int bits = data[i] << 16 | data[i + 1] << 8 | data[i + 2];
                    int consumed = 3;
                    for (int k = 0; k < 4; k++)
                    {
                        int v = (bits >> (18 - 6 * k)) & 0x3F;
                        if (v == 0x1F)
                        {
                            mode = Mode.Ascii;
                            consumed = (6 * (k + 1) + 7) / 8;
                            break;
                        }
                        text.Byte((byte)((v & 0x20) == 0 ? v | 0x40 : v));
                    }
                    i += consumed;
                    break;
                }
                case Mode.Base256:
                {
                    int length = Unrandomize(data[i], i + 1);
                    i++;
                    if (length == 0) length = data.Length - i;
                    else if (length >= 250)
                    {
                        if (i >= data.Length) return null;
                        length = 250 * (length - 249) + Unrandomize(data[i], i + 1);
                        i++;
                    }
                    if (length > data.Length - i) return null;
                    for (int k = 0; k < length; k++, i++) text.Byte((byte)Unrandomize(data[i], i + 1));
                    mode = Mode.Ascii;
                    break;
                }
            }
        }
        if (trailer != null)
        {
            foreach (var ch in trailer) text.Byte((byte)ch);
        }
        return new DecodedContent(text.ToString(), text.Raw.ToArray(), gs1);
    }

    /// <summary>The 255-state randomizing of Base 256 codewords, undone; <paramref name="position"/> counts from 1.</summary>
    private static int Unrandomize(int value, int position)
    {
        int pseudo = 149 * position % 255 + 1;
        int result = value - pseudo;
        return result >= 0 ? result : result + 256;
    }

    private static bool C40Triple(ReadOnlySpan<int> values, bool textSet, CharacterSets.Builder text, ref bool upperShift, ref bool gs1, ref int shift)
    {
        foreach (int v in values)
        {
            int ch = -1;
            switch (shift)
            {
                case 0:
                    if (v < 3) { shift = v + 1; continue; }
                    ch = v == 3 ? ' ' : v < 14 ? '0' + v - 4 : (textSet ? 'a' : 'A') + v - 14;
                    break;
                case 1:
                    ch = v; // control characters 0 to 31
                    break;
                case 2:
                    if (v < 15) ch = "!\"#$%&'()*+,-./"[v];
                    else if (v < 22) ch = ":;<=>?@"[v - 15];
                    else if (v < 27) ch = "[\\]^_"[v - 22];
                    else if (v == 27)
                    {
                        if (text.Length == 0) gs1 = true;
                        else text.Byte(0x1D);
                    }
                    else if (v == 30) upperShift = true;
                    else return false;
                    break;
                default:
                    if (textSet) ch = v == 0 ? '`' : v < 27 ? 'A' + v - 1 : v < 32 ? "{|}~\u007F"[v - 27] : -2;
                    else ch = v < 32 ? 96 + v : -2;
                    if (ch == -2) return false;
                    break;
            }
            shift = 0;
            if (ch >= 0)
            {
                text.Byte((byte)(ch + (upperShift ? 128 : 0)));
                upperShift = false;
            }
        }
        return true;
    }
}
