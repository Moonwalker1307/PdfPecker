namespace PdfPecker.Rendering.Images;

/// <summary>
/// Baseline and progressive JPEG decoder (Huffman coding, any sampling factors, restart intervals, 1/3/4 components,
/// Adobe color transforms). Output is interleaved 8-bit components as stored: gray, RGB, or CMYK (Adobe CMYK JPEGs
/// keep their inverted values; PDF files correct those with a Decode array).
/// </summary>
internal sealed class JpegDecoder
{
    private sealed class Component
    {
        public int Id, H, V, Tq;
        public int BlocksPerLine, BlocksPerColumn;
        public short[] Coeffs = Array.Empty<short>();
        public int Pred;
        public int DcTable, AcTable;
        public byte[] Plane = Array.Empty<byte>();
        public int PlaneStride;
    }

    private sealed class Huffman
    {
        public readonly ushort[] Fast = new ushort[512]; // (length << 8 | symbol) for codes up to 9 bits, 0 = slow path
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValOffset = new int[18];
        public byte[] Values = Array.Empty<byte>();
    }

    private static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55,
        62, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    };

    private readonly byte[] _d;
    private int _pos;
    private readonly ushort[][] _qt = new ushort[4][];
    private readonly Huffman?[] _dc = new Huffman?[4], _ac = new Huffman?[4];
    private Component[] _comps = Array.Empty<Component>();
    private int _width, _height, _mcusX, _mcusY, _hMax, _vMax;
    private bool _progressive;
    private int _restartInterval;
    private int _adobeTransform = -1;
    private bool _jfif;
    private bool? _colorTransform; // set by the container (TIFF says whether its JPEG data is YCbCr), else from markers
    private int _eobRun;

    // Bit reader.
    private uint _bits;
    private int _bitCount;
    private bool _hitMarker;

    public int Width => _width;
    public int Height => _height;
    public int Components => _comps.Length;

    private JpegDecoder(byte[] d) => _d = d;

    /// <summary>Decodes a JPEG. Returns null if the data is not a supported JPEG.</summary>
    public static (byte[] Data, int Width, int Height, int Components)? Decode(byte[] data) => Decode(data, null);

    /// <summary>
    /// Decodes a JPEG whose three components are YCbCr (<paramref name="colorTransform"/> true) or RGB (false), or as
    /// its markers say (null).
    /// </summary>
    public static (byte[] Data, int Width, int Height, int Components)? Decode(byte[] data, bool? colorTransform)
    {
        var j = new JpegDecoder(data) { _colorTransform = colorTransform };
        try
        {
            var result = j.Run();
            return result == null ? null : (result, j._width, j._height, j._comps.Length);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            // Truncated or corrupt: return what was decoded if the frame is known.
            if (j._comps.Length == 0 || j._width == 0) return null;
            try { return (j.Output(), j._width, j._height, j._comps.Length); }
            catch (Exception ex2) when (ex2 is IndexOutOfRangeException or ArgumentException) { return null; }
        }
    }

    /// <summary>Reads only the frame header: dimensions and component count.</summary>
    public static (int Width, int Height, int Components)? Info(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            int m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xD8 || m is >= 0xD0 and <= 0xD7 || m == 0x01) { i += 2; continue; }
            int len = d[i + 2] << 8 | d[i + 3];
            if (m is 0xC0 or 0xC1 or 0xC2) return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6], d[i + 9]);
            i += 2 + len;
        }
        return null;
    }

    private byte[]? Run()
    {
        var d = _d;
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        _pos = 2;
        bool frame = false;
        while (_pos < d.Length)
        {
            if (d[_pos] != 0xFF) { _pos++; continue; }
            int marker = d[_pos + 1];
            _pos += 2;
            if (marker == 0xFF) { _pos--; continue; }
            if (marker == 0xD9) break; // EOI
            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01 || marker == 0x00) continue;
            int len = d[_pos] << 8 | d[_pos + 1];
            int segEnd = _pos + len;
            switch (marker)
            {
                case 0xDB: ReadDqt(_pos + 2, segEnd); break;
                case 0xC4: ReadDht(_pos + 2, segEnd); break;
                case 0xC0: case 0xC1: case 0xC2:
                    _progressive = marker == 0xC2;
                    ReadSof(_pos + 2);
                    frame = true;
                    break;
                case 0xC3: case 0xC5: case 0xC6: case 0xC7: case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                    return null; // lossless, hierarchical or arithmetic coding
                case 0xDD: _restartInterval = d[_pos + 2] << 8 | d[_pos + 3]; break;
                case 0xEE:
                    if (len >= 12 && d[_pos + 2] == 'A' && d[_pos + 3] == 'd' && d[_pos + 4] == 'o' && d[_pos + 5] == 'b' && d[_pos + 6] == 'e')
                        _adobeTransform = d[_pos + 13];
                    break;
                case 0xE0:
                    if (len >= 7 && d[_pos + 2] == 'J' && d[_pos + 3] == 'F' && d[_pos + 4] == 'I' && d[_pos + 5] == 'F') _jfif = true;
                    break;
                case 0xDA:
                    if (!frame) return null;
                    segEnd = ReadScan(_pos + 2);
                    _pos = segEnd;
                    continue;
            }
            _pos = segEnd;
        }
        if (!frame) return null;
        return Output();
    }

    private void ReadDqt(int p, int end)
    {
        var d = _d;
        while (p < end)
        {
            int pq = d[p] >> 4, tq = d[p] & 15;
            p++;
            var t = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                t[ZigZag[i]] = pq == 0 ? d[p + i] : (ushort)(d[p + 2 * i] << 8 | d[p + 2 * i + 1]);
            }
            p += pq == 0 ? 64 : 128;
            _qt[tq & 3] = t;
        }
    }

    private void ReadDht(int p, int end)
    {
        var d = _d;
        while (p + 17 <= end)
        {
            int tc = d[p] >> 4, th = d[p] & 3;
            p++;
            var counts = new int[17];
            int total = 0;
            for (int i = 1; i <= 16; i++) { counts[i] = d[p + i - 1]; total += counts[i]; }
            p += 16;
            var h = new Huffman { Values = d.AsSpan(p, Math.Min(total, end - p)).ToArray() };
            p += total;
            // Canonical codes.
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                h.ValOffset[len] = k - code;
                for (int i = 0; i < counts[len]; i++, k++, code++)
                {
                    if (len <= 9 && k < h.Values.Length)
                    {
                        int shift = 9 - len;
                        int start = code << shift;
                        for (int f = 0; f < 1 << shift; f++) h.Fast[start + f] = (ushort)(len << 8 | h.Values[k]);
                    }
                }
                h.MaxCode[len] = counts[len] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            h.MaxCode[17] = int.MaxValue;
            if (tc == 0) _dc[th] = h;
            else _ac[th] = h;
        }
    }

    private void ReadSof(int p)
    {
        var d = _d;
        if (d[p] != 8) throw new InvalidDataException("Only 8-bit JPEG is supported.");
        _height = d[p + 1] << 8 | d[p + 2];
        _width = d[p + 3] << 8 | d[p + 4];
        int n = d[p + 5];
        if (_width <= 0 || n < 1 || n > 4) throw new InvalidDataException("Bad JPEG frame.");
        if (_height == 0) _height = 1; // DNL-defined height: decode what we can
        if ((long)_width * _height > PdfImageDecoder.MaxPixels) throw new InvalidDataException("JPEG too large.");
        // The components are published only once the whole header has been read: a truncated header must not leave
        // half-built components behind for the partial output that Decode makes of a damaged file.
        var comps = new Component[n];
        for (int i = 0; i < n; i++)
        {
            int o = p + 6 + i * 3;
            comps[i] = new Component { Id = d[o], H = Math.Max(1, d[o + 1] >> 4), V = Math.Max(1, d[o + 1] & 15), Tq = d[o + 2] & 3 };
        }
        _hMax = comps.Max(c => c.H);
        _vMax = comps.Max(c => c.V);
        _mcusX = (_width + 8 * _hMax - 1) / (8 * _hMax);
        _mcusY = (_height + 8 * _vMax - 1) / (8 * _vMax);
        foreach (var c in comps)
        {
            c.BlocksPerLine = _mcusX * c.H;
            c.BlocksPerColumn = _mcusY * c.V;
            c.Coeffs = new short[c.BlocksPerLine * c.BlocksPerColumn * 64];
        }
        _comps = comps;
    }

    // ------------------------------------------------------------------ scans

    private int ReadScan(int p)
    {
        var d = _d;
        int len = d[p - 2] << 8 | d[p - 1];
        int n = d[p];
        var scomps = new Component[n];
        for (int i = 0; i < n; i++)
        {
            int id = d[p + 1 + i * 2], tables = d[p + 2 + i * 2];
            var c = _comps.FirstOrDefault(x => x.Id == id) ?? _comps[Math.Min(i, _comps.Length - 1)];
            c.DcTable = tables >> 4 & 3;
            c.AcTable = tables & 3;
            scomps[i] = c;
        }
        int q = p + 1 + n * 2;
        int ss = d[q], se = d[q + 1], ah = d[q + 2] >> 4, al = d[q + 2] & 15;
        _pos = p - 2 + len;
        foreach (var c in _comps) c.Pred = 0;
        _eobRun = 0;
        ResetBits();

        bool single = n == 1;
        int total;
        int mcusPerLine;
        if (single)
        {
            var c = scomps[0];
            int bw = (int)Math.Ceiling(Math.Ceiling(_width * c.H / (double)_hMax) / 8);
            int bh = (int)Math.Ceiling(Math.Ceiling(_height * c.V / (double)_vMax) / 8);
            mcusPerLine = bw;
            total = bw * bh;
        }
        else
        {
            mcusPerLine = _mcusX;
            total = _mcusX * _mcusY;
        }

        int restart = _restartInterval;
        for (int m = 0; m < total; m++)
        {
            if (restart > 0 && m > 0 && m % restart == 0)
            {
                if (!SyncRestart()) break;
                foreach (var c in _comps) c.Pred = 0;
                _eobRun = 0;
            }
            if (single)
            {
                var c = scomps[0];
                int row = m / mcusPerLine, col = m % mcusPerLine;
                DecodeBlock(c, (row * c.BlocksPerLine + col) * 64, ss, se, ah, al);
            }
            else
            {
                int mrow = m / mcusPerLine, mcol = m % mcusPerLine;
                foreach (var c in scomps)
                {
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                        {
                            int brow = mrow * c.V + v, bcol = mcol * c.H + h;
                            DecodeBlock(c, (brow * c.BlocksPerLine + bcol) * 64, ss, se, ah, al);
                        }
                }
            }
            if (_hitMarker && _bitCount <= 0 && m + 1 < total && (restart == 0 || (m + 1) % restart != 0)) break;
        }
        // Continue after the entropy-coded data: find the next marker that is not a restart marker.
        int e = _pos;
        while (e + 1 < d.Length)
        {
            if (d[e] == 0xFF && d[e + 1] != 0 && !(d[e + 1] >= 0xD0 && d[e + 1] <= 0xD7) && d[e + 1] != 0xFF) break;
            e++;
        }
        return e;
    }

    /// <summary>A Huffman table a scan uses; a damaged file can name one it never defined.</summary>
    private static Huffman Table(Huffman?[] tables, int index) => tables[index] ?? throw new InvalidDataException("Undefined Huffman table");

    private void DecodeBlock(Component c, int offset, int ss, int se, int ah, int al)
    {
        var blk = c.Coeffs.AsSpan(offset, 64);
        if (!_progressive)
        {
            int t = Receive(Table(_dc, c.DcTable));
            int diff = t == 0 ? 0 : Extend(GetBits(t), t);
            c.Pred += diff;
            blk[0] = (short)c.Pred;
            var ac = Table(_ac, c.AcTable);
            for (int k = 1; k < 64;)
            {
                int rs = Receive(ac);
                int r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r == 15) { k += 16; continue; }
                    break;
                }
                k += r;
                if (k > 63) break;
                blk[ZigZag[k]] = (short)Extend(GetBits(s), s);
                k++;
            }
            return;
        }
        if (ss == 0)
        {
            // DC scan.
            if (ah == 0)
            {
                int t = Receive(Table(_dc, c.DcTable));
                int diff = t == 0 ? 0 : Extend(GetBits(t), t);
                c.Pred += diff;
                blk[0] = (short)(c.Pred << al);
            }
            else if (GetBits(1) != 0) blk[0] |= (short)(1 << al);
            return;
        }
        if (ah == 0)
        {
            // AC first pass.
            if (_eobRun > 0) { _eobRun--; return; }
            var ac = Table(_ac, c.AcTable);
            for (int k = ss; k <= se;)
            {
                int rs = Receive(ac);
                int r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        _eobRun = (1 << r) - 1;
                        if (r > 0) _eobRun += GetBits(r);
                        break;
                    }
                    k += 16;
                    continue;
                }
                k += r;
                if (k > 63) break;
                blk[ZigZag[k]] = (short)(Extend(GetBits(s), s) * (1 << al));
                k++;
            }
            return;
        }
        // AC refinement.
        {
            int p1 = 1 << al, m1 = -1 << al;
            var ac = Table(_ac, c.AcTable);
            int k = ss;
            if (_eobRun <= 0)
            {
                for (; k <= se;)
                {
                    int rs = Receive(ac);
                    int r = rs >> 4, s = rs & 15;
                    int value = 0;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            _eobRun = (1 << r);
                            if (r > 0) _eobRun += GetBits(r);
                            break;
                        }
                    }
                    else value = GetBits(1) != 0 ? p1 : m1;
                    while (k <= se)
                    {
                        int z = ZigZag[k];
                        if (blk[z] != 0)
                        {
                            if (GetBits(1) != 0 && (blk[z] & p1) == 0) blk[z] = (short)(blk[z] >= 0 ? blk[z] + p1 : blk[z] + m1);
                        }
                        else
                        {
                            if (r == 0)
                            {
                                if (value != 0) blk[z] = (short)value;
                                k++;
                                break;
                            }
                            r--;
                        }
                        k++;
                    }
                }
            }
            if (_eobRun > 0)
            {
                for (; k <= se; k++)
                {
                    int z = ZigZag[k];
                    if (blk[z] != 0 && GetBits(1) != 0 && (blk[z] & p1) == 0) blk[z] = (short)(blk[z] >= 0 ? blk[z] + p1 : blk[z] + m1);
                }
                _eobRun--;
            }
        }
    }

    // ------------------------------------------------------------------ bits

    private void ResetBits()
    {
        _bits = 0;
        _bitCount = 0;
        _hitMarker = false;
    }

    private void Fill()
    {
        var d = _d;
        while (_bitCount <= 24)
        {
            int b = 0;
            if (!_hitMarker && _pos < d.Length)
            {
                b = d[_pos];
                if (b == 0xFF)
                {
                    int next = _pos + 1 < d.Length ? d[_pos + 1] : 0;
                    if (next == 0) { _pos += 2; }
                    else if (next == 0xFF) { _pos++; continue; }
                    else { _hitMarker = true; b = 0; }
                }
                else _pos++;
            }
            else if (_pos >= d.Length) _hitMarker = true;
            _bits |= (uint)b << (24 - _bitCount);
            _bitCount += 8;
        }
    }

    private int GetBits(int n)
    {
        if (n == 0) return 0;
        if (_bitCount < n) Fill();
        int v = (int)(_bits >> (32 - n));
        _bits <<= n;
        _bitCount -= n;
        return v;
    }

    private int Receive(Huffman h)
    {
        if (_bitCount < 16) Fill();
        int fast = h.Fast[_bits >> 23];
        if (fast != 0)
        {
            int len = fast >> 8;
            _bits <<= len;
            _bitCount -= len;
            return fast & 255;
        }
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            code = code << 1 | (int)(_bits >> 31);
            _bits <<= 1;
            _bitCount--;
            if (code <= h.MaxCode[len])
            {
                int idx = code + h.ValOffset[len];
                return idx >= 0 && idx < h.Values.Length ? h.Values[idx] : 0;
            }
        }
        return 0; // corrupt
    }

    private static int Extend(int v, int t) => v < 1 << (t - 1) ? v - (1 << t) + 1 : v;

    private bool SyncRestart()
    {
        var d = _d;
        // Discard remaining bits and find the RSTn marker.
        int p = _pos;
        while (p + 1 < d.Length)
        {
            if (d[p] == 0xFF && d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7) { _pos = p + 2; ResetBits(); return true; }
            if (d[p] == 0xFF && d[p + 1] != 0 && d[p + 1] != 0xFF) { _pos = p; ResetBits(); return false; }
            p++;
        }
        return false;
    }

    // ------------------------------------------------------------------ output

    private byte[] Output()
    {
        // Dequantize and inverse-transform every block into component planes.
        Span<int> tmp = stackalloc int[64];
        foreach (var c in _comps)
        {
            var q = _qt[c.Tq] ?? _qt.FirstOrDefault(t => t != null) ?? Enumerable.Repeat((ushort)1, 64).ToArray();
            c.PlaneStride = c.BlocksPerLine * 8;
            c.Plane = new byte[c.PlaneStride * c.BlocksPerColumn * 8];
            for (int by = 0; by < c.BlocksPerColumn; by++)
                for (int bx = 0; bx < c.BlocksPerLine; bx++)
                {
                    var blk = c.Coeffs.AsSpan((by * c.BlocksPerLine + bx) * 64, 64);
                    Idct(blk, q, c.Plane, by * 8 * c.PlaneStride + bx * 8, c.PlaneStride, tmp);
                }
            c.Coeffs = Array.Empty<short>();
        }

        int n = _comps.Length;
        var output = new byte[_width * _height * n];
        bool transform = n == 3 && _colorTransform is { } forced ? forced
            : n == 3 ? _adobeTransform != 0 || _jfif || _adobeTransform < 0 && !(_comps[0].Id == 'R' && _comps[1].Id == 'G' && _comps[2].Id == 'B')
            : n == 4 && _adobeTransform == 2;
        var rowBufs = new int[n][];
        for (int i = 0; i < n; i++) rowBufs[i] = new int[_width];
        for (int y = 0; y < _height; y++)
        {
            for (int ci = 0; ci < n; ci++)
            {
                var c = _comps[ci];
                int sy = y * c.V / _vMax;
                int rowOff = sy * c.PlaneStride;
                var rb = rowBufs[ci];
                if (c.H == _hMax) for (int x = 0; x < _width; x++) rb[x] = c.Plane[rowOff + x];
                else for (int x = 0; x < _width; x++) rb[x] = c.Plane[rowOff + x * c.H / _hMax];
            }
            int o = y * _width * n;
            if (n == 1)
            {
                var r0 = rowBufs[0];
                for (int x = 0; x < _width; x++) output[o + x] = (byte)r0[x];
            }
            else if (n == 3)
            {
                var r0 = rowBufs[0]; var r1 = rowBufs[1]; var r2 = rowBufs[2];
                for (int x = 0; x < _width; x++, o += 3)
                {
                    if (transform) YccToRgb(r0[x], r1[x], r2[x], output, o);
                    else { output[o] = (byte)r0[x]; output[o + 1] = (byte)r1[x]; output[o + 2] = (byte)r2[x]; }
                }
            }
            else
            {
                var r0 = rowBufs[0]; var r1 = rowBufs[1]; var r2 = rowBufs[2]; var r3 = rowBufs[3];
                for (int x = 0; x < _width; x++, o += 4)
                {
                    if (transform)
                    {
                        // YCCK: convert YCC to RGB, then store the complements as CMY.
                        YccToRgb(r0[x], r1[x], r2[x], output, o);
                        output[o] = (byte)(255 - output[o]);
                        output[o + 1] = (byte)(255 - output[o + 1]);
                        output[o + 2] = (byte)(255 - output[o + 2]);
                    }
                    else { output[o] = (byte)r0[x]; output[o + 1] = (byte)r1[x]; output[o + 2] = (byte)r2[x]; }
                    output[o + 3] = (byte)r3[x];
                }
            }
        }
        return output;
    }

    private static void YccToRgb(int y, int cb, int cr, byte[] o, int i)
    {
        cb -= 128;
        cr -= 128;
        int yy = y << 16;
        int r = (yy + 91881 * cr + 32768) >> 16;
        int g = (yy - 22554 * cb - 46802 * cr + 32768) >> 16;
        int b = (yy + 116130 * cb + 32768) >> 16;
        o[i] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
        o[i + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
        o[i + 2] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
    }

    // Fixed-point constants (scaled by 4096) for the Loeffler-Ligtenberg-Moschytz IDCT.
    private const int F0298 = 1223, F0390 = 1598, F0541 = 2217, F0765 = 3135, F0899 = 3686, F1175 = 4816, F1501 = 6149,
        F1847 = 7568, F1961 = 8035, F2053 = 8410, F2562 = 10498, F3072 = 12586;

    private static void Idct(ReadOnlySpan<short> blk, ushort[] q, byte[] plane, int outOff, int stride, Span<int> v)
    {
        // Columns.
        for (int i = 0; i < 8; i++)
        {
            if (blk[8 + i] == 0 && blk[16 + i] == 0 && blk[24 + i] == 0 && blk[32 + i] == 0 && blk[40 + i] == 0 && blk[48 + i] == 0 && blk[56 + i] == 0)
            {
                int dc = blk[i] * q[i] * 4;
                for (int k = 0; k < 8; k++) v[k * 8 + i] = dc;
                continue;
            }
            Idct1D(blk[i] * q[i], blk[8 + i] * q[8 + i], blk[16 + i] * q[16 + i], blk[24 + i] * q[24 + i],
                blk[32 + i] * q[32 + i], blk[40 + i] * q[40 + i], blk[48 + i] * q[48 + i], blk[56 + i] * q[56 + i],
                out int x0, out int x1, out int x2, out int x3, out int t0, out int t1, out int t2, out int t3);
            x0 += 512; x1 += 512; x2 += 512; x3 += 512;
            v[i] = (x0 + t3) >> 10;
            v[56 + i] = (x0 - t3) >> 10;
            v[8 + i] = (x1 + t2) >> 10;
            v[48 + i] = (x1 - t2) >> 10;
            v[16 + i] = (x2 + t1) >> 10;
            v[40 + i] = (x2 - t1) >> 10;
            v[24 + i] = (x3 + t0) >> 10;
            v[32 + i] = (x3 - t0) >> 10;
        }
        // Rows.
        for (int r = 0; r < 8; r++)
        {
            int b = r * 8;
            Idct1D(v[b], v[b + 1], v[b + 2], v[b + 3], v[b + 4], v[b + 5], v[b + 6], v[b + 7],
                out int x0, out int x1, out int x2, out int x3, out int t0, out int t1, out int t2, out int t3);
            const int bias = 65536 + (128 << 17);
            x0 += bias; x1 += bias; x2 += bias; x3 += bias;
            int o = outOff + r * stride;
            plane[o] = Clamp((x0 + t3) >> 17);
            plane[o + 7] = Clamp((x0 - t3) >> 17);
            plane[o + 1] = Clamp((x1 + t2) >> 17);
            plane[o + 6] = Clamp((x1 - t2) >> 17);
            plane[o + 2] = Clamp((x2 + t1) >> 17);
            plane[o + 5] = Clamp((x2 - t1) >> 17);
            plane[o + 3] = Clamp((x3 + t0) >> 17);
            plane[o + 4] = Clamp((x3 - t0) >> 17);
        }
    }

    private static byte Clamp(int x) => (byte)(x < 0 ? 0 : x > 255 ? 255 : x);

    private static void Idct1D(int s0, int s1, int s2, int s3, int s4, int s5, int s6, int s7,
        out int x0, out int x1, out int x2, out int x3, out int t0, out int t1, out int t2, out int t3)
    {
        int p2 = s2, p3 = s6;
        int p1 = (p2 + p3) * F0541;
        t2 = p1 - p3 * F1847;
        t3 = p1 + p2 * F0765;
        p2 = s0;
        p3 = s4;
        t0 = (p2 + p3) * 4096;
        t1 = (p2 - p3) * 4096;
        x0 = t0 + t3;
        x3 = t0 - t3;
        x1 = t1 + t2;
        x2 = t1 - t2;
        t0 = s7;
        t1 = s5;
        t2 = s3;
        t3 = s1;
        p3 = t0 + t2;
        int p4 = t1 + t3;
        p1 = t0 + t3;
        p2 = t1 + t2;
        int p5 = (p3 + p4) * F1175;
        t0 *= F0298;
        t1 *= F2053;
        t2 *= F3072;
        t3 *= F1501;
        p1 = p5 - p1 * F0899;
        p2 = p5 - p2 * F2562;
        p3 *= -F1961;
        p4 *= -F0390;
        t3 += p1 + p4;
        t2 += p2 + p3;
        t1 += p2 + p4;
        t0 += p1 + p3;
    }
}
