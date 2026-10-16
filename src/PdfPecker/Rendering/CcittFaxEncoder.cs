using PdfPecker.Rendering.Images;

namespace PdfPecker.Rendering;

/// <summary>
/// Encodes bilevel rows as CCITT Group 4 (ITU-T T.6, "MMR"), the compression fax machines and document archives use.
/// Each row is coded against the one above it, so text pages shrink to a few kilobytes.
/// </summary>
internal static class CcittFaxEncoder
{
    // Run-length codes as (bits, length), indexed by run: 0-63 terminating, then the make-up codes for 64 to 2560.
    private static readonly (int Bits, int Length)[] WhiteTerminating = new (int, int)[64], BlackTerminating = new (int, int)[64];
    private static readonly (int Bits, int Length)[] WhiteMakeup = new (int, int)[41], BlackMakeup = new (int, int)[41];

    static CcittFaxEncoder()
    {
        // The decoder's tables are the single copy of T.4's codes, so the two sides cannot drift apart.
        Fill(CcittFaxDecoder.WhiteCodes(), WhiteTerminating, WhiteMakeup);
        Fill(CcittFaxDecoder.BlackCodes(), BlackTerminating, BlackMakeup);
    }

    private static void Fill(List<(string Code, int Run)> codes, (int, int)[] terminating, (int, int)[] makeup)
    {
        foreach (var (code, run) in codes)
        {
            var entry = (Convert.ToInt32(code, 2), code.Length);
            if (run < 64) terminating[run] = entry;
            else makeup[run / 64] = entry;
        }
    }

    /// <summary>Encodes rows of one byte per pixel (1 black, 0 white), ending with the end-of-facsimile-block code.</summary>
    public static byte[] EncodeGroup4(byte[] pixels, int width, int height)
    {
        var w = new BitWriter(pixels.Length / 40 + 64);
        // The row above the first is an imaginary white row.
        var white = new byte[width];
        for (int y = 0; y < height; y++)
            EncodeRow(w, pixels.AsSpan(y * width, width), y == 0 ? white : pixels.AsSpan((y - 1) * width, width));
        w.Write(0x001, 12);
        w.Write(0x001, 12);
        return w.ToArray();
    }

    /// <summary>
    /// Codes one row against the reference row with the pass, vertical and horizontal modes of T.4 section 4.2.1.3,
    /// following libtiff's loop, which is the reference most decoders are tested against.
    /// </summary>
    private static void EncodeRow(BitWriter w, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> reference)
    {
        int width = cur.Length;
        int a0 = 0;
        int a1 = cur[0] != 0 ? 0 : FindDiff(cur, 0, 0);
        int b1 = reference[0] != 0 ? 0 : FindDiff(reference, 0, 0);
        while (true)
        {
            int b2 = b1 >= width ? width : FindDiff(reference, b1, reference[b1]);
            if (b2 >= a1)
            {
                int d = a1 - b1;
                if (d is >= -3 and <= 3)
                {
                    WriteVertical(w, d);
                    a0 = a1;
                }
                else
                {
                    int a2 = a1 >= width ? width : FindDiff(cur, a1, cur[a1]);
                    w.Write(0b001, 3);
                    // At the start of a row a0 is the imaginary white pixel before the first one.
                    bool whiteFirst = a0 + a1 == 0 || cur[a0] == 0;
                    WriteRun(w, a1 - a0, whiteFirst);
                    WriteRun(w, a2 - a1, !whiteFirst);
                    a0 = a2;
                }
            }
            else
            {
                w.Write(0b0001, 4);
                a0 = b2;
            }
            if (a0 >= width) break;
            byte color = cur[a0];
            a1 = FindDiff(cur, a0, color);
            b1 = FindDiff(reference, a0, (byte)(color ^ 1));
            b1 = FindDiff(reference, b1, color);
        }
    }

    /// <summary>The first position at or after <paramref name="from"/> whose pixel is not <paramref name="color"/>, or the width.</summary>
    private static int FindDiff(ReadOnlySpan<byte> line, int from, byte color)
    {
        if (from >= line.Length) return line.Length;
        int i = line[from..].IndexOfAnyExcept(color);
        return i < 0 ? line.Length : from + i;
    }

    private static void WriteVertical(BitWriter w, int d)
    {
        switch (d)
        {
            case 0: w.Write(0b1, 1); break;
            case 1: w.Write(0b011, 3); break;
            case 2: w.Write(0b000011, 6); break;
            case 3: w.Write(0b0000011, 7); break;
            case -1: w.Write(0b010, 3); break;
            case -2: w.Write(0b000010, 6); break;
            default: w.Write(0b0000010, 7); break;
        }
    }

    private static void WriteRun(BitWriter w, int run, bool white)
    {
        var makeup = white ? WhiteMakeup : BlackMakeup;
        var terminating = white ? WhiteTerminating : BlackTerminating;
        // Runs longer than the biggest make-up code repeat it; T.4 allows any number of make-up codes before the
        // terminating one.
        while (run >= 2560 + 64)
        {
            w.Write(makeup[40].Bits, makeup[40].Length);
            run -= 2560;
        }
        if (run >= 64)
        {
            var m = makeup[run / 64];
            w.Write(m.Bits, m.Length);
            run %= 64;
        }
        w.Write(terminating[run].Bits, terminating[run].Length);
    }

    /// <summary>Collects codes most significant bit first, as TIFF's default FillOrder expects.</summary>
    private sealed class BitWriter
    {
        private byte[] _buffer;
        private int _length;
        private ulong _bits;
        private int _count;

        public BitWriter(int capacity) => _buffer = new byte[Math.Max(64, capacity)];

        public void Write(int code, int length)
        {
            _bits = _bits << length | (uint)code;
            _count += length;
            while (_count >= 8)
            {
                _count -= 8;
                if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
                _buffer[_length++] = (byte)(_bits >> _count);
            }
        }

        public byte[] ToArray()
        {
            if (_count > 0) Write(0, 8 - _count);
            return _buffer.AsSpan(0, _length).ToArray();
        }
    }
}
