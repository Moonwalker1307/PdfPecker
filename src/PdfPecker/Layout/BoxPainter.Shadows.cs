using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>Box shadows: outer ones as bands outside the box, inset ones as rings inside the padding box.</summary>
internal static partial class BoxPainter
{
    // A blur or spread beyond these is cut, so that one shadow cannot paint, and add, pages far away.
    private const float MaxBlur = 300, MaxSpread = 1000;

    // A blur is drawn as bands of about one pixel each, at most this many; a sharp shadow is one band.
    private const float BandPixel = 0.75f;
    private const int MinBands = 2, MaxBands = 24;

    /// <summary>The number of bands a blur of <paramref name="blur"/> points is drawn in; 0 for a sharp edge.</summary>
    private static int BlurBands(float blur) => blur <= 0 ? 0 : Math.Clamp((int)MathF.Ceiling(2 * blur / BandPixel), MinBands, MaxBands);

    /// <summary>
    /// The coverage of a Gaussian-blurred edge at <paramref name="distance"/> inside it (negative outside), with the blur's
    /// standard deviation <paramref name="sigma"/>: 1 well inside, a half on the edge, 0 well outside.
    /// </summary>
    private static float EdgeCoverage(float distance, float sigma) => 0.5f * Erfc(distance / (sigma * MathF.Sqrt(2)));

    /// <summary>The shadow's alpha times <paramref name="cover"/>, as a whole alpha value.</summary>
    private static uint CoveredAlpha(uint alpha, float cover) => (uint)Math.Clamp(MathF.Round(alpha * cover), 0, 255);

    /// <summary>
    /// Paints outer box shadows under the box, the first shadow on top, only outside the border box, as browsers clip
    /// them there. A sharp shadow is opaque, blended over white by its alpha. A blur becomes bands that fade out like a
    /// Gaussian edge, each painted over the one before; they are grey levels in a layer that is a soft mask
    /// (<see cref="LayerOp.MaskFill"/>), through which the shadow's colour is painted, so that what lies under the
    /// shadow shows through it. The shadows go in a group marked as ink overflow (<see cref="GroupOp.Ink"/>), so a
    /// shadow below the last line of content adds no page. Inset shadows are painted by <see cref="PaintInsetShadows"/>.
    /// </summary>
    public static void PaintShadows(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii? radii = null)
    {
        var shadows = s.BoxShadows;
        if (shadows == null || w <= 0 || h <= 0) return;
        var box = group;
        group = new GroupOp { Ink = true };
        box.Ops.Add(group);
        for (int i = shadows.Length - 1; i >= 0; i--)
        {
            var sh = shadows[i];
            if (sh.Inset) continue;
            uint color = sh.Color ?? s.Color;
            uint alpha = color >> 24;
            if (alpha == 0) continue;
            float blur = Math.Min(sh.Blur, MaxBlur), spread = Math.Clamp(sh.Spread, -MaxSpread, MaxSpread);
            // Bands from the outside in, each painted over the one before, so the middle of the edge is half covered.
            int bands = Math.Max(1, BlurBands(blur));
            float sigma = blur / 2;
            var into = group;
            if (bands > 1)
            {
                into = new LayerOp { MaskFill = 0xFF000000 | color };
                group.Ops.Add(into);
            }
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
                    cover = EdgeCoverage(mid - spread, sigma);
                }
                float sx = x + sh.X - grow, sy = y + sh.Y - grow, sw = w + 2 * grow, shh = h + 2 * grow;
                if (sw <= 0 || shh <= 0) continue;
                uint a = CoveredAlpha(alpha, cover);
                if (a == 0) continue;
                // In the mask, the band's alpha is a grey level.
                uint c = bands == 1 ? CssColor.FlattenOnWhite(a << 24 | (color & 0xFFFFFF)) : 0xFF000000 | a << 16 | a << 8 | a;
                if (radii is { } r)
                    into.Ops.Add(new RoundRectOp(sx, sy, sw, shh, r.Outset(grow), c).WithHole(x, y, w, h, r));
                else PaintOutside(into, sx, sy, sw, shh, x, y, w, h, c);
            }
            if (into != group && into.Ops.Count == 0) group.Ops.RemoveAt(group.Ops.Count - 1);
        }
        if (group.Ops.Count == 0) box.Ops.RemoveAt(box.Ops.Count - 1);
    }

    /// <summary>
    /// Paints inset box shadows inside the padding box, over the background and under the borders, the first shadow on
    /// top. The shadow fills the padding box outside a hole: the padding box moved by the offset and shrunk by the
    /// spread. A blur becomes rings around the hole, each in its own translucent shade, so that what lies under them
    /// (a background image) still shows through.
    /// </summary>
    public static void PaintInsetShadows(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii? radii)
    {
        var shadows = s.BoxShadows;
        if (shadows == null) return;
        float bl = s.BorderLeft, bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom;
        float px = x + bl, py = y + bt, pw = w - bl - br, ph = h - bt - bb;
        if (pw <= 0 || ph <= 0) return;
        var padR = InnerRadii(radii, bl, bt, br, bb);
        ClipOp? clip = null;
        for (int i = shadows.Length - 1; i >= 0; i--)
        {
            var sh = shadows[i];
            if (!sh.Inset) continue;
            uint color = sh.Color ?? s.Color;
            if (color >> 24 == 0) continue;
            clip ??= new ClipOp(px, py, pw, ph, padR);
            PaintInsetShadow(clip, sh, color, px, py, pw, ph, padR);
        }
        if (clip != null) group.Ops.Add(clip);
    }

    /// <summary>One inset shadow into the padding box's clip: the area outside the hole, then the blur rings around it.</summary>
    private static void PaintInsetShadow(ClipOp clip, BoxShadow sh, uint color, float px, float py, float pw, float ph, CornerRadii? padR)
    {
        uint alpha = color >> 24;
        float blur = Math.Min(sh.Blur, MaxBlur), spread = Math.Clamp(sh.Spread, -MaxSpread, MaxSpread);
        // Far enough out to cover the padding box wherever the hole is.
        float reach = Math.Abs(sh.X) + Math.Abs(sh.Y) + blur + Math.Abs(spread) + 2;
        float ox = px - reach, oy = py - reach, ow = pw + 2 * reach, oh = ph + 2 * reach;
        // The hole: the padding box moved by the offset and inset on every side.
        (float X, float Y, float W, float H, CornerRadii R) Hole(float inset)
        {
            var r = padR is { } pr ? pr.Outset(-inset) : default;
            return (px + sh.X + inset, py + sh.Y + inset, Math.Max(0, pw - 2 * inset), Math.Max(0, ph - 2 * inset), r);
        }
        int bands = BlurBands(blur);
        float sigma = blur / 2;
        // Band k is the ring between the hole inset by edge(k + 1) and the larger hole inset by edge(k); the
        // insets run from spread + blur, where the shadow fades out, down to spread - blur.
        float Edge(int k) => spread + blur - 2 * blur * k / bands;
        var last = Hole(bands == 0 ? spread : Edge(bands));
        var outer = new RoundRectOp(ox, oy, ow, oh, default, color);
        if (last.W > 0 && last.H > 0) outer.WithHole(last.X, last.Y, last.W, last.H, last.R);
        clip.Ops.Add(outer);
        for (int k = 0; k < bands; k++)
        {
            var big = Hole(Edge(k + 1));
            if (big.W <= 0 || big.H <= 0) continue;
            float mid = Edge(k) - blur / bands;
            uint c = CoveredAlpha(alpha, EdgeCoverage(mid - spread, sigma)) << 24 | (color & 0xFFFFFF);
            if (c >> 24 == 0) continue;
            var ring = new RoundRectOp(big.X, big.Y, big.W, big.H, big.R, c);
            var small = Hole(Edge(k));
            if (small.W > 0 && small.H > 0) ring.WithHole(small.X, small.Y, small.W, small.H, small.R);
            clip.Ops.Add(ring);
        }
    }

    /// <summary>Paints the rectangle (sx, sy, sw, sh) minus the box (x, y, w, h) as up to four rectangles.</summary>
    private static void PaintOutside(GroupOp group, float sx, float sy, float sw, float sh, float x, float y, float w, float h, uint color)
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

    /// <summary>The complementary error function (Abramowitz and Stegun 7.1.26), accurate to about 1e-7.</summary>
    private static float Erfc(float z)
    {
        float t = 1 / (1 + 0.3275911f * MathF.Abs(z));
        float y = t * (0.254829592f + t * (-0.284496736f + t * (1.421413741f + t * (-1.453152027f + t * 1.061405429f)))) * MathF.Exp(-z * z);
        return z >= 0 ? y : 2 - y;
    }
}
