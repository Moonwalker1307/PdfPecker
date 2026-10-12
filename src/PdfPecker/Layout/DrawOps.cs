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
    public CornerRadii? ClipRadii; // rounded corners of the clip (border-radius)

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

/// <summary>
/// The radii of a box's four rounded corners (border-radius), in points: horizontal and vertical radius of the top-left,
/// top-right, bottom-right and bottom-left corner.
/// </summary>
internal readonly struct CornerRadii
{
    public readonly float TLx, TLy, TRx, TRy, BRx, BRy, BLx, BLy;

    public CornerRadii(float tlx, float tly, float trx, float try_, float brx, float bry, float blx, float bly)
    {
        TLx = Math.Max(0, tlx); TLy = Math.Max(0, tly); TRx = Math.Max(0, trx); TRy = Math.Max(0, try_);
        BRx = Math.Max(0, brx); BRy = Math.Max(0, bry); BLx = Math.Max(0, blx); BLy = Math.Max(0, bly);
        // A corner with a zero radius on either axis is square.
        if (TLx == 0 || TLy == 0) TLx = TLy = 0;
        if (TRx == 0 || TRy == 0) TRx = TRy = 0;
        if (BRx == 0 || BRy == 0) BRx = BRy = 0;
        if (BLx == 0 || BLy == 0) BLx = BLy = 0;
    }

    public bool IsZero => TLx == 0 && TRx == 0 && BRx == 0 && BLx == 0;

    /// <summary>
    /// The radii of a w × h box: scaled down together when adjacent radii would overlap (CSS Backgrounds 3 §5.5).
    /// Null when every corner is square.
    /// </summary>
    public static CornerRadii? Fit(float tlx, float tly, float trx, float try_, float brx, float bry, float blx, float bly, float w, float h)
    {
        var r = new CornerRadii(tlx, tly, trx, try_, brx, bry, blx, bly);
        if (r.IsZero || w <= 0 || h <= 0) return null;
        float f = 1;
        if (r.TLx + r.TRx > 0) f = Math.Min(f, w / (r.TLx + r.TRx));
        if (r.BLx + r.BRx > 0) f = Math.Min(f, w / (r.BLx + r.BRx));
        if (r.TLy + r.BLy > 0) f = Math.Min(f, h / (r.TLy + r.BLy));
        if (r.TRy + r.BRy > 0) f = Math.Min(f, h / (r.TRy + r.BRy));
        return f < 1 ? r.Scale(f) : r;
    }

    public CornerRadii Scale(float f) => new(TLx * f, TLy * f, TRx * f, TRy * f, BRx * f, BRy * f, BLx * f, BLy * f);

    /// <summary>The radii of a box inset by the given widths from this one (the padding edge inside a border); null when square.</summary>
    public CornerRadii? Inset(float top, float right, float bottom, float left)
    {
        var r = new CornerRadii(TLx - left, TLy - top, TRx - right, TRy - top, BRx - right, BRy - bottom, BLx - left, BLy - bottom);
        return r.IsZero ? null : r;
    }

    /// <summary>The radii of a box grown by <paramref name="d"/> on every side (a box-shadow's spread); square corners stay square.</summary>
    public CornerRadii Outset(float d)
    {
        static float G(float r, float d) => r > 0 ? Math.Max(0, r + d) : 0;
        return new(G(TLx, d), G(TLy, d), G(TRx, d), G(TRy, d), G(BRx, d), G(BRy, d), G(BLx, d), G(BLy, d));
    }
}

/// <summary>
/// A rounded rectangle (X, Y, W, H) with corner radii <see cref="R"/>: filled, or with <see cref="StrokeWidth"/> stroked
/// along its outline (dashed or dotted when <see cref="Style"/> says so). With a hole, the part inside the hole is left
/// out, which makes a rounded border ring or a shadow outside its box. With <see cref="Clip"/>, only the part inside that
/// polygon is painted: one side of a border whose sides differ.
/// </summary>
internal sealed class RoundRectOp : DrawOp
{
    public float X, Y, W, H;
    public CornerRadii R;
    public uint Color;
    public bool HasHole;
    public float HoleX, HoleY, HoleW, HoleH;
    public CornerRadii HoleR;
    public float StrokeWidth;
    public BorderStyle Style = BorderStyle.Solid;
    public float[]? Clip; // x, y pairs

    public RoundRectOp(float x, float y, float w, float h, CornerRadii r, uint color) { X = x; Y = y; W = w; H = h; R = r; Color = color; }

    public RoundRectOp WithHole(float x, float y, float w, float h, CornerRadii? r)
    {
        HasHole = true;
        HoleX = x; HoleY = y; HoleW = w; HoleH = h;
        HoleR = r ?? default;
        return this;
    }

    public override void Offset(float dx, float dy)
    {
        X += dx; Y += dy; HoleX += dx; HoleY += dy;
        if (Clip != null)
            for (int i = 0; i < Clip.Length; i += 2) { Clip[i] += dx; Clip[i + 1] += dy; }
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
    public CornerRadii? Radii; // rounded corners the image is clipped to
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
internal class GroupOp : DrawOp
{
    public readonly List<DrawOp> Ops = new(2);
    public override void Offset(float dx, float dy) { foreach (var o in Ops) o.Offset(dx, dy); }
}

/// <summary>
/// The ops of a float, a positioned or a transformed box: painted in its own layer (CSS 2.1 Appendix E), and drawn with
/// <see cref="Transform"/> about (<see cref="OriginX"/>, <see cref="OriginY"/>) when it has one.
/// </summary>
internal sealed class LayerOp : GroupOp
{
    // Painting order among siblings: negative z-index, then the normal flow (0), floats, positioned boxes with z-index
    // auto or 0, and positive z-index.
    public const float FloatOrder = 0.25f, PositionedOrder = 0.5f;

    public float Order;
    public float Opacity = 1;    // below 1: painted as one transparency group at this opacity
    public bool StackingContext; // its first op, the box's own background, stays below its negative z-index children
    public Matrix? Transform;    // y-down, about the origin
    public float OriginX, OriginY;

    public override void Offset(float dx, float dy)
    {
        base.Offset(dx, dy);
        OriginX += dx;
        OriginY += dy;
    }

    /// <summary>Sorts ops into painting order, keeping document order within a layer.</summary>
    public static void SortLayers(List<DrawOp> ops, int pinned = 0)
    {
        bool any = false;
        foreach (var op in ops)
        {
            if (op is not LayerOp layer) continue;
            any = true;
            SortLayers(layer.Ops, layer.StackingContext ? 1 : 0);
        }
        if (!any) return;
        var tail = new List<DrawOp>(ops.Count - pinned);
        for (int i = pinned; i < ops.Count; i++) tail.Add(ops[i]);
        var sorted = tail.Select((op, i) => (op, i)).OrderBy(t => t.op is LayerOp l ? l.Order : 0).ThenBy(t => t.i).ToList();
        for (int i = 0; i < sorted.Count; i++) ops[pinned + i] = sorted[i].op;
    }
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
        // Collapsed table borders have no rounded corners.
        var radii = sides == AllSides && !collapsedCell ? Radii(s, w, h) : null;
        if (s.BoxShadows != null && w > 0 && h > 0) PaintShadows(group, s, x, y, w, h, radii);
        if (w > 0 && h > 0 && !s.CanvasBackground)
        {
            var layers = s.BackgroundImages;
            if (CssColor.IsVisible(s.BackgroundColor))
            {
                // The colour is clipped like the bottom layer.
                var clip = Layer(s.BackgroundClips, layers == null ? 0 : layers.Length - 1, BackgroundBox.BorderBox);
                var (cx, cy, cw, ch) = BoxOf(s, clip, x, y, w, h, basis);
                if (cw > 0 && ch > 0)
                {
                    if (InnerRadii(radii, cx - x, cy - y, x + w - cx - cw, y + h - cy - ch) is { } cr)
                        group.Ops.Add(new RoundRectOp(cx, cy, cw, ch, cr, s.BackgroundColor));
                    else group.Ops.Add(new RectOp(cx, cy, cw, ch, s.BackgroundColor));
                }
            }
            if (layers != null)
            {
                uint backdrop = CssColor.IsVisible(s.BackgroundColor) ? CssColor.FlattenOnWhite(s.BackgroundColor) : 0xFFFFFFFF;
                for (int i = layers.Length - 1; i >= 0; i--)
                    if (layers[i] is { } layer) PaintLayer(group, s, layer, i, x, y, w, h, images, basis, backdrop, radii);
            }
        }
        if (!s.HasBorder) return;
        float bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom, bl = s.BorderLeft;
        uint topColor = s.BorderTopColor ?? s.Color;
        if (radii is { } r && w > 0 && h > 0)
        {
            PaintRoundedBorder(group, s, x, y, w, h, r);
            return;
        }
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
    /// Paints the background of the root element (or the body's, propagated to it) on the canvas: positioned against the
    /// root element's border box (x, y, w, h), described by <paramref name="box"/>, and filling the whole clip rectangle,
    /// which is one page area. As in Chrome, background-clip does not apply to the canvas.
    /// </summary>
    public static void PaintCanvas(GroupOp group, ComputedStyle s, ComputedStyle box, float x, float y, float w, float h,
        float clipX, float clipY, float clipW, float clipH, ImageLoader? images, float basis)
    {
        if (!s.Visible || clipW <= 0 || clipH <= 0) return;
        if (CssColor.IsVisible(s.BackgroundColor)) group.Ops.Add(new RectOp(clipX, clipY, clipW, clipH, s.BackgroundColor));
        var layers = s.BackgroundImages;
        if (layers == null || w <= 0 || h <= 0) return;
        uint backdrop = CssColor.IsVisible(s.BackgroundColor) ? CssColor.FlattenOnWhite(s.BackgroundColor) : 0xFFFFFFFF;
        int first = group.Ops.Count;
        for (int i = layers.Length - 1; i >= 0; i--)
            if (layers[i] is { } layer) PaintLayer(group, s, layer, i, x, y, w, h, images, basis, backdrop, null, box, (clipX, clipY, clipW, clipH));
        // Pages far down the document move the tile far up: bring repeated tiles back to the page, so that a tile as tall
        // as the page area (html { height: 100% }) is drawn once and not as a pattern with a seam of rounding error.
        for (int i = first; i < group.Ops.Count; i++)
        {
            if (group.Ops[i] is not BackgroundOp bg) continue;
            if (bg.RepeatX && bg.StepX > 0) bg.TileX = NearTile(bg.TileX, bg.StepX, clipX);
            if (bg.RepeatY && bg.StepY > 0) bg.TileY = NearTile(bg.TileY, bg.StepY, clipY);
        }
    }

    /// <summary>The start of the repeated tile that covers <paramref name="edge"/>, snapped onto it when within rounding.</summary>
    private static float NearTile(float start, float step, float edge)
    {
        double t = start + Math.Floor((edge - (double)start) / step + 1e-4) * step;
        return Math.Abs(t - edge) < 0.01 ? edge : (float)t;
    }

    /// <summary>The border radii of a w × h border box, scaled to fit; null when every corner is square.</summary>
    public static CornerRadii? Radii(ComputedStyle s, float w, float h)
    {
        var r = s.BorderRadii;
        if (r == null) return null;
        return CornerRadii.Fit(r[0].Resolve(w), r[1].Resolve(h), r[2].Resolve(w), r[3].Resolve(h),
            r[4].Resolve(w), r[5].Resolve(h), r[6].Resolve(w), r[7].Resolve(h), w, h);
    }

    /// <summary>The radii of a box inset from the border box by the given distances.</summary>
    public static CornerRadii? InnerRadii(CornerRadii? radii, float left, float top, float right, float bottom)
        => radii is { } r ? r.Inset(top, right, bottom, left) : null;

    /// <summary>
    /// Paints the borders of a box with rounded corners: one ring when the sides are alike, else each side as the part of
    /// the ring between the lines from its outer to its inner corners.
    /// </summary>
    private static void PaintRoundedBorder(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii r)
    {
        float bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom, bl = s.BorderLeft;
        float ix = x + bl, iy = y + bt, iw = Math.Max(0, w - bl - br), ih = Math.Max(0, h - bt - bb);
        var inner = r.Inset(bt, br, bb, bl);
        var sides = new (float Width, BorderStyle Style, uint Color)[]
        {
            (bt, s.BorderTopStyle, s.BorderTopColor ?? s.Color), (br, s.BorderRightStyle, s.BorderRightColor ?? s.Color),
            (bb, s.BorderBottomStyle, s.BorderBottomColor ?? s.Color), (bl, s.BorderLeftStyle, s.BorderLeftColor ?? s.Color),
        };
        bool Shows(int i) => sides[i].Width > 0 && sides[i].Style != BorderStyle.None && CssColor.IsVisible(sides[i].Color);
        int shown = 0;
        for (int i = 0; i < 4; i++) if (Shows(i)) shown++;
        if (shown == 0) return;
        bool sameColor = true, allSolid = true, alike = shown == 4;
        uint first = 0;
        for (int i = 0, n = 0; i < 4; i++)
        {
            if (!Shows(i)) continue;
            if (n++ == 0) first = sides[i].Color;
            else if (sides[i].Color != first) sameColor = false;
            if (sides[i].Style is not (BorderStyle.Solid or BorderStyle.Double)) allSolid = false;
            if (sides[i].Width != sides[0].Width || sides[i].Style != sides[0].Style || sides[i].Color != sides[0].Color) alike = false;
        }
        if (alike && sides[0].Style is BorderStyle.Dashed or BorderStyle.Dotted && 2 * bt < w && 2 * bt < h)
        {
            group.Ops.Add(new RoundRectOp(x, y, w, h, r, first) { StrokeWidth = bt, Style = sides[0].Style });
            return;
        }
        if (sameColor && allSolid)
        {
            // Sides without a border have zero width, so one ring covers exactly the borders that show.
            group.Ops.Add(new RoundRectOp(x, y, w, h, r, first).WithHole(ix, iy, iw, ih, inner));
            return;
        }
        // Each side is the ring cut along the lines from the outer corners through the inner ones, continued inward past
        // the curves (as Chrome splits the corners).
        float x1 = x + w, y1 = y + h, reach = Math.Min(w, h) / 2;
        (float X, float Y) Toward(float cx, float cy, float dx, float dy)
        {
            float k = reach / Math.Max(Math.Max(dx, dy), 0.01f);
            return (cx + Math.Sign(x + w / 2 - cx) * dx * k, cy + Math.Sign(y + h / 2 - cy) * dy * k);
        }
        var tl = Toward(x, y, bl, bt);
        var tr = Toward(x1, y, br, bt);
        var brc = Toward(x1, y1, br, bb);
        var blc = Toward(x, y1, bl, bb);
        float[][] polys =
        {
            new[] { x, y, x1, y, tr.X, tr.Y, tl.X, tl.Y },
            new[] { x1, y, x1, y1, brc.X, brc.Y, tr.X, tr.Y },
            new[] { x1, y1, x, y1, blc.X, blc.Y, brc.X, brc.Y },
            new[] { x, y1, x, y, tl.X, tl.Y, blc.X, blc.Y },
        };
        for (int i = 0; i < 4; i++)
        {
            if (!Shows(i)) continue;
            group.Ops.Add(new RoundRectOp(x, y, w, h, r, sides[i].Color) { Clip = polys[i] }.WithHole(ix, iy, iw, ih, inner));
        }
    }

    /// <summary>
    /// Paints outer box shadows under the box, the first shadow on top. A shadow is opaque, blended over white by its
    /// alpha, and only drawn outside the border box, as browsers clip it there. A blur becomes bands that fade out like a
    /// Gaussian edge. Inset shadows are not drawn.
    /// </summary>
    public static void PaintShadows(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii? radii = null)
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
                if (radii is { } r)
                    group.Ops.Add(new RoundRectOp(sx, sy, sw, shh, r.Outset(grow), c).WithHole(x, y, w, h, r));
                else Outside(group, sx, sy, sw, shh, x, y, w, h, c);
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
        ImageLoader? images, float basis, uint backdrop, CornerRadii? radii,
        ComputedStyle? box = null, (float X, float Y, float W, float H)? canvas = null)
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

        var (ox, oy, ow, oh) = BoxOf(box ?? s, Layer(s.BackgroundOrigins, i, BackgroundBox.PaddingBox), x, y, w, h, basis);
        var (cx, cy, cw, ch) = canvas ?? BoxOf(s, Layer(s.BackgroundClips, i, BackgroundBox.BorderBox), x, y, w, h, basis);
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
            ClipRadii = canvas == null ? InnerRadii(radii, cx - x, cy - y, x + w - cx - cw, y + h - cy - ch) : null,
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
