using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Svg;

/// <summary>
/// An SVG filter: its region in root user space and its primitives, run on the pixels of the filtered content.
/// Every primitive of SVG 1.1 and feDropShadow, each within its primitive subregion. Lengths of the primitives'
/// own pixel grids (convolution kernels, lighting normals) are device pixels, as in browsers.
/// </summary>
internal sealed partial class SvgFilter
{
    public float X, Y, W, H; // the filter region, root user space
    public readonly List<Primitive> Primitives = new();
    public Matrix ToUser = Matrix.Identity; // root space to the user space of the filtered element
    public float UserScale = 1;             // root units per user unit
    // Made by the layout for blurred text-shadows, in the one shape that ApplyShadows runs.
    public bool TextShadows;

    internal enum Kind : byte
    {
        Blur, Offset, DropShadow, Flood, Merge, Composite, Blend, ColorMatrix, ComponentTransfer, Morphology, PassThrough,
        Turbulence, Image, Tile, ConvolveMatrix, DisplacementMap, DiffuseLighting, SpecularLighting,
    }

    internal sealed class Primitive
    {
        public Kind Kind;
        public string? In, In2, Result;
        public List<string?>? Inputs;     // feMerge
        public float Sx, Sy;              // blur deviation or morphology radius, root units
        public float Dx, Dy;              // offset, root units
        public uint Color = CssColor.Black; // flood colour (straight sRGB, with alpha)
        public string Operator = "";      // composite or morphology operator, blend mode, colour matrix type
        public float K1, K2, K3, K4;
        public float[]? Matrix;           // 4 × 5 colour matrix
        public Transfer?[]? Functions;    // R, G, B, A
        public bool Linear = true;        // color-interpolation-filters: linearRGB

        // The primitive subregion: in root space when given, and in user space (for turbulence tiles) either way.
        public bool HasRegion;
        public float RX, RY, RW, RH, UX, UY, UW, UH;

        // feTurbulence.
        public float FreqX, FreqY;
        public int Octaves = 1, Seed;
        public bool Stitch, Fractal;

        // feImage: what it draws, in root space, rendered over the filter region by the writer.
        public List<SvgShape>? Shapes;
        public Element? Element;

        // feConvolveMatrix.
        public int OrderX = 3, OrderY = 3, TargetX, TargetY;
        public float[]? Kernel;
        public float Divisor, Bias;
        public bool PreserveAlpha;

        // feDisplacementMap (Sx, Sy hold the scale in root units) and lighting.
        public int XChannel = 3, YChannel = 3;
        public float SurfaceScale = 1, Constant = 1, SpecularExponent = 1;
        public Light? Light;
    }

    /// <summary>A light source of feDiffuseLighting or feSpecularLighting; positions in root space, z in root units.</summary>
    internal sealed class Light
    {
        public byte Type; // 0 distant, 1 point, 2 spot
        public float Azimuth, Elevation;
        public float X, Y, Z, PointsAtX, PointsAtY, PointsAtZ;
        public float SpotExponent = 1, ConeCos = float.NaN;
    }

    internal sealed class Transfer
    {
        public string Type = "identity";
        public float[] Table = Array.Empty<float>();
        public float Slope = 1, Intercept, Amplitude = 1, Exponent = 1, Offset;

        public float Apply(float c)
        {
            switch (Type)
            {
                case "table" when Table.Length > 0:
                {
                    int n = Table.Length - 1;
                    if (n == 0) return Table[0];
                    float pos = Math.Clamp(c, 0, 1) * n;
                    int k = Math.Min((int)pos, n - 1);
                    return Table[k] + (pos - k) * (Table[k + 1] - Table[k]);
                }
                case "discrete" when Table.Length > 0:
                {
                    int n = Table.Length;
                    return Table[Math.Min((int)(Math.Clamp(c, 0, 1) * n), n - 1)];
                }
                case "linear": return Slope * c + Intercept;
                case "gamma": return Amplitude * MathF.Pow(Math.Max(c, 0), Exponent) + Offset;
                default: return c;
            }
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Reads the primitives of a filter element. Lengths are user units of <paramref name="ctm"/>, or fractions of the
    /// bounding box <paramref name="box"/> for primitiveUnits="objectBoundingBox" (<paramref name="primitiveBox"/>).
    /// <paramref name="region"/> is the filter region and <paramref name="viewport"/> the size percentages refer to,
    /// both in user space. Returns null when the filter has no primitives (it then draws nothing).
    /// </summary>
    public static SvgFilter? Parse(Element filter, Matrix ctm, bool primitiveBox, (float X, float Y, float W, float H) box,
        (float X, float Y, float W, float H) region, (float W, float H) viewport)
    {
        var f = new SvgFilter { UserScale = ctm.ScaleFactor };
        if (ctm.TryInvert(out var toUser)) f.ToUser = toUser;
        float unitX = primitiveBox ? box.W : 1, unitY = primitiveBox ? box.H : 1;
        // Lengths along x and y of user space, measured in root space.
        float lx = MathF.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B) * unitX, ly = MathF.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D) * unitY;
        foreach (var child in filter.Children)
        {
            if (child is not Element e || !e.Name.StartsWith("fe", StringComparison.Ordinal)) continue;
            var p = new Primitive
            {
                In = e.GetAttribute("in")?.Trim(),
                In2 = e.GetAttribute("in2")?.Trim(),
                Result = e.GetAttribute("result")?.Trim(),
                Linear = e.Style?.SvgFilterSrgb != true,
            };
            Subregion(e, p, ctm, primitiveBox, box, region, viewport);
            void Offset()
            {
                float dx = Number(e, "dx", p.Kind == Kind.DropShadow ? 2 : 0) * unitX, dy = Number(e, "dy", p.Kind == Kind.DropShadow ? 2 : 0) * unitY;
                p.Dx = ctm.A * dx + ctm.C * dy;
                p.Dy = ctm.B * dx + ctm.D * dy;
            }
            void Pair(string name, float fallback)
            {
                var v = SvgImage.ParseNumbers(e.GetAttribute(name));
                float a = v.Count > 0 ? v[0] : fallback, b = v.Count > 1 ? v[1] : a;
                p.Sx = a * lx;
                p.Sy = b * ly;
            }
            void Flood()
            {
                var s = e.Style;
                uint c = s?.SvgFloodColor ?? CssColor.Black;
                float alpha = (c >> 24) / 255f * (s?.SvgFloodOpacity ?? 1);
                p.Color = ((uint)Math.Round(Math.Clamp(alpha, 0, 1) * 255) << 24) | (c & 0xFFFFFF);
            }
            switch (e.Name)
            {
                case "fegaussianblur":
                    p.Kind = Kind.Blur;
                    Pair("stddeviation", 0);
                    break;
                case "feoffset":
                    p.Kind = Kind.Offset;
                    Offset();
                    break;
                case "fedropshadow":
                    p.Kind = Kind.DropShadow;
                    Pair("stddeviation", 2);
                    Offset();
                    Flood();
                    break;
                case "feflood":
                    p.Kind = Kind.Flood;
                    Flood();
                    break;
                case "femerge":
                    p.Kind = Kind.Merge;
                    p.Inputs = e.Children.OfType<Element>().Where(n => n.Name == "femergenode").Select(n => n.GetAttribute("in")?.Trim()).ToList();
                    break;
                case "fecomposite":
                    p.Kind = Kind.Composite;
                    p.Operator = (e.GetAttribute("operator") ?? "over").Trim().ToLowerInvariant();
                    p.K1 = Number(e, "k1", 0); p.K2 = Number(e, "k2", 0); p.K3 = Number(e, "k3", 0); p.K4 = Number(e, "k4", 0);
                    break;
                case "feblend":
                    p.Kind = Kind.Blend;
                    p.Operator = (e.GetAttribute("mode") ?? "normal").Trim().ToLowerInvariant();
                    break;
                case "fecolormatrix":
                    p.Kind = Kind.ColorMatrix;
                    p.Matrix = ColorMatrix((e.GetAttribute("type") ?? "matrix").Trim().ToLowerInvariant(), e.GetAttribute("values"));
                    break;
                case "fecomponenttransfer":
                    p.Kind = Kind.ComponentTransfer;
                    p.Functions = new Transfer?[4];
                    foreach (var fn in e.Children.OfType<Element>())
                    {
                        int channel = fn.Name switch { "fefuncr" => 0, "fefuncg" => 1, "fefuncb" => 2, "fefunca" => 3, _ => -1 };
                        if (channel < 0) continue;
                        p.Functions[channel] = new Transfer
                        {
                            Type = (fn.GetAttribute("type") ?? "identity").Trim().ToLowerInvariant(),
                            Table = SvgImage.ParseNumbers(fn.GetAttribute("tablevalues")).ToArray(),
                            Slope = Number(fn, "slope", 1), Intercept = Number(fn, "intercept", 0),
                            Amplitude = Number(fn, "amplitude", 1), Exponent = Number(fn, "exponent", 1), Offset = Number(fn, "offset", 0),
                        };
                    }
                    break;
                case "femorphology":
                    p.Kind = Kind.Morphology;
                    p.Operator = (e.GetAttribute("operator") ?? "erode").Trim().ToLowerInvariant();
                    Pair("radius", 0);
                    break;
                case "feturbulence":
                {
                    p.Kind = Kind.Turbulence;
                    var freq = SvgImage.ParseNumbers(e.GetAttribute("basefrequency"));
                    p.FreqX = freq.Count > 0 ? freq[0] : 0;
                    p.FreqY = freq.Count > 1 ? freq[1] : p.FreqX;
                    if (p.FreqX < 0 || p.FreqY < 0) p.FreqX = p.FreqY = 0;
                    p.Octaves = (int)Math.Clamp(Number(e, "numoctaves", 1), 0, 24);
                    p.Seed = (int)Math.Clamp(MathF.Round(Number(e, "seed", 0)), int.MinValue + 1, int.MaxValue);
                    p.Stitch = string.Equals(e.GetAttribute("stitchtiles")?.Trim(), "stitch", StringComparison.OrdinalIgnoreCase);
                    p.Fractal = string.Equals(e.GetAttribute("type")?.Trim(), "fractalNoise", StringComparison.OrdinalIgnoreCase);
                    break;
                }
                case "feimage":
                    p.Kind = Kind.Image;
                    p.Element = e; // drawn by the SVG image, which can render elements and load files
                    break;
                case "fetile":
                    p.Kind = Kind.Tile;
                    break;
                case "feconvolvematrix":
                    p.Kind = Kind.ConvolveMatrix;
                    ParseConvolve(e, p);
                    break;
                case "fedisplacementmap":
                {
                    p.Kind = Kind.DisplacementMap;
                    float scale = Number(e, "scale", 0);
                    p.Sx = scale * lx;
                    p.Sy = scale * ly;
                    p.XChannel = Channel(e.GetAttribute("xchannelselector"));
                    p.YChannel = Channel(e.GetAttribute("ychannelselector"));
                    break;
                }
                case "fediffuselighting":
                case "fespecularlighting":
                    p.Kind = e.Name == "fediffuselighting" ? Kind.DiffuseLighting : Kind.SpecularLighting;
                    ParseLighting(e, p, ctm, primitiveBox, box);
                    break;
                default:
                    p.Kind = Kind.PassThrough;
                    break;
            }
            f.Primitives.Add(p);
        }
        return f.Primitives.Count > 0 ? f : null;
    }

    private static float Number(Element e, string name, float fallback)
    {
        var v = e.GetAttribute(name);
        return v != null && float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? f : fallback;
    }

    /// <summary>The 4 × 5 matrix of an feColorMatrix (rows R, G, B, A; columns R, G, B, A, offset).</summary>
    private static float[]? ColorMatrix(string type, string? values)
    {
        var v = SvgImage.ParseNumbers(values);
        switch (type)
        {
            case "saturate":
            {
                float s = v.Count > 0 ? v[0] : 1;
                return new[]
                {
                    0.213f + 0.787f * s, 0.715f - 0.715f * s, 0.072f - 0.072f * s, 0, 0,
                    0.213f - 0.213f * s, 0.715f + 0.285f * s, 0.072f - 0.072f * s, 0, 0,
                    0.213f - 0.213f * s, 0.715f - 0.715f * s, 0.072f + 0.928f * s, 0, 0,
                    0, 0, 0, 1, 0,
                };
            }
            case "huerotate":
            {
                float a = (v.Count > 0 ? v[0] : 0) * MathF.PI / 180, c = MathF.Cos(a), s = MathF.Sin(a);
                return new[]
                {
                    0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f, 0, 0,
                    0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f, 0, 0,
                    0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f, 0, 0,
                    0, 0, 0, 1, 0,
                };
            }
            case "luminancetoalpha":
                return new[] { 0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.2125f, 0.7154f, 0.0721f, 0, 0 };
            default:
                return v.Count == 20 ? v.ToArray() : null; // null: identity
        }
    }

    // ------------------------------------------------------------------ pixels

    /// <summary>
    /// A w × h image of premultiplied RGBA floats, in linearRGB or sRGB, transparent outside its subregion (pixel
    /// columns X0 to X1 and rows Y0 to Y1, ends excluded).
    /// </summary>
    private sealed class Image
    {
        public readonly float[] P;
        public readonly bool Linear;
        public int X0, Y0, X1, Y1;
        public Image(float[] p, bool linear) { P = p; Linear = linear; }
        public Image(float[] p, bool linear, Image regionOf) : this(p, linear) { (X0, Y0, X1, Y1) = (regionOf.X0, regionOf.Y0, regionOf.X1, regionOf.Y1); }
    }

    private static readonly float[] ToLinearTable = BuildTable(toLinear: true), ToSrgbTable = BuildTable(toLinear: false);
    private const int TableSize = 4096;

    private static float[] BuildTable(bool toLinear)
    {
        var t = new float[TableSize + 1];
        for (int i = 0; i <= TableSize; i++)
        {
            double c = i / (double)TableSize;
            t[i] = (float)(toLinear
                ? (c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4))
                : (c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055));
        }
        return t;
    }

    private static float Lookup(float[] table, float c) => table[(int)(Math.Clamp(c, 0, 1) * TableSize + 0.5f)];

    /// <summary>
    /// The most work one filter may take, in pixel operations, and the most primitives it may have. Every primitive is
    /// linear in the pixels except for the kernels and octaves that the document chooses, so a few primitives with large
    /// ones on the largest region would otherwise run for hours. A filter beyond these is left out, as browsers leave out
    /// one they can not run, rather than failing the document.
    /// </summary>
    internal const long MaxWork = 500_000_000;
    internal const int MaxPrimitives = 256;

    /// <summary>Whether running the filter on w × h pixels stays within <see cref="MaxWork"/>.</summary>
    public bool WithinBudget(int w, int h)
    {
        if (Primitives.Count > MaxPrimitives) return false;
        // A fixed part per primitive covers the work that does not grow with the pixels.
        long pixels = (long)w * h + 256, work = 0;
        foreach (var p in Primitives)
        {
            long perPixel = p.Kind switch
            {
                Kind.Blur or Kind.DropShadow or Kind.Morphology => 8, // running sums and extremes, whatever the radius
                Kind.ConvolveMatrix => p.Kernel != null ? (long)p.OrderX * p.OrderY : 1,
                Kind.Turbulence => 16L * Math.Max(1, p.Octaves), // four channels of noise for each octave
                Kind.DiffuseLighting or Kind.SpecularLighting => 16,
                Kind.Merge => 1 + (p.Inputs?.Count ?? 0),
                _ => 2,
            };
            work += pixels * perPixel;
            if (work > MaxWork) return false;
        }
        return true;
    }

    /// <summary>The image in the wanted colour space (a copy when it changes).</summary>
    private static Image Convert(Image img, bool linear)
    {
        if (img.Linear == linear) return img;
        var table = linear ? ToLinearTable : ToSrgbTable;
        var src = img.P;
        var dst = new float[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            float a = src[i + 3];
            if (a <= 0) continue;
            dst[i] = Lookup(table, src[i] / a) * a;
            dst[i + 1] = Lookup(table, src[i + 1] / a) * a;
            dst[i + 2] = Lookup(table, src[i + 2] / a) * a;
            dst[i + 3] = a;
        }
        return new Image(dst, linear, img);
    }

    /// <summary>
    /// Runs the filter on the content's pixels (premultiplied sRGB, w × h covering the filter region), with
    /// <paramref name="scaleX"/> and <paramref name="scaleY"/> pixels per root unit. <paramref name="rasterize"/> renders
    /// shapes over the filter region in the same way, for feImage. Returns premultiplied sRGB.
    /// </summary>
    public float[] Apply(float[] sourcePixels, int w, int h, float scaleX, float scaleY, CancellationToken cancel = default,
        Func<List<SvgShape>, float[]?>? rasterize = null)
    {
        var source = new Image(sourcePixels, false) { X1 = w, Y1 = h };
        Image? alpha = null;
        var results = new Dictionary<string, Image>(StringComparer.Ordinal);
        Image last = source;
        Image Input(string? name)
        {
            switch (name)
            {
                case null or "": return last;
                case "SourceGraphic": return source;
                case "SourceAlpha":
                    if (alpha == null)
                    {
                        var a = new float[sourcePixels.Length];
                        for (int i = 3; i < a.Length; i += 4) a[i] = sourcePixels[i];
                        alpha = new Image(a, false, source);
                    }
                    return alpha;
                case "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint":
                    return new Image(new float[sourcePixels.Length], false, source);
                default:
                    return results.TryGetValue(name, out var r) ? r : last;
            }
        }
        foreach (var p in Primitives)
        {
            cancel.ThrowIfCancellationRequested();
            // The subregion: given, else the union of the inputs' (all of the filter region without inputs).
            int x0, y0, x1, y1;
            if (p.HasRegion)
            {
                x0 = Math.Clamp((int)MathF.Floor((p.RX - X) * scaleX + 0.001f), 0, w);
                y0 = Math.Clamp((int)MathF.Floor((p.RY - Y) * scaleY + 0.001f), 0, h);
                x1 = Math.Clamp((int)MathF.Ceiling((p.RX + p.RW - X) * scaleX - 0.001f), x0, w);
                y1 = Math.Clamp((int)MathF.Ceiling((p.RY + p.RH - Y) * scaleY - 0.001f), y0, h);
            }
            else if (p.Kind is Kind.Flood or Kind.Turbulence or Kind.Image or Kind.Tile) (x0, y0, x1, y1) = (0, 0, w, h); // feTile fills the filter region, as in browsers
            else
            {
                var inputs = p.Kind == Kind.Merge ? (p.Inputs ?? new List<string?>()).Select(Input).ToList()
                    : p.Kind is Kind.Composite or Kind.Blend or Kind.DisplacementMap ? new List<Image> { Input(p.In), Input(p.In2) }
                    : new List<Image> { Input(p.In) };
                if (inputs.Count == 0) (x0, y0, x1, y1) = (0, 0, w, h);
                else
                {
                    (x0, y0, x1, y1) = (w, h, 0, 0);
                    foreach (var i in inputs) { x0 = Math.Min(x0, i.X0); y0 = Math.Min(y0, i.Y0); x1 = Math.Max(x1, i.X1); y1 = Math.Max(y1, i.Y1); }
                    if (x1 < x0 || y1 < y0) (x0, y0, x1, y1) = (0, 0, 0, 0);
                }
            }
            Image result = p.Kind switch
            {
                Kind.Blur => new Image(Blur(Convert(Input(p.In), p.Linear).P, w, h, p.Sx * scaleX, p.Sy * scaleY), p.Linear),
                Kind.Offset => new Image(Shift(Convert(Input(p.In), p.Linear).P, w, h, p.Dx * scaleX, p.Dy * scaleY), p.Linear),
                Kind.DropShadow => DropShadow(Convert(Input(p.In), p.Linear), w, h, p, scaleX, scaleY),
                Kind.Flood => new Image(Flood(w, h, p.Color, p.Linear), p.Linear),
                Kind.Merge => Merge(p, Input, w, h),
                Kind.Composite => new Image(Composite(Convert(Input(p.In), p.Linear).P, Convert(Input(p.In2), p.Linear).P, p), p.Linear),
                Kind.Blend => new Image(Blend(Convert(Input(p.In), p.Linear).P, Convert(Input(p.In2), p.Linear).P, p.Operator), p.Linear),
                Kind.ColorMatrix => new Image(ColorMatrix(Convert(Input(p.In), p.Linear).P, p.Matrix), p.Linear),
                Kind.ComponentTransfer => new Image(ComponentTransfer(Convert(Input(p.In), p.Linear).P, p.Functions!), p.Linear),
                Kind.Morphology => new Image(Morphology(Convert(Input(p.In), p.Linear).P, w, h, p.Sx * scaleX, p.Sy * scaleY, p.Operator == "dilate"), p.Linear),
                Kind.Turbulence => new Image(Turbulence(p, w, h, scaleX, scaleY, x0, y0, x1, y1), p.Linear),
                Kind.Image => new Image(p.Shapes is { Count: > 0 } shapes && rasterize?.Invoke(shapes) is { } drawn ? drawn : new float[w * h * 4], false),
                Kind.Tile => new Image(Tile(Convert(Input(p.In), p.Linear), w, x0, y0, x1, y1), p.Linear),
                Kind.ConvolveMatrix => new Image(Convolve(Convert(Input(p.In), p.Linear).P, w, h, p), p.Linear),
                Kind.DisplacementMap => Displace(Input(p.In), Convert(Input(p.In2), p.Linear), w, h, p.Sx * scaleX, p.Sy * scaleY, p),
                Kind.DiffuseLighting or Kind.SpecularLighting => new Image(Lighting(Convert(Input(p.In), p.Linear).P, w, h, scaleX, scaleY, p), p.Linear),
                _ => Input(p.In),
            };
            result = Clip(result, w, h, x0, y0, x1, y1);
            last = result;
            if (!string.IsNullOrEmpty(p.Result)) results[p.Result] = result;
        }
        return Convert(last, false).P;
    }

    /// <summary>The image made transparent outside the subregion (a copy when that changes pixels), with that region.</summary>
    private static Image Clip(Image img, int w, int h, int x0, int y0, int x1, int y1)
    {
        var src = img.P;
        var dst = src;
        if (x0 > 0 || y0 > 0 || x1 < w || y1 < h)
        {
            dst = new float[src.Length];
            for (int y = y0; y < y1; y++) Array.Copy(src, (y * w + x0) * 4, dst, (y * w + x0) * 4, (x1 - x0) * 4);
        }
        return new Image(dst, img.Linear) { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };
    }

    private static float[] Flood(int w, int h, uint color, bool linear)
    {
        float a = (color >> 24) / 255f;
        float r = ((color >> 16) & 255) / 255f, g = ((color >> 8) & 255) / 255f, b = (color & 255) / 255f;
        if (linear) { r = Lookup(ToLinearTable, r); g = Lookup(ToLinearTable, g); b = Lookup(ToLinearTable, b); }
        var p = new float[w * h * 4];
        for (int i = 0; i < p.Length; i += 4) { p[i] = r * a; p[i + 1] = g * a; p[i + 2] = b * a; p[i + 3] = a; }
        return p;
    }

    private static Image Merge(Primitive p, Func<string?, Image> input, int w, int h)
    {
        var dst = new float[w * h * 4];
        foreach (var name in p.Inputs ?? new List<string?>())
        {
            var src = Convert(input(name), p.Linear).P;
            for (int i = 0; i < dst.Length; i += 4)
            {
                float k = 1 - src[i + 3];
                dst[i] = src[i] + dst[i] * k;
                dst[i + 1] = src[i + 1] + dst[i + 1] * k;
                dst[i + 2] = src[i + 2] + dst[i + 2] * k;
                dst[i + 3] = src[i + 3] + dst[i + 3] * k;
            }
        }
        return new Image(dst, p.Linear);
    }

    private static Image DropShadow(Image input, int w, int h, Primitive p, float scaleX, float scaleY)
    {
        // The input's alpha, blurred and offset, filled with the flood colour, under the input.
        var src = input.P;
        var a = new float[src.Length];
        for (int i = 3; i < a.Length; i += 4) a[i] = src[i];
        a = Shift(Blur(a, w, h, p.Sx * scaleX, p.Sy * scaleY), w, h, p.Dx * scaleX, p.Dy * scaleY);
        var shadow = Flood(w, h, p.Color, p.Linear);
        var dst = new float[src.Length];
        for (int i = 0; i < dst.Length; i += 4)
        {
            float sa = a[i + 3], k = 1 - src[i + 3];
            dst[i] = src[i] + shadow[i] * sa * k;
            dst[i + 1] = src[i + 1] + shadow[i + 1] * sa * k;
            dst[i + 2] = src[i + 2] + shadow[i + 2] * sa * k;
            dst[i + 3] = src[i + 3] + shadow[i + 3] * sa * k;
        }
        return new Image(dst, p.Linear);
    }

    /// <summary>Moves the image by a whole number of pixels; what moves in from outside is transparent.</summary>
    private static float[] Shift(float[] src, int w, int h, float dx, float dy)
    {
        int ox = (int)MathF.Round(dx), oy = (int)MathF.Round(dy);
        if (ox == 0 && oy == 0) return src;
        var dst = new float[src.Length];
        for (int y = 0; y < h; y++)
        {
            int sy = y - oy;
            if (sy < 0 || sy >= h) continue;
            int x0 = Math.Max(0, ox), x1 = Math.Min(w, w + ox);
            if (x1 > x0) Array.Copy(src, (sy * w + x0 - ox) * 4, dst, (y * w + x0) * 4, (x1 - x0) * 4);
        }
        return dst;
    }

    /// <summary>
    /// A Gaussian blur, approximated by three box blurs per axis as the SVG specification describes. A deviation of
    /// zero on an axis leaves that axis alone.
    /// </summary>
    private static float[] Blur(float[] src, int w, int h, float sx, float sy)
    {
        var a = (float[])src.Clone();
        var tmp = new float[src.Length];
        if (sx > 0.01f) BlurAxis(ref a, ref tmp, w, h, sx, horizontal: true);
        if (sy > 0.01f) BlurAxis(ref a, ref tmp, w, h, sy, horizontal: false);
        return a;
    }

    private static void BlurAxis(ref float[] a, ref float[] tmp, int w, int h, float sigma, bool horizontal)
    {
        foreach (var (before, after) in BoxPasses(sigma)) { Box(a, tmp, w, h, before, after, horizontal); (a, tmp) = (tmp, a); }
    }

    /// <summary>
    /// The three box blurs that approximate a Gaussian of deviation <paramref name="sigma"/> pixels, as the SVG
    /// specification gives them: how far each reaches before and after the pixel.
    /// </summary>
    private static (int Before, int After)[] BoxPasses(float sigma)
    {
        int d = (int)MathF.Floor(sigma * 3 * MathF.Sqrt(2 * MathF.PI) / 4 + 0.5f);
        d = Math.Clamp(d, 1, 2000);
        // An even size: two boxes of size d, off centre to either side, then one of size d + 1 centred on the pixel.
        return d % 2 == 1 ? new[] { (d / 2, d / 2), (d / 2, d / 2), (d / 2, d / 2) } : new[] { (d / 2, d / 2 - 1), (d / 2 - 1, d / 2), (d / 2, d / 2) };
    }

    /// <summary>
    /// Blurs one channel of w × h values, rows with running sums along them and columns with a running sum per column,
    /// so that both passes read memory in order.
    /// </summary>
    private static float[] BlurChannel(float[] src, int w, int h, float sx, float sy)
    {
        var a = src;
        var tmp = new float[src.Length];
        if (sx > 0.01f)
            foreach (var (before, after) in BoxPasses(sx))
            {
                float inv = 1f / (before + after + 1);
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    double sum = 0;
                    for (int k = 0; k <= Math.Min(after, w - 1); k++) sum += a[row + k];
                    for (int x = 0; x < w; x++)
                    {
                        tmp[row + x] = (float)(sum * inv);
                        int add = x + after + 1, remove = x - before;
                        if (add < w) sum += a[row + add];
                        if (remove >= 0) sum -= a[row + remove];
                    }
                }
                (a, tmp) = (tmp, a == src ? new float[src.Length] : a);
            }
        if (sy > 0.01f)
        {
            var sums = new double[w];
            foreach (var (before, after) in BoxPasses(sy))
            {
                float inv = 1f / (before + after + 1);
                Array.Clear(sums);
                for (int k = 0; k <= Math.Min(after, h - 1); k++)
                    for (int x = 0; x < w; x++) sums[x] += a[k * w + x];
                for (int y = 0; y < h; y++)
                {
                    int row = y * w, add = (y + after + 1) * w, remove = (y - before) * w;
                    for (int x = 0; x < w; x++) tmp[row + x] = (float)(sums[x] * inv);
                    if (y + after + 1 < h) for (int x = 0; x < w; x++) sums[x] += a[add + x];
                    if (y - before >= 0) for (int x = 0; x < w; x++) sums[x] -= a[remove + x];
                }
                (a, tmp) = (tmp, a == src ? new float[src.Length] : a);
            }
        }
        return a;
    }

    /// <summary>
    /// Runs a filter made for blurred text-shadows (each shadow a blur of the source's alpha, an offset, a flood and a
    /// composite, then a merge) on the content's alpha alone, w × h values: the same pixels as <see cref="Apply"/> in a
    /// fraction of its time and memory, which matters with an image for every line of shadowed text. Returns straight
    /// sRGB colour and alpha bytes, or null when nothing shows.
    /// </summary>
    public (byte[] Rgb, byte[] Alpha)? ApplyShadows(float[] alpha, int w, int h, float scaleX, float scaleY, CancellationToken cancel = default)
    {
        int n = w * h;
        float[]? r = null, g = null, b = null;
        var acc = new float[n];
        uint first = 0;
        bool oneColor = true;
        for (int k = 0; k + 3 < Primitives.Count; k += 4)
        {
            cancel.ThrowIfCancellationRequested();
            var (blur, offset, flood) = (Primitives[k], Primitives[k + 1], Primitives[k + 2]);
            var a = BlurChannel(alpha, w, h, blur.Sx * scaleX, blur.Sy * scaleY);
            int ox = (int)MathF.Round(offset.Dx * scaleX), oy = (int)MathF.Round(offset.Dy * scaleY);
            uint c = flood.Color;
            float ca = (c >> 24) / 255f;
            if (k == 0) first = c;
            else if ((c & 0xFFFFFF) != (first & 0xFFFFFF) && r == null)
            {
                // Shadows of different colours: keep premultiplied colour, so far all of the first colour.
                oneColor = false;
                r = new float[n]; g = new float[n]; b = new float[n];
                float fr = ((first >> 16) & 255) / 255f, fg = ((first >> 8) & 255) / 255f, fb = (first & 255) / 255f;
                for (int i = 0; i < n; i++) { r[i] = fr * acc[i]; g[i] = fg * acc[i]; b[i] = fb * acc[i]; }
            }
            float cr = ((c >> 16) & 255) / 255f, cg = ((c >> 8) & 255) / 255f, cb = (c & 255) / 255f;
            // This shadow over the ones before it, moved by whole pixels as feOffset moves them.
            for (int y = Math.Max(0, oy); y < Math.Min(h, h + oy); y++)
            {
                int row = (y - oy) * w;
                for (int x = Math.Max(0, ox); x < Math.Min(w, w + ox); x++)
                {
                    float sa = a[row + x - ox] * ca;
                    if (sa <= 0) continue;
                    int o = y * w + x;
                    float keep = 1 - sa;
                    acc[o] = sa + acc[o] * keep;
                    if (r != null)
                    {
                        r[o] = cr * sa + r[o] * keep;
                        g![o] = cg * sa + g[o] * keep;
                        b![o] = cb * sa + b[o] * keep;
                    }
                }
            }
        }
        var alphaBytes = new byte[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            byte v = (byte)MathF.Round(Math.Clamp(acc[i], 0, 1) * 255);
            alphaBytes[i] = v;
            any |= v != 0;
        }
        if (!any) return null;
        var rgb = new byte[n * 3];
        if (oneColor)
        {
            for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = (byte)(first >> 16); rgb[i + 1] = (byte)(first >> 8); rgb[i + 2] = (byte)first; }
            return (rgb, alphaBytes);
        }
        byte lr = 0, lg = 0, lb = 0;
        for (int i = 0; i < n; i++)
        {
            float av = acc[i];
            if (alphaBytes[i] > 0)
            {
                lr = (byte)MathF.Round(Math.Clamp(r![i] / av, 0, 1) * 255);
                lg = (byte)MathF.Round(Math.Clamp(g![i] / av, 0, 1) * 255);
                lb = (byte)MathF.Round(Math.Clamp(b![i] / av, 0, 1) * 255);
            }
            // A transparent pixel keeps the colour before it, which compresses best.
            rgb[i * 3] = lr; rgb[i * 3 + 1] = lg; rgb[i * 3 + 2] = lb;
        }
        return (rgb, alphaBytes);
    }

    /// <summary>The average of the pixels from <paramref name="before"/> before to <paramref name="after"/> after each pixel.</summary>
    private static void Box(float[] src, float[] dst, int w, int h, int before, int after, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4;
        float inv = 1f / (before + after + 1);
        System.Threading.Tasks.Parallel.For(0, lines, line =>
        {
            int origin = horizontal ? line * w * 4 : line * 4;
            for (int c = 0; c < 4; c++)
            {
                double sum = 0;
                for (int k = 0; k <= Math.Min(after, len - 1); k++) sum += src[origin + k * step + c];
                for (int i = 0; i < len; i++)
                {
                    dst[origin + i * step + c] = (float)(sum * inv);
                    int add = i + after + 1, remove = i - before;
                    if (add < len) sum += src[origin + add * step + c];
                    if (remove >= 0) sum -= src[origin + remove * step + c];
                }
            }
        });
    }

    private static float[] Morphology(float[] src, int w, int h, float rx, float ry, bool dilate)
    {
        int ix = Math.Min((int)MathF.Round(rx), 200), iy = Math.Min((int)MathF.Round(ry), 200);
        if (ix <= 0 && iy <= 0) return src;
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        MorphAxis(src, tmp, w, h, Math.Max(ix, 0), dilate, horizontal: true);
        MorphAxis(tmp, dst, w, h, Math.Max(iy, 0), dilate, horizontal: false);
        return dst;
    }

    /// <summary>
    /// The minimum or maximum over a window of 2r + 1 pixels along each line, by van Herk and Gil-Werman's method: the
    /// line is cut into blocks of the window's size, and every window is the union of the end of one block and the start
    /// of the next, so a running extreme from each block's start and from its end gives every window in three steps a
    /// pixel, whatever the radius. Minimum and maximum are exact, so this matches searching each window.
    /// </summary>
    private static void MorphAxis(float[] src, float[] dst, int w, int h, int r, bool dilate, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h, step = horizontal ? 4 : w * 4;
        int k = 2 * r + 1;
        System.Threading.Tasks.Parallel.For(0, lines, () => (new float[len], new float[len]), (line, _, buffers) =>
        {
            var (fromStart, fromEnd) = buffers;
            int origin = horizontal ? line * w * 4 : line * 4;
            for (int c = 0; c < 4; c++)
            {
                int o = origin + c;
                for (int i = 0; i < len; i++)
                {
                    float s = src[o + i * step];
                    fromStart[i] = i % k == 0 ? s : dilate ? Math.Max(fromStart[i - 1], s) : Math.Min(fromStart[i - 1], s);
                }
                for (int i = len - 1; i >= 0; i--)
                {
                    float s = src[o + i * step];
                    fromEnd[i] = i == len - 1 || (i + 1) % k == 0 ? s : dilate ? Math.Max(fromEnd[i + 1], s) : Math.Min(fromEnd[i + 1], s);
                }
                for (int i = 0; i < len; i++)
                {
                    float v;
                    // Erosion at the edge sees transparent black beyond it; dilation only what is inside.
                    if (!dilate && (i - r < 0 || i + r > len - 1)) v = 0;
                    else
                    {
                        int a = Math.Max(0, i - r), b = Math.Min(len - 1, i + r);
                        // In one block the window starts the block or ends the line (it is cut short only at the ends).
                        if (a / k == b / k) v = a % k == 0 ? fromStart[b] : fromEnd[a];
                        else v = dilate ? Math.Max(fromEnd[a], fromStart[b]) : Math.Min(fromEnd[a], fromStart[b]);
                        v = dilate ? Math.Max(v, 0) : Math.Min(v, 1); // as a search that starts from transparent or opaque
                    }
                    dst[o + i * step] = v;
                }
            }
            return buffers;
        }, _ => { });
    }

    private static float[] Composite(float[] a, float[] b, Primitive p)
    {
        var dst = new float[a.Length];
        for (int i = 0; i < a.Length; i += 4)
        {
            float aa = a[i + 3], ba = b[i + 3];
            for (int c = 0; c < 4; c++)
            {
                float x = a[i + c], y = b[i + c];
                dst[i + c] = p.Operator switch
                {
                    "in" => x * ba,
                    "out" => x * (1 - ba),
                    "atop" => x * ba + y * (1 - aa),
                    "xor" => x * (1 - ba) + y * (1 - aa),
                    "lighter" => Math.Min(1, x + y),
                    "arithmetic" => Math.Clamp(p.K1 * x * y + p.K2 * x + p.K3 * y + p.K4, 0, 1),
                    _ => x + y * (1 - aa),
                };
            }
            // Colour can not exceed alpha in premultiplied pixels.
            float alpha = dst[i + 3];
            dst[i] = Math.Min(dst[i], alpha);
            dst[i + 1] = Math.Min(dst[i + 1], alpha);
            dst[i + 2] = Math.Min(dst[i + 2], alpha);
        }
        return dst;
    }

    /// <summary>Blends <paramref name="s"/> (in) over the backdrop <paramref name="b"/> (in2) with a separable blend mode.</summary>
    private static float[] Blend(float[] s, float[] b, string mode)
    {
        var dst = new float[s.Length];
        for (int i = 0; i < s.Length; i += 4)
        {
            float sa = s[i + 3], ba = b[i + 3];
            for (int c = 0; c < 3; c++)
            {
                float cs = s[i + c], cb = b[i + c];
                float us = sa > 0 ? cs / sa : 0, ub = ba > 0 ? cb / ba : 0;
                float mixed = mode switch
                {
                    "multiply" => us * ub,
                    "screen" => us + ub - us * ub,
                    "darken" => Math.Min(us, ub),
                    "lighten" => Math.Max(us, ub),
                    "overlay" => ub <= 0.5f ? 2 * us * ub : 1 - 2 * (1 - us) * (1 - ub),
                    "hard-light" => us <= 0.5f ? 2 * us * ub : 1 - 2 * (1 - us) * (1 - ub),
                    "color-dodge" => ub <= 0 ? 0 : us >= 1 ? 1 : Math.Min(1, ub / (1 - us)),
                    "color-burn" => ub >= 1 ? 1 : us <= 0 ? 0 : 1 - Math.Min(1, (1 - ub) / us),
                    "soft-light" => us <= 0.5f
                        ? ub - (1 - 2 * us) * ub * (1 - ub)
                        : ub + (2 * us - 1) * ((ub <= 0.25f ? ((16 * ub - 12) * ub + 4) * ub : MathF.Sqrt(ub)) - ub),
                    "difference" => Math.Abs(us - ub),
                    "exclusion" => us + ub - 2 * us * ub,
                    _ => us,
                };
                dst[i + c] = cs * (1 - ba) + cb * (1 - sa) + sa * ba * mixed;
            }
            dst[i + 3] = sa + ba - sa * ba;
        }
        return dst;
    }

    private static float[] ColorMatrix(float[] src, float[]? m)
    {
        if (m == null) return src;
        var dst = new float[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            float a = src[i + 3];
            float r = a > 0 ? src[i] / a : 0, g = a > 0 ? src[i + 1] / a : 0, b = a > 0 ? src[i + 2] / a : 0;
            float nr = Math.Clamp(m[0] * r + m[1] * g + m[2] * b + m[3] * a + m[4], 0, 1);
            float ng = Math.Clamp(m[5] * r + m[6] * g + m[7] * b + m[8] * a + m[9], 0, 1);
            float nb = Math.Clamp(m[10] * r + m[11] * g + m[12] * b + m[13] * a + m[14], 0, 1);
            float na = Math.Clamp(m[15] * r + m[16] * g + m[17] * b + m[18] * a + m[19], 0, 1);
            dst[i] = nr * na; dst[i + 1] = ng * na; dst[i + 2] = nb * na; dst[i + 3] = na;
        }
        return dst;
    }

    private static float[] ComponentTransfer(float[] src, Transfer?[] f)
    {
        var dst = new float[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            float a = src[i + 3];
            float r = a > 0 ? src[i] / a : 0, g = a > 0 ? src[i + 1] / a : 0, b = a > 0 ? src[i + 2] / a : 0;
            if (f[0] != null) r = Math.Clamp(f[0]!.Apply(r), 0, 1);
            if (f[1] != null) g = Math.Clamp(f[1]!.Apply(g), 0, 1);
            if (f[2] != null) b = Math.Clamp(f[2]!.Apply(b), 0, 1);
            if (f[3] != null) a = Math.Clamp(f[3]!.Apply(a), 0, 1);
            dst[i] = r * a; dst[i + 1] = g * a; dst[i + 2] = b * a; dst[i + 3] = a;
        }
        return dst;
    }
}
