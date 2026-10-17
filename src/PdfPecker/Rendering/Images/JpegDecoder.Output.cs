namespace PdfPecker.Rendering.Images;

internal sealed partial class JpegDecoder
{
    private const int AdobeYcck = 2;
    private const int AdobeNoTransform = 0;

    /// <summary>Dequantizes and inverse-transforms the coefficients into planes, then interleaves the components.</summary>
    private byte[] Output()
    {
        BuildPlanes();
        int n = _components.Length;
        var output = new byte[_width * _height * n];
        bool transform = UsesColorTransform();
        var rows = new byte[n][];
        for (int i = 0; i < n; i++) rows[i] = new byte[_width];
        for (int y = 0; y < _height; y++)
        {
            for (int i = 0; i < n; i++) UpsampleRow(_components[i], y, rows[i]);
            var line = output.AsSpan(y * _width * n, _width * n);
            switch (n)
            {
                case 1:
                    rows[0].CopyTo(line);
                    break;
                case 3 when transform:
                    YccToRgbRow(rows[0], rows[1], rows[2], line, stride: 3);
                    break;
                case 3:
                    Interleave(rows[0], rows[1], rows[2], line, stride: 3);
                    break;
                default:
                    if (transform) YcckToCmyRow(rows[0], rows[1], rows[2], line);
                    else Interleave(rows[0], rows[1], rows[2], line, stride: 4);
                    for (int x = 0, o = 3; x < _width; x++, o += 4) line[o] = rows[3][x];
                    break;
            }
        }
        return output;
    }

    /// <summary>
    /// Whether the components are YCbCr (three) or YCCK (four) to be converted. A container's say wins; otherwise three
    /// components are converted unless an Adobe segment says they are not and there is no JFIF segment, and four only
    /// when an Adobe segment says YCCK.
    /// </summary>
    private bool UsesColorTransform() => _components.Length switch
    {
        3 => _colorTransform ?? (_adobeTransform != AdobeNoTransform || _jfif),
        4 => _adobeTransform == AdobeYcck,
        _ => false,
    };

    private void BuildPlanes()
    {
        Span<int> workspace = stackalloc int[BlockSize];
        foreach (var c in _components)
        {
            var q = QuantizationTable(c.QuantizationTable);
            c.PlaneStride = c.BlocksPerLine * 8;
            c.Plane = new byte[c.PlaneStride * c.BlocksPerColumn * 8];
            for (int by = 0; by < c.BlocksPerColumn; by++)
                for (int bx = 0; bx < c.BlocksPerLine; bx++)
                {
                    var block = c.Coefficients.AsSpan((by * c.BlocksPerLine + bx) * BlockSize, BlockSize);
                    Idct(block, q, c.Plane, by * 8 * c.PlaneStride + bx * 8, c.PlaneStride, workspace);
                }
            c.Coefficients = Array.Empty<short>();
        }
    }

    /// <summary>The table a component names, else the first one the file has, else a table of ones.</summary>
    private ushort[] QuantizationTable(int slot)
    {
        if (_quantization[slot] is { } table) return table;
        foreach (var t in _quantization)
            if (t != null) return t;
        var ones = new ushort[BlockSize];
        ones.AsSpan().Fill(1);
        return ones;
    }

    /// <summary>One image row of a component's samples, repeated across as its horizontal sampling factor asks.</summary>
    private void UpsampleRow(Component c, int y, byte[] row)
    {
        int rowStart = y * c.V / _vMax * c.PlaneStride;
        if (c.H == _hMax)
        {
            c.Plane.AsSpan(rowStart, _width).CopyTo(row);
            return;
        }
        var plane = c.Plane;
        for (int x = 0; x < row.Length; x++) row[x] = plane[rowStart + x * c.H / _hMax];
    }

    private static void Interleave(byte[] c0, byte[] c1, byte[] c2, Span<byte> line, int stride)
    {
        for (int x = 0, o = 0; x < c0.Length; x++, o += stride)
        {
            line[o] = c0[x];
            line[o + 1] = c1[x];
            line[o + 2] = c2[x];
        }
    }

    private static void YccToRgbRow(byte[] y, byte[] cb, byte[] cr, Span<byte> line, int stride)
    {
        for (int x = 0, o = 0; x < y.Length; x++, o += stride) YccToRgb(y[x], cb[x], cr[x], line.Slice(o, 3));
    }

    /// <summary>YCCK: the YCC part converted to RGB, then stored as its complement, CMY.</summary>
    private static void YcckToCmyRow(byte[] y, byte[] cb, byte[] cr, Span<byte> line)
    {
        for (int x = 0, o = 0; x < y.Length; x++, o += 4)
        {
            var cmy = line.Slice(o, 3);
            YccToRgb(y[x], cb[x], cr[x], cmy);
            cmy[0] = (byte)(255 - cmy[0]);
            cmy[1] = (byte)(255 - cmy[1]);
            cmy[2] = (byte)(255 - cmy[2]);
        }
    }

    // The JFIF YCbCr to RGB conversion in 16.16 fixed point: 1.402, 0.344136, 0.714136 and 1.772 times 65536.
    private const int CrToR = 91881, CbToG = 22554, CrToG = 46802, CbToB = 116130;
    private const int FixedHalf = 1 << 15;

    private static void YccToRgb(int y, int cb, int cr, Span<byte> rgb)
    {
        cb -= 128;
        cr -= 128;
        int scaled = y << 16;
        rgb[0] = ClampToByte((scaled + CrToR * cr + FixedHalf) >> 16);
        rgb[1] = ClampToByte((scaled - CbToG * cb - CrToG * cr + FixedHalf) >> 16);
        rgb[2] = ClampToByte((scaled + CbToB * cb + FixedHalf) >> 16);
    }

    private static byte ClampToByte(int x) => (byte)(x < 0 ? 0 : x > 255 ? 255 : x);

    // ------------------------------------------------------------------ inverse DCT

    // Fixed-point constants (scaled by 4096) for the Loeffler-Ligtenberg-Moschytz IDCT.
    private const int F0298 = 1223, F0390 = 1598, F0541 = 2217, F0765 = 3135, F0899 = 3686, F1175 = 4816, F1501 = 6149,
        F1847 = 7568, F1961 = 8035, F2053 = 8410, F2562 = 10498, F3072 = 12586;

    // The column pass keeps two more bits than the input (10 bits of rounding off the 12-bit constants); the row pass
    // removes them with the constants' 12 bits and three for the 8x8 normalisation, and adds the level shift of 128.
    private const int ColumnShift = 10, RowShift = 17;
    private const int ColumnRounding = 1 << (ColumnShift - 1);
    private const int RowRoundingAndLevelShift = (1 << (RowShift - 1)) + (128 << RowShift);

    /// <summary>Dequantizes one block and writes its 8x8 samples into a plane.</summary>
    private static void Idct(ReadOnlySpan<short> block, ushort[] q, byte[] plane, int offset, int stride, Span<int> v)
    {
        // Columns.
        for (int i = 0; i < 8; i++)
        {
            if (block[8 + i] == 0 && block[16 + i] == 0 && block[24 + i] == 0 && block[32 + i] == 0 && block[40 + i] == 0 && block[48 + i] == 0 && block[56 + i] == 0)
            {
                // Only the DC term: the column is flat, at the scale the full pass would give it.
                int dc = block[i] * q[i] * 4;
                for (int k = 0; k < 8; k++) v[k * 8 + i] = dc;
                continue;
            }
            Idct1D(block[i] * q[i], block[8 + i] * q[8 + i], block[16 + i] * q[16 + i], block[24 + i] * q[24 + i],
                block[32 + i] * q[32 + i], block[40 + i] * q[40 + i], block[48 + i] * q[48 + i], block[56 + i] * q[56 + i],
                out int x0, out int x1, out int x2, out int x3, out int t0, out int t1, out int t2, out int t3);
            x0 += ColumnRounding; x1 += ColumnRounding; x2 += ColumnRounding; x3 += ColumnRounding;
            v[i] = (x0 + t3) >> ColumnShift;
            v[56 + i] = (x0 - t3) >> ColumnShift;
            v[8 + i] = (x1 + t2) >> ColumnShift;
            v[48 + i] = (x1 - t2) >> ColumnShift;
            v[16 + i] = (x2 + t1) >> ColumnShift;
            v[40 + i] = (x2 - t1) >> ColumnShift;
            v[24 + i] = (x3 + t0) >> ColumnShift;
            v[32 + i] = (x3 - t0) >> ColumnShift;
        }
        // Rows.
        for (int r = 0; r < 8; r++)
        {
            int b = r * 8;
            Idct1D(v[b], v[b + 1], v[b + 2], v[b + 3], v[b + 4], v[b + 5], v[b + 6], v[b + 7],
                out int x0, out int x1, out int x2, out int x3, out int t0, out int t1, out int t2, out int t3);
            x0 += RowRoundingAndLevelShift; x1 += RowRoundingAndLevelShift; x2 += RowRoundingAndLevelShift; x3 += RowRoundingAndLevelShift;
            int o = offset + r * stride;
            plane[o] = ClampToByte((x0 + t3) >> RowShift);
            plane[o + 7] = ClampToByte((x0 - t3) >> RowShift);
            plane[o + 1] = ClampToByte((x1 + t2) >> RowShift);
            plane[o + 6] = ClampToByte((x1 - t2) >> RowShift);
            plane[o + 2] = ClampToByte((x2 + t1) >> RowShift);
            plane[o + 5] = ClampToByte((x2 - t1) >> RowShift);
            plane[o + 3] = ClampToByte((x3 + t0) >> RowShift);
            plane[o + 4] = ClampToByte((x3 - t0) >> RowShift);
        }
    }

    /// <summary>The even part (x0 to x3) and odd part (t0 to t3) of a one-dimensional 8-point IDCT.</summary>
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
