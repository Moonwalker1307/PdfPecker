namespace PdfPecker.Barcodes;

/// <summary>
/// Encodes text as a QR code (ISO/IEC 18004, Model 2, versions 1 to 40) and returns the module grid. The whole text is
/// one segment in the densest mode that holds it: numeric, alphanumeric or UTF-8 bytes. No ECI header is written,
/// because many phone readers show it as text; they read the bytes as UTF-8 by default.
/// </summary>
internal static class QrEncoder
{
    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // Error correction codewords per block and number of blocks, indexed by level (L, M, Q, H) and version (1 to 40).
    // Index 0 of each row is unused.
    private static readonly sbyte[][] EccPerBlock =
    {
        new sbyte[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new sbyte[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new sbyte[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new sbyte[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly byte[][] BlockCount =
    {
        new byte[] { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new byte[] { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new byte[] { 0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new byte[] { 0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    // The two format-information bits of each level, in the order L, M, Q, H.
    private static readonly int[] FormatBits = { 1, 0, 3, 2 };

    private enum Mode { Numeric, Alphanumeric, Byte }

    /// <summary>
    /// The module grid, true for dark, indexed [y, x], without the quiet zone. <paramref name="maxVersion"/> caps the
    /// symbol size (the EPC payment QR allows at most version 13); <paramref name="forcedMask"/> is for tests.
    /// </summary>
    public static bool[,] Encode(string text, QrErrorCorrection level, int maxVersion = 40, int forcedMask = -1)
    {
        var ecl = (int)level;
        if ((uint)ecl > 3)
            throw new ArgumentOutOfRangeException(nameof(level));
        var (mode, data, count) = Classify(text);

        var version = 1;
        int dataBits;
        while (true)
        {
            dataBits = DataCodewords(version, ecl) * 8;
            var needed = 4 + CountBits(mode, version) + PayloadBits(mode, count);
            if (needed <= dataBits && count < 1 << CountBits(mode, version))
                break;
            if (++version > maxVersion)
                throw new ArgumentException(
                    $"The text is too long for a QR code at error correction level {level} (version {maxVersion} at most).", nameof(text));
        }

        var bits = new BitBuffer(dataBits);
        bits.Append(mode switch { Mode.Numeric => 1, Mode.Alphanumeric => 2, _ => 4 }, 4);
        bits.Append(count, CountBits(mode, version));
        AppendPayload(ref bits, mode, text, data);
        bits.Append(0, Math.Min(4, dataBits - bits.Length));
        bits.Append(0, (8 - bits.Length % 8) % 8);
        for (var pad = 0xEC; bits.Length < dataBits; pad ^= 0xEC ^ 0x11)
            bits.Append(pad, 8);

        var codewords = Interleave(bits.Bytes, version, ecl);
        var symbol = new Symbol(version);
        symbol.DrawFunctionPatterns();
        symbol.DrawCodewords(codewords);

        var mask = forcedMask;
        if (mask < 0)
        {
            var best = int.MaxValue;
            for (var m = 0; m < 8; m++)
            {
                symbol.ApplyMask(m);
                symbol.DrawFormat(FormatBits[ecl], m);
                var penalty = symbol.Penalty();
                if (penalty < best)
                {
                    best = penalty;
                    mask = m;
                }
                symbol.ApplyMask(m);
            }
        }
        symbol.ApplyMask(mask);
        symbol.DrawFormat(FormatBits[ecl], mask);
        return symbol.Modules;
    }

    private static (Mode Mode, byte[]? Data, int Count) Classify(string text)
    {
        var numeric = true;
        var alphanumeric = true;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                numeric = false;
            if (!Alphanumeric.Contains(c, StringComparison.Ordinal))
                alphanumeric = false;
        }
        if (text.Length > 0 && numeric)
            return (Mode.Numeric, null, text.Length);
        if (text.Length > 0 && alphanumeric)
            return (Mode.Alphanumeric, null, text.Length);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return (Mode.Byte, bytes, bytes.Length);
    }

    private static int CountBits(Mode mode, int version)
    {
        var range = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return mode switch
        {
            Mode.Numeric => 10 + 2 * range,
            Mode.Alphanumeric => 9 + 2 * range,
            _ => range == 0 ? 8 : 16,
        };
    }

    private static long PayloadBits(Mode mode, int count) => mode switch
    {
        Mode.Numeric => count / 3 * 10L + (count % 3) switch { 1 => 4, 2 => 7, _ => 0 },
        Mode.Alphanumeric => count / 2 * 11L + count % 2 * 6,
        _ => count * 8L,
    };

    private static void AppendPayload(ref BitBuffer bits, Mode mode, string text, byte[]? data)
    {
        switch (mode)
        {
            case Mode.Numeric:
                for (var i = 0; i < text.Length; i += 3)
                {
                    var n = Math.Min(3, text.Length - i);
                    var value = 0;
                    for (var j = 0; j < n; j++)
                        value = value * 10 + (text[i + j] - '0');
                    bits.Append(value, n * 3 + 1);
                }
                break;
            case Mode.Alphanumeric:
                for (var i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length)
                        bits.Append(Alphanumeric.IndexOf(text[i], StringComparison.Ordinal) * 45 + Alphanumeric.IndexOf(text[i + 1], StringComparison.Ordinal), 11);
                    else
                        bits.Append(Alphanumeric.IndexOf(text[i], StringComparison.Ordinal), 6);
                }
                break;
            default:
                foreach (var b in data!)
                    bits.Append(b, 8);
                break;
        }
    }

    /// <summary>Data and error correction modules of a version: everything but the function patterns.</summary>
    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7)
                result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version, int ecl) =>
        RawDataModules(version) / 8 - EccPerBlock[ecl][version] * BlockCount[ecl][version];

    /// <summary>Splits the data into blocks, adds Reed-Solomon codewords to each and interleaves them.</summary>
    private static byte[] Interleave(ReadOnlySpan<byte> data, int version, int ecl)
    {
        int blocks = BlockCount[ecl][version], eccLength = EccPerBlock[ecl][version];
        var raw = RawDataModules(version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;
        var divisor = ReedSolomon.Divisor(eccLength);

        var all = new byte[blocks][];
        var offset = 0;
        for (var i = 0; i < blocks; i++)
        {
            var dataLength = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var block = new byte[shortLength + 1];
            var part = data.Slice(offset, dataLength);
            offset += dataLength;
            // Short blocks keep a gap where the long blocks have their extra data codeword, so all line up by index.
            part.CopyTo(block);
            ReedSolomon.Remainder(part, divisor, block.AsSpan(shortLength + 1 - eccLength));
            all[i] = block;
        }

        var result = new byte[raw];
        var k = 0;
        for (var i = 0; i <= shortLength; i++)
        {
            for (var j = 0; j < blocks; j++)
            {
                if (i != shortLength - eccLength || j >= shortBlocks)
                    result[k++] = all[j][i];
            }
        }
        return result;
    }

    private struct BitBuffer
    {
        public readonly byte[] Bytes;
        public int Length;

        public BitBuffer(int capacityBits)
        {
            Bytes = new byte[(capacityBits + 7) / 8];
            Length = 0;
        }

        public void Append(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--, Length++)
            {
                if (((value >> i) & 1) != 0)
                    Bytes[Length >> 3] |= (byte)(0x80 >> (Length & 7));
            }
        }
    }

    private static class ReedSolomon
    {
        public static byte[] Divisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            var root = 1;
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

        public static void Remainder(ReadOnlySpan<byte> data, ReadOnlySpan<byte> divisor, Span<byte> result)
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

        /// <summary>Multiplication in GF(256) with the QR code's polynomial x^8 + x^4 + x^3 + x^2 + 1.</summary>
        private static byte Multiply(int x, int y)
        {
            var z = 0;
            for (var i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }
    }

    private sealed class Symbol
    {
        public readonly bool[,] Modules;
        private readonly bool[,] _function;
        private readonly int _version;
        private readonly int _size;

        public Symbol(int version)
        {
            _version = version;
            _size = version * 4 + 17;
            Modules = new bool[_size, _size];
            _function = new bool[_size, _size];
        }

        private void Set(int x, int y, bool dark)
        {
            Modules[y, x] = dark;
            _function[y, x] = true;
        }

        public void DrawFunctionPatterns()
        {
            for (var i = 0; i < _size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            Finder(3, 3);
            Finder(_size - 4, 3);
            Finder(3, _size - 4);

            var positions = AlignmentPositions();
            var last = positions.Length - 1;
            for (var i = 0; i <= last; i++)
            {
                for (var j = 0; j <= last; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0))
                        continue;
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                            Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }

            // Reserve the format areas now; DrawFormat fills them once the mask is known.
            DrawFormat(0, 0);
            if (_version >= 7)
            {
                var remainder = _version;
                for (var i = 0; i < 12; i++)
                    remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
                var bits = (_version << 12) | remainder;
                for (var i = 0; i < 18; i++)
                {
                    var dark = ((bits >> i) & 1) != 0;
                    int a = _size - 11 + i % 3, b = i / 3;
                    Set(a, b, dark);
                    Set(b, a, dark);
                }
            }
        }

        private void Finder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if ((uint)x < (uint)_size && (uint)y < (uint)_size)
                    {
                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        Set(x, y, distance != 2 && distance != 4);
                    }
                }
            }
        }

        private int[] AlignmentPositions()
        {
            if (_version == 1)
                return Array.Empty<int>();
            var count = _version / 7 + 2;
            var step = (_version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var result = new int[count];
            result[0] = 6;
            for (int i = count - 1, pos = _size - 7; i >= 1; i--, pos -= step)
                result[i] = pos;
            return result;
        }

        public void DrawFormat(int levelBits, int mask)
        {
            var data = (levelBits << 3) | mask;
            var remainder = data;
            for (var i = 0; i < 10; i++)
                remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            var bits = ((data << 10) | remainder) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;

            for (var i = 0; i <= 5; i++)
                Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (var i = 9; i < 15; i++)
                Set(14 - i, 8, Bit(i));

            for (var i = 0; i < 8; i++)
                Set(_size - 1 - i, 8, Bit(i));
            for (var i = 8; i < 15; i++)
                Set(8, _size - 15 + i, Bit(i));
            Set(8, _size - 8, true);
        }

        /// <summary>Places the codewords in the zigzag order of two-module columns, skipping function patterns.</summary>
        public void DrawCodewords(ReadOnlySpan<byte> data)
        {
            var i = 0;
            var total = data.Length * 8;
            for (var right = _size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                    right = 5;
                var upward = ((right + 1) & 2) == 0;
                for (var vert = 0; vert < _size; vert++)
                {
                    var y = upward ? _size - 1 - vert : vert;
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        if (!_function[y, x] && i < total)
                        {
                            Modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        /// <summary>XORs mask <paramref name="mask"/> onto the data modules; applying it twice undoes it.</summary>
        public void ApplyMask(int mask)
        {
            for (var y = 0; y < _size; y++)
            {
                for (var x = 0; x < _size; x++)
                {
                    if (_function[y, x])
                        continue;
                    var invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert)
                        Modules[y, x] = !Modules[y, x];
                }
            }
        }

        /// <summary>The penalty score of ISO/IEC 18004 section 7.8.3, used to pick the mask.</summary>
        public int Penalty()
        {
            var result = 0;
            var dark = 0;
            for (var a = 0; a < _size; a++)
            {
                result += LinePenalty(a, rows: true) + LinePenalty(a, rows: false);
                for (var b = 0; b < _size; b++)
                {
                    if (Modules[a, b])
                        dark++;
                    if (a + 1 < _size && b + 1 < _size)
                    {
                        var c = Modules[a, b];
                        if (c == Modules[a, b + 1] && c == Modules[a + 1, b] && c == Modules[a + 1, b + 1])
                            result += 3;
                    }
                }
            }
            var total = _size * _size;
            var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return result + k * 10;
        }

        private bool At(int line, int i, bool rows) => rows ? Modules[line, i] : Modules[i, line];

        private int LinePenalty(int line, bool rows)
        {
            var result = 0;
            var run = 1;
            for (var i = 1; i <= _size; i++)
            {
                if (i < _size && At(line, i, rows) == At(line, i - 1, rows))
                {
                    run++;
                    continue;
                }
                if (run >= 5)
                    result += run - 2;
                run = 1;
            }

            // Finder-like 1:1:3:1:1 patterns with four light modules on either side; outside the symbol counts as light.
            for (var i = -4; i + 11 <= _size + 4; i++)
            {
                if (Matches(line, i, rows, 0b00001011101) || Matches(line, i, rows, 0b10111010000))
                    result += 40;
            }
            return result;
        }

        private bool Matches(int line, int start, bool rows, int pattern)
        {
            for (var j = 0; j < 11; j++)
            {
                var p = start + j;
                var dark = (uint)p < (uint)_size && At(line, p, rows);
                if (dark != (((pattern >> (10 - j)) & 1) != 0))
                    return false;
            }
            return true;
        }
    }
}
