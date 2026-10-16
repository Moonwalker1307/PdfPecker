using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// Small encoders for the stream filters the renderer must read (LZW, ASCII85, RunLength, CCITT fax, and a DC-only baseline
/// JPEG). They are written from the specifications and kept deliberately simple, so the renderer tests do not need
/// third-party sample files.
/// </summary>
internal static class TestEncoders
{
    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _cur, _count;

        public void Bit(int b)
        {
            _cur = (_cur << 1) | (b & 1);
            if (++_count == 8) { _bytes.Add((byte)_cur); _cur = 0; _count = 0; }
        }

        public void Bits(string pattern) { foreach (char c in pattern) Bit(c == '1' ? 1 : 0); }

        public void Bits(int value, int count) { for (int i = count - 1; i >= 0; i--) Bit((value >> i) & 1); }

        public void Align() { while (_count != 0) Bit(0); }

        public byte[] ToArray(int padBit = 0)
        {
            var copy = new BitWriter();
            copy._bytes.AddRange(_bytes);
            copy._cur = _cur;
            copy._count = _count;
            while (copy._count != 0) copy.Bit(padBit);
            return copy._bytes.ToArray();
        }
    }

    // ---------------------------------------------------------------- LZW

    /// <summary>PDF LZW (most significant bit first, 9 to 12 bit codes, table reset when full).</summary>
    public static byte[] Lzw(byte[] data, int earlyChange = 1)
    {
        var w = new BitWriter();
        var table = new Dictionary<(int, byte), int>();
        int next = 258, width = 9;
        w.Bits(256, width);
        int prefix = -1;
        foreach (byte b in data)
        {
            if (prefix < 0) { prefix = b; continue; }
            if (table.TryGetValue((prefix, b), out int code)) { prefix = code; continue; }
            w.Bits(prefix, width);
            table[(prefix, b)] = next++;
            if (next + earlyChange > (1 << width) && width < 12) width++;
            if (next >= 4094)
            {
                w.Bits(256, width);
                table.Clear();
                next = 258;
                width = 9;
            }
            prefix = b;
        }
        if (prefix >= 0)
        {
            w.Bits(prefix, width);
            // The decoder adds one more entry for this code, which may widen the next one.
            if (next + earlyChange + 1 > (1 << width) && width < 12) width++;
        }
        w.Bits(257, width);
        return w.ToArray();
    }

    // ---------------------------------------------------------------- ASCII85, ASCIIHex, RunLength

    public static byte[] Ascii85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (k < n ? data[i + k] : (byte)0);
            if (v == 0 && n == 4) { sb.Append('z'); continue; }
            var chars = new char[5];
            for (int k = 4; k >= 0; k--) { chars[k] = (char)('!' + v % 85); v /= 85; }
            sb.Append(chars, 0, n + 1);
        }
        sb.Append("~>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static byte[] AsciiHex(byte[] data) => Encoding.ASCII.GetBytes(FixturePdf.Hex(data) + ">");

    public static byte[] RunLength(byte[] data)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 128) run++;
            if (run >= 2)
            {
                o.Add((byte)(257 - run));
                o.Add(data[i]);
                i += run;
                continue;
            }
            int start = i, lit = 0;
            while (i < data.Length && lit < 128 && !(i + 1 < data.Length && data[i + 1] == data[i])) { i++; lit++; }
            o.Add((byte)(lit - 1));
            for (int k = start; k < start + lit; k++) o.Add(data[k]);
        }
        o.Add(128);
        return o.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(data);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- CCITT fax

    private static readonly string[] WhiteRuns =
        { "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000" };

    private static readonly string[] BlackRuns =
        { "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101" };

    private const string Eol = "000000000001";

    /// <summary>Encodes bitmap rows (true is black), at most 11 pixels wide so only terminating codes are needed.</summary>
    /// <param name="k">Negative for Group 4, 0 for one-dimensional Group 3, positive for mixed Group 3.</param>
    public static byte[] Ccitt(bool[][] rows, int k, bool eols = false, bool byteAlign = false)
    {
        var w = new BitWriter();
        bool[] reference = new bool[rows[0].Length];
        for (int r = 0; r < rows.Length; r++)
        {
            if (byteAlign) w.Align();
            bool oneD = k == 0 || (k > 0 && r % k == 0);
            if (k >= 0 && eols)
            {
                w.Bits(Eol);
                if (k > 0) w.Bits(oneD ? "1" : "0");
            }
            else if (k > 0) w.Bits(oneD ? "1" : "0");
            if (oneD) Row1D(w, rows[r]); else Row2D(w, rows[r], reference);
            reference = rows[r];
        }
        if (k < 0) w.Bits(Eol + Eol);
        return w.ToArray();
    }

    private static void Row1D(BitWriter w, bool[] row)
    {
        int x = 0;
        bool black = false;
        while (x < row.Length)
        {
            int start = x;
            while (x < row.Length && row[x] == black) x++;
            w.Bits((black ? BlackRuns : WhiteRuns)[x - start]);
            black = !black;
        }
    }

    private static bool Pix(bool[] line, int i) => i >= 0 && i < line.Length && line[i];

    private static void Row2D(BitWriter w, bool[] cur, bool[] reference)
    {
        int n = cur.Length, a0 = -1;
        bool color = false;
        while (a0 < n)
        {
            int a1 = n;
            for (int i = a0 + 1; i < n; i++) if (Pix(cur, i) != color) { a1 = i; break; }
            int b1 = n;
            for (int i = a0 + 1; i < n; i++) if (Pix(reference, i) != Pix(reference, i - 1) && Pix(reference, i) != color) { b1 = i; break; }
            int b2 = n;
            for (int i = b1 + 1; i < n; i++) if (Pix(reference, i) != Pix(reference, i - 1)) { b2 = i; break; }
            if (b2 < a1)
            {
                w.Bits("0001");
                a0 = b2;
            }
            else if (Math.Abs(a1 - b1) <= 3)
            {
                w.Bits((a1 - b1) switch { 0 => "1", 1 => "011", 2 => "000011", 3 => "0000011", -1 => "010", -2 => "000010", _ => "0000010" });
                a0 = a1;
                color = !color;
            }
            else
            {
                int a2 = n;
                for (int i = a1 + 1; i < n; i++) if (Pix(cur, i) == color) { a2 = i; break; }
                w.Bits("001");
                w.Bits((color ? BlackRuns : WhiteRuns)[a1 - Math.Max(a0, 0)]);
                w.Bits((color ? WhiteRuns : BlackRuns)[a2 - a1]);
                a0 = a2;
            }
        }
    }

    // ---------------------------------------------------------------- JPEG

    /// <summary>
    /// A baseline JPEG of one flat 8x8 block per component (no subsampling, quantization all ones, so the DC coefficient is
    /// eight times the level shift). With <paramref name="adobeTransform"/> set, an Adobe APP14 marker is written.
    /// </summary>
    public static byte[] FlatJpeg(byte[] components, int? adobeTransform)
    {
        var o = new List<byte> { 0xFF, 0xD8 };
        void Marker(byte code, params byte[] payload)
        {
            o.Add(0xFF); o.Add(code);
            o.Add((byte)((payload.Length + 2) >> 8)); o.Add((byte)(payload.Length + 2));
            o.AddRange(payload);
        }
        if (adobeTransform is int t) Marker(0xEE, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, (byte)t);
        Marker(0xDB, Enumerable.Repeat((byte)1, 65).Select((b, i) => i == 0 ? (byte)0 : b).ToArray());
        var sof = new List<byte> { 8, 0, 8, 0, 8, (byte)components.Length };
        for (int c = 0; c < components.Length; c++) { sof.Add((byte)(c + 1)); sof.Add(0x11); sof.Add(0); }
        Marker(0xC0, sof.ToArray());
        // DC table: twelve 4-bit codes, one per category. AC table: a single 1-bit code for end of block.
        var dc = new List<byte> { 0x00 };
        for (int len = 1; len <= 16; len++) dc.Add((byte)(len == 4 ? 12 : 0));
        for (int s = 0; s < 12; s++) dc.Add((byte)s);
        Marker(0xC4, dc.ToArray());
        var ac = new List<byte> { 0x10 };
        for (int len = 1; len <= 16; len++) ac.Add((byte)(len == 1 ? 1 : 0));
        ac.Add(0);
        Marker(0xC4, ac.ToArray());
        var sos = new List<byte> { (byte)components.Length };
        for (int c = 0; c < components.Length; c++) { sos.Add((byte)(c + 1)); sos.Add(0x00); }
        sos.AddRange(new byte[] { 0, 63, 0 });
        Marker(0xDA, sos.ToArray());

        var w = new BitWriter();
        foreach (byte level in components)
        {
            int dcValue = (level - 128) * 8; // the previous DC of each component is zero
            int size = 0;
            for (int m = Math.Abs(dcValue); m != 0; m >>= 1) size++;
            w.Bits(size, 4);
            if (size > 0) w.Bits(dcValue >= 0 ? dcValue : dcValue + (1 << size) - 1, size);
            w.Bit(0); // end of block
        }
        foreach (byte b in w.ToArray(1))
        {
            o.Add(b);
            if (b == 0xFF) o.Add(0);
        }
        o.Add(0xFF); o.Add(0xD9);
        return o.ToArray();
    }
}
