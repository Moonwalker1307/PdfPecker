namespace PdfPecker.Rendering.Images;

/// <summary>
/// The MQ arithmetic decoder shared by JPEG 2000 (ISO 15444-1 Annex C) and JBIG2 (ITU-T T.88 Annex E). Contexts are bytes
/// holding <c>state index &lt;&lt; 1 | MPS</c>. Reading past the end of the data supplies 0xFF bytes, as the standards
/// require for terminated segments.
/// </summary>
internal sealed class MqDecoder
{
    // Qe, next index after an MPS, next index after an LPS, and whether an LPS switches the MPS (Table C.2 / E.1).
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

    private static readonly bool[] Switch =
    {
        true, false, false, false, false, false, true, false, false, false, false, false, false, false, true, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false,
    };

    private byte[] _data = Array.Empty<byte>();
    private int _pos, _end;
    private uint _c, _a;
    private int _ct;

    public MqDecoder() { }

    public MqDecoder(byte[] data, int start, int end) => Init(data, start, end);

    /// <summary>Starts decoding <paramref name="data"/>[<paramref name="start"/>..<paramref name="end"/>) (INITDEC).</summary>
    public void Init(byte[] data, int start, int end)
    {
        _data = data;
        _end = Math.Min(end, data.Length);
        _pos = start;
        FillBytes = 0;
        _c = (uint)Byte(_pos) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>The position of the next unread byte.</summary>
    public int Position => _pos;

    private int Byte(int p) => p < _end ? _data[p] : 0xFF;

    /// <summary>How many times the decoder has been fed fill bytes after the end of its data (or a marker).</summary>
    public int FillBytes;

    private void ByteIn()
    {
        if (Byte(_pos) == 0xFF)
        {
            int b1 = Byte(_pos + 1);
            if (b1 > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
                FillBytes++;
            }
            else
            {
                _pos++;
                _c += (uint)b1 << 9;
                _ct = 7;
            }
        }
        else
        {
            _pos++;
            if (_pos >= _end) FillBytes++;
            _c += (uint)Byte(_pos) << 8;
            _ct = 8;
        }
    }

    /// <summary>Decodes one binary decision with context <paramref name="cx"/>[<paramref name="i"/>].</summary>
    public int Decode(byte[] cx, int i)
    {
        int st = cx[i];
        int index = st >> 1, mps = st & 1;
        uint qe = Qe[index];
        int d;
        _a -= qe;
        if ((_c >> 16) < qe)
        {
            // LPS exchange.
            if (_a < qe)
            {
                d = mps;
                cx[i] = (byte)(Nmps[index] << 1 | mps);
            }
            else
            {
                d = 1 - mps;
                cx[i] = (byte)(Nlps[index] << 1 | (Switch[index] ? 1 - mps : mps));
            }
            _a = qe;
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) != 0) return mps;
            // MPS exchange.
            if (_a < qe)
            {
                d = 1 - mps;
                cx[i] = (byte)(Nlps[index] << 1 | (Switch[index] ? 1 - mps : mps));
            }
            else
            {
                d = mps;
                cx[i] = (byte)(Nmps[index] << 1 | mps);
            }
        }
        // Renormalize.
        do
        {
            if (_ct == 0) ByteIn();
            _a <<= 1;
            _c <<= 1;
            _ct--;
        } while ((_a & 0x8000) == 0);
        return d;
    }
}
