using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Pdf;
using PdfPecker.Text;

namespace PdfPecker.Svg;

/// <summary>
/// One item of an SVG drawing, in the root element's user space: a filled and/or stroked path, a run of text, the
/// start or end of a clipped or masked group, filtered content, or an embedded image.
/// </summary>
internal sealed class SvgShape
{
    public const byte MoveTo = 0, LineTo = 1, CurveTo = 2, Close = 3;
    public const byte KindPath = 0, KindText = 1, KindClip = 2, KindEndClip = 3, KindMask = 4, KindEndMask = 5, KindFilter = 6, KindImage = 7;

    public byte Kind;
    public byte[] Verbs = Array.Empty<byte>();
    public float[] Points = Array.Empty<float>(); // 2 per MoveTo/LineTo, 6 per CurveTo
    public uint? Fill;
    public Gradient? FillGradient;
    public bool EvenOdd;                          // fill-rule, or clip-rule for a clip
    public uint? Stroke;
    public Gradient? StrokeGradient;
    public float StrokeWidth;
    public SvgLineCap LineCap;
    public SvgLineJoin LineJoin;
    public float MiterLimit = 4;
    public float MinX, MinY, MaxX, MaxY;          // bounds, for tiling repeating gradients

    // Text runs.
    public string? Text;
    public TextFont? Font;                        // one concrete font: a standard or an embedded one
    public bool Rtl;                              // drawn right to left (shaped and shown last glyph first)
    public float FontSize, CharSpacing;
    public Matrix TextMatrix;

    // Tiled fills and strokes (pattern elements).
    public SvgPattern? FillPattern, StrokePattern;

    // Masks: the mask's content (Points hold the mask region); filters: the content the filter applies to.
    public List<SvgShape>? Children;
    public bool MaskAlpha;                        // mask-type: alpha, else luminance
    public SvgFilter? Filter;

    // Images: a raster image drawn through ImageMatrix (its unit square onto root space), or an SVG drawing whose
    // shapes are in Children and whose root space ImageMatrix maps onto this one.
    public PdfPecker.Images.PdfImage? Image;
    public Matrix ImageMatrix;
    public byte ImageAlpha = 255;

    public bool HasFill => Fill != null || FillGradient != null || FillPattern != null;
    public bool HasStroke => Stroke != null || StrokeGradient != null || StrokePattern != null;
}

/// <summary>A pattern element's tile: shapes in tile space, repeated every Width × Height and mapped to the root.</summary>
internal sealed class SvgPattern
{
    public List<SvgShape> Shapes = new();
    public float Width, Height;
    public Matrix ToRoot;      // tile space to the root element's user space
    public byte Alpha = 255;   // fill-opacity or stroke-opacity of the painted shape
}

/// <summary>
/// The drawing work left for a document's inline SVGs. &lt;use&gt; can reference groups that reference groups, so a
/// small drawing can expand exponentially; past the budget, the rest of the drawing is left out.
/// </summary>
internal sealed class SvgBudget
{
    /// <summary>Elements visited plus path points emitted, shared by every SVG of a document.</summary>
    public long Remaining = 4_000_000;
}

/// <summary>
/// An inline &lt;svg&gt; turned into vector paths and text: path, rect, circle, ellipse, line, polyline, polygon, text,
/// tspan, g, use and nested svg, with transforms, fill/stroke styling from attributes and CSS, linear and radial
/// gradients, patterns, markers, clip paths, masks, filters (drawn as images) and embedded raster or SVG images.
/// </summary>
internal sealed partial class SvgImage
{
    public float? Width, Height; // intrinsic size in points, when given
    public bool HasViewBox;
    public float ViewBoxX, ViewBoxY, ViewBoxWidth, ViewBoxHeight;
    public string Align = "xmidymid";
    public bool Slice;
    public List<SvgShape> Shapes { get; private set; } = new();

    private readonly Dictionary<string, Element> _ids = new(StringComparer.Ordinal);
    private int _clipDepth, _maskDepth, _filterDepth, _patternDepth;
    private SvgBudget _budget = null!;
    private Func<string, object?>? _load; // the PdfImage or SvgImage at an image element's href, or null

    /// <summary>
    /// Builds the drawing of an svg element. <paramref name="load"/> resolves the href of image elements to a PdfImage
    /// or an SvgImage; without it, image elements draw nothing.
    /// </summary>
    public static SvgImage Build(Element svg, SvgBudget? budget = null, Func<string, object?>? load = null)
    {
        var img = new SvgImage { _budget = budget ?? new SvgBudget(), _load = load };
        img.Width = IntrinsicLength(svg.GetAttribute("width"));
        img.Height = IntrinsicLength(svg.GetAttribute("height"));
        var vb = ParseNumbers(svg.GetAttribute("viewbox"));
        if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0)
        {
            img.HasViewBox = true;
            img.ViewBoxX = vb[0];
            img.ViewBoxY = vb[1];
            img.ViewBoxWidth = vb[2];
            img.ViewBoxHeight = vb[3];
        }
        var par = svg.GetAttribute("preserveaspectratio");
        if (par != null)
        {
            var parts = par.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0] == "defer") parts = parts.Skip(1).ToArray();
            if (parts.Length > 0) img.Align = parts[0];
            if (parts.Length > 1) img.Slice = parts[1] == "slice";
        }
        img.CollectIds(svg);
        foreach (var child in svg.Children)
            if (child is Element e) img.Render(e, Matrix.Identity, 1, 0);
        return img;
    }

    /// <summary>The largest SVG file that is parsed (16 MB).</summary>
    private const int MaxFileLength = 16 << 20;

    /// <summary>True when the data looks like an SVG document: markup whose first element is an svg element.</summary>
    public static bool IsSvg(byte[] data)
    {
        int i = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (i >= data.Length || data[i] != '<') return false;
        // The svg element comes after any XML declaration, comments and doctype, usually within the first few kilobytes.
        int limit = Math.Min(data.Length - 4, i + 64 * 1024);
        for (int k = i; k < limit; k++)
            if (data[k] == '<' && (data[k + 1] | 0x20) == 's' && (data[k + 2] | 0x20) == 'v' && (data[k + 3] | 0x20) == 'g'
                && (k + 4 >= data.Length || data[k + 4] is (byte)' ' or (byte)'>' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'/'))
                return true;
        return false;
    }

    /// <summary>
    /// Parses an SVG file, styled only by its own &lt;style&gt; elements and presentation attributes (as browsers draw
    /// an SVG image: the page's CSS does not reach into it). Its image elements are resolved by
    /// <paramref name="load"/>, which browsers limit to data URIs. Returns null when it holds no svg element.
    /// </summary>
    public static SvgImage? FromFile(byte[] data, FontResolver? fonts, SvgBudget budget, Func<string, object?>? load = null)
    {
        if (data.Length > MaxFileLength || !IsSvg(data)) return null;
        var doc = HtmlParser.Parse(System.Text.Encoding.UTF8.GetString(data));
        var svg = doc.FindFirst("svg");
        if (svg == null) return null;
        var sheet = new Stylesheet();
        CollectStyles(svg, sheet);
        var root = new ComputedStyle { Display = Display.Block, Fonts = fonts };
        new StyleResolver(sheet).ResolveTree(doc, root);
        return Build(svg, budget, load);
    }

    private static void CollectStyles(Element parent, Stylesheet sheet)
    {
        foreach (var c in parent.Children)
        {
            if (c is not Element e) continue;
            if (e.Name == "style") CssParser.ParseStylesheet(e.InnerText(), sheet);
            else CollectStyles(e, sheet);
        }
    }

    private static float? IntrinsicLength(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("%", StringComparison.Ordinal)) return null;
        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float px)) return px > 0 ? px * 0.75f : null;
        return StyleResolver.TryParseLength(v, 12, 12, out var len) && !len.IsAuto && !len.IsPercent && len.Value > 0 ? len.Value : null;
    }

    private void CollectIds(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce) continue;
            var id = ce.GetAttribute("id");
            if (id != null && !_ids.ContainsKey(id)) _ids[id] = ce;
            CollectIds(ce);
        }
    }

    private Element? Reference(string? url)
        => url != null && url.StartsWith("#", StringComparison.Ordinal) && _ids.TryGetValue(url.Substring(1), out var el) ? el : null;

    // ------------------------------------------------------------------ tree walk

    private void Render(Element e, Matrix ctm, float opacity, int depth)
    {
        if (depth > 32 || --_budget.Remaining < 0) return;
        var s = e.Style;
        if (s == null || s.Display == Display.None) return;
        if (e.GetAttribute("transform") is { } tf) ctm = ParseTransform(tf).Then(ctm);
        float op = opacity * s.Opacity;

        // Effects nest in the order SVG applies them: the filter first, then the clip path, then the mask. Each is
        // applied to what the element drew. Nesting is bounded so that the PDF's save/restore depth stays small.
        var filter = _filterDepth < 4 && Reference(s.SvgFilter) is { Name: "filter" } f ? f : null;
        var clip = _clipDepth < 8 && Reference(s.SvgClipPath) is { Name: "clippath" } c ? c : null;
        var mask = _maskDepth < 4 && Reference(s.SvgMask) is { Name: "mask" } m ? m : null;
        int start = Shapes.Count;
        if (filter != null) _filterDepth++;
        if (clip != null) _clipDepth++;
        if (mask != null) _maskDepth++;
        try { RenderElement(e, s, ctm, op, depth); }
        finally
        {
            if (filter != null) _filterDepth--;
            if (clip != null) _clipDepth--;
            if (mask != null) _maskDepth--;
        }
        if (Shapes.Count == start) return;
        if (filter != null && !ApplyFilter(filter, ctm, start)) return;
        if (clip != null)
        {
            // Wrap what the element drew in the clip; an empty clip path hides everything.
            var marker = new SvgShape { Kind = SvgShape.KindClip };
            Shapes.Insert(start, marker);
            var path = ClipPath(clip, ctm, start + 1, out bool evenOdd);
            if (path == null)
            {
                Shapes.RemoveRange(start, Shapes.Count - start);
                return;
            }
            marker.Verbs = path.Verbs.ToArray();
            marker.Points = path.Points.ToArray();
            marker.EvenOdd = evenOdd;
            Shapes.Add(new SvgShape { Kind = SvgShape.KindEndClip });
        }
        if (mask != null) ApplyMask(mask, ctm, start, depth);
    }

    private void RenderElement(Element e, ComputedStyle s, Matrix ctm, float op, int depth)
    {
        switch (e.Name)
        {
            case "g":
            case "a":
            case "switch":
                foreach (var c in e.Children) if (c is Element ce) Render(ce, ctm, op, depth + 1);
                return;
            case "svg":
            {
                float x = Num(e, "x"), y = Num(e, "y");
                var inner = Matrix.Translate(x, y).Then(ctm);
                var vb = ParseNumbers(e.GetAttribute("viewbox"));
                float w = Num(e, "width", float.NaN), h = Num(e, "height", float.NaN);
                if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0 && !float.IsNaN(w) && !float.IsNaN(h))
                {
                    float sc = Math.Min(w / vb[2], h / vb[3]);
                    inner = Matrix.Translate(-vb[0], -vb[1]).Then(Matrix.Scale(sc, sc)).Then(inner);
                }
                foreach (var c in e.Children) if (c is Element ce) Render(ce, inner, op, depth + 1);
                return;
            }
            case "use":
            {
                var target = Reference(e.GetAttribute("href") ?? e.GetAttribute("xlink:href"));
                if (target == null) return;
                var m = Matrix.Translate(Num(e, "x"), Num(e, "y")).Then(ctm);
                if (target.Name == "symbol")
                {
                    foreach (var c in target.Children) if (c is Element ce) Render(ce, m, op, depth + 1);
                }
                else RenderReferenced(target, e, m, op, depth + 1);
                return;
            }
            case "text":
                RenderText(e, ctm, op);
                return;
            case "image":
                RenderImage(e, s, ctm, op);
                return;
            case "line":
            {
                var line = ShapePath(e);
                Emit(s, ctm, op, line, fillable: false);
                if (line != null) RenderMarkers(s, ctm, op, line, depth);
                return;
            }
            default:
                // Not drawn: defs, symbol (only through use), gradients, clipPath, mask, pattern, marker, filter, style...
                if (ShapePath(e) is { } path)
                {
                    Emit(s, ctm, op, path);
                    if (e.Name is "path" or "polyline" or "polygon") RenderMarkers(s, ctm, op, path, depth);
                }
                return;
        }
    }

    /// <summary>The outline of a basic shape or path element, in its own user space; null for anything else.</summary>
    private static PathBuilder? ShapePath(Element e)
    {
        switch (e.Name)
        {
            case "path":
                return PathParser.Parse(e.GetAttribute("d"));
            case "rect":
                return RectPath(e);
            case "circle":
            {
                float r = Num(e, "r");
                return r > 0 ? EllipsePath(Num(e, "cx"), Num(e, "cy"), r, r) : null;
            }
            case "ellipse":
            {
                float rx = Num(e, "rx", float.NaN), ry = Num(e, "ry", float.NaN);
                if (float.IsNaN(rx)) rx = ry;
                if (float.IsNaN(ry)) ry = rx;
                return rx > 0 && ry > 0 ? EllipsePath(Num(e, "cx"), Num(e, "cy"), rx, ry) : null;
            }
            case "line":
            {
                var p = new PathBuilder();
                p.MoveTo(Num(e, "x1"), Num(e, "y1"));
                p.LineTo(Num(e, "x2"), Num(e, "y2"));
                return p;
            }
            case "polyline":
            case "polygon":
            {
                var pts = ParseNumbers(e.GetAttribute("points"));
                if (pts.Count < 4) return null;
                var p = new PathBuilder();
                p.MoveTo(pts[0], pts[1]);
                for (int i = 2; i + 1 < pts.Count; i += 2) p.LineTo(pts[i], pts[i + 1]);
                if (e.Name == "polygon") p.Close();
                return p;
            }
            default:
                return null;
        }
    }

    /// <summary>Renders an element referenced by &lt;use&gt;; the use element's own presentation styles were already
    /// inherited by the target through the cascade only when the target is its descendant, so they are applied here.</summary>
    private void RenderReferenced(Element target, Element use, Matrix m, float opacity, int depth)
        => Render(target, m, opacity * (use.Style?.Opacity ?? 1), depth);

    private void Emit(ComputedStyle s, Matrix ctm, float opacity, PathBuilder? path, bool fillable = true)
    {
        if (path == null || path.Verbs.Count == 0 || !s.Visible) return;
        if ((_budget.Remaining -= path.Points.Count / 2) < 0) return;
        var pts = path.Points.ToArray();
        Bounds(pts, out float minX, out float minY, out float maxX, out float maxY);
        var shape = new SvgShape
        {
            Verbs = path.Verbs.ToArray(),
            Points = pts,
            EvenOdd = s.SvgEvenOdd,
            StrokeWidth = s.SvgStrokeWidth * ctm.ScaleFactor,
            LineCap = s.SvgLineCap,
            LineJoin = s.SvgLineJoin,
            MiterLimit = s.SvgMiterLimit,
        };
        if (fillable) shape.Fill = Paint(s.SvgFill, s.SvgFillUrl, s.SvgFillOpacity * opacity, minX, minY, maxX, maxY, ctm, out shape.FillGradient, out shape.FillPattern);
        if (s.SvgStrokeWidth > 0)
            shape.Stroke = Paint(s.SvgStroke, s.SvgStrokeUrl, s.SvgStrokeOpacity * opacity, minX, minY, maxX, maxY, ctm, out shape.StrokeGradient, out shape.StrokePattern);
        if (!shape.HasFill && !shape.HasStroke) return;
        for (int i = 0; i + 1 < pts.Length; i += 2) ctm.Apply(ref pts[i], ref pts[i + 1]);
        Bounds(pts, out shape.MinX, out shape.MinY, out shape.MaxX, out shape.MaxY);
        Shapes.Add(shape);
    }

    private static void Bounds(float[] pts, out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        for (int i = 0; i + 1 < pts.Length; i += 2)
        {
            minX = Math.Min(minX, pts[i]); maxX = Math.Max(maxX, pts[i]);
            minY = Math.Min(minY, pts[i + 1]); maxY = Math.Max(maxY, pts[i + 1]);
        }
    }

    /// <summary>
    /// A fill or stroke: a flat colour, a gradient (returned through <paramref name="gradient"/>) or a pattern (returned
    /// through <paramref name="pattern"/>).
    /// </summary>
    private uint? Paint(uint? color, string? url, float opacity, float minX, float minY, float maxX, float maxY, Matrix ctm,
        out Gradient? gradient, out SvgPattern? pattern)
    {
        gradient = null;
        pattern = null;
        uint? c = color;
        if (Reference(url) is { Name: "pattern" } tile)
        {
            float a = Math.Clamp(opacity, 0, 1);
            if (a <= 0.002f) return null;
            pattern = BuildPattern(tile, minX, minY, maxX, maxY, ctm);
            if (pattern != null) pattern.Alpha = (byte)Math.Round(a * 255);
            return null;
        }
        if (Reference(url) is { } server && server.Name is "lineargradient" or "radialgradient")
        {
            gradient = BuildGradient(server, opacity, minX, minY, maxX, maxY, ctm, out var solid);
            if (gradient != null) return null;
            c = solid; // a one-stop gradient is a plain colour (opacity included); one without stops paints nothing
            opacity = 1;
        }
        if (c == null) return null;
        uint v = c.Value;
        float alpha = (v >> 24) / 255f * Math.Clamp(opacity, 0, 1);
        if (alpha <= 0.002f) return null;
        return ((uint)Math.Round(alpha * 255) << 24) | (v & 0xFFFFFF);
    }

    // ------------------------------------------------------------------ gradients

    /// <summary>An attribute of a gradient, following href to the gradients it inherits from.</summary>
    private string? GradientAttr(Element g, string name)
    {
        for (int depth = 0; g != null && depth < 8; depth++)
        {
            if (g.GetAttribute(name) is { } v) return v;
            var parent = Reference(g.GetAttribute("href") ?? g.GetAttribute("xlink:href"));
            if (parent == null) break;
            g = parent;
        }
        return null;
    }

    private List<(float Offset, uint Color)> GradientStops(Element g, float opacity)
    {
        var stops = new List<(float, uint)>();
        for (int depth = 0; depth < 8; depth++)
        {
            foreach (var c in g.Children)
            {
                if (c is not Element stop || stop.Name != "stop") continue;
                string? color = stop.GetAttribute("stop-color");
                string? alphaText = stop.GetAttribute("stop-opacity");
                string? offsetText = stop.GetAttribute("offset");
                var style = stop.GetAttribute("style");
                if (style != null)
                {
                    foreach (var decl in style.Split(';'))
                    {
                        int colon = decl.IndexOf(':');
                        if (colon <= 0) continue;
                        var name = decl.Substring(0, colon).Trim().ToLowerInvariant();
                        if (name == "stop-color") color = decl.Substring(colon + 1).Trim();
                        else if (name == "stop-opacity") alphaText = decl.Substring(colon + 1).Trim();
                    }
                }
                uint current = stop.Style?.Color ?? CssColor.Black;
                if (!CssColor.TryParse(color ?? "black", current, out uint col)) col = CssColor.Black;
                float alpha = (col >> 24) / 255f * Math.Clamp(opacity, 0, 1);
                if (alphaText != null && float.TryParse(alphaText, NumberStyles.Float, CultureInfo.InvariantCulture, out float o)) alpha *= Math.Clamp(o, 0, 1);
                float offset = 0;
                if (offsetText != null)
                {
                    offsetText = offsetText.Trim();
                    bool percent = offsetText.EndsWith("%", StringComparison.Ordinal);
                    if (float.TryParse(percent ? offsetText[..^1] : offsetText, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        offset = percent ? f / 100 : f;
                }
                stops.Add((offset, ((uint)Math.Round(alpha * 255) << 24) | (col & 0xFFFFFF)));
            }
            if (stops.Count > 0) break;
            var parent = Reference(g.GetAttribute("href") ?? g.GetAttribute("xlink:href"));
            if (parent == null) break;
            g = parent;
        }
        return stops;
    }

    /// <summary>
    /// Builds a gradient for a shape whose bounds (in its own user space) are given. Returns null with a flat colour in
    /// <paramref name="solid"/> when the gradient has one stop, or null with no colour when it cannot be painted.
    /// </summary>
    private Gradient? BuildGradient(Element g, float opacity, float minX, float minY, float maxX, float maxY, Matrix ctm, out uint? solid)
    {
        solid = null;
        var stops = GradientStops(g, opacity);
        if (stops.Count == 0) return null;
        if (stops.Count == 1)
        {
            solid = stops[0].Color;
            return null;
        }
        bool bbox = !string.Equals(GradientAttr(g, "gradientunits")?.Trim(), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
        Matrix units = Matrix.Identity;
        if (bbox)
        {
            float w = maxX - minX, h = maxY - minY;
            if (!(w > 0) || !(h > 0)) return null;
            units = Matrix.Scale(w, h).Then(Matrix.Translate(minX, minY));
        }
        float vw = HasViewBox ? ViewBoxWidth : (Width ?? 300 * 0.75f) / 0.75f;
        float vh = HasViewBox ? ViewBoxHeight : (Height ?? 150 * 0.75f) / 0.75f;
        float Coord(string name, string fallback, float viewport)
        {
            var v = (GradientAttr(g, name) ?? fallback).Trim();
            bool percent = v.EndsWith("%", StringComparison.Ordinal);
            if (percent) v = v[..^1];
            if (!bbox && v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v[..^2];
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return 0;
            return percent ? f / 100 * (bbox ? 1 : viewport) : f;
        }
        var grad = new Gradient();
        (grad.Offsets, grad.Colors) = Gradient.NormalizeStops(stops, 0xFFFFFFFF);
        if (g.Name == "lineargradient")
        {
            grad.X0 = Coord("x1", "0%", vw);
            grad.Y0 = Coord("y1", "0%", vh);
            grad.X1 = Coord("x2", "100%", vw);
            grad.Y1 = Coord("y2", "0%", vh);
        }
        else
        {
            float diag = MathF.Sqrt((vw * vw + vh * vh) / 2);
            grad.Radial = true;
            grad.X1 = Coord("cx", "50%", vw);
            grad.Y1 = Coord("cy", "50%", vh);
            grad.R1 = Coord("r", "50%", diag);
            if (!(grad.R1 > 0)) { solid = stops[^1].Color; return null; }
            grad.X0 = GradientAttr(g, "fx") != null ? Coord("fx", "50%", vw) : grad.X1;
            grad.Y0 = GradientAttr(g, "fy") != null ? Coord("fy", "50%", vh) : grad.Y1;
            grad.R0 = Math.Clamp(Coord("fr", "0%", diag), 0, grad.R1);
        }
        grad.Spread = GradientAttr(g, "spreadmethod")?.Trim().ToLowerInvariant() switch
        {
            "reflect" => GradientSpread.Reflect,
            "repeat" => GradientSpread.Repeat,
            _ => GradientSpread.Pad,
        };
        var transform = GradientAttr(g, "gradienttransform") is { } gt ? ParseTransform(gt) : Matrix.Identity;
        grad.Transform = transform.Then(units).Then(ctm);
        return grad;
    }

    // ------------------------------------------------------------------ clip paths

    /// <summary>
    /// The union of a clipPath's shapes in root user space. Shapes drawn from <paramref name="firstShape"/> on give the
    /// bounding box for clipPathUnits="objectBoundingBox". Null when the clip path contains nothing.
    /// </summary>
    private PathBuilder? ClipPath(Element clip, Matrix ctm, int firstShape, out bool evenOdd)
    {
        bool rule = false;
        evenOdd = false;
        var m = clip.GetAttribute("transform") is { } tf ? ParseTransform(tf) : Matrix.Identity;
        if (clip.GetAttribute("clippathunits") is { } u && u.Trim().Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase))
        {
            // The bounding box is taken in root space and mapped back, which is exact without rotation or skew.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = firstShape; i < Shapes.Count; i++)
            {
                var sh = Shapes[i];
                if (sh.Kind is SvgShape.KindPath or SvgShape.KindText)
                {
                    minX = Math.Min(minX, sh.MinX); minY = Math.Min(minY, sh.MinY);
                    maxX = Math.Max(maxX, sh.MaxX); maxY = Math.Max(maxY, sh.MaxY);
                }
            }
            if (!(maxX > minX) || !(maxY > minY) || !ctm.TryInvert(out var inv)) return null;
            float x0 = minX, y0 = minY, x1 = maxX, y1 = maxY;
            inv.Apply(ref x0, ref y0);
            inv.Apply(ref x1, ref y1);
            m = m.Then(Matrix.Scale(x1 - x0, y1 - y0)).Then(Matrix.Translate(x0, y0));
        }
        m = m.Then(ctm);
        var result = new PathBuilder();
        bool first = true;
        void Add(Element child, Matrix t, int depth)
        {
            if (depth > 8 || child.Style == null || child.Style.Display == Display.None || !child.Style.Visible) return;
            if (child.GetAttribute("transform") is { } ctf) t = ParseTransform(ctf).Then(t);
            if (child.Name == "use")
            {
                var target = Reference(child.GetAttribute("href") ?? child.GetAttribute("xlink:href"));
                if (target != null) Add(target, Matrix.Translate(Num(child, "x"), Num(child, "y")).Then(t), depth + 1);
                return;
            }
            var path = ShapePath(child);
            if (path == null || child.Name == "line") return;
            if (first) rule = child.Style.SvgClipEvenOdd;
            first = false;
            var pts = path.Points;
            for (int i = 0; i + 1 < pts.Count; i += 2)
            {
                float x = pts[i], y = pts[i + 1];
                t.Apply(ref x, ref y);
                pts[i] = x; pts[i + 1] = y;
            }
            result.Verbs.AddRange(path.Verbs);
            result.Points.AddRange(pts);
        }
        foreach (var c in clip.Children) if (c is Element ce) Add(ce, m, 0);
        evenOdd = rule;
        return result.Verbs.Count > 0 ? result : null;
    }

    // ------------------------------------------------------------------ text

    private readonly struct TextRun
    {
        public readonly string Text;
        public readonly ComputedStyle Style;
        public readonly float X, Y, Width;
        public TextRun(string text, ComputedStyle style, float x, float y, float width) { Text = text; Style = style; X = x; Y = y; Width = width; }
    }

    /// <summary>
    /// Lays out a &lt;text&gt; element and its tspans: x/y start a new text chunk (aligned by text-anchor), dx/dy shift
    /// the pen, white space collapses as with xml:space="default". Only the first value of each position list is used.
    /// </summary>
    private void RenderText(Element text, Matrix ctm, float opacity)
    {
        var runs = new List<TextRun>();
        var chunkStarts = new List<int>();
        float penX = 0, penY = 0;
        bool lastSpace = true;

        void Walk(Element el, int depth)
        {
            if (depth > 16 || el.Style == null || el.Style.Display == Display.None) return;
            var xs = ParseNumbers(el.GetAttribute("x"));
            var ys = ParseNumbers(el.GetAttribute("y"));
            if (xs.Count > 0 || ys.Count > 0 || depth == 0) chunkStarts.Add(runs.Count);
            if (xs.Count > 0) penX = xs[0];
            if (ys.Count > 0) penY = ys[0];
            var dxs = ParseNumbers(el.GetAttribute("dx"));
            var dys = ParseNumbers(el.GetAttribute("dy"));
            if (dxs.Count > 0) penX += dxs[0];
            if (dys.Count > 0) penY += dys[0];
            foreach (var child in el.Children)
            {
                if (child is Element ce)
                {
                    if (ce.Name is "tspan" or "a" or "textpath") Walk(ce, depth + 1);
                    continue;
                }
                if (child is not TextNode tn) continue;
                var sb = new System.Text.StringBuilder(tn.Text.Length);
                foreach (char ch in tn.Text)
                {
                    if (ch is '\n' or '\r') continue;
                    char c = ch == '\t' ? ' ' : ch;
                    if (c == ' ' && lastSpace) continue;
                    sb.Append(c);
                    lastSpace = c == ' ';
                }
                if (sb.Length == 0) continue;
                var str = sb.ToString();
                var st = el.Style;
                float size = st.FontSize / 0.75f;
                float w = st.Font.Measure(str, size) + st.LetterSpacing / 0.75f * str.Length;
                runs.Add(new TextRun(str, st, penX, penY, w));
                penX += w;
            }
        }
        Walk(text, 0);
        if (runs.Count == 0) return;
        // Trailing white space of the element is dropped.
        var last = runs[^1];
        if (last.Text.EndsWith(" ", StringComparison.Ordinal))
        {
            var trimmed = last.Text.TrimEnd(' ');
            float size = last.Style.FontSize / 0.75f;
            runs[^1] = new TextRun(trimmed, last.Style, last.X, last.Y,
                last.Style.Font.Measure(trimmed, size) + last.Style.LetterSpacing / 0.75f * trimmed.Length);
        }

        chunkStarts.Add(runs.Count);
        for (int k = 0; k + 1 < chunkStarts.Count; k++)
        {
            int from = chunkStarts[k], to = chunkStarts[k + 1];
            if (from >= to) continue;
            float left = float.MaxValue, right = float.MinValue;
            for (int i = from; i < to; i++) { left = Math.Min(left, runs[i].X); right = Math.Max(right, runs[i].X + runs[i].Width); }
            var anchor = runs[from].Style.SvgTextAnchor;
            if (text.Style!.Rtl || NeedsBidi(runs, from, to))
            {
                EmitBidiChunk(runs, from, to, text.Style.Rtl, anchor, ctm, opacity);
                continue;
            }
            float shift = anchor == 1 ? -(right - left) / 2 : anchor == 2 ? -(right - left) : 0;
            for (int i = from; i < to; i++) EmitText(runs[i], shift, ctm, opacity, rtl: false);
        }
    }

    private static bool NeedsBidi(List<TextRun> runs, int from, int to)
    {
        for (int i = from; i < to; i++)
            foreach (char c in runs[i].Text)
                if (c >= 0x0590 && UnicodeTables.NeedsBidi(c)) return true;
        return false;
    }

    /// <summary>
    /// Lays out a text chunk with right-to-left text: its characters are put in visual order by the Unicode
    /// Bidirectional Algorithm, with the direction of the &lt;text&gt; element, and drawn one after another from the
    /// chunk's start. Right to left, text-anchor: start puts the chunk's right end at its x, as in browsers.
    /// </summary>
    private void EmitBidiChunk(List<TextRun> runs, int from, int to, bool rtl, int anchor, Matrix ctm, float opacity)
    {
        var all = new System.Text.StringBuilder();
        for (int i = from; i < to; i++) all.Append(runs[i].Text);
        string s = all.ToString();
        if (s.Length == 0) return;
        var types = new BidiClass[s.Length];
        var cps = new int[s.Length];
        for (int k = 0; k < s.Length; k++)
        {
            int cp = s[k];
            if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1])) cp = char.ConvertToUtf32(s[k], s[k + 1]);
            else if (char.IsLowSurrogate(s[k]) && k > 0 && char.IsHighSurrogate(s[k - 1])) cp = char.ConvertToUtf32(s[k - 1], s[k]);
            types[k] = UnicodeTables.Bidi(cp);
            cps[k] = char.IsLowSurrogate(s[k]) ? 0 : cp;
        }
        int para = rtl ? 1 : 0;
        var levels = Bidi.ResolveLevels(types, cps, para);
        Bidi.ResetWhitespace(types, levels, para);

        // Pieces of one run and one level, measured in their own style.
        var pieces = new List<(int Run, int Start, int Length, float Width)>();
        var pieceLevels = new List<byte>();
        int offset = 0;
        for (int i = from; i < to; i++)
        {
            var run = runs[i];
            for (int k = 0; k < run.Text.Length;)
            {
                int end = k + 1;
                while (end < run.Text.Length && levels[offset + end] == levels[offset + k]) end++;
                var st = run.Style;
                float size = st.FontSize / 0.75f;
                float w = st.Font.MeasureUnits(run.Text, k, end - k) * size / 1000f + st.LetterSpacing / 0.75f * (end - k);
                pieces.Add((i, k, end - k, w));
                pieceLevels.Add(levels[offset + k]);
                k = end;
            }
            offset += run.Text.Length;
        }
        float total = 0;
        foreach (var p in pieces) total += p.Width;
        float x = runs[from].X;
        // text-anchor start and end are the start and end of the text in its direction.
        x += anchor == 1 ? -total / 2 : (anchor == 2) != rtl ? -total : 0;
        foreach (int k in Bidi.VisualOrder(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pieceLevels)))
        {
            var p = pieces[k];
            var run = runs[p.Run];
            var piece = new TextRun(run.Text.Substring(p.Start, p.Length), run.Style, x, run.Y, p.Width);
            EmitText(piece, 0, ctm, opacity, (pieceLevels[k] & 1) != 0);
            x += p.Width;
        }
    }

    /// <summary>Adds a run of text as one shape per font that draws part of it; right to left, the last part is leftmost.</summary>
    private void EmitText(TextRun run, float shift, Matrix ctm, float opacity, bool rtl)
    {
        var s = run.Style;
        if (!s.Visible || run.Text.Length == 0) return;
        var fontRuns = new List<FontRun>();
        s.Font.Split(run.Text, fontRuns);
        if (fontRuns.Count == 1)
        {
            EmitTextShape(run, fontRuns[0].Font, shift, ctm, opacity, rtl);
            return;
        }
        float size = s.FontSize / 0.75f, x = run.X;
        for (int r = 0; r < fontRuns.Count; r++)
        {
            var fr = fontRuns[rtl ? fontRuns.Count - 1 - r : r];
            float w = fr.Font.MeasureUnits(run.Text, fr.Start, fr.Length) * size / 1000f + s.LetterSpacing / 0.75f * fr.Length;
            EmitTextShape(new TextRun(run.Text.Substring(fr.Start, fr.Length), s, x, run.Y, w), fr.Font, shift, ctm, opacity, rtl);
            x += w;
        }
    }

    private void EmitTextShape(TextRun run, TextFont font, float shift, Matrix ctm, float opacity, bool rtl)
    {
        var s = run.Style;
        float size = s.FontSize / 0.75f;
        float x = run.X + shift;
        float y = run.Y + s.SvgBaseline switch
        {
            1 => 0.35f * size,  // middle, central
            2 => 0.8f * size,   // hanging, text-before-edge
            3 => -0.2f * size,  // text-after-edge, ideographic
            _ => 0,
        };
        float minX = x, minY = y - 0.8f * size, maxX = x + run.Width, maxY = y + 0.2f * size;
        var shape = new SvgShape
        {
            Kind = SvgShape.KindText,
            Text = run.Text,
            Font = font,
            Rtl = rtl,
            FontSize = size,
            CharSpacing = s.LetterSpacing / 0.75f,
            TextMatrix = new Matrix(1, 0, 0, -1, x, y).Then(ctm),
            StrokeWidth = s.SvgStrokeWidth * ctm.ScaleFactor,
            LineJoin = s.SvgLineJoin,
            LineCap = s.SvgLineCap,
            MiterLimit = s.SvgMiterLimit,
        };
        shape.Fill = Paint(s.SvgFill, s.SvgFillUrl, s.SvgFillOpacity * opacity, minX, minY, maxX, maxY, ctm, out shape.FillGradient, out shape.FillPattern);
        if (s.SvgStrokeWidth > 0)
            shape.Stroke = Paint(s.SvgStroke, s.SvgStrokeUrl, s.SvgStrokeOpacity * opacity, minX, minY, maxX, maxY, ctm, out shape.StrokeGradient, out shape.StrokePattern);
        if (!shape.HasFill && !shape.HasStroke) return;
        var corners = new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY };
        for (int i = 0; i < 8; i += 2) ctm.Apply(ref corners[i], ref corners[i + 1]);
        Bounds(corners, out shape.MinX, out shape.MinY, out shape.MaxX, out shape.MaxY);
        Shapes.Add(shape);
    }

    // ------------------------------------------------------------------ basic shapes

    private static PathBuilder? RectPath(Element e)
    {
        float x = Num(e, "x"), y = Num(e, "y"), w = Num(e, "width"), h = Num(e, "height");
        if (w <= 0 || h <= 0) return null;
        float rx = Num(e, "rx", float.NaN), ry = Num(e, "ry", float.NaN);
        if (float.IsNaN(rx)) rx = float.IsNaN(ry) ? 0 : ry;
        if (float.IsNaN(ry)) ry = rx;
        rx = Math.Clamp(rx, 0, w / 2);
        ry = Math.Clamp(ry, 0, h / 2);
        var p = new PathBuilder();
        if (rx <= 0 || ry <= 0)
        {
            p.MoveTo(x, y);
            p.LineTo(x + w, y);
            p.LineTo(x + w, y + h);
            p.LineTo(x, y + h);
            p.Close();
            return p;
        }
        const float k = 0.5522848f;
        p.MoveTo(x + rx, y);
        p.LineTo(x + w - rx, y);
        p.CurveTo(x + w - rx + rx * k, y, x + w, y + ry - ry * k, x + w, y + ry);
        p.LineTo(x + w, y + h - ry);
        p.CurveTo(x + w, y + h - ry + ry * k, x + w - rx + rx * k, y + h, x + w - rx, y + h);
        p.LineTo(x + rx, y + h);
        p.CurveTo(x + rx - rx * k, y + h, x, y + h - ry + ry * k, x, y + h - ry);
        p.LineTo(x, y + ry);
        p.CurveTo(x, y + ry - ry * k, x + rx - rx * k, y, x + rx, y);
        p.Close();
        return p;
    }

    private static PathBuilder EllipsePath(float cx, float cy, float rx, float ry)
    {
        const float k = 0.5522848f;
        var p = new PathBuilder();
        p.MoveTo(cx + rx, cy);
        p.CurveTo(cx + rx, cy + ry * k, cx + rx * k, cy + ry, cx, cy + ry);
        p.CurveTo(cx - rx * k, cy + ry, cx - rx, cy + ry * k, cx - rx, cy);
        p.CurveTo(cx - rx, cy - ry * k, cx - rx * k, cy - ry, cx, cy - ry);
        p.CurveTo(cx + rx * k, cy - ry, cx + rx, cy - ry * k, cx + rx, cy);
        p.Close();
        return p;
    }

    // ------------------------------------------------------------------ attribute helpers

    private static float Num(Element e, string name, float fallback = 0)
    {
        var v = e.GetAttribute(name);
        if (v == null) return fallback;
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 2);
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
    }

    internal static List<float> ParseNumbers(string? s)
    {
        var list = new List<float>();
        if (s == null) return list;
        var lexer = new NumberLexer(s, 0);
        while (lexer.TryNumber(out float f)) list.Add(f);
        return list;
    }

    internal static Matrix ParseTransform(string s)
    {
        var m = Matrix.Identity;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            int nameStart = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            if (i == nameStart) break;
            string name = s.Substring(nameStart, i - nameStart).ToLowerInvariant();
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length || s[i] != '(') break;
            int close = s.IndexOf(')', i);
            if (close < 0) break;
            var a = ParseNumbers(s.Substring(i + 1, close - i - 1));
            i = close + 1;
            Matrix t;
            switch (name)
            {
                case "matrix" when a.Count == 6: t = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case "translate" when a.Count >= 1: t = Matrix.Translate(a[0], a.Count > 1 ? a[1] : 0); break;
                case "scale" when a.Count >= 1: t = Matrix.Scale(a[0], a.Count > 1 ? a[1] : a[0]); break;
                case "rotate" when a.Count >= 1:
                    t = Matrix.Rotate(a[0]);
                    if (a.Count >= 3) t = Matrix.Translate(-a[1], -a[2]).Then(t).Then(Matrix.Translate(a[1], a[2]));
                    break;
                case "skewx" when a.Count >= 1: t = new Matrix(1, 0, (float)Math.Tan(a[0] * Math.PI / 180), 1, 0, 0); break;
                case "skewy" when a.Count >= 1: t = new Matrix(1, (float)Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0); break;
                default: continue;
            }
            // The rightmost transform applies first.
            m = t.Then(m);
        }
        return m;
    }
}

/// <summary>An affine transform [a c e; b d f; 0 0 1] mapping (x, y) to (a x + c y + e, b x + d y + f).</summary>
internal readonly struct Matrix
{
    public readonly float A, B, C, D, E, F;
    public Matrix(float a, float b, float c, float d, float e, float f) { A = a; B = b; C = c; D = d; E = e; F = f; }

    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
    public static Matrix Translate(float x, float y) => new(1, 0, 0, 1, x, y);
    public static Matrix Scale(float x, float y) => new(x, 0, 0, y, 0, 0);

    public static Matrix Rotate(float degrees)
    {
        double r = degrees * Math.PI / 180;
        float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
        return new Matrix(c, s, -s, c, 0, 0);
    }

    /// <summary>This transform followed by <paramref name="o"/>.</summary>
    public Matrix Then(Matrix o)
        => new(o.A * A + o.C * B, o.B * A + o.D * B, o.A * C + o.C * D, o.B * C + o.D * D, o.A * E + o.C * F + o.E, o.B * E + o.D * F + o.F);

    public void Apply(ref float x, ref float y)
    {
        float nx = A * x + C * y + E;
        y = B * x + D * y + F;
        x = nx;
    }

    public bool TryInvert(out Matrix inverse)
    {
        float det = A * D - B * C;
        if (Math.Abs(det) < 1e-12f) { inverse = Identity; return false; }
        inverse = new Matrix(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
        return true;
    }

    /// <summary>Average scale, used for stroke widths.</summary>
    public float ScaleFactor => (float)Math.Sqrt(Math.Abs(A * D - B * C));
}

internal sealed class PathBuilder
{
    public readonly List<byte> Verbs = new();
    public readonly List<float> Points = new();
    public List<int>? Ends; // for path data: the verb count after each command (an arc is several curves); else every verb ends one

    /// <summary>Marks the end of a path command, for markers.</summary>
    public void EndCommand()
    {
        Ends ??= new List<int>();
        if (Verbs.Count > 0 && (Ends.Count == 0 || Ends[^1] != Verbs.Count)) Ends.Add(Verbs.Count);
    }

    public void MoveTo(float x, float y) { Verbs.Add(SvgShape.MoveTo); Points.Add(x); Points.Add(y); }
    public void LineTo(float x, float y) { Verbs.Add(SvgShape.LineTo); Points.Add(x); Points.Add(y); }

    public void CurveTo(float x1, float y1, float x2, float y2, float x, float y)
    {
        Verbs.Add(SvgShape.CurveTo);
        Points.Add(x1); Points.Add(y1); Points.Add(x2); Points.Add(y2); Points.Add(x); Points.Add(y);
    }

    public void Close() => Verbs.Add(SvgShape.Close);
}

/// <summary>Reads SVG numbers: "1.5.5" is two numbers, "-1-2" is two, exponents are allowed, separators are optional.</summary>
internal struct NumberLexer
{
    private readonly string _s;
    public int Pos;

    public NumberLexer(string s, int pos) { _s = s; Pos = pos; }

    public void SkipSeparators()
    {
        while (Pos < _s.Length && (char.IsWhiteSpace(_s[Pos]) || _s[Pos] == ',')) Pos++;
    }

    public bool AtNumber()
    {
        SkipSeparators();
        if (Pos >= _s.Length) return false;
        char c = _s[Pos];
        return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
    }

    public bool TryNumber(out float value)
    {
        value = 0;
        if (!AtNumber()) return false;
        int start = Pos;
        if (_s[Pos] == '+' || _s[Pos] == '-') Pos++;
        bool dot = false, digits = false;
        while (Pos < _s.Length)
        {
            char c = _s[Pos];
            if (char.IsDigit(c)) { digits = true; Pos++; }
            else if (c == '.' && !dot) { dot = true; Pos++; }
            else break;
        }
        if (digits && Pos < _s.Length && (_s[Pos] == 'e' || _s[Pos] == 'E'))
        {
            int save = Pos;
            Pos++;
            if (Pos < _s.Length && (_s[Pos] == '+' || _s[Pos] == '-')) Pos++;
            if (Pos < _s.Length && char.IsDigit(_s[Pos])) { while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++; }
            else Pos = save;
        }
        if (!digits) { Pos = start + 1; return false; }
        return Parse(_s.AsSpan(start, Pos - start), out value);
    }

    private static readonly double[] Pow10 = { 1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15 };

    /// <summary>Fast path for plain decimals (by far the most common in path data); anything else goes to float.TryParse.</summary>
    private static bool Parse(ReadOnlySpan<char> text, out float value)
    {
        int i = 0;
        bool negative = false;
        if (text[0] == '-' || text[0] == '+') { negative = text[0] == '-'; i++; }
        long mantissa = 0;
        int digits = 0, fraction = -1;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.') { fraction = 0; continue; }
            if (c < '0' || c > '9' || digits >= 15)
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            mantissa = mantissa * 10 + (c - '0');
            if (mantissa != 0) digits++;
            if (fraction >= 0) fraction++;
        }
        double v = fraction > 0 ? mantissa / Pow10[Math.Min(fraction, 15)] : mantissa;
        value = (float)(negative ? -v : v);
        return true;
    }

    /// <summary>An arc flag: a single 0 or 1, which may be followed directly by the next number.</summary>
    public bool TryFlag(out bool flag)
    {
        flag = false;
        SkipSeparators();
        if (Pos >= _s.Length || _s[Pos] is not ('0' or '1')) return false;
        flag = _s[Pos] == '1';
        Pos++;
        return true;
    }
}

/// <summary>Parses SVG path data into absolute move/line/cubic commands. Quadratic curves and arcs become cubics.</summary>
internal static class PathParser
{
    public static PathBuilder? Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        var p = new PathBuilder();
        var lx = new NumberLexer(d!, 0);
        float cx = 0, cy = 0, sx = 0, sy = 0;  // current point, subpath start
        float lcx = 0, lcy = 0;                // last control point (for S/T)
        char prev = ' ';
        char cmd = ' ';
        bool hasCurrent = false;
        while (true)
        {
            lx.SkipSeparators();
            if (lx.Pos >= d!.Length) break;
            char c = d[lx.Pos];
            if (char.IsLetter(c))
            {
                cmd = c;
                lx.Pos++;
            }
            else if (cmd == ' ' || !lx.AtNumber()) break;
            else if (cmd == 'M') cmd = 'L'; // extra coordinate pairs after a moveto are linetos
            else if (cmd == 'm') cmd = 'l';

            bool rel = char.IsLower(cmd);
            float ox = rel ? cx : 0, oy = rel ? cy : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    if (!lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    cx = sx = ox + x; cy = sy = oy + y;
                    p.MoveTo(cx, cy);
                    hasCurrent = true;
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'Z':
                    if (hasCurrent) p.Close();
                    cx = sx; cy = sy;
                    lcx = cx; lcy = cy;
                    break;
                case 'L':
                {
                    if (!lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    cx = ox + x; cy = oy + y;
                    p.LineTo(cx, cy);
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'H':
                {
                    if (!lx.TryNumber(out float x)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    cx = (rel ? cx : 0) + x;
                    p.LineTo(cx, cy);
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'V':
                {
                    if (!lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    cy = (rel ? cy : 0) + y;
                    p.LineTo(cx, cy);
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'C':
                {
                    if (!lx.TryNumber(out float x1) || !lx.TryNumber(out float y1) || !lx.TryNumber(out float x2) || !lx.TryNumber(out float y2)
                        || !lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    p.CurveTo(ox + x1, oy + y1, ox + x2, oy + y2, ox + x, oy + y);
                    lcx = ox + x2; lcy = oy + y2;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'S':
                {
                    if (!lx.TryNumber(out float x2) || !lx.TryNumber(out float y2) || !lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    bool smooth = "CcSs".IndexOf(prev) >= 0;
                    float x1 = smooth ? 2 * cx - lcx : cx, y1 = smooth ? 2 * cy - lcy : cy;
                    p.CurveTo(x1, y1, ox + x2, oy + y2, ox + x, oy + y);
                    lcx = ox + x2; lcy = oy + y2;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'Q':
                {
                    if (!lx.TryNumber(out float qx) || !lx.TryNumber(out float qy) || !lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    Quad(p, cx, cy, ox + qx, oy + qy, ox + x, oy + y);
                    lcx = ox + qx; lcy = oy + qy;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'T':
                {
                    if (!lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    bool smooth = "QqTt".IndexOf(prev) >= 0;
                    float qx = smooth ? 2 * cx - lcx : cx, qy = smooth ? 2 * cy - lcy : cy;
                    Quad(p, cx, cy, qx, qy, ox + x, oy + y);
                    lcx = qx; lcy = qy;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'A':
                {
                    if (!lx.TryNumber(out float rx) || !lx.TryNumber(out float ry) || !lx.TryNumber(out float rot)
                        || !lx.TryFlag(out bool large) || !lx.TryFlag(out bool sweep) || !lx.TryNumber(out float x) || !lx.TryNumber(out float y))
                        return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    Arc(p, cx, cy, rx, ry, rot, large, sweep, ox + x, oy + y);
                    cx = ox + x; cy = oy + y;
                    lcx = cx; lcy = cy;
                    break;
                }
                default:
                    return Finish(p);
            }
            prev = cmd;
            p.EndCommand();
        }
        return Finish(p);
    }

    private static PathBuilder? Finish(PathBuilder p)
    {
        if (p.Verbs.Count == 0) return null;
        p.EndCommand(); // a command cut short by bad data still ends where it stopped
        return p;
    }

    private static void EnsureStart(PathBuilder p, ref bool hasCurrent, float x, float y)
    {
        if (hasCurrent) return;
        p.MoveTo(x, y);
        hasCurrent = true;
    }

    private static void Quad(PathBuilder p, float x0, float y0, float qx, float qy, float x, float y)
        => p.CurveTo(x0 + 2f / 3 * (qx - x0), y0 + 2f / 3 * (qy - y0), x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y), x, y);

    /// <summary>Converts an endpoint-parameterised elliptical arc (SVG 1.1 F.6) into cubic Béziers.</summary>
    private static void Arc(PathBuilder p, float x1, float y1, float rx, float ry, float angle, bool large, bool sweep, float x2, float y2)
    {
        if (x1 == x2 && y1 == y2) return;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            p.LineTo(x2, y2);
            return;
        }
        double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
        double lambda = x1p * x1p / (rx * (double)rx) + y1p * y1p / (ry * (double)ry);
        double Rx = rx, Ry = ry;
        if (lambda > 1)
        {
            double sq = Math.Sqrt(lambda);
            Rx *= sq;
            Ry *= sq;
        }
        double num = Rx * Rx * Ry * Ry - Rx * Rx * y1p * y1p - Ry * Ry * x1p * x1p;
        double den = Rx * Rx * y1p * y1p + Ry * Ry * x1p * x1p;
        double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
        if (large == sweep) coef = -coef;
        double cxp = coef * Rx * y1p / Ry, cyp = -coef * Ry * x1p / Rx;
        double cx = cos * cxp - sin * cyp + (x1 + x2) / 2.0, cy = sin * cxp + cos * cyp + (y1 + y2) / 2.0;

        static double AngleOf(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        double theta1 = AngleOf(1, 0, (x1p - cxp) / Rx, (y1p - cyp) / Ry);
        double delta = AngleOf((x1p - cxp) / Rx, (y1p - cyp) / Ry, (-x1p - cxp) / Rx, (-y1p - cyp) / Ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-6));
        double step = delta / segments;
        double t = 4.0 / 3.0 * Math.Tan(step / 4);
        double a0 = theta1;
        for (int i = 0; i < segments; i++)
        {
            double a1 = a0 + step;
            double c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
            // Unit-circle control points, then scale, rotate and translate.
            double px1 = c0 - t * s0, py1 = s0 + t * c0;
            double px2 = c1 + t * s1, py2 = s1 - t * c1;
            (double, double) Map(double ux, double uy)
            {
                double ex = ux * Rx, ey = uy * Ry;
                return (cos * ex - sin * ey + cx, sin * ex + cos * ey + cy);
            }
            var (ax1, ay1) = Map(px1, py1);
            var (ax2, ay2) = Map(px2, py2);
            var (ax, ay) = i == segments - 1 ? (x2, y2) : Map(c1, s1);
            p.CurveTo((float)ax1, (float)ay1, (float)ax2, (float)ay2, (float)ax, (float)ay);
            a0 = a1;
        }
    }
}
