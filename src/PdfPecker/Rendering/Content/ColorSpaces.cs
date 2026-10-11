using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Content;

/// <summary>A PDF color space, converting component values (0-1 for most spaces) to 8-bit sRGB.</summary>
internal abstract class ColorSpace
{
    public abstract int Components { get; }
    public abstract string Name { get; }

    /// <summary>Converts one color. Returns packed R | G &lt;&lt; 8 | B &lt;&lt; 16.</summary>
    public abstract uint ToRgb(ReadOnlySpan<float> c);

    public virtual float[] InitialColor()
    {
        var c = new float[Components];
        return c;
    }

    /// <summary>The default Decode array for image samples.</summary>
    public virtual double[] DefaultDecode(int bitsPerComponent)
    {
        var d = new double[Components * 2];
        for (int i = 0; i < Components; i++) { d[2 * i] = 0; d[2 * i + 1] = 1; }
        return d;
    }

    protected static uint Rgb(double r, double g, double b)
        => (uint)(Byte(r) | Byte(g) << 8 | Byte(b) << 16);

    protected static int Byte(double v) => v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5);

    public static readonly ColorSpace Gray = new DeviceGray();
    public static readonly ColorSpace RgbSpace = new DeviceRgb();
    public static readonly ColorSpace Cmyk = new DeviceCmyk();

    /// <summary>Resolves a color space from a name or array, looking names up in the resources' /ColorSpace dictionary.</summary>
    public static ColorSpace? Parse(PdfFile file, PdfObject? obj, PdfDict? resources, int depth = 0)
    {
        if (depth > 10) return null;
        obj = file.Resolve(obj);
        if (obj is PdfName n)
        {
            switch (n.Value)
            {
                case "DeviceGray": case "G": case "CalGray": return Gray;
                case "DeviceRGB": case "RGB": case "CalRGB": return RgbSpace;
                case "DeviceCMYK": case "CMYK": return Cmyk;
                case "Pattern": return new PatternSpace(null);
                case "Indexed": case "I": return null;
            }
            var csDict = file.GetDict(resources, "ColorSpace");
            if (csDict != null && csDict[n.Value] is PdfObject named) return Parse(file, named, resources, depth + 1);
            return null;
        }
        if (obj is not PdfArray a || a.Count == 0) return null;
        var family = (file.Resolve(a[0]) as PdfName)?.Value;
        switch (family)
        {
            case "DeviceGray": case "G": case "CalGray": return Gray;
            case "DeviceRGB": case "RGB": case "CalRGB": return RgbSpace;
            case "DeviceCMYK": case "CMYK": return Cmyk;
            case "ICCBased":
            {
                var s = a.Count > 1 ? file.Resolve(a[1]) as PdfStream : null;
                int nComp = file.GetInt(s, "N", 3);
                var alt = s?["Alternate"] is PdfObject ao ? Parse(file, ao, resources, depth + 1) : null;
                if (alt != null && alt.Components == nComp) return alt;
                return nComp switch { 1 => Gray, 4 => Cmyk, _ => RgbSpace };
            }
            case "Lab":
            {
                var d = a.Count > 1 ? file.Resolve(a[1]) as PdfDict : null;
                return new LabSpace(file.GetNumbers(d, "WhitePoint"), file.GetNumbers(d, "Range"));
            }
            case "Indexed":
            case "I":
            {
                if (a.Count < 4) return null;
                var baseCs = Parse(file, a[1], resources, depth + 1);
                if (baseCs == null) return null;
                int hival = Math.Clamp(file.GetIntValue(a[2]), 0, 255);
                var lookupObj = file.Resolve(a[3]);
                byte[] lookup = lookupObj switch
                {
                    PdfString str => str.Bytes,
                    PdfStream st => file.DecodeStream(st),
                    _ => Array.Empty<byte>(),
                };
                return new IndexedSpace(baseCs, hival, lookup);
            }
            case "Separation":
            {
                if (a.Count < 4) return null;
                var name = (file.Resolve(a[1]) as PdfName)?.Value ?? "";
                var alt = Parse(file, a[2], resources, depth + 1) ?? Gray;
                var fn = PdfFunction.Create(file, a[3]);
                return new SeparationSpace(1, name == "None", name == "All", alt, fn);
            }
            case "DeviceN":
            {
                if (a.Count < 4) return null;
                var names = file.Resolve(a[1]) as PdfArray;
                int nComp = Math.Max(1, names?.Count ?? 1);
                var alt = Parse(file, a[2], resources, depth + 1) ?? Gray;
                var fn = PdfFunction.Create(file, a[3]);
                bool none = names != null && names.Items.All(x => (file.Resolve(x) as PdfName)?.Value == "None");
                return new SeparationSpace(nComp, none, false, alt, fn);
            }
            case "Pattern":
                return new PatternSpace(a.Count > 1 ? Parse(file, a[1], resources, depth + 1) : null);
        }
        return null;
    }
}

internal sealed class DeviceGray : ColorSpace
{
    public override int Components => 1;
    public override string Name => "DeviceGray";
    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        int g = Byte(c.Length > 0 ? c[0] : 0);
        return (uint)(g | g << 8 | g << 16);
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
    public override float[] InitialColor() => new float[] { 0, 0, 0, 1 };

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        if (c.Length < 4) return 0;
        var (r, g, b) = Convert(c[0], c[1], c[2], c[3]);
        return (uint)(r | g << 8 | b << 16);
    }

    /// <summary>
    /// CMYK to sRGB with a polynomial fitted to a typical press profile (U.S. Web Coated SWOP): pure process inks and
    /// black come out as they look in print, not as the over-saturated naive formula gives.
    /// </summary>
    public static (int R, int G, int B) Convert(float cf, float mf, float yf, float kf)
    {
        double c = Math.Clamp(cf, 0, 1), m = Math.Clamp(mf, 0, 1), y = Math.Clamp(yf, 0, 1), k = Math.Clamp(kf, 0, 1);
        double r = 255 + c * (-4.387332384609988 * c + 54.48615194189176 * m + 18.82290502165302 * y + 212.25662451639585 * k - 285.2331026137004)
                       + m * (1.7149763477362134 * m - 5.6096736904047315 * y - 17.873870861415444 * k - 5.497006427196366)
                       + y * (-2.5217340131683033 * y - 21.248923337353073 * k + 17.5119270841813)
                       + k * (-21.86122147463605 * k - 189.48180835922747);
        double g = 255 + c * (8.841041422036149 * c + 60.118027045597366 * m + 6.871425592049007 * y + 31.159100130055922 * k - 79.2970844816548)
                       + m * (-15.310361306967817 * m + 17.575251261109482 * y + 131.35250912493976 * k - 190.9453302588951)
                       + y * (4.444339102852739 * y + 9.8632861493405 * k - 24.86741582555878)
                       + k * (-20.737325471181034 * k - 187.80453709719578);
        double b = 255 + c * (0.8842522430003296 * c + 8.078677503112928 * m + 30.89978309703729 * y - 0.23883238689178934 * k - 14.183576799673286)
                       + m * (10.49593273432072 * m + 63.02378494754052 * y + 50.606957656360734 * k - 112.23884253719248)
                       + y * (0.03296041114873217 * y + 115.60384449646641 * k - 193.58209356861505)
                       + k * (-22.33816807309886 * k - 180.12613974708367);
        return ((int)Math.Clamp(r + 0.5, 0, 255), (int)Math.Clamp(g + 0.5, 0, 255), (int)Math.Clamp(b + 0.5, 0, 255));
    }
}

internal sealed class LabSpace : ColorSpace
{
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
        double m = (l + 16) / 116, ll = m + a / 500, n = m - b / 200;
        static double G(double x) => x >= 6.0 / 29 ? x * x * x : 108.0 / 841 * (x - 4.0 / 29);
        // Adapt the document's white point to D65 by scaling (von Kries), so Lab white is sRGB white.
        double x = 0.9505 * G(ll), y = G(m), z = 1.089 * G(n);
        // XYZ (D65) to linear sRGB.
        double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double bb = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        return Rgb(Gamma(r), Gamma(g), Gamma(bb));
    }

    private static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
}

internal sealed class IndexedSpace : ColorSpace
{
    public readonly ColorSpace Base;
    public readonly int HiVal;
    /// <summary>RGB per index, packed as in <see cref="ColorSpace.ToRgb"/>.</summary>
    public readonly uint[] Palette;

    public IndexedSpace(ColorSpace baseCs, int hival, byte[] lookup)
    {
        Base = baseCs;
        HiVal = hival;
        Palette = new uint[256];
        int nc = baseCs.Components;
        Span<float> comp = stackalloc float[Math.Max(1, nc)];
        var dec = baseCs.DefaultDecode(8);
        for (int i = 0; i <= hival; i++)
        {
            for (int k = 0; k < nc; k++)
            {
                int idx = i * nc + k;
                double v = idx < lookup.Length ? lookup[idx] / 255.0 : 0;
                comp[k] = (float)(dec[2 * k] + v * (dec[2 * k + 1] - dec[2 * k]));
            }
            Palette[i] = baseCs.ToRgb(comp);
        }
        for (int i = hival + 1; i < 256; i++) Palette[i] = Palette[hival];
    }

    public override int Components => 1;
    public override string Name => "Indexed";

    public override double[] DefaultDecode(int bitsPerComponent) => new double[] { 0, (1 << bitsPerComponent) - 1 };

    public override uint ToRgb(ReadOnlySpan<float> c)
    {
        int i = c.Length > 0 ? (int)Math.Round(c[0]) : 0;
        return Palette[Math.Clamp(i, 0, 255)];
    }
}

internal sealed class SeparationSpace : ColorSpace
{
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
            int i = (int)(Math.Clamp(c.Length > 0 ? c[0] : 0, 0, 1) * 255 + 0.5f);
            return _lut[i];
        }
        Span<double> input = stackalloc double[_n];
        for (int i = 0; i < _n; i++) input[i] = i < c.Length ? c[i] : 0;
        Span<double> output = stackalloc double[Math.Max(4, _alt.Components)];
        output.Clear();
        if (_fn != null) _fn.Eval(input, output);
        else
        {
            // No usable tint transform: show the tint as gray.
            double t = 0;
            for (int i = 0; i < _n; i++) t = Math.Max(t, input[i]);
            for (int i = 0; i < _alt.Components; i++) output[i] = _alt.Components == 4 ? (i == 3 ? t : 0) : 1 - t;
        }
        Span<float> f = stackalloc float[_alt.Components];
        for (int i = 0; i < f.Length; i++) f[i] = (float)output[i];
        return _alt.ToRgb(f);
    }

    /// <summary>Builds a 256-entry lookup table for single-component use (images, repeated fills).</summary>
    public void BuildLut()
    {
        if (_n != 1 || _lut != null) return;
        var lut = new uint[256];
        Span<float> c = stackalloc float[1];
        for (int i = 0; i < 256; i++)
        {
            c[0] = i / 255f;
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
