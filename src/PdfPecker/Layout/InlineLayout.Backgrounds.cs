using System.Runtime.InteropServices;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// The backgrounds, shadows, borders and outlines of inline boxes. Each line notes the fragments of the boxes on it, each with a
/// group in the draw ops behind the line's text; once all the lines of the paragraph are placed, the groups are painted
/// as Chrome paints them: with box-decoration-break: slice a background runs on from one fragment to the next as if the
/// fragments were one box, and with clone each fragment is a box of its own.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>One line fragment of an inline box: its border box, the group it is painted into and the ops of its line's text.</summary>
    private struct InlineFragment
    {
        public InlineDeco Deco;
        public float X, Y, W, H;
        public GroupOp Group;
        public List<DrawOp> Ops;
        public int TextFrom, TextTo;
    }

    /// <summary>A run of a line's items inside one inline box.</summary>
    private readonly record struct DecoRun(InlineDeco Deco, int Start, int End);

    /// <summary>How far through the fragments of one inline box painting is: their total width, and the width and number before.</summary>
    private struct FragmentProgress
    {
        public float Total, Before;
        public int Count, Seen;
    }

    /// <summary>
    /// How deep inline boxes may be nested and still paint their decorations. Each line paints a fragment for every
    /// box open on it, so deep nesting multiplies the work by the depth; templates never come close.
    /// </summary>
    internal const int MaxInlineDecoDepth = 16;

    /// <summary>How many inline box fragments one layout paints; inline boxes on later lines go unpainted.</summary>
    internal const int MaxInlineFragments = 50_000;

    /// <summary>
    /// The work the backgrounds, shadows, borders and outlines of one layout's inline fragments may spend: one unit for
    /// each background layer, border and outline, four for each shadow (drawn in bands when blurred), and one for each draw op that
    /// background-clip: text searches for the fragment's text. Past it, fragments paint only their background colour.
    /// </summary>
    internal const long MaxInlinePaintWork = 100_000;

    // The fragments of the paragraph being placed; null until a line has one.
    private List<InlineFragment>? _inlineFragments;
    private int _inlineFragmentCount;
    private long _inlinePaintWork;
    private readonly List<DecoRun> _decoRuns = new();
    private readonly List<(InlineDeco Deco, int Start)> _openDecos = new();

    /// <summary>
    /// Notes the fragments of the inline boxes on a line, the boxes around others first, each with an empty group in
    /// the draw ops; returns the index of the first, or -1 when the line has none.
    /// </summary>
    private int NoteInlineFragments(List<Item> items, float[] xs, float baseline, float lineA, float lineD)
    {
        if (!FindDecoRuns(items)) return -1;
        var runs = _decoRuns;
        // In the order the boxes are painted in: by where they start on the line, a box before the boxes inside it.
        runs.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Deco.Depth.CompareTo(b.Deco.Depth));
        var fragments = _inlineFragments ??= new List<InlineFragment>();
        int first = fragments.Count;
        foreach (var run in runs)
        {
            var deco = run.Deco;
            bool paints = deco.Paints && deco.Depth < MaxInlineDecoDepth && _inlineFragmentCount < MaxInlineFragments;
            if (!paints && !deco.HoldsAbsolutes) continue;
            var s = deco.Style;
            var it = items[run.Start];
            s.Font.LineMetrics(s.TextSize, out float asc, out float desc);
            float above = asc + s.PaddingTop.Resolve(0) + s.BorderTop, below = desc + s.PaddingBottom.Resolve(0) + s.BorderBottom;
            float fx = xs[run.Start] + it.RelX, fw = xs[run.End] - xs[run.Start];
            float fy = baseline + TextShift(s, lineA, lineD) - above + it.RelY, fh = above + below;
            if (deco.HoldsAbsolutes) NoteInlineCb(deco.Element, baseline, fx, fy, fw, fh);
            if (!paints) continue;
            _inlineFragmentCount++;
            var group = new GroupOp();
            _ops.Add(group);
            fragments.Add(new InlineFragment
            {
                Deco = deco,
                X = fx, W = fw, Y = fy, H = fh,
                Group = group, Ops = _ops,
            });
        }
        return fragments.Count > first ? first : -1;
    }

    /// <summary>
    /// Notes an empty fragment, where its absolutely positioned box is, for each inline containing block on the line that
    /// has no content of its own to make a fragment from, as Chrome gives such an element.
    /// </summary>
    private void NoteEmptyInlineCbs(List<Item> items, float[] xs, float baseline, float lineA, float lineD)
    {
        if (_measuring) return;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (it.Kind != ItemKind.Absolute || it.Deco is not { } d) continue;
            if (_inlineCbIndex != null && _inlineCbIndex.TryGetValue(d.Element, out var cb) && cb.Placed) continue;
            var s = d.Style;
            s.Font.LineMetrics(s.TextSize, out float asc, out float desc);
            float above = asc + s.PaddingTop.Resolve(0) + s.BorderTop, below = desc + s.PaddingBottom.Resolve(0) + s.BorderBottom;
            NoteInlineCb(d.Element, baseline, xs[k] + it.RelX, baseline + TextShift(s, lineA, lineD) - above + it.RelY, 0, above + below);
        }
    }

    /// <summary>
    /// The runs of a line's items inside each inline box, in <see cref="_decoRuns"/>, found in one pass that opens and
    /// closes the boxes between neighbouring items; false when there are none. Markers neither open nor close a box.
    /// </summary>
    private bool FindDecoRuns(List<Item> items)
    {
        var runs = _decoRuns;
        var open = _openDecos;
        runs.Clear();
        open.Clear();
        InlineDeco? current = null;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (it.IsMarker || it.Kind == ItemKind.Float || ReferenceEquals(it.Deco, current)) continue;
            var common = CommonDeco(current, it.Deco);
            while (open.Count > 0 && !ReferenceEquals(open[^1].Deco, common))
            {
                runs.Add(new DecoRun(open[^1].Deco, open[^1].Start, k));
                open.RemoveAt(open.Count - 1);
            }
            // The boxes from the common one inward, outermost first: added innermost first, then turned round.
            int at = open.Count;
            for (var d = it.Deco; d != null && !ReferenceEquals(d, common); d = d.Parent) open.Add((d, k));
            open.Reverse(at, open.Count - at);
            current = it.Deco;
        }
        for (int o = open.Count - 1; o >= 0; o--) runs.Add(new DecoRun(open[o].Deco, open[o].Start, items.Count));
        open.Clear();
        return runs.Count > 0;
    }

    /// <summary>
    /// The innermost inline box that both <paramref name="a"/> and <paramref name="b"/> are in, from the chain of
    /// <paramref name="a"/>, or null. Boxes are matched by their element, since the items of a ::first-line paragraph's
    /// first line come from a collection of their own.
    /// </summary>
    private static InlineDeco? CommonDeco(InlineDeco? a, InlineDeco? b)
    {
        while (a != null && b != null && !ReferenceEquals(a.Element, b.Element))
        {
            if (a.Depth >= b.Depth) a = a.Parent;
            else b = b.Parent;
        }
        return a != null && b != null ? a : null;
    }

    /// <summary>Records which draw ops hold the text of the line whose fragments start at <paramref name="first"/>.</summary>
    private void NoteFragmentText(int first, int textFrom)
    {
        var fragments = CollectionsMarshal.AsSpan(_inlineFragments);
        for (int f = first; f < fragments.Length; f++)
        {
            fragments[f].TextFrom = textFrom;
            fragments[f].TextTo = _ops.Count;
        }
    }

    /// <summary>Paints the fragments of a paragraph's inline boxes into their groups.</summary>
    private void PaintInlineFragments(List<InlineFragment> fragments)
    {
        var progress = new Dictionary<Element, FragmentProgress>(ReferenceEqualityComparer.Instance);
        foreach (var f in fragments)
        {
            ref var p = ref CollectionsMarshal.GetValueRefOrAddDefault(progress, f.Deco.Element, out _);
            p.Total += f.W;
            p.Count++;
        }
        foreach (var f in fragments)
        {
            ref var p = ref CollectionsMarshal.GetValueRefOrNullRef(progress, f.Deco.Element);
            PaintInlineFragment(f, p);
            p.Before += f.W;
            p.Seen++;
            if (p.Seen == p.Count && MergesOutline(f.Deco.Style, p)) PaintMergedOutline(fragments, f);
        }
    }

    /// <summary>Whether a box's outline goes around all its fragments as one shape rather than around each.</summary>
    private static bool MergesOutline(ComputedStyle s, in FragmentProgress p)
        => s.OutlineStyle != BorderStyle.None && p.Count is > 1 and <= BoxPainter.MaxMergedOutlineFragments;

    /// <summary>Paints the outline of the box whose last fragment is <paramref name="last"/> around all its visible fragments.</summary>
    private void PaintMergedOutline(List<InlineFragment> fragments, in InlineFragment last)
    {
        var s = last.Deco.Style;
        if (!s.Visible || _inlinePaintWork >= MaxInlinePaintWork) return;
        var rects = new List<(float X, float Y, float W, float H)>();
        CornerRadii? radii = null;
        foreach (var f in fragments)
        {
            if (f.Deco.Element != last.Deco.Element || !(f.W > 0 && f.H > 0)) continue;
            rects.Add((f.X, f.Y, f.W, f.H));
            radii ??= BoxPainter.Radii(s, f.W, f.H);
        }
        _inlinePaintWork += rects.Count;
        BoxPainter.PaintMergedOutline(last.Group, s, CollectionsMarshal.AsSpan(rects), radii);
    }

    /// <summary>
    /// Paints one fragment: its shadows, background, borders and outline. With box-decoration-break: slice its background is
    /// its part of the fragments put side by side in line order (from the right in a right-to-left box), and only the
    /// first and last fragment have the box's rounded corners on their outer side.
    /// </summary>
    private void PaintInlineFragment(in InlineFragment f, in FragmentProgress p)
    {
        var s = f.Deco.Style;
        if (!s.Visible || !(f.W > 0 && f.H > 0)) return;
        float boxX = f.X, boxW = f.W;
        bool left = true, right = true;
        if (!s.BoxDecorationClone)
        {
            bool first = p.Seen == 0, last = p.Seen == p.Count - 1;
            boxW = p.Total;
            boxX = f.X - (s.Rtl ? p.Total - p.Before - f.W : p.Before);
            left = s.Rtl ? last : first;
            right = s.Rtl ? first : last;
        }
        var group = f.Group;
        bool clipsToText = BoxPainter.ClipsToText(s);
        long work = (s.BackgroundImages?.Length ?? 0) + 4 * (s.BoxShadows?.Length ?? 0)
            + (s.OutlineStyle != BorderStyle.None ? 1 : 0) + (s.HasBorder ? 1 : 0) + (clipsToText ? f.TextTo - f.TextFrom : 0);
        if (work > 0 && _inlinePaintWork >= MaxInlinePaintWork)
        {
            // Past the budget only the colour is painted, as a plain rectangle in the fragment.
            if (!clipsToText && CssColor.IsVisible(s.BackgroundColor))
                group.Ops.Add(new RectOp(f.X, f.Y, f.W, f.H, CssColor.Fade(s.BackgroundColor, s.InlineFade)));
            return;
        }
        _inlinePaintWork += work;
        var radii = BoxPainter.FragmentRadii(s, boxW, f.H, f.W, f.H, left, right);
        if (s.BoxShadows != null) BoxPainter.PaintShadows(group, s, f.X, f.Y, f.W, f.H, radii);
        BoxPainter.PaintFragment(group, s, boxX, f.Y, boxW, f.H, (f.X, f.Y, f.W, f.H), radii, _images, s.InlineFade);
        if (clipsToText) ClipBackgroundToText(group, f.Ops, f.TextFrom, f.TextTo, f.X, f.X + f.W);
        if (s.HasBorder) BoxPainter.PaintFragmentBorders(group, s, boxX, f.Y, boxW, f.H, (f.X, f.Y, f.W, f.H), !(left && right), s.InlineFade);
        if (s.OutlineStyle != BorderStyle.None && !MergesOutline(s, p)) BoxPainter.PaintOutline(group, s, f.X, f.Y, f.W, f.H, radii);
    }
}
