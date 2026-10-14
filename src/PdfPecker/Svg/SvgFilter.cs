using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Svg;

/// <summary>
/// An SVG filter: its region in root user space and its primitives, run on the pixels of the filtered content.
/// Supported: feGaussianBlur, feOffset, feDropShadow, feFlood, feMerge, feComposite, feBlend, feColorMatrix,
/// feComponentTransfer and feMorphology. Other primitives pass their input through unchanged. Primitive subregions
/// are not used: every primitive covers the whole filter region.
/// </summary>
internal sealed class SvgFilter
{
    public float X, Y, W, H; // the filter region, root user space
    public readonly List<Primitive> Primitives = new();

    internal enum Kind : byte { Blur, Offset, DropShadow, Flood, Merge, Composite, Blend, ColorMatrix, ComponentTransfer, Morphology, PassThrough }

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
    /// Reads the primitives of a filter element. Lengths are user units of <paramref name="ctm"/>, times the bounding box
    /// size (<paramref name="unitX"/>, <paramref name="unitY"/>) for primitiveUnits="objectBoundingBox". Returns null
    /// when the filter has no primitives (it then draws nothing).
    /// </summary>
    public static SvgFilter? Parse(Element filter, Matrix ctm, float unitX, float unitY)
    {
        var f = new SvgFilter();
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

    /// <summary>A w × h image of premultiplied RGBA floats, in linearRGB or sRGB.</summary>
    private sealed class Image
    {
        public readonly float[] P;
        public readonly bool Linear;
        public Image(float[] p, bool linear) { P = p; Linear = linear; }
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
        return new Image(dst, linear);
    }

    /// <summary>
    /// Runs the filter on the content's pixels (premultiplied sRGB, w × h covering the filter region), with
    /// <paramref name="scaleX"/> and <paramref name="scaleY"/> pixels per root unit. Returns premultiplied sRGB.
    /// </summary>
    public float[] Apply(float[] sourcePixels, int w, int h, float scaleX, float scaleY)
    {
        var source = new Image(sourcePixels, false);
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
                        alpha = new Image(a, false);
                    }
                    return alpha;
                case "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint":
                    return new Image(new float[sourcePixels.Length], false);
                default:
                    return results.TryGetValue(name, out var r) ? r : last;
            }
        }
        foreach (var p in Primitives)
        {
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
                _ => Input(p.In),
            };
            last = result;
            if (!string.IsNullOrEmpty(p.Result)) results[p.Result] = result;
        }
        return Convert(last, false).P;
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
        int d = (int)MathF.Floor(sigma * 3 * MathF.Sqrt(2 * MathF.PI) / 4 + 0.5f);
        d = Math.Clamp(d, 1, 2000);
        if (d % 2 == 1)
        {
            for (int pass = 0; pass < 3; pass++) { Box(a, tmp, w, h, d / 2, d / 2, horizontal); (a, tmp) = (tmp, a); }
        }
        else
        {
            // Two boxes of size d, off centre to either side, then one of size d + 1 centred on the pixel.
            Box(a, tmp, w, h, d / 2, d / 2 - 1, horizontal); (a, tmp) = (tmp, a);
            Box(a, tmp, w, h, d / 2 - 1, d / 2, horizontal); (a, tmp) = (tmp, a);
            Box(a, tmp, w, h, d / 2, d / 2, horizontal); (a, tmp) = (tmp, a);
        }
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
        MorphAxis(src, tmp, w, h, ix, dilate, horizontal: true);
        MorphAxis(tmp, dst, w, h, iy, dilate, horizontal: false);
        return dst;
    }

    private static void MorphAxis(float[] src, float[] dst, int w, int h, int r, bool dilate, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h, step = horizontal ? 4 : w * 4;
        for (int line = 0; line < lines; line++)
        {
            int origin = horizontal ? line * w * 4 : line * 4;
            for (int i = 0; i < len; i++)
                for (int c = 0; c < 4; c++)
                {
                    float v = dilate ? 0 : 1;
                    for (int k = Math.Max(0, i - r); k <= Math.Min(len - 1, i + r); k++)
                    {
                        float s = src[origin + k * step + c];
                        v = dilate ? Math.Max(v, s) : Math.Min(v, s);
                    }
                    // Erosion at the edge sees transparent black beyond it.
                    if (!dilate && (i - r < 0 || i + r > len - 1)) v = 0;
                    dst[origin + i * step + c] = v;
                }
        }
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
