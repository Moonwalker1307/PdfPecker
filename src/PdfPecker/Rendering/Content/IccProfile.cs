using System.Buffers.Binary;

namespace PdfPecker.Rendering.Content;

/// <summary>
/// An ICC colour profile (ICC.1:2022, and the version 2 profiles PDFs mostly carry) as a transform from its colour
/// space to the profile connection space, CIE XYZ under D50. It reads gray and RGB matrix/TRC profiles and the
/// lut8, lut16 and lutAtoB tables of gray, RGB and CMYK profiles; anything else is not a profile this reads, and the
/// caller falls back to the alternate space. Every count and offset is checked against the data before it is used.
/// </summary>
internal sealed class IccProfile
{
    /// <summary>The largest profile read; real profiles are a few megabytes at most.</summary>
    private const int MaxProfileBytes = 16 << 20;
    /// <summary>The most entries a colour lookup table may have, so a table costs at most a few megabytes.</summary>
    private const int MaxClutValues = 1 << 21;
    private const int MaxCurveEntries = 1 << 16;

    /// <summary>The D50 white of the profile connection space.</summary>
    public static readonly double[] D50 = { 0.9642, 1.0, 0.8249 };

    private readonly Curve[]? _trc;
    private readonly double[]? _matrix; // matrix/TRC: column j is the XYZ of channel j
    private readonly Lut? _lut;

    private IccProfile(int components, Curve[] trc, double[]? matrix)
    {
        Components = components;
        _trc = trc;
        _matrix = matrix;
    }

    private IccProfile(int components, Lut lut)
    {
        Components = components;
        _lut = lut;
    }

    /// <summary>The number of colour components: 1 for gray, 3 for RGB, 4 for CMYK.</summary>
    public int Components { get; }

    /// <summary>The profile in <paramref name="data"/>, or null when it is not one this reads.</summary>
    public static IccProfile? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132 || data.Length > MaxProfileBytes) return null;
        var reader = new Reader(data);
        if (reader.U32(36) != 0x61637370) return null; // 'acsp'
        int components = reader.U32(16) switch
        {
            0x47524159 => 1, // 'GRAY'
            0x52474220 => 3, // 'RGB '
            0x434D594B => 4, // 'CMYK'
            _ => 0,
        };
        bool labPcs = reader.U32(20) == 0x4C616220; // 'Lab '
        if (components == 0 || !labPcs && reader.U32(20) != 0x58595A20) return null; // 'XYZ '
        var tags = Tags(reader);
        if (tags == null) return null;

        // The perceptual table first, as viewers use it for images and pages on screen, then the colorimetric ones.
        foreach (uint tag in new uint[] { 0x41324230, 0x41324231, 0x41324232 }) // 'A2B0', 'A2B1', 'A2B2'
        {
            if (tags.TryGetValue(tag, out var at) && Lut.Parse(reader, at.Offset, labPcs) is { } lut && lut.Inputs == components)
                return new IccProfile(components, lut);
        }
        if (labPcs) return null;
        if (components == 1)
        {
            return tags.TryGetValue(0x6B545243, out var k) && Curve.Parse(reader, k.Offset, out _) is { } gray // 'kTRC'
                ? new IccProfile(1, new[] { gray }, null)
                : null;
        }
        if (components != 3) return null;
        var trc = new Curve[3];
        var matrix = new double[9];
        uint[] curveTags = { 0x72545243, 0x67545243, 0x62545243 }; // 'rTRC', 'gTRC', 'bTRC'
        uint[] xyzTags = { 0x7258595A, 0x6758595A, 0x6258595A }; // 'rXYZ', 'gXYZ', 'bXYZ'
        for (int i = 0; i < 3; i++)
        {
            if (!tags.TryGetValue(curveTags[i], out var c) || Curve.Parse(reader, c.Offset, out _) is not { } curve) return null;
            if (!tags.TryGetValue(xyzTags[i], out var x) || reader.U32(x.Offset) != 0x58595A20) return null; // 'XYZ '
            trc[i] = curve;
            for (int j = 0; j < 3; j++) matrix[j * 3 + i] = reader.S15Fixed16(x.Offset + 8 + 4 * j);
        }
        return reader.Failed ? null : new IccProfile(3, trc, matrix);
    }

    private static Dictionary<uint, (int Offset, int Size)>? Tags(Reader reader)
    {
        int count = reader.U32(128);
        if (count < 0 || count > (reader.Length - 132) / 12) return null;
        var tags = new Dictionary<uint, (int, int)>();
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + 12 * i;
            int offset = reader.U32(entry + 4), size = reader.U32(entry + 8);
            if (offset < 0 || size < 0 || offset > reader.Length - size) continue;
            tags.TryAdd((uint)reader.U32(entry), (offset, size));
        }
        return tags;
    }

    /// <summary>Converts components in 0-1 to D50 XYZ, with Y = 1 for the profile's white.</summary>
    public void ToXyz(ReadOnlySpan<double> input, Span<double> xyz)
    {
        if (_lut != null)
        {
            _lut.Eval(input, xyz);
            return;
        }
        if (Components == 1)
        {
            double y = _trc![0].Eval(input[0]);
            for (int i = 0; i < 3; i++) xyz[i] = D50[i] * y;
            return;
        }
        double r = _trc![0].Eval(input[0]), g = _trc[1].Eval(input[1]), b = _trc[2].Eval(input[2]);
        var m = _matrix!;
        for (int i = 0; i < 3; i++) xyz[i] = m[i * 3] * r + m[i * 3 + 1] * g + m[i * 3 + 2] * b;
    }

    /// <summary>Lab under D50 as XYZ under D50.</summary>
    private static void LabToXyz(double l, double a, double b, Span<double> xyz)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        xyz[0] = D50[0] * InverseF(fx);
        xyz[1] = InverseF(fy);
        xyz[2] = D50[2] * InverseF(fz);
    }

    private static double InverseF(double t) => t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);

    /// <summary>Big-endian reads that give 0 and set <see cref="Failed"/> instead of reading outside the data.</summary>
    private sealed class Reader
    {
        private readonly byte[] _data;

        public Reader(ReadOnlySpan<byte> data) => _data = data.ToArray();

        public int Length => _data.Length;
        public bool Failed { get; private set; }

        public bool Has(int offset, long length)
        {
            if (offset >= 0 && length >= 0 && offset + length <= _data.Length) return true;
            Failed = true;
            return false;
        }

        public int U8(int offset) => Has(offset, 1) ? _data[offset] : 0;
        public int U16(int offset) => Has(offset, 2) ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset)) : 0;
        public int U32(int offset) => Has(offset, 4) ? (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset)) : 0;
        public double S15Fixed16(int offset) => Has(offset, 4) ? BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(offset)) / 65536.0 : 0;
    }

    /// <summary>A one-dimensional curve from 0-1 to 0-1: a table, a gamma or one of the parametric functions.</summary>
    private sealed class Curve
    {
        private readonly double[]? _table;
        private readonly int _function = -1;
        private readonly double[] _p = Array.Empty<double>();

        private Curve(double[]? table) => _table = table;

        private Curve(int function, double[] parameters)
        {
            _function = function;
            _p = parameters;
        }

        /// <summary>A curve through a table of evenly spaced values, as lut8 and lut16 tables hold them.</summary>
        public static Curve FromTable(double[] table) => new(table);

        /// <summary>The identity, which a table of no entries stands for.</summary>
        public static readonly Curve Identity = new((double[]?)null);

        /// <summary>A curve tag ('curv' or 'para') and the bytes it takes, padded to four as tables pack them.</summary>
        public static Curve? Parse(Reader reader, int offset, out int length)
        {
            length = 0;
            switch ((uint)reader.U32(offset))
            {
                case 0x63757276: // 'curv'
                {
                    int count = reader.U32(offset + 8);
                    if (count < 0 || count > MaxCurveEntries || !reader.Has(offset + 12, 2L * count)) return null;
                    length = (12 + 2 * count + 3) & ~3;
                    if (count == 0) return Identity;
                    if (count == 1) return new Curve(0, new[] { reader.U16(offset + 12) / 256.0 });
                    var table = new double[count];
                    for (int i = 0; i < count; i++) table[i] = reader.U16(offset + 12 + 2 * i) / 65535.0;
                    return new Curve(table);
                }
                case 0x70617261: // 'para'
                {
                    int function = reader.U16(offset + 8);
                    int count = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
                    if (count < 0 || !reader.Has(offset + 12, 4L * count)) return null;
                    var p = new double[count];
                    for (int i = 0; i < count; i++) p[i] = reader.S15Fixed16(offset + 12 + 4 * i);
                    length = 12 + 4 * count;
                    return new Curve(function, p);
                }
                default:
                    return null;
            }
        }

        public double Eval(double x)
        {
            x = Math.Clamp(x, 0, 1);
            if (_table != null)
            {
                double pos = x * (_table.Length - 1);
                int i = Math.Min((int)pos, _table.Length - 2);
                return _table[i] + (pos - i) * (_table[i + 1] - _table[i]);
            }
            if (_function < 0) return x;
            var p = _p;
            double y = _function switch
            {
                0 => Pow(x, p[0]),
                1 => x >= -p[2] / p[1] ? Pow(p[1] * x + p[2], p[0]) : 0,
                2 => x >= -p[2] / p[1] ? Pow(p[1] * x + p[2], p[0]) + p[3] : p[3],
                3 => x >= p[4] ? Pow(p[1] * x + p[2], p[0]) : p[3] * x,
                _ => x >= p[4] ? Pow(p[1] * x + p[2], p[0]) + p[5] : p[3] * x + p[6],
            };
            return double.IsFinite(y) ? Math.Clamp(y, 0, 1) : 0;
        }

        private static double Pow(double x, double g) => x <= 0 ? 0 : Math.Pow(x, g);
    }

    /// <summary>
    /// A lut8, lut16 or lutAtoB table: curves on each input, a colour lookup table, then curves (and in lutAtoB a
    /// matrix and more curves) on each output, giving PCS values in 0-1 that the encoding turns into XYZ or Lab.
    /// </summary>
    private sealed class Lut
    {
        private Curve[] _a = Array.Empty<Curve>();
        private int[] _grid = Array.Empty<int>();
        private float[]? _clut;
        private Curve[]? _m;
        private double[]? _matrix; // 3x3 then three offsets
        private Curve[] _b = Array.Empty<Curve>();
        private bool _lab;
        private double _pcsScale; // the PCS value of a 1.0 output, 1 for most encodings

        public int Inputs => _a.Length;

        public static Lut? Parse(Reader reader, int offset, bool lab)
        {
            var lut = (uint)reader.U32(offset) switch
            {
                0x6D667431 => ParseLut8Or16(reader, offset, sixteen: false, lab), // 'mft1'
                0x6D667432 => ParseLut8Or16(reader, offset, sixteen: true, lab), // 'mft2'
                0x6D414220 => ParseAtoB(reader, offset, lab), // 'mAB '
                _ => null,
            };
            return lut == null || reader.Failed ? null : lut;
        }

        private static Lut? ParseLut8Or16(Reader reader, int offset, bool sixteen, bool lab)
        {
            int inputs = reader.U8(offset + 8), outputs = reader.U8(offset + 9), points = reader.U8(offset + 10);
            if (inputs is < 1 or > 4 || outputs != 3 || points < 2) return null;
            int inEntries = sixteen ? reader.U16(offset + 48) : 256, outEntries = sixteen ? reader.U16(offset + 50) : 256;
            if (inEntries is < 2 or > 4096 || outEntries is < 2 or > 4096) return null;
            int pos = offset + (sixteen ? 52 : 48), width = sixteen ? 2 : 1;
            var lut = new Lut { _lab = lab };
            lut._a = new Curve[inputs];
            for (int i = 0; i < inputs; i++, pos += inEntries * width) lut._a[i] = Table(reader, pos, inEntries, width);
            lut._grid = Enumerable.Repeat(points, inputs).ToArray();
            lut._clut = Clut(reader, pos, lut._grid, outputs, width);
            if (lut._clut == null) return null;
            pos += lut._clut.Length * width;
            lut._b = new Curve[outputs];
            for (int i = 0; i < outputs; i++, pos += outEntries * width) lut._b[i] = Table(reader, pos, outEntries, width);
            // Version 2 encodes 16-bit Lab with 0xFF00 as the top of the range; XYZ has 1.0 at 0x8000 in every version.
            lut._pcsScale = lab ? (sixteen ? 65535.0 / 65280 : 1) : 65535.0 / 32768;
            return lut;
        }

        private static Curve Table(Reader reader, int offset, int entries, int width)
        {
            if (!reader.Has(offset, (long)entries * width)) return Curve.Identity;
            var table = new double[entries];
            for (int i = 0; i < entries; i++) table[i] = width == 2 ? reader.U16(offset + 2 * i) / 65535.0 : reader.U8(offset + i) / 255.0;
            return Curve.FromTable(table);
        }

        private static float[]? Clut(Reader reader, int offset, int[] grid, int outputs, int width)
        {
            long count = outputs;
            foreach (int g in grid) count *= g;
            if (count > MaxClutValues || !reader.Has(offset, count * width)) return null;
            var clut = new float[count];
            for (int i = 0; i < clut.Length; i++)
                clut[i] = width == 2 ? reader.U16(offset + 2 * i) / 65535f : reader.U8(offset + i) / 255f;
            return clut;
        }

        private static Lut? ParseAtoB(Reader reader, int offset, bool lab)
        {
            int inputs = reader.U8(offset + 8), outputs = reader.U8(offset + 9);
            if (inputs is < 1 or > 4 || outputs != 3) return null;
            int bOffset = reader.U32(offset + 12), matrixOffset = reader.U32(offset + 16), mOffset = reader.U32(offset + 20);
            int clutOffset = reader.U32(offset + 24), aOffset = reader.U32(offset + 28);
            var lut = new Lut { _lab = lab, _pcsScale = lab ? 1 : 65535.0 / 32768 };
            if (bOffset == 0 || Curves(reader, offset + bOffset, outputs) is not { } b) return null;
            lut._b = b;
            if (matrixOffset != 0 && mOffset != 0)
            {
                if (Curves(reader, offset + mOffset, outputs) is not { } m) return null;
                lut._m = m;
                lut._matrix = new double[12];
                for (int i = 0; i < 12; i++) lut._matrix[i] = reader.S15Fixed16(offset + matrixOffset + 4 * i);
            }
            if (clutOffset != 0)
            {
                if (aOffset == 0 || Curves(reader, offset + aOffset, inputs) is not { } a) return null;
                lut._a = a;
                int at = offset + clutOffset;
                lut._grid = new int[inputs];
                for (int i = 0; i < inputs; i++)
                {
                    lut._grid[i] = reader.U8(at + i);
                    if (lut._grid[i] < 2) return null;
                }
                int precision = reader.U8(at + 16);
                if (precision is not (1 or 2)) return null;
                lut._clut = Clut(reader, at + 20, lut._grid, outputs, precision);
                if (lut._clut == null) return null;
            }
            else
            {
                // Without a table the inputs go straight to the outputs, so there must be as many.
                if (inputs != outputs) return null;
                lut._a = Enumerable.Repeat(Curve.Identity, inputs).ToArray();
            }
            return lut;
        }

        private static Curve[]? Curves(Reader reader, int offset, int count)
        {
            var curves = new Curve[count];
            for (int i = 0; i < count; i++)
            {
                if (Curve.Parse(reader, offset, out int length) is not { } curve) return null;
                curves[i] = curve;
                offset += length;
            }
            return curves;
        }

        public void Eval(ReadOnlySpan<double> input, Span<double> xyz)
        {
            int n = _a.Length;
            Span<double> a = stackalloc double[4];
            for (int i = 0; i < n; i++) a[i] = _a[i].Eval(input[i]);
            Span<double> v = stackalloc double[3];
            if (_clut != null) Interpolate(a[..n], v);
            else a[..3].CopyTo(v);
            if (_matrix != null)
            {
                for (int i = 0; i < 3; i++) v[i] = _m![i].Eval(v[i]);
                var m = _matrix;
                double x = v[0], y = v[1], z = v[2];
                for (int i = 0; i < 3; i++) v[i] = m[i * 3] * x + m[i * 3 + 1] * y + m[i * 3 + 2] * z + m[9 + i];
            }
            for (int i = 0; i < 3; i++) v[i] = _b[i].Eval(v[i]) * _pcsScale;
            if (_lab) LabToXyz(v[0] * 100, v[1] * 255 - 128, v[2] * 255 - 128, xyz);
            else v.CopyTo(xyz);
        }

        /// <summary>
        /// Interpolation in the colour lookup table, whose first input varies slowest, as LittleCMS does it (profiles are
        /// made and checked with it, and coarse tables differ visibly between methods): linear for one input, bilinear
        /// for two, tetrahedral for three, and for four tetrahedral in the last three at the two grid levels of the
        /// first and linear between them.
        /// </summary>
        private void Interpolate(ReadOnlySpan<double> input, Span<double> output)
        {
            int n = input.Length;
            Span<int> low = stackalloc int[4];
            Span<double> frac = stackalloc double[4];
            Span<int> stride = stackalloc int[4];
            int s = 3;
            for (int i = n - 1; i >= 0; i--)
            {
                stride[i] = s;
                s *= _grid[i];
            }
            int at = 0;
            for (int i = 0; i < n; i++)
            {
                double pos = Math.Clamp(input[i], 0, 1) * (_grid[i] - 1);
                low[i] = Math.Min((int)pos, _grid[i] - 2);
                frac[i] = pos - low[i];
                at += low[i] * stride[i];
            }
            var clut = _clut!;
            switch (n)
            {
                case 3:
                    Tetrahedral(clut, at, frac[0], frac[1], frac[2], stride[0], stride[1], stride[2], output);
                    return;
                case 4:
                {
                    Span<double> high = stackalloc double[3];
                    Tetrahedral(clut, at, frac[1], frac[2], frac[3], stride[1], stride[2], stride[3], output);
                    Tetrahedral(clut, at + stride[0], frac[1], frac[2], frac[3], stride[1], stride[2], stride[3], high);
                    for (int k = 0; k < 3; k++) output[k] += frac[0] * (high[k] - output[k]);
                    return;
                }
            }
            output.Clear();
            for (int corner = 0; corner < 1 << n; corner++)
            {
                double weight = 1;
                int index = at;
                for (int i = 0; i < n; i++)
                {
                    bool high = (corner >> i & 1) != 0;
                    weight *= high ? frac[i] : 1 - frac[i];
                    if (high) index += stride[i];
                }
                for (int k = 0; k < 3; k++) output[k] += weight * clut[index + k];
            }
        }

        /// <summary>Tetrahedral interpolation in the cube at <paramref name="at"/> with edges of the given strides.</summary>
        private static void Tetrahedral(float[] clut, int at, double fx, double fy, double fz, int sx, int sy, int sz, Span<double> output)
        {
            int c1, c2;
            double w1, w2, w3;
            if (fx >= fy)
            {
                if (fy >= fz) { c1 = sx; c2 = sx + sy; w1 = fx; w2 = fy; w3 = fz; }
                else if (fx >= fz) { c1 = sx; c2 = sx + sz; w1 = fx; w2 = fz; w3 = fy; }
                else { c1 = sz; c2 = sx + sz; w1 = fz; w2 = fx; w3 = fy; }
            }
            else
            {
                if (fx >= fz) { c1 = sy; c2 = sx + sy; w1 = fy; w2 = fx; w3 = fz; }
                else if (fy >= fz) { c1 = sy; c2 = sy + sz; w1 = fy; w2 = fz; w3 = fx; }
                else { c1 = sz; c2 = sy + sz; w1 = fz; w2 = fy; w3 = fx; }
            }
            int c3 = sx + sy + sz;
            for (int k = 0; k < 3; k++)
                output[k] = (1 - w1) * clut[at + k] + (w1 - w2) * clut[at + c1 + k] + (w2 - w3) * clut[at + c2 + k] + w3 * clut[at + c3 + k];
        }
    }
}
