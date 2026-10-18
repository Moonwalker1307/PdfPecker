using System.Text;
using PdfPecker.Css;
using PdfPecker.Pdf;

namespace PdfPecker.Layout;

/// <summary>
/// Placing the lines of a paragraph: each line's extent, its page, its alignment and justification, and the draw ops
/// of its items (text runs, images, boxes, leaders, inline backgrounds and links).
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>Points per CSS pixel, the unit Chrome rounds half-leading and baseline shifts to.</summary>
    private const float CssPixel = 0.75f;

    // The half-leading is rounded down to whole pixels, with a little slack against rounding error.
    private const float HalfLeadingSlack = 0.001f;

    // vertical-align: middle centres an image on the baseline plus a quarter of the block's font size (about half the x-height).
    private const float MiddleRaiseRatio = 0.25f;

    // A leader's pattern keeps a gap of a quarter of the font size from the text on both sides, and must be at least
    // this wide to be repeated at all.
    private const float LeaderGapRatio = 0.25f, MinLeaderPattern = 0.01f, LeaderSlack = 0.001f;

    // A leader is never drawn longer than the largest PDF page (200 inches) nor with more copies than this: a line
    // made billions of points wide by a huge negative padding would otherwise build a string of billions of dots.
    private const float MaxLeaderLength = 14400;
    private const int MaxLeaderCopies = 1 << 16;

    private readonly StringBuilder _runText = new(256);
    private float[] _xs = new float[64];
    private float[] _lineA = new float[32], _lineD = new float[32];
    private float[] _kern = new float[16];

    // ------------------------------------------------------------------ metrics

    private static void TextMetrics(ComputedStyle s, out float a, out float d)
    {
        s.Font.LineMetrics(s.FontSize, out float asc, out float desc);
        float lineH = s.LineHeight;
        // Chrome gives the top half of the leading whole CSS pixels and the rest to the bottom.
        float half = MathF.Floor((lineH - (asc + desc)) / 2 / CssPixel + HalfLeadingSlack) * CssPixel;
        a = asc + half;
        d = lineH - a;
        float shift = BaselineShift(s);
        a -= shift;
        d += shift;
    }

    /// <summary>
    /// Downward baseline shift for sub/superscripts, as Chrome computes it from the parent's font size in whole pixels:
    /// up by a third plus one pixel, down by a fifth plus one pixel.
    /// </summary>
    private static float BaselineShift(ComputedStyle s)
    {
        if (s.VerticalAlign is not (VerticalAlign.Super or VerticalAlign.Sub)) return 0;
        int px = (int)MathF.Round(s.ParentFontSize / CssPixel);
        return s.VerticalAlign == VerticalAlign.Super ? -(px / 3 + 1) * CssPixel : (px / 5 + 1) * CssPixel;
    }

    private static void ImageMetrics(Item it, ComputedStyle block, out float a, out float d)
    {
        switch (it.Style.VerticalAlign)
        {
            case VerticalAlign.Middle:
                a = it.Height / 2 + block.FontSize * MiddleRaiseRatio;
                d = it.Height - a;
                break;
            case VerticalAlign.Top:
            case VerticalAlign.TextTop:
                TextMetrics(block, out a, out _);
                d = it.Height - a;
                if (d < 0) { a = it.Height; d = 0; }
                break;
            default:
                a = it.Height;
                d = 0;
                break;
        }
    }

    /// <summary>
    /// The kerning between neighbouring words and spaces that are drawn as one run of text (same style), which the
    /// items' own widths leave out: kern[k] is added after item k. Pairs after the last word of the line are not
    /// counted, as nothing visible follows them. Returns the total.
    /// </summary>
    private static float KernBetweenItems(List<Item> items, float[] kern)
    {
        int lastText = -1;
        for (int k = 0; k < items.Count; k++)
        {
            kern[k] = 0;
            if (items[k].IsText) lastText = k;
        }
        float total = 0;
        for (int k = 0; k < lastText; k++)
        {
            Item a = items[k], b = items[k + 1];
            if (!IsTextOrSpace(a) || !IsTextOrSpace(b)) continue;
            if (!ReferenceEquals(a.Style, b.Style) || a.Length == 0 || b.Length == 0 || a.Level != 0 || b.Level != 0) continue;
            var font = a.Style.Font;
            if (!font.HasKerning) continue;
            int units = font.KernUnits(a.Source[a.Start + a.Length - 1], b.Source[b.Start]);
            if (units == 0) continue;
            kern[k] = units * a.Style.FontSize / 1000f;
            total += kern[k];
        }
        return total;
    }

    private static bool IsTextOrSpace(Item it) => it.Kind is ItemKind.Text or ItemKind.Space;

    /// <summary>The kerning of a line's items into the shared buffer, grown as needed; returns the total.</summary>
    private float LineKerning(List<Item> items)
    {
        if (_kern.Length < items.Count) _kern = new float[items.Count * 2];
        return KernBetweenItems(items, _kern);
    }

    private static int CountOf(List<Item> items, ItemKind kind)
    {
        int count = 0;
        foreach (var it in items)
            if (it.Kind == kind) count++;
        return count;
    }

    // ------------------------------------------------------------------ placing

    private float PlaceLines(List<Line> lines, ComputedStyle block, float x, float y, float w, float indent, bool besideFloats = false)
    {
        // The backgrounds of inline boxes are painted once all their fragments are placed; a footnote laid out while
        // placing the lines has fragments of its own.
        var outerFragments = _inlineFragments;
        _inlineFragments = null;
        try
        {
            y = PlaceLinesCore(lines, block, x, y, w, indent, besideFloats);
            if (_inlineFragments != null) PaintInlineFragments(_inlineFragments);
            return y;
        }
        finally
        {
            _inlineFragments = outerFragments;
        }
    }

    private float PlaceLinesCore(List<Line> lines, ComputedStyle block, float x, float y, float w, float indent, bool besideFloats)
    {
        int n = ClampLines(lines, block, w, indent);
        var (lineA, lineD) = LineExtents(lines, n, block);
        // Beside floats the lines' positions were fixed while breaking them.
        bool[]? breakBefore = _paginate && n > 1 && !besideFloats ? PlanBreaks(n, lineA, lineD, y, block.Orphans, block.Widows) : null;
        var chunk = new LineChunk { Start = -1 };

        for (int li = 0; li < n; li++)
        {
            var line = lines[li];
            if (!float.IsNaN(line.Top) && line.Top > y) y = line.Top;
            float lineLeft = line.HasRoom ? line.Left : 0, lineW = line.HasRoom ? line.Avail : w;
            if (float.IsNaN(lineA[li]))
            {
                PlaceMarkers(line.Items, x + lineLeft, y);
                continue;
            }
            float lineH = lineA[li] + lineD[li];
            y = FitOnPage(y, lineH);
            CheckPageLimit(y);
            if (breakBefore != null && breakBefore[li])
            {
                if (_columnNotes != null) NotePlannedBreak(li, y, lineA, lineD, n);
                y = NextPage(y);
            }
            if (_columnNotes != null) NoteOrphans(ref chunk, li, y, lineA, lineD, n, block.Orphans);
            if (Footnotes != null && _paginate && !_measuring && _columnNotes == null) y = PlaceFootnoteCalls(line.Items, y, lineH);
            DrawLine(line, block, x, y, lineLeft, lineW, li == 0 ? indent : 0, lineA[li], lineH);
            y += lineH;
        }
        return y;
    }

    /// <summary>
    /// Line clamping: only the first lines are laid out, the last of them ending in an ellipsis. Returns the number of
    /// lines left.
    /// </summary>
    private int ClampLines(List<Line> lines, ComputedStyle block, float w, float indent)
    {
        int n = lines.Count;
        if (block.LineClamp <= 0 || n <= block.LineClamp) return n;
        n = block.LineClamp;
        var last = lines[n - 1];
        lines.RemoveRange(n, lines.Count - n);
        float room = (last.HasRoom ? last.Avail : w) - (n == 1 ? indent : 0);
        Ellipsize(last, block, room, mark: "…");
        last.Forced = true;
        return n;
    }

    /// <summary>The ascent and descent of each line, in the shared buffers (or copies of them when footnotes may be placed).</summary>
    private (float[] Ascents, float[] Descents) LineExtents(List<Line> lines, int n, ComputedStyle block)
    {
        if (_lineA.Length < n) { _lineA = new float[n * 2]; _lineD = new float[n * 2]; }
        float[] lineA = _lineA, lineD = _lineD;
        for (int li = 0; li < n; li++) LineExtent(lines[li], block, out lineA[li], out lineD[li]);
        // Placing a footnote lays it out, which reuses the shared line buffers: these lines keep their own copy.
        if (Footnotes != null && _paginate) { lineA = (float[])lineA.Clone(); lineD = (float[])lineD.Clone(); }
        return (lineA, lineD);
    }

    /// <summary>A line with nothing to draw still places its anchors and positioned boxes.</summary>
    private void PlaceMarkers(List<Item> items, float x, float y)
    {
        foreach (var it in items)
        {
            if (it.Kind == ItemKind.Absolute) QueueAbsolute(it.Element!, x, y);
            else if (it.Kind == ItemKind.Anchor) SetAnchor(it.AnchorId!, y);
        }
    }

    /// <summary>Where a line's items start and how its free space is spread: over its spaces (justify) or its leaders.</summary>
    private readonly record struct LineFit(float Start, float SpaceExtra, float LeaderWidth);

    /// <summary>
    /// Aligns a line in its room: its start, the extra each space gets when it is justified and the width of its leaders.
    /// An overflowing line of a clipped block with text-overflow is cut with an ellipsis first.
    /// </summary>
    private LineFit AlignLine(Line line, ComputedStyle block, float x, float lineLeft, float lineW, float lineIndent, bool rtl, int spaces)
    {
        var items = line.Items;
        float lineKern = LineKerning(items);
        float free = lineW - lineIndent - line.Width - lineKern;
        float lx = x + lineLeft + (rtl ? 0 : lineIndent);
        float extra = 0, leaderW = 0;
        int leaders = CountOf(items, ItemKind.Leader);
        // text-align-last aligns the last line and lines before a forced break; a justified block's last line
        // otherwise starts at the start.
        var align = ComputedStyle.Resolve(line.Forced && block.TextAlignLast is { } lastAlign ? lastAlign : block.TextAlign, rtl);
        // Leaders take the line's free space, so the text after them ends at the line's end.
        if (leaders > 0) { leaderW = Math.Max(0, free) / leaders; align = TextAlign.Left; }
        switch (align)
        {
            case TextAlign.Right: if (free > 0) lx += free; break;
            case TextAlign.Center: if (free > 0) lx += free / 2; break;
            case TextAlign.Justify:
                if ((!line.Forced || block.TextAlignLast == TextAlign.Justify) && spaces > 0 && free > 0) extra = free / spaces;
                else if (rtl && free > 0) lx += free;
                break;
        }

        if (block.TextOverflowEllipsis && block.ClipsX && !_measuring && line.Width + lineKern > lineW - lineIndent + Eps)
        {
            Ellipsize(line, block, lineW - lineIndent, visual: true, rtl);
            lineKern = LineKerning(items);
            free = lineW - lineIndent - line.Width - lineKern;
            // The cut line fills the box from its start, so alignment no longer moves it.
            lx = x + lineLeft + (rtl ? Math.Max(0, free) : lineIndent);
            extra = 0;
        }
        return new LineFit(lx, extra, leaderW);
    }

    /// <summary>The x of each item of the line, and after the last one, in the shared buffer.</summary>
    private float[] ItemPositions(List<Item> items, LineFit fit)
    {
        if (_xs.Length < items.Count + 1) _xs = new float[items.Count * 2];
        float[] xs = _xs;
        float cx = fit.Start;
        for (int k = 0; k < items.Count; k++)
        {
            xs[k] = cx;
            cx += items[k].Width + _kern[k] + items[k].Kind switch { ItemKind.Space => fit.SpaceExtra, ItemKind.Leader => fit.LeaderWidth, _ => 0 };
        }
        xs[items.Count] = cx;
        return xs;
    }

    /// <summary>Draws one line at <paramref name="y"/>: its items in visual order, aligned in the line's room.</summary>
    private void DrawLine(Line line, ComputedStyle block, float x, float y, float lineLeft, float lineW, float lineIndent, float ascent, float lineH)
    {
        var items = line.Items;
        int spaces = CountOf(items, ItemKind.Space);
        float baseline = y + ascent;
        // Bidirectional text: the items in visual order; a right-to-left paragraph starts at the right, where its
        // text-indent goes.
        bool rtl = ReorderLine(items, ref _lineLevels);
        var fit = AlignLine(line, block, x, lineLeft, lineW, lineIndent, rtl, spaces);
        float[] xs = ItemPositions(items, fit);

        int fragments = NoteInlineFragments(items, xs, baseline);
        EmitLinks(items, xs, y, lineH);

        // Footnote calls inside inline boxes are placed once the line is drawn, not while its buffers are in use.
        bool deferCalls = _deferFootnoteCalls;
        if (Footnotes != null) _deferFootnoteCalls = true;
        int textFrom = _ops.Count;
        DrawItems(items, xs, block, x, y, baseline, fit.SpaceExtra);
        if (fragments >= 0) NoteFragmentText(fragments, textFrom);
        _deferFootnoteCalls = deferCalls;
        if (_deferredFootnoteCalls is { Count: > 0 } && !deferCalls) PlaceDeferredFootnoteCalls();
        _lastBaseline = baseline;
        if (_marker != null) EmitMarker(baseline, y);
    }

    private void DrawItems(List<Item> items, float[] xs, ComputedStyle block, float x, float y, float baseline, float spaceExtra)
    {
        int i = 0;
        while (i < items.Count)
        {
            var it = items[i];
            switch (it.Kind)
            {
                case ItemKind.Text:
                case ItemKind.Space:
                    i = DrawTextRun(items, i, xs, y, baseline, spaceExtra);
                    continue;
                case ItemKind.Image:
                    if (it.Image != null)
                    {
                        ImageMetrics(it, block, out float ia, out _);
                        _ops.Add(new ImageOp(xs[i] + it.RelX, baseline - ia + it.RelY, it.Width, it.Height, it.Image) { Tag = it.Tag });
                    }
                    break;
                case ItemKind.Box:
                {
                    float dx = xs[i] + it.RelX, dy = baseline - it.BoxBaseline + it.RelY;
                    if (it.Capture != null) Place(it.Capture, dx, dy);
                    else
                        foreach (var op in it.BoxOps!)
                        {
                            op.Offset(dx, dy);
                            _ops.Add(op);
                        }
                    break;
                }
                case ItemKind.Absolute:
                    QueueAbsolute(it.Element!, xs[i], y);
                    break;
                case ItemKind.Anchor:
                    SetAnchor(it.AnchorId!, y);
                    break;
                case ItemKind.Leader:
                    if (it.Style.Visible) DrawLeader(it, x, xs[i] + it.RelX, xs[i + 1] + it.RelX, baseline + BaselineShift(it.Style) + it.RelY, y);
                    break;
            }
            i++;
        }
    }

    /// <summary>
    /// Draws the words and spaces from <paramref name="start"/> that share a style, tag and bidi level as one run of
    /// text, with its shadows and decorations; returns the index after the run.
    /// </summary>
    private int DrawTextRun(List<Item> items, int start, float[] xs, float lineTop, float baseline, float spaceExtra)
    {
        var first = items[start];
        var style = first.Style;
        int end = start;
        while (end < items.Count)
        {
            var r = items[end];
            if (!IsTextOrSpace(r) || !ReferenceEquals(r.Style, style) || r.Tag != first.Tag || r.Level != first.Level) break;
            end++;
        }
        // Right-to-left words are drawn as one run in logical order, which the writer lays out from the right; the
        // visual order of the items is the reverse.
        bool rtlRun = (first.Level & 1) != 0;
        var sb = _runText.Clear();
        if (rtlRun)
            for (int k = end - 1; k >= start; k--) sb.Append(items[k].Source, items[k].Start, items[k].Length);
        else
            for (int k = start; k < end; k++) sb.Append(items[k].Source, items[k].Start, items[k].Length);
        float rx = first.RelX;
        float runX = xs[start] + rx, runEnd = xs[end] + rx;
        if (style.Visible)
        {
            float bl = baseline + BaselineShift(style) + first.RelY;
            string text = sb.ToString();
            float wordSpacing = style.WordSpacing + spaceExtra;
            if (style.TextShadows != null) TextShadows(style, runX, runEnd - runX, bl, lineTop, text, wordSpacing, rtlRun);
            _ops.Add(new TextOp(runX, bl, lineTop, style.Font, style.FontSize, CssColor.Fade(style.Color, style.InlineFade), text,
                wordSpacing, style.LetterSpacing) { Tag = first.Tag, Rtl = rtlRun });
            if (style.TextDecoration != TextDecoration.None) Decorate(style, runX, runEnd - runX, bl);
        }
        return end;
    }

    /// <summary>
    /// Draws a leader's pattern between <paramref name="from"/> and <paramref name="to"/>, the copies aligned to a grid
    /// from the block's left edge so that the leaders of a table of contents line up, with room left at both ends.
    /// </summary>
    private void DrawLeader(Item it, float blockLeft, float from, float to, float baseline, float lineTop)
    {
        var style = it.Style;
        var pattern = it.Source;
        float pw = style.Font.Measure(pattern, style.FontSize) + style.LetterSpacing * pattern.Length;
        if (!(pw > MinLeaderPattern)) return;
        // A visible gap between the text and the pattern on both sides, unless the pattern is blank.
        float gap = string.IsNullOrWhiteSpace(pattern) ? 0 : style.FontSize * LeaderGapRatio;
        float start = blockLeft + MathF.Ceiling((from + gap - blockLeft) / pw - LeaderSlack) * pw;
        double copies = Math.Floor((to - gap - start) / pw + LeaderSlack);
        if (!(copies >= 1)) return;
        int count = (int)Math.Min(copies, Math.Min(MaxLeaderCopies, Math.Ceiling(MaxLeaderLength / pw)));
        var text = new StringBuilder(pattern.Length * count);
        for (int k = 0; k < count; k++) text.Append(pattern);
        _ops.Add(new TextOp(start, baseline, lineTop, style.Font, style.FontSize, CssColor.Fade(style.Color, style.InlineFade), text.ToString(),
            style.WordSpacing, style.LetterSpacing) { Tag = it.Tag });
    }

    /// <summary>One link op over each run of items with the same href.</summary>
    private void EmitLinks(List<Item> items, float[] xs, float lineTop, float lineH)
    {
        int i = 0;
        while (i < items.Count)
        {
            var href = items[i].Href;
            if (href == null) { i++; continue; }
            int start = i;
            StructElem? link = null;
            while (i < items.Count && items[i].Href == href)
            {
                link ??= items[i].Tag?.Parent.Closest("Link");
                i++;
            }
            _ops.Add(new LinkOp(xs[start], lineTop, xs[i] - xs[start], lineH, href) { Link = link });
        }
    }
}
