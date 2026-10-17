using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Content;

/// <summary>A PDF color space, converting component values (0-1 for most spaces) to 8-bit sRGB.</summary>
internal abstract class ColorSpace
{
    // Color spaces refer to other color spaces (bases, alternates, named resources), and a document can loop them.
    private const int MaxNestingDepth = 10;

    public abstract int Components { get; }
    public abstract string Name { get; }

    /// <summary>Converts one color. Returns packed R | G &lt;&lt; 8 | B &lt;&lt; 16.</summary>
    public abstract uint ToRgb(ReadOnlySpan<float> c);

    public virtual float[] InitialColor() => new float[Components];

    /// <summary>The default Decode array for image samples.</summary>
    public virtual double[] DefaultDecode(int bitsPerComponent)
    {
        var decode = new double[Components * 2];
        for (int i = 0; i < Components; i++) decode[2 * i + 1] = 1;
        return decode;
    }

    /// <summary>Packs 0-255 channel values as <see cref="ToRgb"/> returns them.</summary>
    protected static uint Pack(int r, int g, int b) => (uint)(r | g << 8 | b << 16);

    /// <summary>Packs 0-1 channel values as <see cref="ToRgb"/> returns them.</summary>
    protected static uint Rgb(double r, double g, double b) => Pack(ToByte(r), ToByte(g), ToByte(b));

    /// <summary>Scales a 0-1 channel value to 0-255, rounding to nearest; NaN gives 0.</summary>
    protected static int ToByte(double v) => v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5);

    /// <summary>Converts CIE XYZ (D65) to linear sRGB, as a row-major 3x3 matrix.</summary>
    protected static readonly double[] XyzToLinearSrgb = { 3.2406, -1.5372, -0.4986, -0.9689, 1.8758, 0.0415, 0.0557, -0.2040, 1.0570 };

    public static readonly ColorSpace Gray = new DeviceGray();
    public static readonly ColorSpace RgbSpace = new DeviceRgb();
    public static readonly ColorSpace Cmyk = new DeviceCmyk();

    /// <summary>Resolves a color space from a name or array, looking names up in the resources' /ColorSpace dictionary.</summary>
    public static ColorSpace? Parse(PdfFile file, PdfObject? obj, PdfDict? resources, int depth = 0)
    {
        if (depth > MaxNestingDepth) return null;
        return file.Resolve(obj) switch
        {
            PdfName name => ParseName(file, name.Value, resources, depth),
            PdfArray { Count: > 0 } array => ParseArray(file, array, resources, depth),
            _ => null,
        };
    }

    /// <summary>A device space by name, or else a named entry of the resources' /ColorSpace dictionary.</summary>
    private static ColorSpace? ParseName(PdfFile file, string name, PdfDict? resources, int depth)
    {
        switch (name)
        {
            case "DeviceGray": case "G": case "CalGray": return Gray;
            case "DeviceRGB": case "RGB": case "CalRGB": return RgbSpace;
            case "DeviceCMYK": case "CMYK": return Cmyk;
            case "Pattern": return new PatternSpace(null);
            // Indexed needs its base and table, which only the array form has.
            case "Indexed": case "I": return null;
        }
        var named = file.GetDict(resources, "ColorSpace")?[name];
        return named != null ? Parse(file, named, resources, depth + 1) : null;
    }

    /// <summary>A color space array: the family name followed by its parameters.</summary>
    private static ColorSpace? ParseArray(PdfFile file, PdfArray array, PdfDict? resources, int depth)
    {
        switch ((file.Resolve(array[0]) as PdfName)?.Value)
        {
            case "DeviceGray": case "G": return Gray;
            case "DeviceRGB": case "RGB": return RgbSpace;
            case "DeviceCMYK": case "CMYK": return Cmyk;
            case "CalGray": return ParseCalibrated(file, array, rgb: false);
            case "CalRGB": return ParseCalibrated(file, array, rgb: true);
            case "ICCBased": return ParseIccBased(file, array, resources, depth);
            case "Lab": return ParseLab(file, array);
            case "Indexed": case "I": return ParseIndexed(file, array, resources, depth);
            case "Separation": return ParseSeparation(file, array, resources, depth);
            case "DeviceN": return ParseDeviceN(file, array, resources, depth);
            case "Pattern": return new PatternSpace(array.Count > 1 ? Parse(file, array[1], resources, depth + 1) : null);
            default: return null;
        }
    }

    /// <summary>[/CalGray dict] or [/CalRGB dict]; without a valid white point it is read as the device space.</summary>
    private static ColorSpace ParseCalibrated(PdfFile file, PdfArray array, bool rgb)
    {
        var dict = Parameters(file, array);
        var whitePoint = file.GetNumbers(dict, "WhitePoint");
        var gammas = rgb ? file.GetNumbers(dict, "Gamma") : null;
        double? grayGamma = rgb ? null : file.Num(dict?["Gamma"], 1);
        var matrix = rgb ? file.GetNumbers(dict, "Matrix") : null;
        return CalSpace.Create(rgb, whitePoint, gammas, grayGamma, matrix) ?? (rgb ? RgbSpace : Gray);
    }

    /// <summary>
    /// [/ICCBased stream]: the profile itself is not applied. The alternate space is used when its component count
    /// matches /N, and otherwise the device space with N components.
    /// </summary>
    private static ColorSpace ParseIccBased(PdfFile file, PdfArray array, PdfDict? resources, int depth)
    {
        var stream = array.Count > 1 ? file.Resolve(array[1]) as PdfStream : null;
        int components = file.GetInt(stream, "N", 3);
        var alternateObj = stream?["Alternate"];
        var alternate = alternateObj != null ? Parse(file, alternateObj, resources, depth + 1) : null;
        if (alternate != null && alternate.Components == components) return alternate;
        return components switch { 1 => Gray, 4 => Cmyk, _ => RgbSpace };
    }

    private static ColorSpace ParseLab(PdfFile file, PdfArray array)
    {
        var dict = Parameters(file, array);
        return new LabSpace(file.GetNumbers(dict, "WhitePoint"), file.GetNumbers(dict, "Range"));
    }

    /// <summary>[/Indexed base hival lookup], where the lookup table is a string or a stream.</summary>
    private static ColorSpace? ParseIndexed(PdfFile file, PdfArray array, PdfDict? resources, int depth)
    {
        if (array.Count < 4) return null;
        var baseSpace = Parse(file, array[1], resources, depth + 1);
        if (baseSpace == null) return null;
        int hiVal = Math.Clamp(file.GetIntValue(array[2]), 0, IndexedSpace.MaxHiVal);
        byte[] lookup = file.Resolve(array[3]) switch
        {
            PdfString str => str.Bytes,
            PdfStream stream => file.DecodeStream(stream),
            _ => Array.Empty<byte>(),
        };
        return new IndexedSpace(baseSpace, hiVal, lookup);
    }

    /// <summary>[/Separation name alternate tintTransform]; an alternate that does not parse is read as DeviceGray.</summary>
    private static ColorSpace? ParseSeparation(PdfFile file, PdfArray array, PdfDict? resources, int depth)
    {
        if (array.Count < 4) return null;
        var colorant = (file.Resolve(array[1]) as PdfName)?.Value ?? "";
        var alternate = Parse(file, array[2], resources, depth + 1) ?? Gray;
        var tintTransform = PdfFunction.Create(file, array[3]);
        return new SeparationSpace(1, colorant == "None", colorant == "All", alternate, tintTransform);
    }

    /// <summary>[/DeviceN names alternate tintTransform ...]; it paints nothing when every colorant is None.</summary>
    private static ColorSpace? ParseDeviceN(PdfFile file, PdfArray array, PdfDict? resources, int depth)
    {
        if (array.Count < 4) return null;
        var colorants = file.Resolve(array[1]) as PdfArray;
        int components = Math.Max(1, colorants?.Count ?? 1);
        var alternate = Parse(file, array[2], resources, depth + 1) ?? Gray;
        var tintTransform = PdfFunction.Create(file, array[3]);
        bool none = colorants != null && AllNamesAre(file, colorants, "None");
        return new SeparationSpace(components, none, false, alternate, tintTransform);
    }

    /// <summary>The dictionary that follows the family name, if there is one.</summary>
    private static PdfDict? Parameters(PdfFile file, PdfArray array) => array.Count > 1 ? file.Resolve(array[1]) as PdfDict : null;

    /// <summary>Whether every entry is the given name; true for an empty array.</summary>
    private static bool AllNamesAre(PdfFile file, PdfArray array, string name)
    {
        foreach (var item in array.Items)
        {
            if ((file.Resolve(item) as PdfName)?.Value != name) return false;
        }
        return true;
    }
}

internal sealed class DeviceGray : ColorSpace
{
    public override int Components => 1;
    public override string Name => "DeviceGray";

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        int gray = ToByte(c.Length > 0 ? c[0] : 0);
        return Pack(gray, gray, gray);
    }
}

internal sealed class DeviceRgb : ColorSpace
{
    public override int Components => 3;
    public override string Name => "DeviceRGB";
    public override uint ToRgb(ReadOnlySpan<float> c) => c.Length < 3 ? 0 : Rgb(c[0], c[1], c[2]);
}

internal sealed class DeviceCmyk : ColorSpace
{
    public override int Components => 4;
    public override string Name => "DeviceCMYK";

    /// <summary>The initial color is black, from the black ink alone.</summary>
    public override float[] InitialColor() => new float[] { 0, 0, 0, 1 };

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        if (c.Length < 4) return 0;
        var (r, g, b) = Convert(c[0], c[1], c[2], c[3]);
        return Pack(r, g, b);
    }

    private static readonly CmykFit RedFit = new(
        -4.387332384609988, 54.48615194189176, 18.82290502165302, 212.25662451639585, -285.2331026137004,
        1.7149763477362134, -5.6096736904047315, -17.873870861415444, -5.497006427196366,
        -2.5217340131683033, -21.248923337353073, 17.5119270841813,
        -21.86122147463605, -189.48180835922747);

    private static readonly CmykFit GreenFit = new(
        8.841041422036149, 60.118027045597366, 6.871425592049007, 31.159100130055922, -79.2970844816548,
        -15.310361306967817, 17.575251261109482, 131.35250912493976, -190.9453302588951,
        4.444339102852739, 9.8632861493405, -24.86741582555878,
        -20.737325471181034, -187.80453709719578);

    private static readonly CmykFit BlueFit = new(
        0.8842522430003296, 8.078677503112928, 30.89978309703729, -0.23883238689178934, -14.183576799673286,
        10.49593273432072, 63.02378494754052, 50.606957656360734, -112.23884253719248,
        0.03296041114873217, 115.60384449646641, -193.58209356861505,
        -22.33816807309886, -180.12613974708367);

    /// <summary>
    /// CMYK to sRGB with a polynomial fitted to a typical press profile (U.S. Web Coated SWOP): pure process inks and
    /// black come out as they look in print, not as the over-saturated naive formula gives.
    /// </summary>
    public static (int R, int G, int B) Convert(float cf, float mf, float yf, float kf)
    {
        double c = Math.Clamp(cf, 0, 1), m = Math.Clamp(mf, 0, 1), y = Math.Clamp(yf, 0, 1), k = Math.Clamp(kf, 0, 1);
        return (RedFit.Channel(c, m, y, k), GreenFit.Channel(c, m, y, k), BlueFit.Channel(c, m, y, k));
    }

    /// <summary>
    /// The coefficients of one output channel's second-degree polynomial in C, M, Y and K, grouped by the first ink of
    /// each term: C*(CC*C + CM*M + CY*Y + CK*K + C1) + M*(MM*M + MY*Y + MK*K + M1) + Y*(YY*Y + YK*K + Y1) + K*(KK*K + K1).
    /// </summary>
    private readonly record struct CmykFit(
        double CC, double CM, double CY, double CK, double C1,
        double MM, double MY, double MK, double M1,
        double YY, double YK, double Y1,
        double KK, double K1)
    {
        /// <summary>The channel as 0-255, starting from paper white and rounded to nearest.</summary>
        public int Channel(double c, double m, double y, double k)
        {
            double v = 255 + c * (CC * c + CM * m + CY * y + CK * k + C1)
                           + m * (MM * m + MY * y + MK * k + M1)
                           + y * (YY * y + YK * k + Y1)
                           + k * (KK * k + K1);
            return (int)Math.Clamp(v + 0.5, 0, 255);
        }
    }
}

internal sealed class LabSpace : ColorSpace
{
    /// <summary>The minimum and maximum of a* and then of b*.</summary>
    private readonly double[] _range;

    public LabSpace(double[]? whitePoint, double[]? range)
    {
        _ = whitePoint; // colors are adapted to D65 white, so the document's white point is not needed
        _range = range is { Length: >= 4 } ? range : new double[] { -100, 100, -100, 100 };
    }

    public override int Components => 3;
    public override string Name => "Lab";

    public override double[] DefaultDecode(int bitsPerComponent) => new double[] { 0, 100, _range[0], _range[1], _range[2], _range[3] };

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        if (c.Length < 3) return 0;
        double l = c[0], a = Math.Clamp(c[1], _range[0], _range[1]), b = Math.Clamp(c[2], _range[2], _range[3]);
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        // Adapt the document's white point to D65 by scaling (von Kries), so Lab white is sRGB white.
        double x = 0.9505 * InverseF(fx), y = InverseF(fy), z = 1.089 * InverseF(fz);
        var m = XyzToLinearSrgb;
        return Rgb(Encode(m[0] * x + m[1] * y + m[2] * z), Encode(m[3] * x + m[4] * y + m[5] * z), Encode(m[6] * x + m[7] * y + m[8] * z));
    }

    /// <summary>The inverse of the CIE companding function f, linear below 6/29.</summary>
    private static double InverseF(double t) => t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);

    /// <summary>Linear sRGB to the sRGB transfer curve, without the cap at 1 that <see cref="CalSpace.Encode"/> has.</summary>
    private static double Encode(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
}

/// <summary>
/// CalGray and CalRGB (ISO 32000-1, 8.6.5.2 and 8.6.5.3): each component is raised to its gamma, CalRGB components are
/// then mixed by the matrix into CIE XYZ, and the result is adapted to D65 by scaling with the white point (as
/// <see cref="LabSpace"/> does) and encoded as sRGB.
/// </summary>
internal sealed class CalSpace : ColorSpace
{
    private const int EncodeTableSize = 4096;

    private readonly int _n;
    private readonly double[] _gamma;
    /// <summary>Row-major 3x3 matrix from the gamma-applied components straight to linear sRGB.</summary>
    public readonly double[] ToLinearSrgb;

    private CalSpace(int n, double[] gamma, double[] toLinear)
    {
        _n = n;
        _gamma = gamma;
        ToLinearSrgb = toLinear;
    }

    private static double[] Identity() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>Returns null when the white point is missing or invalid, so the caller can fall back to the device space.</summary>
    public static CalSpace? Create(bool rgb, double[]? whitePoint, double[]? gammas, double? gray, double[]? matrix)
    {
        if (whitePoint is not { Length: >= 3 } || whitePoint[0] <= 0 || whitePoint[1] <= 0 || whitePoint[2] <= 0) return null;
        if (!rgb)
        {
            // Gray maps to a multiple of the white point, which the adaptation turns into D65 white: a neutral grey with
            // linear sRGB value A^G, so no matrix is needed.
            return new CalSpace(1, new[] { ValidGamma(gray ?? 1) }, Identity());
        }
        var gamma = gammas is { Length: >= 3 } ? new[] { ValidGamma(gammas[0]), ValidGamma(gammas[1]), ValidGamma(gammas[2]) } : new double[] { 1, 1, 1 };
        var m = matrix is { Length: >= 9 } && AllFinite(matrix.AsSpan(0, 9)) ? matrix : Identity();
        // The PDF matrix lists XA YA ZA, XB YB ZB, XC YC ZC: column j holds the XYZ of component j.
        double[] adapt = { 0.9505 / whitePoint[0], 1 / whitePoint[1], 1.089 / whitePoint[2] };
        var toLinear = new double[9];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        {
            double sum = 0;
            for (int k = 0; k < 3; k++) sum += XyzToLinearSrgb[row * 3 + k] * adapt[k] * m[column * 3 + k];
            toLinear[row * 3 + column] = sum;
        }
        return new CalSpace(3, gamma, toLinear);
    }

    /// <summary>A gamma that is not positive and finite is read as 1.</summary>
    private static double ValidGamma(double g) => g > 0 && double.IsFinite(g) ? g : 1;

    private static bool AllFinite(ReadOnlySpan<double> values)
    {
        foreach (double v in values)
        {
            if (!double.IsFinite(v)) return false;
        }
        return true;
    }

    public override int Components => _n;
    public override string Name => _n == 1 ? "CalGray" : "CalRGB";

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        if (c.Length < _n) return 0;
        if (_n == 1)
        {
            int gray = ToByte(Encode(Linear(c[0], 0)));
            return Pack(gray, gray, gray);
        }
        double a = Linear(c[0], 0), b = Linear(c[1], 1), cc = Linear(c[2], 2);
        var m = ToLinearSrgb;
        return Rgb(Encode(m[0] * a + m[1] * b + m[2] * cc), Encode(m[3] * a + m[4] * b + m[5] * cc), Encode(m[6] * a + m[7] * b + m[8] * cc));
    }

    /// <summary>A component in 0-1 raised to its gamma.</summary>
    public double Linear(double v, int component) => v <= 0 ? 0 : Math.Pow(Math.Min(v, 1), _gamma[component]);

    /// <summary>Linear sRGB to the sRGB transfer curve.</summary>
    public static double Encode(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Min(v, 1), 1 / 2.4) - 0.055;

    private static byte[]? _encodeTable;

    /// <summary>
    /// <see cref="Encode"/> sampled at 4096 points of linear light and rounded to 8 bits, so images avoid a power per
    /// pixel; the spacing keeps the error under one level even on the steep dark end of the curve.
    /// </summary>
    public static byte[] EncodeTable => _encodeTable ??= BuildEncodeTable();

    private static byte[] BuildEncodeTable()
    {
        var table = new byte[EncodeTableSize];
        for (int i = 0; i < table.Length; i++) table[i] = (byte)ToByte(Encode(i / (double)(EncodeTableSize - 1)));
        return table;
    }
}

internal sealed class IndexedSpace : ColorSpace
{
    /// <summary>The largest index a palette can have; the table always has 256 entries.</summary>
    public const int MaxHiVal = 255;

    public readonly ColorSpace Base;
    public readonly int HiVal;
    /// <summary>RGB per index, packed as in <see cref="ColorSpace.ToRgb"/>.</summary>
    public readonly uint[] Palette;

    public IndexedSpace(ColorSpace baseCs, int hival, byte[] lookup)
    {
        Base = baseCs;
        HiVal = hival;
        Palette = new uint[MaxHiVal + 1];
        int components = baseCs.Components;
        // The base gets at least one component, so a base without components (an empty Pattern) still gets a color.
        Span<float> color = stackalloc float[Math.Max(1, components)];
        var decode = baseCs.DefaultDecode(8);
        for (int i = 0; i <= hival; i++)
        {
            for (int k = 0; k < components; k++)
            {
                // Each table byte spans the base's default decode range, e.g. 0 to 100 for Lab L*.
                int offset = i * components + k;
                double v = offset < lookup.Length ? lookup[offset] / 255.0 : 0;
                color[k] = (float)(decode[2 * k] + v * (decode[2 * k + 1] - decode[2 * k]));
            }
            Palette[i] = baseCs.ToRgb(color);
        }
        // Indexes above hival, which damaged images can hold, show the last color.
        Palette.AsSpan(hival + 1).Fill(Palette[hival]);
    }

    public override int Components => 1;
    public override string Name => "Indexed";

    public override double[] DefaultDecode(int bitsPerComponent) => new double[] { 0, (1 << bitsPerComponent) - 1 };

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        int i = c.Length > 0 ? (int)Math.Round(c[0]) : 0;
        return Palette[Math.Clamp(i, 0, MaxHiVal)];
    }
}

/// <summary>Separation and DeviceN: the tints go through the tint transform into the alternate space.</summary>
internal sealed class SeparationSpace : ColorSpace
{
    private const int LutSize = 256;

    private readonly int _n;
    public readonly bool IsNone, IsAll;
    private readonly ColorSpace _alt;
    private readonly PdfFunction? _fn;
    private uint[]? _lut;

    public SeparationSpace(int n, bool none, bool all, ColorSpace alt, PdfFunction? fn)
    {
        _n = n;
        IsNone = none;
        IsAll = all;
        _alt = alt;
        _fn = fn;
    }

    public override int Components => _n;
    public override string Name => "Separation";

    /// <summary>The initial color is full tint of every colorant.</summary>
    public override float[] InitialColor()
    {
        var c = new float[_n];
        Array.Fill(c, 1f);
        return c;
    }

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        if (_n == 1 && _lut != null)
        {
            int i = (int)(Math.Clamp(c.Length > 0 ? c[0] : 0, 0, 1) * (LutSize - 1) + 0.5f);
            return _lut[i];
        }
        Span<double> tints = stackalloc double[_n];
        for (int i = 0; i < _n; i++) tints[i] = i < c.Length ? c[i] : 0;
        // The buffer has room for four outputs even when the alternate space has fewer, as some transforms give more.
        Span<double> alternate = stackalloc double[Math.Max(4, _alt.Components)];
        if (_fn != null) _fn.Eval(tints, alternate);
        else TintAsGray(tints, alternate);
        Span<float> components = stackalloc float[_alt.Components];
        for (int i = 0; i < components.Length; i++) components[i] = (float)alternate[i];
        return _alt.ToRgb(components);
    }

    /// <summary>
    /// The fallback without a usable tint transform: the strongest tint as gray, from black ink alone in a CMYK
    /// alternate space and as 1 - tint in the others.
    /// </summary>
    private void TintAsGray(ReadOnlySpan<double> tints, Span<double> alternate)
    {
        double t = 0;
        foreach (double tint in tints) t = Math.Max(t, tint);
        int components = _alt.Components;
        for (int i = 0; i < components; i++) alternate[i] = components == 4 ? (i == 3 ? t : 0) : 1 - t;
    }

    /// <summary>Builds a 256-entry lookup table for single-component use (images, repeated fills).</summary>
    public void BuildLut()
    {
        if (_n != 1 || _lut != null) return;
        var lut = new uint[LutSize];
        Span<float> c = stackalloc float[1];
        for (int i = 0; i < lut.Length; i++)
        {
            c[0] = i / (float)(LutSize - 1);
            lut[i] = ToRgb(c);
        }
        _lut = lut;
    }
}

/// <summary>The Pattern color space; <see cref="Under"/> is the underlying space of uncolored tiling patterns.</summary>
internal sealed class PatternSpace : ColorSpace
{
    public readonly ColorSpace? Under;
    public PatternSpace(ColorSpace? under) => Under = under;
    public override int Components => Under?.Components ?? 0;
    public override string Name => "Pattern";
    public override uint ToRgb(ReadOnlySpan<float> c) => Under?.ToRgb(c) ?? 0;
}
