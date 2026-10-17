using PdfPecker.Css;
using PdfPecker.Images;

namespace PdfPecker.Layout;

/// <summary>
/// Turns a box's computed style into draw ops: its shadows, background colour and layers, border image or borders, and
/// outline, in painting order. The borders, outline, shadows, background layers and border image each have a partial
/// file of their own.
/// </summary>
internal static partial class BoxPainter
{
    // Border sides for Paint: which of the box's borders to draw.
    public const int Top = 1, Right = 2, Bottom = 4, Left = 8, AllSides = 15;

    /// <summary>Points per CSS pixel: the size an image's pixels and a border image's slice numbers are measured in.</summary>
    private const float CssPixel = 0.75f;

    /// <summary>The white a translucent background colour is flattened on when nothing shows through it.</summary>
    private const uint White = 0xFFFFFFFF;

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
        bool hasArea = w > 0 && h > 0;
        if (s.BoxShadows != null && hasArea) PaintShadows(group, s, x, y, w, h, radii);
        if (hasArea && !s.CanvasBackground) PaintBackground(group, s, x, y, w, h, images, basis, radii);
        if (s.BoxShadows != null && hasArea && !s.CanvasBackground) PaintInsetShadows(group, s, x, y, w, h, radii);
        // A border image replaces the border styles; when its image cannot be shown, the styles are drawn instead.
        // It does not apply to collapsed table borders.
        bool borderImage = s.BorderImageSource != null && !collapsedCell && PaintBorderImage(group, s, x, y, w, h, images);
        if (s.HasBorder && !borderImage) PaintBorders(group, s, x, y, w, h, sides, collapsedCell, radii);
        if (s.OutlineStyle != BorderStyle.None && !collapsedCell) PaintOutline(group, s, x, y, w, h, radii);
    }

    /// <summary>The background colour, clipped like the bottom layer, under the background layers, the last layer first.</summary>
    private static void PaintBackground(GroupOp group, ComputedStyle s, float x, float y, float w, float h, ImageLoader? images, float basis, CornerRadii? radii)
    {
        var layers = s.BackgroundImages;
        // With background-blend-mode, the background is a group of its own: its layers blend with each other and
        // with the background colour, not with what is behind the box.
        if (layers != null && s.BackgroundBlendModes != null)
        {
            var isolated = new LayerOp { Isolated = true };
            group.Ops.Add(isolated);
            group = isolated;
        }
        if (CssColor.IsVisible(s.BackgroundColor))
        {
            var clip = LayerValue(s.BackgroundClips, layers == null ? 0 : layers.Length - 1, BackgroundBox.BorderBox);
            var (cx, cy, cw, ch) = BoxOf(s, clip, x, y, w, h, basis);
            if (cw > 0 && ch > 0)
            {
                if (InnerRadii(radii, cx - x, cy - y, x + w - cx - cw, y + h - cy - ch) is { } cr)
                    group.Ops.Add(new RoundRectOp(cx, cy, cw, ch, cr, s.BackgroundColor));
                else group.Ops.Add(new RectOp(cx, cy, cw, ch, s.BackgroundColor));
            }
        }
        if (layers == null) return;
        uint backdrop = Backdrop(s);
        for (int i = layers.Length - 1; i >= 0; i--)
        {
            if (layers[i] is not { } layer) continue;
            int before = group.Ops.Count;
            PaintLayer(group, s, layer, i, x, y, w, h, images, basis, backdrop, radii);
            if (s.BackgroundBlendModes is { Length: > 0 } modes && group.Ops.Count > before && group.Ops[^1] is BackgroundOp bg)
                bg.BlendMode = modes[i % modes.Length];
        }
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
        uint backdrop = Backdrop(s);
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

    // A tile start within this distance of the page edge is put on the edge; a tile count this far below a whole
    // number is rounded up to it.
    private const double TileSnap = 0.01, TileCountSlack = 1e-4;

    /// <summary>The start of the repeated tile that covers <paramref name="edge"/>, snapped onto it when within rounding.</summary>
    private static float NearTile(float start, float step, float edge)
    {
        double t = start + Math.Floor((edge - (double)start) / step + TileCountSlack) * step;
        return Math.Abs(t - edge) < TileSnap ? edge : (float)t;
    }

    /// <summary>The colour a gradient's transparent stops are blended on: the background colour over white, or white.</summary>
    private static uint Backdrop(ComputedStyle s)
        => CssColor.IsVisible(s.BackgroundColor) ? CssColor.FlattenOnWhite(s.BackgroundColor) : White;

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

    /// <summary>The value of background layer <paramref name="i"/> in a comma-separated list, which repeats when shorter than the layers.</summary>
    private static T LayerValue<T>(T[]? list, int i, T initial) => list is { Length: > 0 } ? list[i % list.Length] : initial;

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
}
