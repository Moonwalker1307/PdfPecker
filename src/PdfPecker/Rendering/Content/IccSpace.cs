using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Content;

/// <summary>
/// An ICCBased colour space with its profile applied: colours go through the profile to D50 XYZ and on to sRGB, with
/// black point compensation as Acrobat applies it by default, so a press profile's darkest black shows as black on
/// screen. The conversion is sampled once per profile on a grid (256 points for gray, 33³ for RGB and 17⁴ for CMYK)
/// that colours are interpolated in (as LittleCMS does for 8-bit images), and the grids of recent profiles are shared between documents.
/// </summary>
internal sealed class IccSpace : ColorSpace
{
    private const int MaxCachedProfiles = 8;
    private static readonly ConcurrentDictionary<string, Lazy<ColorSpace?>> Cache = new(StringComparer.Ordinal);

    // The profiles one document uses, by their streams. Sampling a profile takes some milliseconds, and the shared cache
    // above keeps only a few: without this, a page whose inline images take turns among more profiles than it keeps
    // sampled one again for every image, so a small file kept a page busy for minutes. A document's profiles past this
    // many are not applied, and their colours go through the alternate space.
    private const int MaxProfilesPerDocument = 16;
    private static readonly ConditionalWeakTable<PdfFile, Dictionary<PdfStream, ColorSpace?>> DocumentProfiles = new();

    // D50 XYZ to linear sRGB, with Bradford adaptation from D50 to D65 folded in.
    private static readonly double[] D50ToLinearSrgb =
    {
        3.1338561, -1.6168667, -0.4906146,
        -0.9787684, 1.9161415, 0.0334540,
        0.0719453, -0.2289914, 1.4052427,
    };

    private readonly int _n;
    private readonly int _points;
    /// <summary>
    /// sRGB at each grid point, three values a point in 0-255 and beyond for colours outside sRGB, clipped only after
    /// interpolation; the first component varies slowest.
    /// </summary>
    private readonly float[] _grid;
    /// <summary>For each 8-bit sample: the grid index below it, and the fraction towards the next one in 0-1.</summary>
    private readonly int[] _sampleLow;
    private readonly float[] _sampleFraction;

    private IccSpace(int n, int points, float[] grid)
    {
        _n = n;
        _points = points;
        _grid = grid;
        _sampleLow = new int[256];
        _sampleFraction = new float[256];
        for (int v = 0; v < 256; v++)
        {
            double pos = v / 255.0 * (points - 1);
            _sampleLow[v] = Math.Min((int)pos, points - 2);
            _sampleFraction[v] = (float)(pos - _sampleLow[v]);
        }
    }

    public override int Components => _n;
    public override string Name => "ICCBased";

    /// <summary>The initial colour is black: 0 for gray and RGB, and black ink alone for CMYK.</summary>
    public override float[] InitialColor() => _n == 4 ? new float[] { 0, 0, 0, 1 } : new float[_n];

    /// <summary>
    /// The colour space for a profile with <paramref name="components"/> components: an <see cref="IccSpace"/>, or the
    /// device space of that many components when the profile gives the same colours to within one level (an sRGB
    /// profile, for example), or null when the profile cannot be read, does not have that many components, or is past
    /// the most profiles one document has applied. Each profile stream of a document is read once.
    /// </summary>
    public static ColorSpace? Create(PdfFile file, PdfStream stream, int components)
    {
        var profiles = DocumentProfiles.GetValue(file, static _ => new Dictionary<PdfStream, ColorSpace?>(ReferenceEqualityComparer.Instance));
        lock (profiles)
        {
            if (profiles.TryGetValue(stream, out var known)) return known;
            if (profiles.Count >= MaxProfilesPerDocument) return null;
            var space = Create(file.DecodeStream(stream), components);
            profiles.Add(stream, space);
            return space;
        }
    }

    /// <summary>
    /// The colour space for a profile with <paramref name="components"/> components, as <see cref="Create(PdfFile, PdfStream, int)"/>
    /// gives it, from the profile's bytes.
    /// </summary>
    public static ColorSpace? Create(byte[] profile, int components)
    {
        string key = Convert.ToHexString(SHA256.HashData(profile)) + ":" + components.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Cache.TryGetValue(key, out var cached)) return cached.Value;
        // A small cache: profiles repeat within a document and across documents from one source.
        if (Cache.Count >= MaxCachedProfiles) Cache.Clear();
        return Cache.GetOrAdd(key, _ => new Lazy<ColorSpace?>(() => Build(profile, components))).Value;
    }

    private static ColorSpace? Build(byte[] data, int components)
    {
        if (IccProfile.Parse(data) is not { } profile || profile.Components != components) return null;
        var device = components switch { 1 => Gray, 3 => RgbSpace, _ => Cmyk };
        var transform = new Transform(profile);
        if (SameAsDevice(transform, device, components)) return device;
        int points = components switch { 1 => 256, 3 => 33, _ => 17 };
        int count = 1;
        for (int i = 0; i < components; i++) count *= points;
        var grid = new float[count * 3];
        Span<double> input = stackalloc double[components];
        Span<double> rgb = stackalloc double[3];
        for (int index = 0; index < count; index++)
        {
            int rest = index;
            for (int i = components - 1; i >= 0; i--)
            {
                input[i] = rest % points / (double)(points - 1);
                rest /= points;
            }
            transform.ToSrgb(input, rgb);
            for (int k = 0; k < 3; k++) grid[index * 3 + k] = (float)(rgb[k] * 255);
        }
        return new IccSpace(components, points, grid);
    }

    /// <summary>Whether the profile gives the device space's colours, to within one level, on a coarse grid.</summary>
    private static bool SameAsDevice(Transform transform, ColorSpace device, int components)
    {
        const int Steps = 9;
        int count = 1;
        for (int i = 0; i < components; i++) count *= Steps;
        Span<double> input = stackalloc double[components];
        Span<float> deviceInput = stackalloc float[components];
        Span<double> rgb = stackalloc double[3];
        for (int index = 0; index < count; index++)
        {
            int rest = index;
            for (int i = components - 1; i >= 0; i--)
            {
                input[i] = rest % Steps / (double)(Steps - 1);
                deviceInput[i] = (float)input[i];
                rest /= Steps;
            }
            transform.ToSrgb(input, rgb);
            uint expected = device.ToRgb(deviceInput);
            for (int k = 0; k < 3; k++)
            {
                if (Math.Abs(ToByte(rgb[k]) - (int)(expected >> (8 * k) & 255)) > 1) return false;
            }
        }
        return true;
    }

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        if (c.Length < _n) return 0;
        Span<int> low = stackalloc int[4];
        Span<float> frac = stackalloc float[4];
        for (int i = 0; i < _n; i++)
        {
            float pos = Math.Clamp(float.IsNaN(c[i]) ? 0 : c[i], 0, 1) * (_points - 1);
            low[i] = Math.Min((int)pos, _points - 2);
            frac[i] = pos - low[i];
        }
        return Interpolate(low, frac);
    }

    /// <summary>
    /// Converts 8-bit samples of this space, <see cref="Components"/> bytes a pixel and inverted when the image's Decode
    /// array runs from 1 to 0, to opaque 0xAABBGGRR pixels.
    /// </summary>
    public void ConvertSamples(ReadOnlySpan<byte> samples, bool inverted, Span<uint> pixels)
    {
        Span<int> low = stackalloc int[4];
        Span<float> frac = stackalloc float[4];
        for (int p = 0, s = 0; p < pixels.Length; p++, s += _n)
        {
            for (int i = 0; i < _n; i++)
            {
                int v = samples[s + i];
                if (inverted) v = 255 - v;
                low[i] = _sampleLow[v];
                frac[i] = _sampleFraction[v];
            }
            pixels[p] = Interpolate(low, frac) | 0xFF000000;
        }
    }

    /// <summary>
    /// Interpolates between the grid points around a colour: linearly for gray, tetrahedrally for RGB, and for CMYK
    /// tetrahedrally in M, Y and K at the two C levels around the colour and linearly between them, as LittleCMS does.
    /// </summary>
    private uint Interpolate(ReadOnlySpan<int> low, ReadOnlySpan<float> frac)
    {
        var grid = _grid;
        int p = _points;
        float r, g, b;
        switch (_n)
        {
            case 1:
            {
                int at = low[0] * 3;
                float f = frac[0];
                r = grid[at] + f * (grid[at + 3] - grid[at]);
                g = grid[at + 1] + f * (grid[at + 4] - grid[at + 1]);
                b = grid[at + 2] + f * (grid[at + 5] - grid[at + 2]);
                break;
            }
            case 3:
                Tetrahedral(grid, ((low[0] * p + low[1]) * p + low[2]) * 3, frac[0], frac[1], frac[2], 3 * p * p, 3 * p, 3, out r, out g, out b);
                break;
            default:
            {
                int at = (((low[0] * p + low[1]) * p + low[2]) * p + low[3]) * 3;
                int sc = 3 * p * p * p, sm = 3 * p * p, sy = 3 * p;
                Tetrahedral(grid, at, frac[1], frac[2], frac[3], sm, sy, 3, out float r0, out float g0, out float b0);
                Tetrahedral(grid, at + sc, frac[1], frac[2], frac[3], sm, sy, 3, out float r1, out float g1, out float b1);
                float k = frac[0];
                r = r0 + k * (r1 - r0);
                g = g0 + k * (g1 - g0);
                b = b0 + k * (b1 - b0);
                break;
            }
        }
        return Pack(Round(r), Round(g), Round(b));
    }

    /// <summary>
    /// Tetrahedral interpolation in the cube at <paramref name="at"/> whose edges are <paramref name="sx"/>,
    /// <paramref name="sy"/> and <paramref name="sz"/> floats long: the cube is cut into six tetrahedra by the order of
    /// the fractions, and the colour is weighted from the four corners of the one it falls in.
    /// </summary>
    private static void Tetrahedral(float[] grid, int at, float fx, float fy, float fz, int sx, int sy, int sz, out float r, out float g, out float b)
    {
        int c1, c2;
        float w1, w2, w3;
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
        // c000 + w1 (c1 - c000) + w2 (c2 - c1) + w3 (c111 - c2)
        float w0 = 1 - w1;
        float a1 = w1 - w2, a2 = w2 - w3;
        r = w0 * grid[at] + a1 * grid[at + c1] + a2 * grid[at + c2] + w3 * grid[at + c3];
        g = w0 * grid[at + 1] + a1 * grid[at + c1 + 1] + a2 * grid[at + c2 + 1] + w3 * grid[at + c3 + 1];
        b = w0 * grid[at + 2] + a1 * grid[at + c1 + 2] + a2 * grid[at + c2 + 2] + w3 * grid[at + c3 + 2];
    }

    private static int Round(float v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f);

    /// <summary>The exact conversion through the profile, with black point compensation, to sRGB (0-1 inside its gamut).</summary>
    private sealed class Transform
    {
        /// <summary>
        /// The sRGB curve, mirrored for negative values and not capped at 1: the grid keeps colours outside sRGB
        /// unclipped, so interpolating next to the edge of the gamut does not pull colours off it.
        /// </summary>
        private static double Encode(double v)
        {
            double a = Math.Abs(v);
            double e = a <= 0.0031308 ? 12.92 * a : 1.055 * Math.Pow(a, 1 / 2.4) - 0.055;
            return v < 0 ? -e : e;
        }

        private readonly IccProfile _profile;
        private readonly double[] _black = new double[3]; // the XYZ of the profile's darkest colour, which is scaled to 0
        private readonly bool _compensate;

        public Transform(IccProfile profile)
        {
            _profile = profile;
            Span<double> darkest = stackalloc double[profile.Components];
            // Full ink for CMYK; no light for gray and RGB.
            darkest.Fill(profile.Components == 4 ? 1 : 0);
            profile.ToXyz(darkest, _black);
            for (int i = 0; i < 3; i++)
            {
                // A black lighter than mid-grey is a broken profile rather than a paper; it is left alone.
                if (!double.IsFinite(_black[i]) || _black[i] < 0 || _black[i] > IccProfile.D50[i] / 2) _black.AsSpan().Clear();
            }
            _compensate = _black[1] > 0;
        }

        public void ToSrgb(ReadOnlySpan<double> input, Span<double> rgb)
        {
            Span<double> xyz = stackalloc double[3];
            _profile.ToXyz(input, xyz);
            if (_compensate)
            {
                // Scales each channel so that the darkest colour becomes black and the D50 white stays white.
                for (int i = 0; i < 3; i++) xyz[i] = IccProfile.D50[i] * (xyz[i] - _black[i]) / (IccProfile.D50[i] - _black[i]);
            }
            var m = D50ToLinearSrgb;
            for (int i = 0; i < 3; i++)
            {
                double linear = m[i * 3] * xyz[0] + m[i * 3 + 1] * xyz[1] + m[i * 3 + 2] * xyz[2];
                rgb[i] = double.IsFinite(linear) ? Math.Clamp(Encode(linear), -1, 2) : 0;
            }
        }
    }
}
