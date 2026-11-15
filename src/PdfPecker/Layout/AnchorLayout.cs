using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// The border box of a box with an anchor-name, kept among the draw ops so that it moves wherever the box's content is
/// moved; it paints nothing.
/// </summary>
internal sealed class AnchorBoxOp : GroupOp
{
    public float X, Y, W, H;

    public AnchorBoxOp(float x, float y, float w, float h) => (X, Y, W, H) = (x, y, w, h);

    protected override void OffsetOwn(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>Anchor positioning (CSS Anchor Positioning 1): anchors, anchor(), anchor-size(), position-area and fallbacks.</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>The elements laid out with each anchor name, in the order they were first laid out.</summary>
    private Dictionary<string, List<Element>>? _anchorNames;

    /// <summary>Each anchor's boxes from its latest layout (one, or a fragment per line for an inline box).</summary>
    private Dictionary<Element, (object Pass, List<AnchorBoxOp> Boxes)>? _anchorBoxes;

    /// <summary>
    /// Notes the border box of an element that has an anchor-name, adding its marker to <paramref name="into"/>. A box
    /// laid out again (measured first, or split into fragments) starts a new <paramref name="pass"/>, which replaces
    /// the boxes of the one before.
    /// </summary>
    internal void NoteAnchor(Element e, List<DrawOp> into, object pass, float x, float y, float w, float h)
    {
        if (e.Style?.Anchor?.Names is not { } names) return;
        var op = new AnchorBoxOp(x, y, w, h);
        into.Add(op);
        _anchorBoxes ??= new Dictionary<Element, (object, List<AnchorBoxOp>)>(ReferenceEqualityComparer.Instance);
        if (!_anchorBoxes.TryGetValue(e, out var known) || !ReferenceEquals(known.Pass, pass))
        {
            bool first = known.Boxes == null;
            _anchorBoxes[e] = known = (pass, new List<AnchorBoxOp>(1));
            if (first)
            {
                _anchorNames ??= new Dictionary<string, List<Element>>(StringComparer.Ordinal);
                // Each list stays in tree order, which layout order nearly is, so the search can stop at the first fit.
                int order = TreeOrder(e);
                foreach (var name in names)
                {
                    if (!_anchorNames.TryGetValue(name, out var list)) _anchorNames[name] = list = new List<Element>(1);
                    int at = list.Count;
                    while (at > 0 && TreeOrder(list[at - 1]) > order) at--;
                    list.Insert(at, e);
                }
            }
        }
        known.Boxes.Add(op);
    }

    /// <summary>
    /// The border box of the anchor that <paramref name="name"/> names for <paramref name="query"/>, or null when none
    /// is acceptable: the last one in tree order that is inside the query's containing block element (any one when that
    /// is the initial containing block), and neither the query itself nor inside it.
    /// </summary>
    private AnchorRect? FindAnchor(Element query, string? name, Element? cbElement)
    {
        if (name == null || _anchorNames == null || !_anchorNames.TryGetValue(name, out var list)) return null;
        Element? best = null;
        for (int i = list.Count - 1; i >= 0 && best == null; i--)
        {
            var a = list[i];
            if (Inside(a, query) || cbElement != null && !Inside(a, cbElement)) continue;
            // An anchor that is itself positioned against the same containing block counts only when it comes first.
            if (a.Style is { OutOfFlow: true } && ReferenceEquals(ContainingBlockElement(a), cbElement) && TreeOrder(a) > TreeOrder(query)) continue;
            if (InsideAbsoluteBox(a, cbElement)) continue;
            best = a;
        }
        if (best == null || _anchorBoxes == null || !_anchorBoxes.TryGetValue(best, out var known) || known.Boxes.Count == 0) return null;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var b in known.Boxes)
        {
            x0 = Math.Min(x0, b.X); y0 = Math.Min(y0, b.Y);
            x1 = Math.Max(x1, b.X + b.W); y1 = Math.Max(y1, b.Y + b.H);
        }
        return new AnchorRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Whether an anchor is inside an absolutely positioned box whose containing block is the query's: that box is laid
    /// out after the query's place in the flow, so its content is not an anchor for it.
    /// </summary>
    private static bool InsideAbsoluteBox(Element anchor, Element? cbElement)
    {
        for (var c = ContainingBlockElement(anchor); c != null && !ReferenceEquals(c, cbElement);)
        {
            var next = ContainingBlockElement(c);
            if (ReferenceEquals(next, cbElement) && c.Style is { OutOfFlow: true }) return true;
            c = next;
        }
        return false;
    }

    /// <summary>Whether <paramref name="e"/> is <paramref name="ancestor"/> or inside it.</summary>
    private static bool Inside(Element e, Element ancestor)
    {
        for (Element? a = e; a != null; a = a.Parent)
            if (ReferenceEquals(a, ancestor)) return true;
        return false;
    }

    /// <summary>The element whose box is the containing block of an absolutely positioned one, or null for the initial containing block.</summary>
    private static Element? ContainingBlockElement(Element e)
    {
        for (var a = e.Parent; a != null; a = a.Parent)
            if (a.Style is { } s && (s.Positioned || s.Transform != null || s.Perspective != null || s.Filters != null)) return a;
        return null;
    }

    /// <summary>
    /// How an anchored box is placed on each axis: NaN factors and centres keep the usual placement between the insets;
    /// a factor aligns the box in the space between them (0 start, 1 end) instead of stretching it, and a centre puts
    /// the middle of the box there (anchor-center).
    /// </summary>
    private readonly record struct AnchorAlign(float FactorX, float FactorY, float CenterX, float CenterY)
    {
        public static readonly AnchorAlign None = new(float.NaN, float.NaN, float.NaN, float.NaN);
        public bool AlignsX => !float.IsNaN(FactorX) || !float.IsNaN(CenterX);
        public bool AlignsY => !float.IsNaN(FactorY) || !float.IsNaN(CenterY);
    }

    /// <summary>
    /// Lays out an absolutely positioned box whose position or size depends on an anchor: with its own placement, then
    /// with each of its position-try-fallbacks until one fits its containing block; when none does, the first.
    /// </summary>
    private void LayoutAnchored(Element e, AnchorStyle anchor, float staticX, float staticY, float cbX, float cbY, float cbW, float cbH)
    {
        // Each option lays the box out again, and with it the anchored boxes inside it: only the outer levels try
        // their fallbacks, which keeps nested ones from multiplying the work.
        int options = _anchorTries < MaxAnchorTryDepth ? 1 + Math.Min(anchor.Fallbacks?.Length ?? 0, MaxFallbacks) : 1;
        _anchorTries++;
        try
        {
            LayoutAnchoredOptions(e, anchor, options, staticX, staticY, cbX, cbY, cbW, cbH);
        }
        finally { _anchorTries--; }
    }

    /// <summary>How deep anchored boxes inside anchored boxes still try their position-try-fallbacks.</summary>
    private const int MaxAnchorTryDepth = 2;

    /// <summary>The most position-try-fallbacks options tried; a longer list is cut there.</summary>
    private const int MaxFallbacks = 16;

    private int _anchorTries;

    private void LayoutAnchoredOptions(Element e, AnchorStyle anchor, int options, float staticX, float staticY, float cbX, float cbY, float cbW, float cbH)
    {
        var original = e.Style;
        var cbElement = ContainingBlockElement(e);
        var cb = new AnchorRect(cbX, cbY, cbW, cbH);
        AnchorRect? Find(string? name) => FindAnchor(e, name ?? anchor.Default, cbElement);
        (Captured C, float X, float Y, bool Fits) Try(TryOption option)
        {
            var style = AnchoredStyle(original, anchor, option, Find, cb, out var area, out var align);
            var (ax, ay, aw, ah) = area ?? (cbX, cbY, cbW, cbH);
            e.Style = style;
            try
            {
                var c = MeasureAbsolute(e, staticX, staticY, ax, ay, aw, ah, align, out float x, out float y);
                return (c, x, y, FitsInsetArea(style, c, x, y, (ax, ay, aw, ah)));
            }
            finally { e.Style = original; }
        }

        var chosen = Try(default);
        for (int i = 1; i < options && !chosen.Fits; i++)
        {
            var next = Try(anchor.Fallbacks![i - 1]);
            if (next.Fits) chosen = next;
            // When none fits the box keeps its own placement. That was laid out before the others, which noted the
            // anchors inside it again: lay it out once more to make its own the latest.
            else if (i == options - 1) chosen = Try(default);
        }
        PlaceAbsolute(e, chosen.C, chosen.X, chosen.Y);
    }

    /// <summary>Whether the border box laid out at (x, y) stays inside the area between the insets that are set.</summary>
    private static bool FitsInsetArea(ComputedStyle s, Captured c, float x, float y, (float X, float Y, float W, float H) area)
    {
        float x0 = area.X + (s.Left.IsAuto ? 0 : s.Left.Resolve(area.W)), x1 = area.X + area.W - (s.Right.IsAuto ? 0 : s.Right.Resolve(area.W));
        float y0 = area.Y + (s.Top.IsAuto ? 0 : s.Top.Resolve(area.H)), y1 = area.Y + area.H - (s.Bottom.IsAuto ? 0 : s.Bottom.Resolve(area.H));
        const float eps = 0.01f;
        return x >= x0 - eps && x + c.BoxW <= x1 + eps && y >= y0 - eps && y + c.BoxH <= y1 + eps;
    }

    private static readonly string[] AnchorProperties =
        ["top", "right", "bottom", "left", "width", "height", "min-width", "min-height", "max-width", "max-height",
         "margin-top", "margin-right", "margin-bottom", "margin-left"];

    /// <summary>
    /// A copy of the style with the values of one option: anchor() and anchor-size() resolved, the flips applied, and
    /// with a position-area the area that becomes the containing block, its auto insets zero, and how the box aligns
    /// in it.
    /// </summary>
    private static ComputedStyle AnchoredStyle(ComputedStyle s, AnchorStyle anchor, TryOption option, Func<string?, AnchorRect?> find, AnchorRect cb,
        out (float X, float Y, float W, float H)? areaRect, out AnchorAlign align)
    {
        var c = s.Clone();
        bool flips = option.FlipBlock || option.FlipInline;
        foreach (var prop in AnchorProperties)
        {
            string source = flips ? Flipped(prop, option.FlipBlock, option.FlipInline) : prop;
            if (anchor.Functions != null && anchor.Functions.TryGetValue(source, out var fn))
            {
                var text = AnchorFunctions.Substitute(fn.Value, prop, find, cb, s.Rtl, option.FlipBlock, option.FlipInline);
                // A call without an anchor or a fallback makes the declaration invalid: the property is initial.
                Length len = InitialOf(prop);
                if (text != null && (text == "none" && prop.StartsWith("max-", StringComparison.Ordinal) || StyleResolver.TryParseLength(text, fn.Em, fn.Rem, out len, fn.Vw, fn.Vh)))
                    len = text == "none" ? Length.Auto : StyleResolver.ToLayoutUnits(len);
                SetLength(c, prop, len);
            }
            else if (!string.Equals(source, prop, StringComparison.Ordinal)) SetLength(c, prop, GetLength(s, source));
        }

        areaRect = null;
        align = AnchorAlign.None;
        var a = find(null);
        if (a is not { } box) return c;
        var area = option.Area ?? anchor.Area;
        if (option.Area == null && area is { } own && flips) area = own.Flip(option.FlipInline, option.FlipBlock);
        float fx = float.NaN, fy = float.NaN, cx = float.NaN, cy = float.NaN;
        if (area is { } ar)
        {
            // The grid of the containing block's edges and the anchor's. An anchor outside the containing block, such
            // as one on a later page than the initial containing block's first, leaves the tracks past it empty.
            ReadOnlySpan<float> xs = [cb.X, box.X, box.X + box.W, cb.X + cb.W], ys = [cb.Y, box.Y, box.Y + box.H, cb.Y + cb.H];
            float left = xs[ar.Col0], right = xs[ar.Col1 + 1], top = ys[ar.Row0], bottom = ys[ar.Row1 + 1];
            areaRect = (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            if (c.Left.IsAuto) c.Left = Length.Zero;
            if (c.Right.IsAuto) c.Right = Length.Zero;
            if (c.Top.IsAuto) c.Top = Length.Zero;
            if (c.Bottom.IsAuto) c.Bottom = Length.Zero;
            fx = AreaFactor(ar.Col0, ar.Col1, s.JustifySelf, anchor.CenterX, s.Rtl, option.FlipInline);
            fy = AreaFactor(ar.Row0, ar.Row1, s.AlignSelf, anchor.CenterY, false, option.FlipBlock);
            if (float.IsInfinity(fx)) (fx, cx) = (float.NaN, box.X + box.W / 2);
            if (float.IsInfinity(fy)) (fy, cy) = (float.NaN, box.Y + box.H / 2);
        }
        else
        {
            // anchor-center without a position-area centres the box on an axis whose insets are both auto.
            if (anchor.CenterX && c.Left.IsAuto && c.Right.IsAuto) cx = box.X + box.W / 2;
            if (anchor.CenterY && c.Top.IsAuto && c.Bottom.IsAuto) cy = box.Y + box.H / 2;
        }
        align = new AnchorAlign(fx, fy, cx, cy);
        return c;
    }

    /// <summary>
    /// Where a box goes in its position-area on one axis: 0 at the start, 1 at the end, NaN to stretch, and infinity for
    /// anchor-center. normal aligns it towards the anchor, and centres it when the area is the anchor's track alone or
    /// spans all three.
    /// </summary>
    private static float AreaFactor(int from, int to, BoxAlign self, bool anchorCenter, bool rtl, bool flip)
    {
        if (anchorCenter) return float.PositiveInfinity;
        float f;
        switch (self)
        {
            case BoxAlign.Start: f = rtl ? 1 : 0; break;
            case BoxAlign.End: f = rtl ? 0 : 1; break;
            case BoxAlign.Center: f = 0.5f; break;
            case BoxAlign.Stretch: return float.NaN;
            default:
                if (from == 0 && to == 2) return float.PositiveInfinity;
                return from == 1 && to == 1 ? 0.5f : from == 0 ? 1 : 0;
        }
        return flip ? 1 - f : f;
    }

    /// <summary>The property whose value a flip puts in <paramref name="prop"/>.</summary>
    private static string Flipped(string prop, bool block, bool inline) => prop switch
    {
        "top" when block => "bottom",
        "bottom" when block => "top",
        "left" when inline => "right",
        "right" when inline => "left",
        "margin-top" when block => "margin-bottom",
        "margin-bottom" when block => "margin-top",
        "margin-left" when inline => "margin-right",
        "margin-right" when inline => "margin-left",
        _ => prop,
    };

    private static Length InitialOf(string prop) => prop is "min-height" || prop.StartsWith("margin", StringComparison.Ordinal) ? Length.Zero : Length.Auto;

    private static Length GetLength(ComputedStyle s, string prop) => prop switch
    {
        "top" => s.Top,
        "right" => s.Right,
        "bottom" => s.Bottom,
        "left" => s.Left,
        "width" => s.Width,
        "height" => s.Height,
        "min-width" => s.MinWidth,
        "min-height" => s.MinHeight,
        "max-width" => s.MaxWidth,
        "max-height" => s.MaxHeight,
        "margin-top" => s.MarginTop,
        "margin-right" => s.MarginRight,
        "margin-bottom" => s.MarginBottom,
        _ => s.MarginLeft,
    };

    private static void SetLength(ComputedStyle s, string prop, Length v)
    {
        switch (prop)
        {
            case "top": s.Top = v; break;
            case "right": s.Right = v; break;
            case "bottom": s.Bottom = v; break;
            case "left": s.Left = v; break;
            case "width": s.Width = v; break;
            case "height": s.Height = v; break;
            case "min-width": s.MinWidth = v; break;
            case "min-height": s.MinHeight = v; break;
            case "max-width": s.MaxWidth = v; break;
            case "max-height": s.MaxHeight = v; break;
            case "margin-top": s.MarginTop = v; break;
            case "margin-right": s.MarginRight = v; break;
            case "margin-bottom": s.MarginBottom = v; break;
            default: s.MarginLeft = v; break;
        }
    }
}
