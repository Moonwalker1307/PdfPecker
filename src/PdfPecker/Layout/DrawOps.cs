using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// A paint instruction in "flow" coordinates: points from the top-left of the first page's content area,
/// with y growing downward and continuing across pages (page n covers y in [n*H, (n+1)*H)).
/// </summary>
internal abstract class DrawOp
{
    public abstract void Offset(float dx, float dy);
}

internal sealed class RectOp : DrawOp
{
    public float X, Y, W, H;
    public uint Color;
    public bool IsDecoration; // text underline, overline or line-through: Chrome paints these at fractional x
    public RectOp(float x, float y, float w, float h, uint color) { X = x; Y = y; W = w; H = h; Color = color; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>
/// A border whose four sides are solid and alike, which Chrome strokes as one rectangle instead of filling each side.
/// Viewers draw a thin stroke and a thin fill differently (pdfium widens a filled sliver to whole pixels), so this
/// keeps hairline box borders looking as they do in Chrome's PDFs.
/// </summary>
internal sealed class BorderRectOp : DrawOp
{
    public float X, Y, W, H, Width; // the border box, and the border width
    public uint Color;
    public BorderRectOp(float x, float y, float w, float h, float width, uint color) { X = x; Y = y; W = w; H = h; Width = width; Color = color; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }

    /// <summary>The four sides as filled rectangles, for a border split by a page break.</summary>
    public RectOp[] Sides() => new[]
    {
        new RectOp(X, Y, W, Width, Color), new RectOp(X, Y + H - Width, W, Width, Color),
        new RectOp(X, Y, Width, H, Color), new RectOp(X + W - Width, Y, Width, H, Color),
    };
}

/// <summary>
/// A background layer: an image or a gradient drawn as one tile at (TileX, TileY, TileW, TileH), repeated every StepX /
/// StepY along the axes that repeat, and clipped to the painting area (ClipX, ClipY, ClipW, ClipH). A gradient's geometry
/// is relative to the tile's top-left corner.
/// </summary>
internal sealed class BackgroundOp : DrawOp
{
    public float ClipX, ClipY, ClipW, ClipH;
    public float TileX, TileY, TileW, TileH;
    public float StepX, StepY;
    public bool RepeatX, RepeatY;
    public PdfImage? Image;
    public Gradient? Gradient;

    public override void Offset(float dx, float dy) { ClipX += dx; ClipY += dy; TileX += dx; TileY += dy; }

    /// <summary>The flow rectangle actually painted: the clip, narrowed to the tile's row or column on axes that do not repeat.</summary>
    public (float X0, float Y0, float X1, float Y1) Painted()
    {
        float x0 = ClipX, y0 = ClipY, x1 = ClipX + ClipW, y1 = ClipY + ClipH;
        if (!RepeatX) { x0 = Math.Max(x0, TileX); x1 = Math.Min(x1, TileX + TileW); }
        if (!RepeatY) { y0 = Math.Max(y0, TileY); y1 = Math.Min(y1, TileY + TileH); }
        return (x0, y0, x1, y1);
    }
}

/// <summary>A stroked line for dashed or dotted borders.</summary>
internal sealed class LineOp : DrawOp
{
    public float X1, Y1, X2, Y2, Width;
    public uint Color;
    public bool Dotted;
    public LineOp(float x1, float y1, float x2, float y2, float width, uint color, bool dotted)
    { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Width = width; Color = color; Dotted = dotted; }
    public override void Offset(float dx, float dy) { X1 += dx; X2 += dx; Y1 += dy; Y2 += dy; }
}

internal sealed class TextOp : DrawOp
{
    public float X, Baseline, LineTop; // LineTop decides the page
    public TextFont Font;
    public float Size;
    public uint Color;
    public string Text;
    public float WordSpacing, CharSpacing;

    public TextOp(float x, float baseline, float lineTop, TextFont font, float size, uint color, string text, float wordSpacing, float charSpacing)
    {
        X = x; Baseline = baseline; LineTop = lineTop; Font = font; Size = size; Color = color; Text = text;
        WordSpacing = wordSpacing; CharSpacing = charSpacing;
    }

    public override void Offset(float dx, float dy) { X += dx; Baseline += dy; LineTop += dy; }
}

internal sealed class ImageOp : DrawOp
{
    public float X, Y, W, H;
    public PdfImage Image;
    public ImageOp(float x, float y, float w, float h, PdfImage image) { X = x; Y = y; W = w; H = h; Image = image; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>An inline SVG drawing scaled into the box (X, Y, W, H).</summary>
internal sealed class SvgOp : DrawOp
{
    public float X, Y, W, H;
    public SvgImage Image;
    public SvgOp(float x, float y, float w, float h, SvgImage image) { X = x; Y = y; W = w; H = h; Image = image; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

internal sealed class LinkOp : DrawOp
{
    public float X, Y, W, H;
    public string Target; // an absolute URI, or "#id" for an internal destination
    public LinkOp(float x, float y, float w, float h, string target) { X = x; Y = y; W = w; H = h; Target = target; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>A filled or stroked circle (list markers).</summary>
internal sealed class CircleOp : DrawOp
{
    public float Cx, Cy, R;
    public uint Color;
    public bool Filled;
    public CircleOp(float cx, float cy, float r, uint color, bool filled) { Cx = cx; Cy = cy; R = r; Color = color; Filled = filled; }
    public override void Offset(float dx, float dy) { Cx += dx; Cy += dy; }
}

/// <summary>A slot reserved before an element's content, filled with its background and borders once its size is known.</summary>
internal sealed class GroupOp : DrawOp
{
    public readonly List<DrawOp> Ops = new(2);
    public override void Offset(float dx, float dy) { foreach (var o in Ops) o.Offset(dx, dy); }
}

internal static class BoxPainter
{
    // Border sides for Paint: which of the box's borders to draw.
    public const int Top = 1, Right = 2, Bottom = 4, Left = 8, AllSides = 15;

    /// <summary>
    /// Paints background and borders of a border box into <paramref name="group"/>. <paramref name="images"/> loads
    /// url() backgrounds; <paramref name="basis"/> is the containing block width that percentage paddings refer to.
    /// </summary>
    public static void Paint(GroupOp group, ComputedStyle s, float x, float y, float w, float h, ImageLoader? images = null, float basis = 0,
        int sides = AllSides, bool collapsedCell = false)
    {
        if (!s.Visible) return;
        if (s.BoxShadows != null && w > 0 && h > 0) PaintShadows(group, s, x, y, w, h);
        if (w > 0 && h > 0)
        {
            var layers = s.BackgroundImages;
            if (CssColor.IsVisible(s.BackgroundColor))
            {
                // The colour is clipped like the bottom layer.
                var clip = Layer(s.BackgroundClips, layers == null ? 0 : layers.Length - 1, BackgroundBox.BorderBox);
                var (cx, cy, cw, ch) = BoxOf(s, clip, x, y, w, h, basis);
                if (cw > 0 && ch > 0) group.Ops.Add(new RectOp(cx, cy, cw, ch, s.BackgroundColor));
            }
            if (layers != null)
            {
                uint backdrop = CssColor.IsOpaque(s.BackgroundColor) ? s.BackgroundColor : 0xFFFFFFFF;
                for (int i = layers.Length - 1; i >= 0; i--)
                    if (layers[i] is { } layer) PaintLayer(group, s, layer, i, x, y, w, h, images, basis, backdrop);
            }
        }
        if (!s.HasBorder) return;
        float bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom, bl = s.BorderLeft;
        uint topColor = s.BorderTopColor ?? s.Color;
        // Chrome fills collapsed table borders side by side, and strokes other boxes' uniform borders.
        if (sides == AllSides && !collapsedCell && bt > 0 && bt == br && bt == bb && bt == bl && 2 * bt < w && 2 * bt < h
            && s.BorderTopStyle == BorderStyle.Solid && s.BorderRightStyle == BorderStyle.Solid
            && s.BorderBottomStyle == BorderStyle.Solid && s.BorderLeftStyle == BorderStyle.Solid
            && CssColor.IsVisible(topColor) && (s.BorderRightColor ?? s.Color) == topColor
            && (s.BorderBottomColor ?? s.Color) == topColor && (s.BorderLeftColor ?? s.Color) == topColor)
        {
            group.Ops.Add(new BorderRectOp(x, y, w, h, bt, topColor));
            return;
        }
        if ((sides & Top) != 0) Side(group, s.BorderTopStyle, bt, s.BorderTopColor ?? s.Color, x, y, w, bt, horizontal: true);
        if ((sides & Bottom) != 0) Side(group, s.BorderBottomStyle, bb, s.BorderBottomColor ?? s.Color, x, y + h - bb, w, bb, horizontal: true);
        if ((sides & Left) != 0) Side(group, s.BorderLeftStyle, bl, s.BorderLeftColor ?? s.Color, x, y, bl, h, horizontal: false);
        if ((sides & Right) != 0) Side(group, s.BorderRightStyle, br, s.BorderRightColor ?? s.Color, x + w - br, y, br, h, horizontal: false);
    }

    /// <summary>
    /// Paints outer box shadows under the box, the first shadow on top. PDF output has no transparency, so a shadow is
    /// opaque, blended over white by its alpha, and only drawn outside the border box, as browsers clip it there. A blur
    /// becomes bands that fade out like a Gaussian edge. Inset shadows are not drawn.
    /// </summary>
    public static void PaintShadows(GroupOp group, ComputedStyle s, float x, float y, float w, float h)
    {
        var shadows = s.BoxShadows;
        if (shadows == null || w <= 0 || h <= 0) return;
        for (int i = shadows.Length - 1; i >= 0; i--)
        {
            var sh = shadows[i];
            if (sh.Inset) continue;
            uint color = sh.Color ?? s.Color;
            uint alpha = color >> 24;
            if (alpha == 0) continue;
            float blur = Math.Min(sh.Blur, 300), spread = Math.Clamp(sh.Spread, -1000, 1000);
            // Bands from the outside in, each painted over the one before, so the middle of the edge is half covered.
            int bands = blur <= 0 ? 1 : Math.Clamp((int)MathF.Ceiling(2 * blur / 0.75f), 2, 24); // about one pixel each
            float sigma = blur / 2;
            for (int k = 0; k < bands; k++)
            {
                float grow, cover;
                if (bands == 1) { grow = spread; cover = 1; }
                else
                {
                    // Band k covers grow values from spread + blur down to spread - blur; its colour is the coverage of
                    // the Gaussian-blurred edge at the band's middle.
                    float outer = spread + blur - 2 * blur * k / bands, mid = outer - blur / bands;
                    grow = outer;
                    cover = 0.5f * Erfc((mid - spread) / (sigma * MathF.Sqrt(2)));
                }
                float sx = x + sh.X - grow, sy = y + sh.Y - grow, sw = w + 2 * grow, shh = h + 2 * grow;
                if (sw <= 0 || shh <= 0) continue;
                uint a = (uint)Math.Clamp(MathF.Round(alpha * cover), 0, 255);
                if (a == 0) continue;
                uint c = CssColor.FlattenOnWhite(a << 24 | (color & 0xFFFFFF));
                Outside(group, sx, sy, sw, shh, x, y, w, h, c);
            }
        }
    }

    // Paints rectangle (sx, sy, sw, sh) minus the box (x, y, w, h) as up to four rectangles.
    private static void Outside(GroupOp group, float sx, float sy, float sw, float sh, float x, float y, float w, float h, uint color)
    {
        float sr = sx + sw, sb = sy + sh, r = x + w, b = y + h;
        if (x >= sr || r <= sx || y >= sb || b <= sy)
        {
            group.Ops.Add(new RectOp(sx, sy, sw, sh, color));
            return;
        }
        if (y > sy) group.Ops.Add(new RectOp(sx, sy, sw, y - sy, color));
        if (sb > b) group.Ops.Add(new RectOp(sx, b, sw, sb - b, color));
        float top = Math.Max(sy, y), bottom = Math.Min(sb, b);
        if (bottom > top)
        {
            if (x > sx) group.Ops.Add(new RectOp(sx, top, x - sx, bottom - top, color));
            if (sr > r) group.Ops.Add(new RectOp(r, top, sr - r, bottom - top, color));
        }
    }

    // Complementary error function (Abramowitz and Stegun 7.1.26), accurate to about 1e-7.
    private static float Erfc(float z)
    {
        float t = 1 / (1 + 0.3275911f * MathF.Abs(z));
        float y = t * (0.254829592f + t * (-0.284496736f + t * (1.421413741f + t * (-1.453152027f + t * 1.061405429f)))) * MathF.Exp(-z * z);
        return z >= 0 ? y : 2 - y;
    }

    private static T Layer<T>(T[]? list, int i, T initial) => list is { Length: > 0 } ? list[i % list.Length] : initial;

    /// <summary>The border, padding or content box of a border box.</summary>
    private static (float X, float Y, float W, float H) BoxOf(ComputedStyle s, BackgroundBox box, float x, float y, float w, float h, float basis)
    {
        if (box == BackgroundBox.BorderBox) return (x, y, w, h);
        float l = s.BorderLeft, t = s.BorderTop, r = s.BorderRight, b = s.BorderBottom;
        if (box == BackgroundBox.ContentBox)
        {
            l += s.PaddingLeft.Resolve(basis);
            t += s.PaddingTop.Resolve(basis);
            r += s.PaddingRight.Resolve(basis);
            b += s.PaddingBottom.Resolve(basis);
        }
        return (x + l, y + t, Math.Max(0, w - l - r), Math.Max(0, h - t - b));
    }

    // Tiles smaller than this are not drawn: they are invisible, and a viewer would have to repeat them millions of times.
    private const float MinTile = 0.05f;

    private static void PaintLayer(GroupOp group, ComputedStyle s, BackgroundImage layer, int i, float x, float y, float w, float h,
        ImageLoader? images, float basis, uint backdrop)
    {
        PdfImage? image = null;
        float iw = 0, ih = 0;
        if (layer.Url != null)
        {
            image = images?.Load(layer.Url);
            if (image == null || image.Width <= 0 || image.Height <= 0) return;
            iw = image.Width * 0.75f; // CSS pixels
            ih = image.Height * 0.75f;
        }
        else if (layer.Gradient == null) return;

        var (ox, oy, ow, oh) = BoxOf(s, Layer(s.BackgroundOrigins, i, BackgroundBox.PaddingBox), x, y, w, h, basis);
        var (cx, cy, cw, ch) = BoxOf(s, Layer(s.BackgroundClips, i, BackgroundBox.BorderBox), x, y, w, h, basis);
        if (cw <= 0 || ch <= 0) return;

        // Tile size. A gradient has no intrinsic size or ratio: it fills the positioning area unless sized.
        var size = Layer(s.BackgroundSizes, i, BackgroundSize.Auto);
        float tw, th;
        if (size.Kind != BackgroundSizeKind.Explicit)
        {
            if (image == null) { tw = ow; th = oh; }
            else
            {
                float sx = ow / iw, sy = oh / ih;
                float k = size.Kind == BackgroundSizeKind.Cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
                tw = iw * k;
                th = ih * k;
            }
        }
        else
        {
            bool autoW = size.Width.IsAuto, autoH = size.Height.IsAuto;
            tw = size.Width.Resolve(ow);
            th = size.Height.Resolve(oh);
            if (autoW && autoH) { tw = image == null ? ow : iw; th = image == null ? oh : ih; }
            else if (autoW) tw = image == null ? ow : th * iw / ih;
            else if (autoH) th = image == null ? oh : tw * ih / iw;
        }

        var (repeatX, repeatY) = Layer(s.BackgroundRepeats, i, (BackgroundRepeat.Repeat, BackgroundRepeat.Repeat));
        // round: scale the tile so that a whole number fits; an auto other dimension keeps the image's ratio.
        bool keepRatio = size.Kind == BackgroundSizeKind.Explicit && (size.Width.IsAuto || size.Height.IsAuto) && image != null;
        if (repeatX == BackgroundRepeat.Round && tw > 0 && ow > 0)
        {
            float nw = ow / Math.Max(1, MathF.Round(ow / tw));
            if (keepRatio && repeatY != BackgroundRepeat.Round) th *= nw / tw;
            tw = nw;
        }
        if (repeatY == BackgroundRepeat.Round && th > 0 && oh > 0)
        {
            float nh = oh / Math.Max(1, MathF.Round(oh / th));
            if (keepRatio && repeatX != BackgroundRepeat.Round) tw *= nh / th;
            th = nh;
        }
        if (!(tw >= MinTile && th >= MinTile) || float.IsInfinity(tw) || float.IsInfinity(th)) return;

        float px = ox + Layer(s.BackgroundPositionsX, i, BackgroundOffset.Start).Resolve(ow - tw);
        float py = oy + Layer(s.BackgroundPositionsY, i, BackgroundOffset.Start).Resolve(oh - th);
        float stepX = tw, stepY = th;
        bool rx = repeatX != BackgroundRepeat.NoRepeat, ry = repeatY != BackgroundRepeat.NoRepeat;
        // space: as many whole tiles as fit, the first and last touching the edges; one tile or less is not repeated.
        if (repeatX == BackgroundRepeat.Space)
        {
            int n = (int)Math.Floor(ow / tw + 1e-4f);
            if (n >= 2) { px = ox; stepX = tw + (ow - n * tw) / (n - 1); }
            else rx = false;
        }
        if (repeatY == BackgroundRepeat.Space)
        {
            int n = (int)Math.Floor(oh / th + 1e-4f);
            if (n >= 2) { py = oy; stepY = th + (oh - n * th) / (n - 1); }
            else ry = false;
        }

        var op = new BackgroundOp
        {
            ClipX = cx, ClipY = cy, ClipW = cw, ClipH = ch,
            TileX = px, TileY = py, TileW = tw, TileH = th,
            StepX = stepX, StepY = stepY,
            RepeatX = rx, RepeatY = ry,
            Image = image,
            Gradient = layer.Gradient?.Resolve(tw, th, backdrop),
        };
        var (x0, y0, x1, y1) = op.Painted();
        if (x1 > x0 && y1 > y0) group.Ops.Add(op);
    }

    public static void Side(GroupOp group, BorderStyle style, float width, uint color, float x, float y, float w, float h, bool horizontal)
    {
        if (width <= 0 || style == BorderStyle.None || !CssColor.IsVisible(color)) return;
        switch (style)
        {
            case BorderStyle.Dashed:
            case BorderStyle.Dotted:
                if (horizontal) group.Ops.Add(new LineOp(x, y + h / 2, x + w, y + h / 2, h, color, style == BorderStyle.Dotted));
                else group.Ops.Add(new LineOp(x + w / 2, y, x + w / 2, y + h, w, color, style == BorderStyle.Dotted));
                break;
            case BorderStyle.Double when width >= 2.25f:
                if (horizontal)
                {
                    float t = h / 3;
                    group.Ops.Add(new RectOp(x, y, w, t, color));
                    group.Ops.Add(new RectOp(x, y + h - t, w, t, color));
                }
                else
                {
                    float t = w / 3;
                    group.Ops.Add(new RectOp(x, y, t, h, color));
                    group.Ops.Add(new RectOp(x + w - t, y, t, h, color));
                }
                break;
            default:
                group.Ops.Add(new RectOp(x, y, w, h, color));
                break;
        }
    }
}
