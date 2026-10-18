using PdfPecker.Css;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>The border image (CSS Backgrounds 3 §6): nine slices of an image laid as tiles into the border image area.</summary>
internal static partial class BoxPainter
{
    // Indexes of the nine parts of a border image: the corners, the edges and the middle.
    private const int TopLeftPart = 0, TopRightPart = 1, BottomRightPart = 2, BottomLeftPart = 3,
        TopPart = 4, BottomPart = 5, LeftPart = 6, RightPart = 7, MiddlePart = 8;

    /// <summary>An outset beyond this is cut, like a shadow's spread, so that a huge one cannot paint, and add, pages far away.</summary>
    private const float MaxOutset = 1000;

    /// <summary>A border image width is capped here so that a huge multiple stays finite in the scaling.</summary>
    private const float MaxBorderImageWidth = 1e6f;

    /// <summary>
    /// Paints the border image of a border box (CSS Backgrounds 3 §6): the image is cut into nine parts by the slice
    /// lines, and each part is scaled, and repeated along its edge, into the matching part of the border image area,
    /// which is the border box grown by the outsets. Each part becomes a background op, so that images, SVG files and
    /// gradients take the usual paths, also in the renderer: a raster slice cut out as an image of its own, or else the
    /// whole image placed so that only its slice shows in the tile. False when nothing could be loaded, so that the
    /// border styles are drawn instead.
    /// </summary>
    private static bool PaintBorderImage(GroupOp group, ComputedStyle s, float x, float y, float w, float h, ImageLoader? images)
    {
        var source = s.BorderImageSource!;
        PdfImage? image = null;
        SvgImage? svg = null;
        if (source.Url != null)
        {
            image = images?.Load(source.Url);
            if (image is { Width: <= 0 } or { Height: <= 0 }) image = null;
            if (image == null) svg = images?.LoadSvg(source.Url);
            if (image == null && svg == null) return false;
        }
        else if (source.Gradient == null) return false;
        if (!(w > 0 && h > 0)) return true;

        // The border widths that border-image-width multiplies; a side whose style is none has none.
        float[] border = [s.BorderTop, s.BorderRight, s.BorderBottom, s.BorderLeft];
        var outsets = s.BorderImageOutsets;
        float[] o = new float[4];
        for (int k = 0; k < 4; k++)
            o[k] = outsets == null ? 0 : Math.Clamp(outsets[k].IsNumber ? outsets[k].Number * border[k] : outsets[k].Length.Resolve(0), 0, MaxOutset);
        float ax = x - o[SideLeft], ay = y - o[SideTop], aw = w + o[SideRight] + o[SideLeft], ah = h + o[SideTop] + o[SideBottom];
        if (!(aw > 0 && ah > 0) || !float.IsFinite(aw) || !float.IsFinite(ah)) return true;

        // The image's size: its natural one, or for a gradient (and an SVG file without one) the border image area's.
        float iw, ih;
        bool natural = true;
        if (image != null) { iw = image.Width * CssPixel; ih = image.Height * CssPixel; }
        else if (svg != null) (iw, ih, _, natural) = SvgSize(svg, aw, ah);
        else { iw = aw; ih = ah; natural = false; }
        if (!(iw > 0 && ih > 0) || !float.IsFinite(iw) || !float.IsFinite(ih)) return true;

        // Slices, in the image's points: numbers are image pixels (CSS pixels for vector images), percentages refer to
        // the image's width for the left and right slice and to its height for the top and bottom one.
        var slices = s.BorderImageSlice;
        float st = Slice(slices, SideTop, ih), sr = Slice(slices, SideRight, iw), sb = Slice(slices, SideBottom, ih), sl = Slice(slices, SideLeft, iw);

        // Widths of the border image area's edges: a multiple of the border width, a length, a percentage of the area,
        // or auto (the slice's own size, or the border width for an image without one).
        var widths = s.BorderImageWidths;
        float wt = EdgeWidth(widths, border, natural, SideTop, st, ah), wr = EdgeWidth(widths, border, natural, SideRight, sr, aw);
        float wb = EdgeWidth(widths, border, natural, SideBottom, sb, ah), wl = EdgeWidth(widths, border, natural, SideLeft, sl, aw);
        // Edges that overlap are scaled down together until they meet.
        float f = 1;
        if (wl + wr > aw) f = Math.Min(f, aw / (wl + wr));
        if (wt + wb > ah) f = Math.Min(f, ah / (wt + wb));
        if (f < 1) { wt *= f; wr *= f; wb *= f; wl *= f; }

        float midW = aw - wl - wr, midH = ah - wt - wb;   // the area's middle
        float smw = iw - sl - sr, smh = ih - st - sb;     // the image's middle
        float mx = ax + wl, my = ay + wt;
        var rx = s.BorderImageRepeatX;
        var ry = s.BorderImageRepeatY;

        // A raster image is cut into its nine slices (corners, edges, middle), which are then drawn whole; when it cannot
        // be cut, each part draws the whole image clipped to its slice.
        PdfImage?[]? cuts = null;
        if (image != null && images != null)
        {
            cuts = images.Slices(image,
            [
                PixelRect(0, 0, sl, st), PixelRect(iw - sr, 0, sr, st), PixelRect(iw - sr, ih - sb, sr, sb), PixelRect(0, ih - sb, sl, sb),
                PixelRect(sl, 0, smw, st), PixelRect(sl, ih - sb, smw, sb), PixelRect(0, st, sl, smh), PixelRect(iw - sr, st, sr, smh),
                s.BorderImageFill ? PixelRect(sl, st, smw, smh) : (0, 0, 0, 0),
            ]);
        }
        var parts = new BorderImageParts(group, source, image, svg, cuts, iw, ih, Backdrop(s));

        // The corners are stretched into their regions.
        const BorderImageRepeat Stretch = BorderImageRepeat.Stretch;
        parts.Paint(TopLeftPart, 0, 0, sl, st, ax, ay, wl, wt, wl, wt, Stretch, Stretch);
        parts.Paint(TopRightPart, iw - sr, 0, sr, st, ax + aw - wr, ay, wr, wt, wr, wt, Stretch, Stretch);
        parts.Paint(BottomRightPart, iw - sr, ih - sb, sr, sb, ax + aw - wr, ay + ah - wb, wr, wb, wr, wb, Stretch, Stretch);
        parts.Paint(BottomLeftPart, 0, ih - sb, sl, sb, ax, ay + ah - wb, wl, wb, wl, wb, Stretch, Stretch);
        // The edges are as thick as their region and keep the slice's proportions along it, before repeating.
        float kt = st > 0 ? wt / st : 0, kb = sb > 0 ? wb / sb : 0, kl = sl > 0 ? wl / sl : 0, kr = sr > 0 ? wr / sr : 0;
        parts.Paint(TopPart, sl, 0, smw, st, mx, ay, midW, wt, smw * kt, wt, rx, Stretch);
        parts.Paint(BottomPart, sl, ih - sb, smw, sb, mx, ay + ah - wb, midW, wb, smw * kb, wb, rx, Stretch);
        parts.Paint(LeftPart, 0, st, sl, smh, ax, my, wl, midH, wl, smh * kl, Stretch, ry);
        parts.Paint(RightPart, iw - sr, st, sr, smh, ax + aw - wr, my, wr, midH, wr, smh * kr, Stretch, ry);
        // The middle, with fill only: scaled like the top edge across and like the left edge down, or failing those,
        // like the bottom and the right edge, or not at all.
        if (s.BorderImageFill)
        {
            float hx = kt > 0 ? kt : kb > 0 ? kb : 1;
            float hy = kl > 0 ? kl : kr > 0 ? kr : 1;
            parts.Paint(MiddlePart, sl, st, smw, smh, mx, my, midW, midH, smw * hx, smh * hy, rx, ry);
        }
        return true;
    }

    /// <summary>The slice on one side, in the image's points, clamped to the image; the whole side without a value.</summary>
    private static float Slice(BorderImageValue[]? slices, int side, float size)
    {
        if (slices == null) return size; // 100%
        var v = slices[side];
        return Math.Clamp(v.IsNumber ? v.Number * CssPixel : v.Length.Resolve(size), 0, size);
    }

    /// <summary>The width of one edge of the border image area; the border width without a value.</summary>
    private static float EdgeWidth(BorderImageValue[]? widths, float[] border, bool natural, int side, float slice, float basis)
    {
        if (widths == null) return border[side];
        var v = widths[side];
        float width = v.IsNumber ? v.Number * border[side] : v.IsAuto ? natural ? slice : border[side] : v.Length.Resolve(basis);
        return width > 0 ? Math.Min(width, MaxBorderImageWidth) : 0;
    }

    /// <summary>The rectangle (sx, sy, sw, sh) of the image's points as whole pixels of the image.</summary>
    private static (int X, int Y, int W, int H) PixelRect(float sx, float sy, float sw, float sh)
    {
        int x0 = (int)MathF.Round(sx / CssPixel), y0 = (int)MathF.Round(sy / CssPixel);
        return (x0, y0, (int)MathF.Round((sx + sw) / CssPixel) - x0, (int)MathF.Round((sy + sh) / CssPixel) - y0);
    }

    /// <summary>What a border image's parts are made of: the image, its cuts, and what the whole image is scaled from.</summary>
    private readonly struct BorderImageParts
    {
        private readonly GroupOp _group;
        private readonly BackgroundImage _source;
        private readonly PdfImage? _image;
        private readonly SvgImage? _svg;
        private readonly PdfImage?[]? _cuts;
        private readonly float _imageWidth, _imageHeight;
        private readonly uint _backdrop;

        public BorderImageParts(GroupOp group, BackgroundImage source, PdfImage? image, SvgImage? svg, PdfImage?[]? cuts, float imageWidth, float imageHeight, uint backdrop)
        {
            _group = group;
            _source = source;
            _image = image;
            _svg = svg;
            _cuts = cuts;
            _imageWidth = imageWidth;
            _imageHeight = imageHeight;
            _backdrop = backdrop;
        }

        /// <summary>
        /// Paints the slice (sx, sy, sw, sh) of the image, which is cut slice <paramref name="k"/>, into the region
        /// (dx, dy, dw, dh), as tiles of tw × th points laid out along each axis by its repeat keyword.
        /// </summary>
        public void Paint(int k, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh, float tw, float th,
            BorderImageRepeat repeatX, BorderImageRepeat repeatY)
        {
            if (!(sw > 0 && sh > 0 && dw > 0 && dh > 0 && tw > 0 && th > 0)) return;
            var cut = _cuts?[k];
            if (_cuts != null && cut == null) return; // less than a pixel
            if (Axis(dx, dw, tw, repeatX) is not { } ex || Axis(dy, dh, th, repeatY) is not { } ey) return;
            float kx = ex.Size / sw, ky = ey.Size / sh;
            var inner = (X: -sx * kx, Y: -sy * ky, W: _imageWidth * kx, H: _imageHeight * ky);
            Gradient? gradient = null;
            if (_source.Gradient is { } cg)
            {
                // The gradient is laid out over the whole image, then scaled and moved with it into the tile.
                gradient = cg.Resolve(_imageWidth, _imageHeight, _backdrop);
                gradient.Transform = gradient.Transform.Then(Matrix.Scale(kx, ky)).Then(Matrix.Translate(inner.X, inner.Y));
            }
            var op = new BackgroundOp
            {
                ClipX = dx, ClipY = dy, ClipW = dw, ClipH = dh,
                TileX = ex.Start, TileY = ey.Start, TileW = ex.Size, TileH = ey.Size,
                StepX = ex.Step, StepY = ey.Step,
                RepeatX = ex.Repeat, RepeatY = ey.Repeat,
                Image = cut ?? _image,
                Svg = _svg,
                Gradient = gradient,
                Inner = cut != null ? null : inner,
            };
            if (op.PaintsAnything()) _group.Ops.Add(op);
        }
    }

    /// <summary>
    /// Lays out tiles of <paramref name="size"/> along one axis of a border image region (start, length): stretched
    /// over it, repeated from its middle, rounded to a whole number of tiles, or a whole number of tiles with equal
    /// gaps around them. Null when no tile is drawn.
    /// </summary>
    private static (float Start, float Size, float Step, bool Repeat)? Axis(float start, float length, float size, BorderImageRepeat repeat)
    {
        switch (repeat)
        {
            case BorderImageRepeat.Stretch:
                return (start, length, length, false);
            case BorderImageRepeat.Round:
            {
                float n = Math.Max(1, MathF.Round(length / size));
                float t = length / n;
                return t < MinTile ? null : (start, t, t, n > 1);
            }
            case BorderImageRepeat.Space:
            {
                if (size < MinTile) return null;
                float n = MathF.Floor(length / size + SpaceCountSlack);
                if (n < 1) return null;
                float gap = Math.Max(0, (length - n * size) / (n + 1));
                return (start + gap, size, size + gap, n > 1);
            }
            default:
                if (size < MinTile) return null;
                // One tile is centred on the region, the others continue from it in both directions.
                return (start + (length - size) / 2, size, size, size < length);
        }
    }
}
