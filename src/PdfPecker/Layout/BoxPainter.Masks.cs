using PdfPecker.Css;
using PdfPecker.Images;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>The mask layers of mask-image, painted as background layers are.</summary>
internal static partial class BoxPainter
{
    private static readonly BackgroundBox[] BorderBoxOnly = { BackgroundBox.BorderBox };

    /// <summary>
    /// The mask layers of a box laid out on the border box (x, y, w, h), topmost first; null when every layer is none or none can be shown.
    /// A layer is sized, positioned, repeated and clipped like a background layer, except that it is positioned against
    /// the border box unless mask-origin says otherwise. A url(#id) layer is the SVG mask element
    /// <paramref name="svgMask"/> finds for it, drawn over the border box. A layer that cannot be shown, such as an
    /// image that does not load, paints nothing, but when no layer can be shown at all the box is not masked, as in
    /// Chrome.
    /// </summary>
    public static MaskLayerOps[]? PaintMask(ComputedStyle s, float x, float y, float w, float h, ImageLoader? images, float basis,
        Func<string, (SvgImage Image, bool Alpha)?>? svgMask)
    {
        var layers = s.MaskImages;
        if (layers == null || w <= 0 || h <= 0) return null;
        // A view of the style whose background lists are the mask lists, for the background painting code.
        var view = s.Clone();
        view.BackgroundImages = layers;
        view.BackgroundRepeats = s.MaskRepeats;
        view.BackgroundPositionsX = s.MaskPositionsX;
        view.BackgroundPositionsY = s.MaskPositionsY;
        view.BackgroundSizes = s.MaskSizes;
        view.BackgroundOrigins = s.MaskOrigins ?? BorderBoxOnly;
        view.BackgroundClips = s.MaskClips;
        var radii = Radii(s, w, h);
        var result = new MaskLayerOps[layers.Length];
        bool any = false;
        for (int i = 0; i < layers.Length; i++)
        {
            var mode = LayerValue(s.MaskModes, i, MaskMode.MatchSource);
            var layer = result[i] = new MaskLayerOps
            {
                Composite = LayerValue(s.MaskComposites, i, MaskComposite.Add),
                Luminance = mode == MaskMode.Luminance,
            };
            if (layers[i] is not { } image) continue;
            if (image.Url is ['#', ..] url)
            {
                // An SVG mask element: its content over the border box, in luminance unless its mask-type says alpha.
                if (svgMask?.Invoke(url) is not var (svg, alpha)) continue;
                any = true;
                var (cx, cy, cw, ch) = BoxOf(s, LayerValue(s.MaskClips, i, BackgroundBox.BorderBox), x, y, w, h, basis);
                layer.Luminance = mode == MaskMode.Luminance || mode == MaskMode.MatchSource && !alpha;
                var op = new BackgroundOp
                {
                    ClipX = cx, ClipY = cy, ClipW = cw, ClipH = ch,
                    TileX = x, TileY = y, TileW = w, TileH = h, StepX = w, StepY = h,
                    Svg = svg, ClipRadii = InnerRadii(radii, cx - x, cy - y, x + w - cx - cw, y + h - cy - ch),
                };
                if (op.PaintsAnything()) layer.Ops.Add(op);
                continue;
            }
            if (!LayerSource.TryLoad(image, images, s.ImageOrientationNone, out _)) continue;
            any = true;
            var group = new GroupOp();
            PaintLayer(group, view, image, i, x, y, w, h, images, basis, White, radii);
            layer.Ops.AddRange(group.Ops);
        }
        return any ? result : null;
    }
}
