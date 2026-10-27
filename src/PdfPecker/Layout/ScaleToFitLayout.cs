using PdfPecker.Html;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// -pdfpecker-scale-to-fit: shrink. A box with a height (or max-height) whose content is taller than that, or wider
/// than the box, has its content laid out wider and drawn smaller by the same factor, the largest that makes it fit, as
/// QuestPDF's ScaleToFit does. Text therefore reflows into fewer lines before it gets smaller. The box itself, its
/// borders and background keep their size.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// How many boxes scaled to fit may be nested; deeper ones are laid out unscaled. Each level lays out its content
    /// up to <see cref="MaxFitLayouts"/> times, and the levels inside it each time, so the work multiplies per level.
    /// </summary>
    private const int MaxScaledNesting = 2;

    /// <summary>The most layouts of the content tried to find the scale: about 1% of the best scale after eight.</summary>
    private const int MaxFitLayouts = 8;

    /// <summary>The smallest scale tried: content that does not fit at a twentieth of its size is drawn at that.</summary>
    private const float MinFitScale = 0.05f;

    /// <summary>The boxes whose content is being laid out to be scaled, which inside that layout are ordinary boxes.</summary>
    private readonly HashSet<Element> _scaling = new();

    /// <summary>
    /// The content height a box scaled to fit fits its content into: its definite height, or else its max-height. Null
    /// when it has neither, while widths are measured, or when it is nested too deeply or laid out scaled already.
    /// </summary>
    private float? FitHeight(Element e, float? definiteH, float maxContentH)
    {
        if (_measuring || _scaling.Count >= MaxScaledNesting || _scaling.Contains(e)) return null;
        return definiteH ?? (float.IsPositiveInfinity(maxContentH) ? null : maxContentH);
    }

    /// <summary>
    /// Lays out the content of <paramref name="e"/> at (<paramref name="x"/>, <paramref name="y"/>), scaled down when it
    /// does not fit <paramref name="w"/> by <paramref name="fitH"/>, and returns the bottom of the drawn content.
    /// </summary>
    private float LayoutScaledToFit(Element e, float x, float y, float w, float? definiteH, float minH, float fitH)
    {
        var outerCb = _cbHeight;
        _scaling.Add(e);
        try
        {
            var natural = Lay(1);
            float reach = Reach(natural);
            Captured best = natural;
            float scale = 1;
            if (!Fits(natural, 1, reach))
            {
                // Laid out wider, content is at most as tall, so the scale that fits it as it is fits it wider too, unless
                // something in it grows with the width. Between that and 1 the best scale is searched by halving.
                float lo = 0, hi = 1;
                float guess = Math.Clamp(Math.Min(natural.Height > 0 ? fitH / natural.Height : 1, reach > w ? w / reach : 1), MinFitScale, 1);
                Captured? fitting = null;
                Captured smallest = natural;
                for (int i = 1; i < MaxFitLayouts; i++)
                {
                    var c = Lay(guess);
                    if (Fits(c, guess, Reach(c)))
                    {
                        (fitting, lo) = (c, guess);
                        scale = guess;
                    }
                    else
                    {
                        hi = guess;
                        smallest = c;
                    }
                    if (guess <= MinFitScale && fitting == null) break;
                    if (hi - lo < 0.005f) break;
                    guess = fitting == null ? Math.Max(MinFitScale, guess / 2) : (lo + hi) / 2;
                }
                if (fitting != null) best = fitting;
                else
                {
                    best = smallest;
                    scale = MinFitScale;
                }
            }

            _cbHeight = outerCb;
            if (scale == 1)
            {
                Place(best, x, y);
                return y + best.Height;
            }
            // Right to left, the content keeps to the right edge as it shrinks; otherwise to the left.
            bool rtl = e.Style.Rtl;
            float originX = rtl ? x + w : x;
            int start = _ops.Count;
            Place(best, rtl ? originX - w / scale : x, y);
            var layer = WrapLayer(start, 0, stackingContext: false);
            layer.Transform = Matrix.Scale(scale, scale);
            layer.OriginX = originX;
            layer.OriginY = y;
            _lastBaseline = float.NaN;
            _flowTrailing = default;
            float bottom = y + best.Height * scale;
            if (_paginate) _lastContentBottom = bottom;
            return bottom;
        }
        finally
        {
            _scaling.Remove(e);
            _cbHeight = outerCb;
        }

        // The content laid out as wide as the box is at this scale, with percentage heights against its scaled height.
        Captured Lay(float s)
        {
            _cbHeight = outerCb is { } cb ? new CbHeights(cb.First / s, cb.Odd / s, cb.Even / s) : null;
            return Capture(() => LayoutContent(e, 0, 0, w / s, definiteH / s, minH / s));
        }

        bool Fits(Captured c, float s, float r) => c.Height * s <= fitH + Eps && r * s <= w + Eps;

        static float Reach(Captured c) => ContentReach.Of(c.Ops) is var r && float.IsFinite(r) ? r : 0;
    }
}
