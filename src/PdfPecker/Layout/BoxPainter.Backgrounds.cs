using PdfPecker.Css;
using PdfPecker.Images;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>The background layers: an image, an SVG file or a gradient sized, positioned and repeated as a tile.</summary>
internal static partial class BoxPainter
{
    /// <summary>Tiles smaller than this are not drawn: they are invisible, and a viewer would have to repeat them millions of times.</summary>
    private const float MinTile = 0.05f;

    /// <summary>A tile count this far below a whole number still counts as that number (background-repeat: space).</summary>
    private const float SpaceCountSlack = 1e-4f;

    /// <summary>What a background layer draws: a raster image, an SVG file or neither (a gradient), with its size in points.</summary>
    private struct LayerSource
    {
        public PdfImage? Image;
        public SvgImage? Svg;
        public float Width, Height;
        /// <summary>The image keeps its aspect ratio when only one side is sized: not so for gradients and SVG files with neither a size nor a viewBox.</summary>
        public bool HasRatio;

        public readonly bool IsGradient => Image == null && Svg == null;

        /// <summary>Loads the layer's image or SVG file; false when the layer draws nothing (no gradient, or an image that cannot be shown).</summary>
        public static bool TryLoad(BackgroundImage layer, ImageLoader? images, out LayerSource source)
        {
            source = default;
            if (layer.Url == null) return layer.Gradient != null;
            source.Image = images?.Load(layer.Url);
            if (source.Image is { } image)
            {
                if (image.Width <= 0 || image.Height <= 0) return false;
                source.Width = image.Width * CssPixel;
                source.Height = image.Height * CssPixel;
                source.HasRatio = true;
                return true;
            }
            source.Svg = images?.LoadSvg(layer.Url);
            return source.Svg != null;
        }

        /// <summary>Sizes an SVG file against its positioning area; false when it has no size to draw at.</summary>
        public bool SizeSvg(float areaW, float areaH)
        {
            (Width, Height, HasRatio, _) = SvgSize(Svg!, areaW, areaH);
            return Width > 0 && Height > 0;
        }
    }

    /// <summary>
    /// The size an SVG file is drawn at in an area: its own width and height; with only an aspect ratio (a viewBox), as
    /// large as fits the area (CSS contain) or the one side it gives; otherwise the area itself on each side it does not
    /// give. Natural is true when both sides come from the file.
    /// </summary>
    private static (float W, float H, bool HasRatio, bool Natural) SvgSize(SvgImage svg, float areaW, float areaH)
    {
        if (svg.Width is { } width && svg.Height is { } height) return (width, height, true, true);
        if (svg.HasViewBox && svg.ViewBoxWidth > 0 && svg.ViewBoxHeight > 0 && areaW > 0 && areaH > 0)
        {
            float r = svg.ViewBoxHeight / svg.ViewBoxWidth;
            if (svg.Width is { } w1) return (w1, w1 * r, true, true);
            if (svg.Height is { } h1) return (h1 / r, h1, true, true);
            if (areaH / areaW > r) return (areaW, areaW * r, true, false);
            return (areaH / r, areaH, true, false);
        }
        return (svg.Width ?? areaW, svg.Height ?? areaH, false, false);
    }

    private static void PaintLayer(GroupOp group, ComputedStyle s, BackgroundImage layer, int i, float x, float y, float w, float h,
        ImageLoader? images, float basis, uint backdrop, CornerRadii? radii,
        ComputedStyle? box = null, (float X, float Y, float W, float H)? canvas = null, (float X, float Y, float W, float H)? fragment = null)
    {
        if (!LayerSource.TryLoad(layer, images, out var source)) return;
        var (ox, oy, ow, oh) = BoxOf(box ?? s, LayerValue(s.BackgroundOrigins, i, BackgroundBox.PaddingBox), x, y, w, h, basis);
        // A line fragment of an inline box paints only its own part of the box, inside its own corners.
        var (cx, cy, cw, ch) = canvas ?? Within(BoxOf(s, LayerValue(s.BackgroundClips, i, BackgroundBox.BorderBox), x, y, w, h, basis), fragment);
        var (fx, fy, fw, fh) = fragment ?? (x, y, w, h);
        if (cw <= 0 || ch <= 0) return;
        if (source.Svg != null && !source.SizeSvg(ow, oh)) return;

        var size = LayerValue(s.BackgroundSizes, i, BackgroundSize.Auto);
        var (tw, th) = TileSize(size, source, ow, oh);
        var (repeatX, repeatY) = LayerValue(s.BackgroundRepeats, i, (BackgroundRepeat.Repeat, BackgroundRepeat.Repeat));
        // round: scale the tile so that a whole number fits; an auto other dimension keeps the image's ratio.
        bool keepRatio = size.Kind == BackgroundSizeKind.Explicit && (size.Width.IsAuto || size.Height.IsAuto) && source.HasRatio;
        if (repeatX == BackgroundRepeat.Round && tw > 0 && ow > 0)
        {
            float nw = RoundedTile(ow, tw);
            if (keepRatio && repeatY != BackgroundRepeat.Round) th *= nw / tw;
            tw = nw;
        }
        if (repeatY == BackgroundRepeat.Round && th > 0 && oh > 0)
        {
            float nh = RoundedTile(oh, th);
            if (keepRatio && repeatX != BackgroundRepeat.Round) tw *= nh / th;
            th = nh;
        }
        if (!(tw >= MinTile && th >= MinTile) || float.IsInfinity(tw) || float.IsInfinity(th)) return;

        float px = ox + LayerValue(s.BackgroundPositionsX, i, BackgroundOffset.Start).Resolve(ow - tw);
        float py = oy + LayerValue(s.BackgroundPositionsY, i, BackgroundOffset.Start).Resolve(oh - th);
        float stepX = tw, stepY = th;
        bool rx = repeatX != BackgroundRepeat.NoRepeat, ry = repeatY != BackgroundRepeat.NoRepeat;
        if (repeatX == BackgroundRepeat.Space) SpaceTiles(ox, ow, tw, ref px, ref stepX, ref rx);
        if (repeatY == BackgroundRepeat.Space) SpaceTiles(oy, oh, th, ref py, ref stepY, ref ry);

        var op = new BackgroundOp
        {
            ClipX = cx, ClipY = cy, ClipW = cw, ClipH = ch,
            TileX = px, TileY = py, TileW = tw, TileH = th,
            StepX = stepX, StepY = stepY,
            RepeatX = rx, RepeatY = ry,
            Image = source.Image,
            Svg = source.Svg,
            Gradient = layer.Gradient?.Resolve(tw, th, backdrop),
            ClipRadii = canvas == null ? InnerRadii(radii, cx - fx, cy - fy, fx + fw - cx - cw, fy + fh - cy - ch) : null,
        };
        if (op.PaintsAnything()) group.Ops.Add(op);
    }

    /// <summary>
    /// The tile size of a layer in a positioning area of ow × oh: cover or contain scale the image to the area; explicit
    /// sizes resolve against it, with an auto side taken from the image's ratio, or from the image or the area when it
    /// has none. A gradient has no intrinsic size or ratio: it fills the positioning area unless sized.
    /// </summary>
    private static (float W, float H) TileSize(BackgroundSize size, in LayerSource source, float ow, float oh)
    {
        float iw = source.Width, ih = source.Height;
        if (size.Kind != BackgroundSizeKind.Explicit)
        {
            if (source.IsGradient) return (ow, oh);
            float sx = ow / iw, sy = oh / ih;
            float k = size.Kind == BackgroundSizeKind.Cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
            return (iw * k, ih * k);
        }
        bool autoW = size.Width.IsAuto, autoH = size.Height.IsAuto;
        float tw = size.Width.Resolve(ow), th = size.Height.Resolve(oh);
        if (autoW && autoH) return source.IsGradient ? (ow, oh) : (iw, ih);
        if (autoW) tw = source.HasRatio ? th * iw / ih : source.Svg != null ? iw : ow;
        else if (autoH) th = source.HasRatio ? tw * ih / iw : source.Svg != null ? ih : oh;
        return (tw, th);
    }

    /// <summary>The tile size that fits a whole number of tiles (at least one) into <paramref name="area"/> (background-repeat: round).</summary>
    private static float RoundedTile(float area, float tile) => area / Math.Max(1, MathF.Round(area / tile));

    /// <summary>
    /// background-repeat: space along one axis: as many whole tiles as fit, the first and last touching the edges of the
    /// area; one tile or less is not repeated.
    /// </summary>
    private static void SpaceTiles(float areaStart, float area, float tile, ref float position, ref float step, ref bool repeat)
    {
        int n = (int)Math.Floor(area / tile + SpaceCountSlack);
        if (n >= 2)
        {
            position = areaStart;
            step = tile + (area - n * tile) / (n - 1);
        }
        else repeat = false;
    }
}
