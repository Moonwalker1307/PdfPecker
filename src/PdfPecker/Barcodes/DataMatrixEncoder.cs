using System.Text;

namespace PdfPecker.Barcodes;

/// <summary>
/// Encodes Data Matrix ECC 200 (ISO/IEC 16022) square symbols, from 10 x 10 to 144 x 144 modules, as a grid of modules,
/// true for dark. Text is stored in ASCII encodation, which packs two digits into one codeword; that is the best choice
/// for the numbers that make up most GS1 and product codes. The quiet zone is added by the SVG writer.
/// </summary>
internal static class DataMatrixEncoder
{
    // Size, data region size, data codewords, error correction codewords and Reed-Solomon blocks of the 24 square
    // symbols, from table 7 of the standard.
    private static readonly (int Size, int Region, int Data, int Ecc, int Blocks)[] Symbols =
    {
        (10, 8, 3, 5, 1), (12, 10, 5, 7, 1), (14, 12, 8, 10, 1), (16, 14, 12, 12, 1), (18, 16, 18, 14, 1),
        (20, 18, 22, 18, 1), (22, 20, 30, 20, 1), (24, 22, 36, 24, 1), (26, 24, 44, 28, 1), (32, 14, 62, 36, 1),
        (36, 16, 86, 42, 1), (40, 18, 114, 48, 1), (44, 20, 144, 56, 1), (48, 22, 174, 68, 1), (52, 24, 204, 84, 2),
        (64, 14, 280, 112, 2), (72, 16, 368, 144, 4), (80, 18, 456, 192, 4), (88, 20, 576, 224, 4),
        (96, 22, 696, 272, 4), (104, 24, 816, 336, 6), (120, 18, 1050, 408, 6), (132, 20, 1304, 496, 8),
        (144, 22, 1558, 620, 10),
    };

    private const int Fnc1 = 232, UpperShift = 235, Eci = 241, Pad = 129;

    /// <summary>The most data codewords a symbol holds.</summary>
    public const int MaxCodewords = 1558;

    // GS1 application identifiers whose data has a fixed length, by their first two digits, with the length of the
    // whole field (identifier and data). Only these may go without an FNC1 separator after them.
    private static readonly (string Prefix, int Length)[] Gs1FixedLength =
    {
        ("00", 20), ("01", 16), ("02", 16), ("03", 16), ("04", 18), ("11", 8), ("12", 8), ("13", 8), ("14", 8),
        ("15", 8), ("16", 8), ("17", 8), ("18", 8), ("19", 8), ("20", 4), ("31", 10), ("32", 10), ("33", 10),
        ("34", 10), ("35", 10), ("36", 10), ("41", 16),
    };

    /// <summary>
    /// A symbol holding <paramref name="text"/>. Text within ISO 8859-1 is stored as it is, which is how scanners read
    /// Data Matrix by default; text with other characters is stored as UTF-8 behind the ECI that says so.
    /// </summary>
    public static bool[,] Encode(string text) => Place(Codewords(text, gs1: false));

    /// <summary>
    /// A GS1 Data Matrix symbol for an element string written with the application identifiers in parentheses, such
    /// as "(01)09501101530003(17)260131(10)AB12". It starts with FNC1, as GS1 requires, and separates a field of
    /// variable length from the next with another FNC1.
    /// </summary>
    public static bool[,] EncodeGs1(string elementString) => Place(Codewords(elementString, gs1: true));

    /// <summary>The data, padding and error correction codewords of the symbol, and the symbol they fill.</summary>
    internal static (byte[] Codewords, int Size, int Region) Codewords(string text, bool gs1) =>
        Finish(gs1 ? Gs1Data(text) : TextData(text));

    private static List<byte> TextData(string text)
    {
        if (text.Length == 0)
            throw new ArgumentException("A Data Matrix code needs at least one character.", nameof(text));
        // Two digits per codeword is the densest any text gets, so a longer text cannot fit.
        if (text.Length > MaxCodewords * 2)
            throw new ArgumentException("The text does not fit in the largest (144 x 144) Data Matrix code.", nameof(text));

        var codewords = new List<byte>(text.Length + 8);
        byte[] bytes;
        if (text.AsSpan().ContainsAnyExceptInRange('\0', '\u00FF'))
        {
            // ECI 000026 is UTF-8; an ECI number below 127 is written as one codeword of the number plus one.
            codewords.Add(Eci);
            codewords.Add(27);
            bytes = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            bytes = Encoding.Latin1.GetBytes(text);
        }
        AppendAscii(codewords, bytes);
        return codewords;
    }

    private static List<byte> Gs1Data(string elementString)
    {
        if (elementString.Length > MaxCodewords * 2)
            throw new ArgumentException("The element string does not fit in the largest (144 x 144) Data Matrix code.", nameof(elementString));
        var fields = ParseGs1(elementString);
        var codewords = new List<byte>(elementString.Length + 8) { Fnc1 };
        var field = new List<byte>(96);
        for (var i = 0; i < fields.Count; i++)
        {
            field.Clear();
            foreach (var c in fields[i])
                field.Add((byte)c);
            AppendAscii(codewords, field);
            if (i < fields.Count - 1 && FixedLength(fields[i]) == 0)
                codewords.Add(Fnc1);
        }
        return codewords;
    }

    /// <summary>Splits "(01)...(10)..." into fields of identifier and data, and checks each.</summary>
    private static List<string> ParseGs1(string elementString)
    {
        var fields = new List<string>();
        var position = 0;
        if (elementString.Length == 0)
            throw new ArgumentException("A GS1 element string needs at least one application identifier.", nameof(elementString));
        while (position < elementString.Length)
        {
            if (elementString[position] != '(')
                throw new ArgumentException("A GS1 element string is written as application identifiers in parentheses, each followed by its data, such as \"(01)09501101530003(10)AB12\".", nameof(elementString));
            var close = elementString.IndexOf(')', position);
            if (close < 0)
                throw new ArgumentException("An application identifier is missing its closing parenthesis.", nameof(elementString));
            var ai = elementString.AsSpan(position + 1, close - position - 1);
            if (ai.Length is < 2 or > 4 || ai.ContainsAnyExceptInRange('0', '9'))
                throw new ArgumentException($"\"{ai}\" is not an application identifier: it has two to four digits.", nameof(elementString));
            var next = elementString.IndexOf('(', close);
            if (next < 0)
                next = elementString.Length;
            var data = elementString.AsSpan(close + 1, next - close - 1);
            if (data.Length == 0)
                throw new ArgumentException($"Application identifier ({ai}) has no data.", nameof(elementString));
            if (data.Length > 90)
                throw new ArgumentException($"The data of application identifier ({ai}) is longer than 90 characters.", nameof(elementString));
            foreach (var c in data)
            {
                // GS1's character set 82: the printable ASCII characters other than space, # $ @ [ \ ] ^ ` { | } ~.
                if (c is < '!' or > 'z' or '#' or '$' or '@' or '[' or '\\' or ']' or '^' or '`' or ')')
                    throw new ArgumentException($"The data of application identifier ({ai}) has a character GS1 does not allow: '{c}'.", nameof(elementString));
            }
            var field = string.Concat(ai, data);
            var fixedLength = FixedLength(field);
            if (fixedLength != 0 && (field.Length != fixedLength || field.AsSpan().ContainsAnyExceptInRange('0', '9')))
                throw new ArgumentException($"Application identifier ({ai}) needs exactly {fixedLength - ai.Length} digits of data.", nameof(elementString));
            fields.Add(field);
            position = next;
        }
        return fields;
    }

    private static int FixedLength(string field)
    {
        foreach (var (prefix, length) in Gs1FixedLength)
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal))
                return length;
        }
        return 0;
    }

    /// <summary>ASCII encodation: two digits in one codeword, other characters as their value plus one.</summary>
    private static void AppendAscii(List<byte> codewords, IReadOnlyList<byte> bytes)
    {
        for (var i = 0; i < bytes.Count; i++)
        {
            var b = bytes[i];
            if (IsDigit(b) && i + 1 < bytes.Count && IsDigit(bytes[i + 1]))
            {
                codewords.Add((byte)(130 + (b - '0') * 10 + (bytes[i + 1] - '0')));
                i++;
            }
            else if (b >= 128)
            {
                codewords.Add(UpperShift);
                codewords.Add((byte)(b - 127));
            }
            else
            {
                codewords.Add((byte)(b + 1));
            }
            if (codewords.Count > MaxCodewords)
                throw new ArgumentException("The text does not fit in the largest (144 x 144) Data Matrix code.", "text");
        }
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static (byte[] Codewords, int Size, int Region) Finish(List<byte> data)
    {
        var symbol = Array.Find(Symbols, s => s.Data >= data.Count);
        if (symbol.Size == 0)
            throw new ArgumentException("The text does not fit in the largest (144 x 144) Data Matrix code.", "text");

        var codewords = new byte[symbol.Data + symbol.Ecc];
        for (var i = 0; i < data.Count; i++)
            codewords[i] = data[i];
        // The first pad is 129; the rest are scrambled by their position so that long runs of padding don't form a
        // pattern a scanner could mistake for something else.
        for (var i = data.Count; i < symbol.Data; i++)
        {
            if (i == data.Count)
            {
                codewords[i] = Pad;
                continue;
            }
            var pad = Pad + 149 * (i + 1) % 253 + 1;
            codewords[i] = (byte)(pad <= 254 ? pad : pad - 254);
        }
        AddErrorCorrection(codewords, symbol.Data, symbol.Ecc, symbol.Blocks);
        return (codewords, symbol.Size, symbol.Region);
    }

    /// <summary>
    /// Reed-Solomon error correction. Codewords are dealt to the blocks in turn, and each block's error correction
    /// codewords are interleaved the same way after the data.
    /// </summary>
    private static void AddErrorCorrection(byte[] codewords, int dataCount, int eccCount, int blocks)
    {
        var eccPerBlock = eccCount / blocks;
        var divisor = Divisor(eccPerBlock);
        var blockData = new byte[dataCount / blocks + 1];
        Span<byte> remainder = stackalloc byte[eccPerBlock];
        for (var block = 0; block < blocks; block++)
        {
            var length = 0;
            for (var i = block; i < dataCount; i += blocks)
                blockData[length++] = codewords[i];
            Remainder(blockData.AsSpan(0, length), divisor, remainder);
            for (var i = 0; i < eccPerBlock; i++)
                codewords[dataCount + i * blocks + block] = remainder[i];
        }
    }

    /// <summary>The generator polynomial (x - a^1)(x - a^2)...(x - a^degree), highest power first and left out.</summary>
    private static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 2;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = Multiply(root, 2);
        }
        return result;
    }

    private static void Remainder(ReadOnlySpan<byte> data, ReadOnlySpan<byte> divisor, Span<byte> result)
    {
        result.Clear();
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            result[1..].CopyTo(result);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
                result[i] ^= Multiply(divisor[i], factor);
        }
    }

    /// <summary>Multiplication in GF(256) with Data Matrix's polynomial x^8 + x^5 + x^3 + x^2 + 1.</summary>
    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x12D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    /// <summary>
    /// Places the codewords in the data regions with the standard's diagonal "utah" pattern (annex F) and draws the
    /// finder and timing patterns around each region.
    /// </summary>
    private static bool[,] Place((byte[] Codewords, int Size, int Region) symbol)
    {
        var (codewords, size, region) = symbol;
        var regions = size / (region + 2);
        var rows = regions * region;
        var placement = new Placement(codewords, rows, rows);
        placement.Run();

        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var localY = y % (region + 2);
                var localX = x % (region + 2);
                if (localX == 0 || localY == region + 1)
                    modules[y, x] = true; // the solid L of the finder pattern
                else if (localY == 0)
                    modules[y, x] = localX % 2 == 0; // the timing pattern along the top
                else if (localX == region + 1)
                    modules[y, x] = localY % 2 == 1; // the timing pattern down the right
                else
                    modules[y, x] = placement.Get(y / (region + 2) * region + localY - 1, x / (region + 2) * region + localX - 1);
            }
        }
        return modules;
    }

    private sealed class Placement
    {
        private readonly byte[] _codewords;
        private readonly int _rows;
        private readonly int _cols;
        // 0 not placed yet, 1 light, 2 dark.
        private readonly byte[] _bits;

        public Placement(byte[] codewords, int rows, int cols)
        {
            _codewords = codewords;
            _rows = rows;
            _cols = cols;
            _bits = new byte[rows * cols];
        }

        public bool Get(int row, int col) => _bits[row * _cols + col] == 2;

        private bool Placed(int row, int col) => _bits[row * _cols + col] != 0;

        public void Run()
        {
            var pos = 0;
            var row = 4;
            var col = 0;
            do
            {
                if (row == _rows && col == 0)
                    Corner1(pos++);
                if (row == _rows - 2 && col == 0 && _cols % 4 != 0)
                    Corner2(pos++);
                if (row == _rows - 2 && col == 0 && _cols % 8 == 4)
                    Corner3(pos++);
                if (row == _rows + 4 && col == 2 && _cols % 8 == 0)
                    Corner4(pos++);
                // Up and to the right, then down and to the left.
                do
                {
                    if (row < _rows && col >= 0 && !Placed(row, col))
                        Utah(row, col, pos++);
                    row -= 2;
                    col += 2;
                }
                while (row >= 0 && col < _cols);
                row++;
                col += 3;
                do
                {
                    if (row >= 0 && col < _cols && !Placed(row, col))
                        Utah(row, col, pos++);
                    row += 2;
                    col -= 2;
                }
                while (row < _rows && col >= 0);
                row += 3;
                col++;
            }
            while (row < _rows || col < _cols);

            // Symbols whose size leaves four modules over fill the bottom right corner with a fixed pattern.
            if (!Placed(_rows - 1, _cols - 1))
            {
                _bits[(_rows - 1) * _cols + _cols - 1] = 2;
                _bits[(_rows - 2) * _cols + _cols - 2] = 2;
                _bits[(_rows - 1) * _cols + _cols - 2] = 1;
                _bits[(_rows - 2) * _cols + _cols - 1] = 1;
            }
        }

        private void Module(int row, int col, int pos, int bit)
        {
            if (row < 0)
            {
                row += _rows;
                col += 4 - (_rows + 4) % 8;
            }
            if (col < 0)
            {
                col += _cols;
                row += 4 - (_cols + 4) % 8;
            }
            var dark = (_codewords[pos] & (1 << (8 - bit))) != 0;
            _bits[row * _cols + col] = dark ? (byte)2 : (byte)1;
        }

        private void Utah(int row, int col, int pos)
        {
            Module(row - 2, col - 2, pos, 1);
            Module(row - 2, col - 1, pos, 2);
            Module(row - 1, col - 2, pos, 3);
            Module(row - 1, col - 1, pos, 4);
            Module(row - 1, col, pos, 5);
            Module(row, col - 2, pos, 6);
            Module(row, col - 1, pos, 7);
            Module(row, col, pos, 8);
        }

        private void Corner1(int pos)
        {
            Module(_rows - 1, 0, pos, 1);
            Module(_rows - 1, 1, pos, 2);
            Module(_rows - 1, 2, pos, 3);
            Module(0, _cols - 2, pos, 4);
            Module(0, _cols - 1, pos, 5);
            Module(1, _cols - 1, pos, 6);
            Module(2, _cols - 1, pos, 7);
            Module(3, _cols - 1, pos, 8);
        }

        private void Corner2(int pos)
        {
            Module(_rows - 3, 0, pos, 1);
            Module(_rows - 2, 0, pos, 2);
            Module(_rows - 1, 0, pos, 3);
            Module(0, _cols - 4, pos, 4);
            Module(0, _cols - 3, pos, 5);
            Module(0, _cols - 2, pos, 6);
            Module(0, _cols - 1, pos, 7);
            Module(1, _cols - 1, pos, 8);
        }

        private void Corner3(int pos)
        {
            Module(_rows - 3, 0, pos, 1);
            Module(_rows - 2, 0, pos, 2);
            Module(_rows - 1, 0, pos, 3);
            Module(0, _cols - 2, pos, 4);
            Module(0, _cols - 1, pos, 5);
            Module(1, _cols - 1, pos, 6);
            Module(2, _cols - 1, pos, 7);
            Module(3, _cols - 1, pos, 8);
        }

        private void Corner4(int pos)
        {
            Module(_rows - 1, 0, pos, 1);
            Module(_rows - 1, _cols - 1, pos, 2);
            Module(0, _cols - 3, pos, 3);
            Module(0, _cols - 2, pos, 4);
            Module(0, _cols - 1, pos, 5);
            Module(1, _cols - 3, pos, 6);
            Module(1, _cols - 2, pos, 7);
            Module(1, _cols - 1, pos, 8);
        }
    }
}
