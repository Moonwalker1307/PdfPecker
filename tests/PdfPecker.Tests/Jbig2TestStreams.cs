namespace PdfPecker.Tests;

/// <summary>
/// Writes JBIG2 streams by hand for decoder paths that the fixture encoder does not produce: segment headers, values
/// coded with the standard Huffman tables (T.88 Annex B) and refinement bitmaps coded with an MQ encoder (Annex E).
/// The encoders follow the standard on their own, so a decoder that agrees with them decodes what was meant.
/// </summary>
internal static class Jbig2TestStreams
{
    public const int SymbolDictionary = 0, ImmediateTextRegion = 6, PageInformation = 48;

    /// <summary>
    /// A segment with a one-byte page association. Up to four referred segments fit in the short form of the count, more
    /// take the long form with its retention bits (7.2.4); each referred number takes the bytes the segment's number needs.
    /// </summary>
    public static byte[] Segment(int number, int type, int[] refs, byte[] data)
    {
        var s = new List<byte>();
        s.AddRange(U32(number));
        s.Add((byte)type);
        if (refs.Length <= 4) s.Add((byte)(refs.Length << 5));
        else
        {
            s.AddRange(U32(7 << 29 | refs.Length));
            s.AddRange(new byte[(refs.Length + 8) / 8]);
        }
        foreach (int r in refs)
        {
            if (number > 65536) s.AddRange(U32(r));
            else if (number > 256) s.AddRange(U16(r));
            else s.Add((byte)r);
        }
        s.Add(1);
        s.AddRange(U32(data.Length));
        s.AddRange(data);
        return s.ToArray();
    }

    /// <summary>A page information segment's data: the size, no resolution, white default pixel, no striping.</summary>
    public static byte[] PageInfo(int width, int height) =>
        U32(width).Concat(U32(height)).Concat(new byte[11]).ToArray();

    public static byte[] U32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    public static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    // ------------------------------------------------------------------ Huffman tables (Annex B)

    /// <summary>One line of a table: the lowest value, the prefix and range lengths, and whether it is the upper range or OOB.</summary>
    public readonly record struct Line(int Low, int PrefixLength, int RangeLength, bool Upper = false, bool Oob = false);

    public static readonly Line[] B1 = { new(0, 1, 4), new(16, 2, 8), new(272, 3, 16), new(65808, 3, 32, Upper: true) };

    public static readonly Line[] B2 =
        { new(0, 1, 0), new(1, 2, 0), new(2, 3, 0), new(3, 4, 3), new(11, 5, 6), new(75, 6, 32, Upper: true), new(0, 6, 0, Oob: true) };

    public static readonly Line[] B4 = { new(1, 1, 0), new(2, 2, 0), new(3, 3, 0), new(4, 4, 3), new(12, 5, 6), new(76, 5, 32, Upper: true) };

    public static readonly Line[] B6 =
    {
        new(-2048, 5, 10), new(-1024, 4, 9), new(-512, 4, 8), new(-256, 4, 7), new(-128, 5, 6), new(-64, 5, 5), new(-32, 4, 5),
        new(0, 2, 7), new(128, 3, 7), new(256, 3, 8), new(512, 4, 9), new(1024, 4, 10), new(-2049, 6, 32), new(2048, 6, 32, Upper: true),
    };

    public static readonly Line[] B8 =
    {
        new(-15, 8, 3), new(-7, 9, 1), new(-5, 8, 1), new(-3, 9, 0), new(-2, 7, 0), new(-1, 4, 0), new(0, 2, 1), new(2, 5, 0),
        new(3, 6, 0), new(4, 3, 4), new(20, 6, 1), new(22, 4, 4), new(38, 4, 5), new(70, 5, 6), new(134, 5, 7), new(262, 6, 7),
        new(390, 7, 8), new(646, 6, 10), new(-16, 9, 32), new(1670, 9, 32, Upper: true), new(0, 2, 0, Oob: true),
    };

    public static readonly Line[] B11 =
    {
        new(1, 1, 0), new(2, 2, 1), new(4, 4, 0), new(5, 4, 1), new(7, 5, 1), new(9, 5, 2), new(13, 6, 2), new(17, 7, 2),
        new(21, 7, 3), new(29, 7, 4), new(45, 7, 5), new(77, 7, 6), new(141, 7, 32, Upper: true),
    };

    public static readonly Line[] B15 =
    {
        new(-24, 7, 4), new(-8, 6, 2), new(-4, 5, 1), new(-2, 4, 0), new(-1, 3, 0), new(0, 1, 0), new(1, 3, 0), new(2, 4, 0),
        new(3, 5, 1), new(5, 6, 2), new(9, 7, 4), new(-25, 7, 32), new(25, 7, 32, Upper: true),
    };

    /// <summary>A bit writer, most significant bit first, with the Huffman coding of B.3 for the tables above.</summary>
    public sealed class Bits
    {
        private readonly List<byte> _bytes = new();
        private int _bit;

        public Bits Write(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_bit == 0) _bytes.Add(0);
                if ((value >> i & 1) != 0) _bytes[^1] |= (byte)(0x80 >> _bit);
                _bit = (_bit + 1) & 7;
            }
            return this;
        }

        public Bits Huffman(Line[] table, int value)
        {
            for (int i = 0; i < table.Length; i++)
            {
                var line = table[i];
                if (line.Oob || value < line.Low || !line.Upper && (long)value - line.Low >= 1L << line.RangeLength) continue;
                // The lower range line (32 bits below the table) is not needed by these tests.
                if (line.RangeLength == 32 && !line.Upper) continue;
                return Write(Code(table, i), line.PrefixLength).Write((long)value - line.Low, line.RangeLength);
            }
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        public Bits Oob(Line[] table)
        {
            int i = Array.FindIndex(table, l => l.Oob);
            return Write(Code(table, i), table[i].PrefixLength);
        }

        public Bits Align()
        {
            _bit = 0;
            return this;
        }

        public Bits Bytes(byte[] data)
        {
            Align();
            _bytes.AddRange(data);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();

        /// <summary>The canonical code of line <paramref name="index"/> (B.3): shorter prefixes first, then in table order.</summary>
        private static int Code(Line[] table, int index)
        {
            int code = 0;
            for (int length = 1; length <= table[index].PrefixLength; length++)
            {
                for (int i = 0; i < table.Length; i++)
                {
                    if (table[i].PrefixLength != length) continue;
                    if (i == index) return code;
                    code++;
                }
                code <<= 1;
            }
            throw new InvalidOperationException();
        }
    }

    // ------------------------------------------------------------------ MQ encoding (Annex E)

    private static readonly ushort[] Qe =
    {
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
        0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
        0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
        0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    };

    private static readonly byte[] Nmps =
    {
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    };

    private static readonly byte[] Nlps =
    {
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    };

    /// <summary>The MQ encoder of E.2, with the byte stuffing after 0xFF and the 0xFF 0xAC terminator.</summary>
    public sealed class MqEncoder
    {
        private readonly List<byte> _out = new();
        private readonly int[] _index, _mps;
        private uint _a = 0x8000, _c;
        private int _ct = 12, _b = -1; // the byte being built; -1 is the place before the first output byte

        public MqEncoder(int contexts)
        {
            _index = new int[contexts];
            _mps = new int[contexts];
        }

        public void Encode(int cx, int d)
        {
            int i = _index[cx];
            uint qe = Qe[i];
            _a -= qe;
            if (d == _mps[cx])
            {
                if ((_a & 0x8000) != 0)
                {
                    _c += qe;
                    return;
                }
                if (_a < qe) _a = qe;
                else _c += qe;
                _index[cx] = Nmps[i];
            }
            else
            {
                if (_a < qe) _c += qe;
                else _a = qe;
                if (i is 0 or 6 or 14) _mps[cx] = 1 - _mps[cx]; // the states whose LPS switches the MPS
                _index[cx] = Nlps[i];
            }
            do
            {
                _a <<= 1;
                _c <<= 1;
                if (--_ct == 0) ByteOut();
            } while ((_a & 0x8000) == 0);
        }

        public byte[] Flush()
        {
            uint temp = _c + _a;
            _c |= 0xFFFF;
            if (_c >= temp) _c -= 0x8000;
            _c <<= _ct;
            ByteOut();
            _c <<= _ct;
            ByteOut();
            if (_b != 0xFF) Emit(0xFF);
            Emit(0xAC);
            if (_b >= 0) _out.Add((byte)_b);
            return _out.ToArray();
        }

        private void ByteOut()
        {
            if (_b == 0xFF)
            {
                Emit((int)(_c >> 20));
                _c &= 0xFFFFF;
                _ct = 7;
                return;
            }
            if (_c >= 0x8000000)
            {
                _b++;
                if (_b == 0xFF)
                {
                    _c &= 0x7FFFFFF;
                    Emit((int)(_c >> 20));
                    _c &= 0xFFFFF;
                    _ct = 7;
                    return;
                }
            }
            Emit((int)(_c >> 19));
            _c &= 0x7FFFF;
            _ct = 8;
        }

        private void Emit(int next)
        {
            if (_b >= 0) _out.Add((byte)_b);
            _b = next;
        }
    }

    /// <summary>
    /// Codes <paramref name="target"/> as a refinement of <paramref name="reference"/> placed at (dx, dy), with template 1
    /// and fresh contexts (6.3.5.3, Figure 13). The context numbering does not matter, only which pixels form it.
    /// </summary>
    public static byte[] Refinement(int[,] target, int[,] reference, int dx, int dy)
    {
        int h = target.GetLength(0), w = target.GetLength(1);
        int T(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? target[y, x] : 0;
        int R(int x, int y) => (uint)x < (uint)reference.GetLength(1) && (uint)y < (uint)reference.GetLength(0) ? reference[y, x] : 0;
        var mq = new MqEncoder(1 << 10);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int rx = x - dx, ry = y - dy;
                int cx = T(x - 1, y) | T(x + 1, y - 1) << 1 | T(x, y - 1) << 2 | T(x - 1, y - 1) << 3
                    | R(rx + 1, ry + 1) << 4 | R(rx, ry + 1) << 5 | R(rx + 1, ry) << 6 | R(rx, ry) << 7 | R(rx - 1, ry) << 8
                    | R(rx, ry - 1) << 9;
                mq.Encode(cx, target[y, x]);
            }
        return mq.Flush();
    }
}
