using System.Runtime.CompilerServices;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>clip-path, mix-blend-mode, isolation and filter on HTML boxes.</summary>
internal sealed partial class LayoutEngine
{
    private Dictionary<string, Element>? _idIndex;

    /// <summary>Sets the clip, blend mode, isolation and filter of a box's layer, from the border box just laid out.</summary>
    private void ApplyEffects(Element e, LayerOp layer, float cbW)
    {
        var s = e.Style;
        float x = _lastBoxX, y = _lastBoxTop, w = _lastBoxW, h = _lastBoxH;
        layer.BlendMode = s.BlendMode;
        layer.Isolated = s.Isolate;
        if (s.ClipShape is { } shape)
        {
            var (bx, by, bw, bh, radii) = ReferenceBox(s, shape.Box, x, y, w, h, cbW);
            var (verbs, points) = shape.Resolve(bx, by, bw, bh, radii);
            layer.ClipVerbs = verbs;
            layer.ClipPoints = points;
            layer.ClipEvenOdd = shape.EvenOdd;
        }
        else if (s.SvgClipPath is { } url && FindById(e, url) is { Name: "clippath" } clip)
        {
            // A clipPath of an inline SVG: its user space is the border box, in CSS pixels.
            var path = SvgImage.HtmlClipPath(clip, x, y, w, h, out bool evenOdd);
            layer.ClipVerbs = path?.Verbs.ToArray() ?? Array.Empty<byte>();
            layer.ClipPoints = path?.Points.ToArray() ?? Array.Empty<float>();
            layer.ClipEvenOdd = evenOdd;
        }
        if (s.Filters is { } filters) ApplyFilter(layer, filters, s.Color);
    }

    /// <summary>The reference box of a clip-path shape, with its rounded corners.</summary>
    private static (float X, float Y, float W, float H, CornerRadii? Radii) ReferenceBox(ComputedStyle s, ShapeBox box, float x, float y, float w, float h, float cbW)
    {
        var radii = BoxPainter.Radii(s, w, h);
        switch (box)
        {
            case ShapeBox.PaddingBox:
            case ShapeBox.ContentBox:
            {
                float l = s.BorderLeft, t = s.BorderTop, r = s.BorderRight, b = s.BorderBottom;
                if (box == ShapeBox.ContentBox)
                {
                    l += s.PaddingLeft.Resolve(cbW);
                    t += s.PaddingTop.Resolve(cbW);
                    r += s.PaddingRight.Resolve(cbW);
                    b += s.PaddingBottom.Resolve(cbW);
                }
                return (x + l, y + t, Math.Max(0, w - l - r), Math.Max(0, h - t - b), BoxPainter.InnerRadii(radii, l, t, r, b));
            }
            case ShapeBox.MarginBox:
            {
                float l = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(cbW), r = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(cbW);
                float t = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(cbW), b = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(cbW);
                return (x - l, y - t, w + l + r, h + t + b, radii?.Outset(Math.Max(Math.Max(l, r), Math.Max(t, b))));
            }
            default:
                return (x, y, w, h, radii);
        }
    }

    /// <summary>The element of the document with the id of a "#id" reference, or null.</summary>
    private Element? FindById(Element e, string url)
    {
        if (url.Length < 2 || url[0] != '#') return null;
        if (_idIndex == null)
        {
            _idIndex = new Dictionary<string, Element>(StringComparer.Ordinal);
            var root = e;
            while (root.Parent != null) root = root.Parent;
            var stack = new Stack<Element>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                if (el.Id != null) _idIndex.TryAdd(el.Id, el);
                for (int i = el.Children.Count - 1; i >= 0; i--)
                    if (el.Children[i] is Element c) stack.Push(c);
            }
        }
        return _idIndex.TryGetValue(url.Substring(1), out var found) ? found : null;
    }

    /// <summary>
    /// Applies filter functions to a layer. Colour functions change the colours of what the layer draws, which keeps text
    /// as text; opacity() joins the layer's opacity. A filter with blur() runs on the content's pixels instead, and
    /// drop-shadow() draws shadows computed from the pixels under the content.
    /// </summary>
    private static void ApplyFilter(LayerOp layer, FilterFunction[] functions, uint currentColor)
    {
        foreach (var f in functions)
            if (f.Kind == FilterFunction.FilterKind.Opacity) layer.Opacity *= f.Amount;
        bool blur = functions.Any(f => f.Kind == FilterFunction.FilterKind.Blur && f.Amount > 0);
        if (blur)
        {
            layer.Raster = PixelFilter(functions, currentColor);
            layer.RasterReplaces = true;
            return;
        }
        var colors = new ColorFilter(functions);
        if (!colors.IsIdentity) Recolor(layer.Ops, colors);
        if (functions.Any(f => f.Kind == FilterFunction.FilterKind.DropShadow)) layer.Raster = ShadowFilter(functions, currentColor);
    }

    // The extent of a Gaussian blur that is visible: three deviations.
    private const float BlurReach = 3;

    /// <summary>The whole filter as SVG filter primitives on the content's pixels (sRGB, as browsers run filter functions).</summary>
    private static SvgFilter PixelFilter(FilterFunction[] functions, uint currentColor)
    {
        var f = new SvgFilter();
        float reachX = 0, reachY = 0;
        foreach (var fn in functions)
        {
            switch (fn.Kind)
            {
                case FilterFunction.FilterKind.Opacity:
                    break; // the layer's opacity
                case FilterFunction.FilterKind.Blur:
                    f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.Blur, Sx = fn.Amount, Sy = fn.Amount, Linear = false });
                    reachX += BlurReach * fn.Amount;
                    reachY += BlurReach * fn.Amount;
                    break;
                case FilterFunction.FilterKind.DropShadow:
                {
                    float sigma = fn.Blur; // unlike box-shadow, Chrome takes the blur value as the deviation
                    f.Primitives.Add(new SvgFilter.Primitive
                    {
                        Kind = SvgFilter.Kind.DropShadow, Sx = sigma, Sy = sigma, Dx = fn.X, Dy = fn.Y, Color = fn.Color ?? currentColor, Linear = false,
                    });
                    reachX += Math.Abs(fn.X) + BlurReach * sigma;
                    reachY += Math.Abs(fn.Y) + BlurReach * sigma;
                    break;
                }
                default:
                    f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.ColorMatrix, Matrix = fn.Matrix(), Linear = false });
                    break;
            }
        }
        SetReach(f, reachX, reachY);
        return f;
    }

    /// <summary>
    /// The shadows of a filter whose other functions are drawn as vectors: each drop-shadow() is the shadow of the content
    /// and the shadows before it, and the colour functions after it change its colour.
    /// </summary>
    private static SvgFilter ShadowFilter(FilterFunction[] functions, uint currentColor)
    {
        var f = new SvgFilter();
        void Add(SvgFilter.Kind kind, string? input, string result, Action<SvgFilter.Primitive>? set = null)
        {
            var p = new SvgFilter.Primitive { Kind = kind, In = input, Result = result, Linear = false };
            set?.Invoke(p);
            f.Primitives.Add(p);
        }
        Add(SvgFilter.Kind.Flood, null, "s", p => p.Color = 0);
        float reachX = 0, reachY = 0;
        foreach (var fn in functions)
        {
            if (fn.IsColor)
            {
                Add(SvgFilter.Kind.ColorMatrix, "s", "s", p => p.Matrix = fn.Matrix());
                continue;
            }
            if (fn.Kind != FilterFunction.FilterKind.DropShadow) continue;
            float sigma = fn.Blur; // unlike box-shadow, Chrome takes the blur value as the deviation
            uint color = fn.Color ?? currentColor;
            Add(SvgFilter.Kind.Merge, null, "t", p => p.Inputs = new List<string?> { "s", "SourceGraphic" });
            Add(SvgFilter.Kind.Blur, "t", "b", p => { p.Sx = sigma; p.Sy = sigma; });
            Add(SvgFilter.Kind.Offset, "b", "b", p => { p.Dx = fn.X; p.Dy = fn.Y; });
            Add(SvgFilter.Kind.Flood, null, "f", p => p.Color = color);
            Add(SvgFilter.Kind.Composite, "f", "sh", p => { p.In2 = "b"; p.Operator = "in"; });
            Add(SvgFilter.Kind.Merge, null, "s", p => p.Inputs = new List<string?> { "sh", "s" });
            reachX += Math.Abs(fn.X) + BlurReach * sigma;
            reachY += Math.Abs(fn.Y) + BlurReach * sigma;
        }
        Add(SvgFilter.Kind.Merge, null, "s", p => p.Inputs = new List<string?> { "s" });
        SetReach(f, reachX, reachY);
        return f;
    }

    // The filter region is the content's bounds grown by this much on each side; the writer finds the bounds.
    private static void SetReach(SvgFilter f, float x, float y)
    {
        f.X = -x;
        f.Y = -y;
        f.W = 2 * x;
        f.H = 2 * y;
    }

    // Filtered copies of images, by the filter: the same image under the same filter is filtered once.
    private static readonly ConditionalWeakTable<PdfImage, Dictionary<string, PdfImage>> FilteredImages = new();

    /// <summary>Changes the colours of the ops by the colour functions of a filter.</summary>
    internal static void Recolor(List<DrawOp> ops, ColorFilter filter)
    {
        foreach (var op in ops) Recolor(op, filter);
    }

    private static void Recolor(DrawOp op, ColorFilter f)
    {
        switch (op)
        {
            case RectOp r: r.Color = f.Apply(r.Color); break;
            case BorderRectOp br: br.Color = f.Apply(br.Color); break;
            case RoundRectOp rr: rr.Color = f.Apply(rr.Color); break;
            case LineOp l: l.Color = f.Apply(l.Color); break;
            case TextOp t: t.Color = f.Apply(t.Color); break;
            case CircleOp c: c.Color = f.Apply(c.Color); break;
            case ImageOp im: im.Image = FilterImage(im.Image, f); break;
            case SvgOp sv: sv.Image = sv.Image.Recolored(f.Apply); break;
            case BackgroundOp bg:
                if (bg.Image != null) bg.Image = FilterImage(bg.Image, f);
                if (bg.Svg != null) bg.Svg = bg.Svg.Recolored(f.Apply);
                if (bg.Gradient != null) bg.Gradient = bg.Gradient.Recolored(f.Apply);
                break;
            case LayerOp { MaskFill: { } fill } mask:
                // The ops are grey levels of a soft mask, not colours: only the colour painted through it changes.
                mask.MaskFill = f.Apply(fill);
                break;
            case LayerOp layer:
                // The shadows and floods of an inner filter take this filter's colours too.
                if (layer.Raster != null)
                    foreach (var p in layer.Raster.Primitives)
                        if (p.Kind is SvgFilter.Kind.Flood or SvgFilter.Kind.DropShadow) p.Color = f.Apply(p.Color);
                Recolor(layer.Ops, f);
                break;
            case FieldOp field:
                field.Color = f.Apply(field.Color);
                Recolor(field.Ops, f);
                break;
            case GroupOp g: Recolor(g.Ops, f); break;
        }
    }

    /// <summary>A copy of the image with the filter applied to its pixels; the image itself when they cannot be read.</summary>
    private static PdfImage FilterImage(PdfImage image, ColorFilter f)
    {
        var key = f.Key;
        Dictionary<string, PdfImage> byFilter;
        lock (FilteredImages)
        {
            byFilter = FilteredImages.GetOrCreateValue(image);
            if (byFilter.TryGetValue(key, out var done)) return done;
        }
        var decoded = image.DecodeRgb();
        PdfImage result = image;
        if (decoded is var (rgb, width, height))
        {
            var lookup = new Dictionary<int, int>();
            for (int i = 0; i + 2 < rgb.Length; i += 3)
            {
                int c = rgb[i] << 16 | rgb[i + 1] << 8 | rgb[i + 2];
                if (!lookup.TryGetValue(c, out int mapped))
                {
                    mapped = (int)(f.Apply(0xFF000000u | (uint)c) & 0xFFFFFF);
                    if (lookup.Count < 1 << 16) lookup[c] = mapped;
                }
                rgb[i] = (byte)(mapped >> 16);
                rgb[i + 1] = (byte)(mapped >> 8);
                rgb[i + 2] = (byte)mapped;
            }
            result = new PdfImage
            {
                Width = width, Height = height, ColorSpace = "/DeviceRGB", BitsPerComponent = 8,
                Data = PdfImage.Deflate(rgb), SMask = image.SMask?.Copy(),
            };
        }
        lock (FilteredImages) byFilter[key] = result;
        return result;
    }
}
