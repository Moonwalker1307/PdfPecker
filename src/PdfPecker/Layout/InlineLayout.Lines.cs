using System.Text;
using PdfPecker.Css;
using PdfPecker.Fonts;
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
    // The extents of the inline boxes aligned to the top or bottom of the line being drawn (see AlignedSubtrees).
    private Dictionary<ComputedStyle, (float A, float D)>? _alignedSubtrees;

    // ------------------------------------------------------------------ metrics

    private static void TextMetrics(ComputedStyle s, out float a, out float d)
    {
        StrutMetrics(s, out a, out d);
        float shift = s.BaselineShift;
        a -= shift;
        d += shift;
    }

    /// <summary>
    /// The ascent and descent of a block's strut: its font and line height on the line's own baseline. The block's own
    /// vertical-align moves the block on the line it sits on, not the lines inside it.
    /// </summary>
    private static void StrutMetrics(ComputedStyle s, out float a, out float d) => s.Strut(out a, out d);

    /// <summary>
    /// How far below the line's baseline text in style <paramref name="s"/> sits, on a line whose box reaches
    /// <paramref name="lineA"/> above and <paramref name="lineD"/> below its baseline: text in an inline box aligned to
    /// the line's top or bottom moves with that box (see <see cref="AlignedShift"/>).
    /// </summary>
    private float TextShift(ComputedStyle s, float lineA, float lineD)
        => AlignedRootOf(s) is { } root ? s.BaselineShift + AlignedShift(root, lineA, lineD) : s.BaselineShift;

    /// <summary>
    /// How much further down than on the baseline the content of an inline box aligned to its line's top or bottom
    /// goes: the box and everything inside it (its aligned subtree, CSS 2.1 §10.8.1) hang from the line's top or stand
    /// on its bottom as one.
    /// </summary>
    private float AlignedShift(ComputedStyle root, float lineA, float lineD)
    {
        var (a, d) = _alignedSubtrees != null && _alignedSubtrees.TryGetValue(root, out var extent) ? extent : StrutOf(root);
        return (root.VerticalAlign == VerticalAlign.Top ? a - lineA : lineD - d) - root.BaselineShift;
    }

    private static (float A, float D) StrutOf(ComputedStyle s)
    {
        StrutMetrics(s, out float a, out float d);
        return (a, d);
    }

    /// <summary>
    /// The inline box whose vertical-align: top or bottom places content in style <paramref name="s"/>: the nearest
    /// inline box from it outward that is aligned that way, or null when the content sits on the baseline.
    /// </summary>
    private static ComputedStyle? AlignedRootOf(ComputedStyle? s)
    {
        // Deeply nested inline boxes are rare; the walk stops after a few dozen, as their content would be unreadable anyway.
        for (int depth = 0; s is { Display: Display.Inline } && depth < MaxAlignedDepth; s = s.ParentStyle, depth++)
            if (s.VerticalAlign is VerticalAlign.Top or VerticalAlign.Bottom) return s;
        return null;
    }

    private const int MaxAlignedDepth = 32;

    /// <summary>
    /// The inline box aligned to the line's top or bottom that an item is in, or null. An image or inline-block aligned
    /// that way itself is placed on its own (see <see cref="IsLineAligned"/>).
    /// </summary>
    private static ComputedStyle? AlignedRoot(Item it) => it.Kind switch
    {
        ItemKind.Text or ItemKind.Space => AlignedRootOf(it.Style),
        ItemKind.Image or ItemKind.Box when it.Style.VerticalAlign is not (VerticalAlign.Top or VerticalAlign.Bottom) => AlignedRootOf(it.Style.ParentStyle),
        _ => null,
    };

    /// <summary>
    /// The extent of each inline box on a line that is aligned to the line's top or bottom, above and below its own
    /// baseline: its strut and the content inside it, which may be taller (an inline-block, larger text). Null when
    /// the line has none.
    /// </summary>
    private static Dictionary<ComputedStyle, (float A, float D)>? AlignedSubtrees(List<Item> items, ComputedStyle block)
    {
        Dictionary<ComputedStyle, (float A, float D)>? extents = null;
        ComputedStyle? lastStyle = null, lastRoot = null;
        foreach (var it in items)
        {
            ComputedStyle? root;
            if (it.Kind is ItemKind.Text or ItemKind.Space && ReferenceEquals(it.Style, lastStyle)) root = lastRoot;
            else
            {
                root = AlignedRoot(it);
                if (it.Kind is ItemKind.Text or ItemKind.Space)
                {
                    lastStyle = it.Style;
                    lastRoot = root;
                }
            }
            if (root == null) continue;
            extents ??= new Dictionary<ComputedStyle, (float, float)>(ReferenceEqualityComparer.Instance);
            if (!extents.TryGetValue(root, out var extent)) extent = StrutOf(root);
            float a, d;
            if (it.Kind is ItemKind.Image or ItemKind.Box) AtomicMetrics(it, block, out a, out d);
            else TextMetrics(it.Style, out a, out d);
            // Measured on the line's baseline, where the root's baseline is its shift below it.
            float shift = root.BaselineShift;
            extents[root] = (Math.Max(extent.A, a + shift), Math.Max(extent.D, d - shift));
        }
        return extents;
    }

    /// <summary>
    /// The ascent and descent of an image or inline-block on its line's baseline. One aligned to the line's top or
    /// bottom has none of its own (see <see cref="IsLineAligned"/>); text-top hangs it from the top of the block's strut.
    /// </summary>
    private static void AtomicMetrics(Item it, ComputedStyle block, out float a, out float d)
    {
        float baseline = it.Kind == ItemKind.Box ? it.BoxBaseline : it.Height;
        switch (it.Style.VerticalAlign)
        {
            case VerticalAlign.Middle:
                a = it.Height / 2 + block.FontSize * MiddleRaiseRatio;
                d = it.Height - a;
                break;
            case VerticalAlign.TextTop:
                StrutMetrics(block, out a, out _);
                d = it.Height - a;
                if (d < 0) { a = it.Height; d = 0; }
                break;
            default:
                float shift = it.Style.AtomicBaselineShift;
                a = baseline - shift;
                d = it.Height - baseline + shift;
                break;
        }
    }

    /// <summary>Whether an item is aligned to its line's top or bottom edge rather than to the baseline.</summary>
    private static bool IsLineAligned(Item it) => it.Kind switch
    {
        ItemKind.Image or ItemKind.Box when it.Style.VerticalAlign is VerticalAlign.Top or VerticalAlign.Bottom => true,
        ItemKind.Image or ItemKind.Box or ItemKind.Text or ItemKind.Space => AlignedRoot(it) != null,
        _ => false,
    };

    /// <summary>
    /// How far above the baseline the top of an image or inline-block is drawn on a line whose box reaches
    /// <paramref name="lineA"/> above and <paramref name="lineD"/> below its baseline.
    /// </summary>
    private float AtomicAscent(Item it, ComputedStyle block, float lineA, float lineD) => it.Style.VerticalAlign switch
    {
        VerticalAlign.Top => lineA,
        VerticalAlign.Bottom => it.Height - lineD,
        _ => AtomicAscentOnBaseline(it, block) - (AlignedRoot(it) is { } root ? AlignedShift(root, lineA, lineD) : 0),
    };

    private static float AtomicAscentOnBaseline(Item it, ComputedStyle block)
    {
        AtomicMetrics(it, block, out float a, out _);
        return a;
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
        // The words of a line mostly share a style, whose font is found once.
        ComputedStyle? style = null;
        TextFont? font = null;
        for (int k = 0; k < lastText; k++)
        {
            kern[k] = PairKern(items[k], items[k + 1], ref style, ref font);
            total += kern[k];
        }
        return total;
    }

    /// <summary>
    /// The kerning between two neighbouring items when they are drawn as one run of text (same style, left to right),
    /// which neither item's own width holds.
    /// </summary>
    private static float PairKern(Item a, Item b)
    {
        ComputedStyle? style = null;
        TextFont? font = null;
        return PairKern(a, b, ref style, ref font);
    }

    /// <summary>
    /// <see cref="PairKern(Item, Item)"/>, with the font of the last style asked about, which the caller keeps for the
    /// next pair.
    /// </summary>
    private static float PairKern(Item a, Item b, ref ComputedStyle? style, ref TextFont? font)
    {
        if (!IsTextOrSpace(a) || !IsTextOrSpace(b)) return 0;
        if (!ReferenceEquals(a.Style, b.Style) || a.Length == 0 || b.Length == 0 || a.Level != 0 || b.Level != 0) return 0;
        if (!ReferenceEquals(a.Style, style))
        {
            style = a.Style;
            font = style.Font;
        }
        if (!font!.HasKerning) return 0;
        int units = font.KernUnits(a.Source[a.Start + a.Length - 1], b.Source[b.Start]);
        return units == 0 ? 0 : units * a.Style.TextSize / 1000f;
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
            if (_paginate) _lastContentBottom = y;
        }
        return y;
    }

    /// <summary>
    /// Line clamping: only the first lines are laid out, the last of them ending in an ellipsis or the block-ellipsis
    /// string (nothing for none). Returns the number of lines left.
    /// </summary>
    private int ClampLines(List<Line> lines, ComputedStyle block, float w, float indent)
    {
        int n = lines.Count;
        if (block.LineClamp <= 0 || n <= block.LineClamp) return n;
        n = block.LineClamp;
        var last = lines[n - 1];
        lines.RemoveRange(n, lines.Count - n);
        float room = (last.HasRoom ? last.Avail : w) - (n == 1 ? indent : 0);
        Ellipsize(last, block, room, mark: block.BlockEllipsis ?? Ellipsis);
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

    /// <summary>
    /// Where a line's items start and how its free space is spread: over its spaces (justify), its leaders, or its
    /// justification opportunities (CharExtra each, set when they are not just its spaces; the items say where).
    /// </summary>
    private readonly record struct LineFit(float Start, float SpaceExtra, float LeaderWidth, float CharExtra = 0);

    /// <summary>
    /// Aligns a line in its room: its start, the extra each space gets when it is justified and the width of its leaders.
    /// An overflowing line of a clipped block with text-overflow is cut with an ellipsis first.
    /// </summary>
    private LineFit AlignLine(Line line, ComputedStyle block, float x, float lineLeft, float lineW, float lineIndent, bool rtl, int spaces)
    {
        var items = line.Items;
        int leaders = CountOf(items, ItemKind.Leader);
        // text-align-last aligns the last line and lines before a forced break; a justified block's last line
        // otherwise starts at the start.
        var align = ComputedStyle.Resolve(line.Forced && block.TextAlignLast is { } lastAlign ? lastAlign : block.TextAlign, rtl);
        bool justify = align == TextAlign.Justify && leaders == 0 && (!line.Forced || block.TextAlignLast == TextAlign.Justify);
        float lineKern = LineKerning(items);
        float free = lineW - lineIndent - line.Width - lineKern;
        int opportunities = -1;
        if (justify && free > 0)
        {
            // Finding the opportunities may split words into pieces. Together with the kerning between them they are
            // as wide as the words, so only the kerning of each item is found again, when there are pieces.
            _justifySplit = false;
            opportunities = JustificationOpportunities(line, block.TextJustify, rtl);
            if (_justifySplit) LineKerning(items);
        }
        float lx = x + lineLeft + (rtl ? 0 : lineIndent);
        float extra = 0, charExtra = 0, leaderW = 0;
        // Leaders take the line's free space, so the text after them ends at the line's end.
        if (leaders > 0) { leaderW = Math.Max(0, free) / leaders; align = TextAlign.Left; }
        switch (align)
        {
            case TextAlign.Right: if (free > 0) lx += free; break;
            case TextAlign.Center: if (free > 0) lx += free / 2; break;
            case TextAlign.Justify:
                if (justify && opportunities > 0 && free > 0) extra = charExtra = free / opportunities;
                else if (justify && opportunities < 0 && spaces > 0 && free > 0) extra = free / spaces;
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
            extra = charExtra = 0;
        }
        return new LineFit(lx, extra, leaderW, charExtra);
    }

    /// <summary>The x of each item of the line, and after the last one, in the shared buffer.</summary>
    private float[] ItemPositions(List<Item> items, LineFit fit)
    {
        if (_xs.Length < items.Count + 1) _xs = new float[items.Count * 2];
        float[] xs = _xs;
        float cx = fit.Start, e = fit.CharExtra;
        for (int k = 0; k < items.Count; k++)
        {
            if (e != 0)
            {
                var it = items[k];
                byte j = it.Justify;
                if ((j & JustifyBefore) != 0) cx += e;
                xs[k] = cx;
                cx += it.Width + _kern[k] + e * (((j & JustifyLetters) != 0 ? it.Length - 1 : 0) + ((j & JustifyAfter) != 0 ? 1 : 0));
                continue;
            }
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
        NoteLineReach(items, xs, x + lineLeft + lineW);

        _alignedSubtrees = AlignedSubtrees(items, block);
        int fragments = NoteInlineFragments(items, xs, baseline, ascent, lineH - ascent);
        EmitLinks(items, xs, y, lineH);

        // Footnote calls inside inline boxes are placed once the line is drawn, not while its buffers are in use.
        bool deferCalls = _deferFootnoteCalls;
        if (Footnotes != null) _deferFootnoteCalls = true;
        int textFrom = _ops.Count;
        DrawItems(items, xs, block, x, y, baseline, y + lineH - baseline, fit.SpaceExtra, fit.CharExtra);
        if (fragments >= 0) NoteFragmentText(fragments, textFrom);
        _deferFootnoteCalls = deferCalls;
        if (_deferredFootnoteCalls is { Count: > 0 } && !deferCalls) PlaceDeferredFootnoteCalls();
        _lastBaseline = baseline;
        if (_marker != null) EmitMarker(baseline, y);
    }

    /// <summary>
    /// A line whose content is wider than its room, such as a long word or text that may not wrap, marks how far it
    /// reaches with an empty group: that counts toward shrinking the page to fit, as Chrome does. A trailing space hangs
    /// and does not count.
    /// </summary>
    private void NoteLineReach(List<Item> items, float[] xs, float roomRight)
    {
        int last = items.Count - 1;
        while (last >= 0 && items[last].Kind == ItemKind.Space) last--;
        if (last >= 0 && xs[last + 1] > roomRight + 0.01f) _ops.Add(new GroupOp { Reach = xs[last + 1] });
    }

    private void DrawItems(List<Item> items, float[] xs, ComputedStyle block, float x, float y, float baseline, float descent, float spaceExtra, float charExtra)
    {
        int i = 0;
        while (i < items.Count)
        {
            var it = items[i];
            switch (it.Kind)
            {
                case ItemKind.Text:
                case ItemKind.Space:
                    i = DrawTextRun(items, i, xs, y, baseline, descent, spaceExtra, charExtra);
                    continue;
                case ItemKind.Image:
                    if (it.Image != null)
                    {
                        float ia = AtomicAscent(it, block, baseline - y, descent);
                        _ops.Add(new ImageOp(xs[i] + it.RelX, baseline - ia + it.RelY, it.Width, it.Height, it.Image) { Tag = it.Tag });
                    }
                    if (it.PageExcess > 0) _ops.Add(new GroupOp { Reach = xs[i] + it.RelX + it.Width + it.PageExcess });
                    break;
                case ItemKind.Box:
                {
                    float dx = xs[i] + it.RelX, dy = baseline - AtomicAscent(it, block, baseline - y, descent) + it.RelY;
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
                    if (it.Style.Visible) DrawLeader(it, x, xs[i] + it.RelX, xs[i + 1] + it.RelX, baseline + it.Style.BaselineShift + it.RelY, y);
                    break;
            }
            i++;
        }
    }

    /// <summary>
    /// Draws the words and spaces from <paramref name="start"/> that share a style, tag and bidi level as one run of
    /// text, with its shadows and decorations; returns the index after the run. On a line justified between characters
    /// (<paramref name="charExtra"/>), a run of words spaced out letter by letter draws with that extra letter spacing,
    /// and the run ends where the line puts the next item somewhere else than the text would.
    /// </summary>
    private int DrawTextRun(List<Item> items, int start, float[] xs, float lineTop, float baseline, float descent, float spaceExtra, float charExtra)
    {
        var first = items[start];
        var style = first.Style;
        float letterExtra = charExtra != 0 && (first.Justify & JustifyLetters) != 0 ? charExtra : 0;
        int end = start;
        while (end < items.Count)
        {
            var r = items[end];
            if (!IsTextOrSpace(r) || !ReferenceEquals(r.Style, style) || r.Tag != first.Tag || r.Level != first.Level) break;
            // Trimmed punctuation is drawn closer to its neighbour than the font would put it, so the run ends there.
            if (end > start && (items[end - 1].TrimEnd != 0 || r.TrimStart != 0)) break;
            if (charExtra != 0 && end > start && !JoinsRun(items, end - 1, xs, spaceExtra, letterExtra)) break;
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
        // An opening bracket trimmed on its left is drawn that much further left, into the space it gave up.
        float textX = runX - (rtlRun ? 0 : first.TrimStart);
        if (style.Visible)
        {
            float bl = baseline + TextShift(style, baseline - lineTop, descent) + first.RelY;
            string text = sb.ToString();
            float wordSpacing = style.WordSpacing + spaceExtra - letterExtra, letterSpacing = style.LetterSpacing + letterExtra;
            if (style.TextShadows != null) TextShadows(style, textX, runEnd - textX, bl, lineTop, text, wordSpacing, letterSpacing, rtlRun);
            var op = new TextOp(textX, bl, lineTop, style.Font, style.TextSize, CssColor.Fade(style.TextFillColor ?? style.Color, style.InlineFade), text,
                wordSpacing, letterSpacing) { Tag = first.Tag, Rtl = rtlRun }.WithStroke(style);
            _ops.Add(op);
            if (style.TextDecoration != TextDecoration.None) Decorate(style, runX, runEnd - runX, bl, text: op);
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
        float pw = style.Font.Measure(pattern, style.TextSize) + style.LetterSpacing * pattern.Length;
        if (!(pw > MinLeaderPattern)) return;
        // A visible gap between the text and the pattern on both sides, unless the pattern is blank.
        float gap = string.IsNullOrWhiteSpace(pattern) ? 0 : style.TextSize * LeaderGapRatio;
        float start = blockLeft + MathF.Ceiling((from + gap - blockLeft) / pw - LeaderSlack) * pw;
        double copies = Math.Floor((to - gap - start) / pw + LeaderSlack);
        if (!(copies >= 1)) return;
        int count = (int)Math.Min(copies, Math.Min(MaxLeaderCopies, Math.Ceiling(MaxLeaderLength / pw)));
        var text = new StringBuilder(pattern.Length * count);
        for (int k = 0; k < count; k++) text.Append(pattern);
        _ops.Add(new TextOp(start, baseline, lineTop, style.Font, style.TextSize, CssColor.Fade(style.TextFillColor ?? style.Color, style.InlineFade), text.ToString(),
            style.WordSpacing, style.LetterSpacing) { Tag = it.Tag }.WithStroke(style));
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
