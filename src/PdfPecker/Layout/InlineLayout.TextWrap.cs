using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>
/// text-wrap: balance and pretty, as Chrome lays them out. The lines are broken greedily first; then balance chooses
/// all the breaks of a paragraph of up to six lines again, and pretty those of a paragraph's last four lines when the
/// last line is a single short word or the two lines before it end in hyphens. The breaks are chosen as Knuth and
/// Plass's do, keeping the number of lines: the sum of the squares of each line's free space, plus penalties for
/// hyphens and for a single word on the last line, is made as small as it can be. For balance the last line's free
/// space counts too, which evens the lines out; when there are too few break opportunities to choose from, balance
/// instead breaks the lines at the narrowest width that keeps their number.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Chrome's limits: balance only paragraphs of up to six lines, and pretty only the last four lines of a paragraph.
    private const int MaxBalancedLines = 6, MaxPrettyLines = 4;

    // Pretty only rewraps a paragraph whose last line is shorter than a third of the line.
    private const int ShortLastLineDenominator = 3;

    // The scores, in square CSS pixels as Chrome's: an overfull line, the penalty that keeps the last break
    // opportunity from leaving one word alone on the last line, and how much more a hyphen before the last line costs.
    private const float OverfullScore = 1e12f, OrphanPenalty = 10000, LastLinePenaltyMultiplier = 4;

    // Choosing breaks costs the square of the break opportunities in a line, so lines of thousands of tiny words keep
    // their greedy breaks (or balance by width), which costs only a few more greedy passes.
    private const int MaxWrapBreaks = 512;

    // The narrowest width balance tries is 80% of the greedy lines' average width, as in Chrome.
    private const float BalanceSearchFloor = 0.8f;

    // What a planned paragraph's line breaker does at each item: break greedily, start a line, or keep to the line.
    private const byte PlanGreedy = 0, PlanBreak = 1, PlanKeep = 2;

    /// <summary>A place a line may end: the start of the next line, and where the line ends if it breaks here.</summary>
    private struct WrapBreak
    {
        public int Item;        // the item the next line starts with
        public float NoBreak;   // the position of that item, where the next line starts (CSS pixels from the paragraph's start)
        public float IfBreak;   // where the line ends if it breaks here: without trailing spaces, with a hyphen
        public float Penalty;
        public bool Hyphenated;
    }

    /// <summary>
    /// The lines of a paragraph with text-wrap: balance or pretty, from its greedy lines: the same lines when nothing
    /// changes, or new ones (the greedy ones are returned to the pool).
    /// </summary>
    private List<Line> WrapLines(List<Item> items, List<Line> lines, ComputedStyle block, float availW, LineIndent indent)
    {
        if (lines.Count < 2 || !CanRewrap(items)) return lines;
        int[]? starts = LineStarts(items, lines);
        if (starts == null) return lines;
        var pos = WrapPositions(items);
        int n = lines.Count;
        // A line that overflows its room has no better breaks, as in Chrome.
        for (int li = 0; li < n; li++)
            if (LineSpan(items, pos, starts[li], li + 1 < n ? starts[li + 1] : items.Count) > availW - indent.Of(lines, li) + Eps) return lines;

        byte[]? plan = null;
        if (block.TextWrap == TextWrapStyle.Balance)
        {
            // Only the first paragraph of the block, up to a forced break, is balanced.
            int end = 0;
            while (!lines[end].Forced) end++;
            int count = end + 1;
            if (count < 2 || count > MaxBalancedLines) return lines;
            int to = end + 1 < n ? starts[end + 1] : items.Count;
            var breaks = ChooseBreaks(items, pos, starts[0], to, count, block, availW, indent.First, indent.Wrapped, balanced: true);
            if (breaks != null) plan = Plan(plan, items.Count, starts[0], to, breaks);
            else if (count == n && !HasForcedBreak(items)) return BalanceByWidth(items, lines, availW, indent);
        }
        else
        {
            // Each paragraph between forced breaks on its own, its last lines only.
            for (int first = 0; first < n;)
            {
                int end = first;
                while (!lines[end].Forced) end++;
                int count = Math.Min(end - first + 1, MaxPrettyLines), from = end + 1 - count;
                int to = end + 1 < n ? starts[end + 1] : items.Count;
                if (count >= 2 && WorthRewrapping(items, pos, starts, from, end, to, availW))
                {
                    var breaks = ChooseBreaks(items, pos, starts[from], to, count, block, availW, indent.Of(lines, from), indent.Wrapped, balanced: false);
                    if (breaks != null) plan = Plan(plan, items.Count, starts[from], to, breaks);
                }
                first = end + 1;
            }
        }
        if (plan == null) return lines;
        var breaker = new LineBreaker(this, items, availW, indent, null, null, plan);
        breaker.Run();
        ReturnLines(lines);
        return breaker.Lines;
    }

    /// <summary>
    /// Whether the breaks can be chosen again: not beside floats or leaders, nor when word-break or line-break let
    /// words break between any two characters at the end of every line. (overflow-wrap only breaks a word too wide for
    /// a line, and such a line keeps its greedy breaks.)
    /// </summary>
    private static bool CanRewrap(List<Item> items)
    {
        foreach (var it in items)
        {
            if (it.Kind is ItemKind.Float or ItemKind.Leader) return false;
            if (it.IsText && BreaksAtLineEnd(it.Style)) return false;
        }
        return true;
    }

    private static bool HasForcedBreak(List<Item> items)
    {
        foreach (var it in items)
            if (it.Kind == ItemKind.Break) return true;
        return false;
    }

    /// <summary>
    /// The index of the item each line starts with. Null when a line starts inside an item, a word too wide for a line
    /// that overflow-wrap broke, or when a line's only word was given a hyphen.
    /// </summary>
    private static int[]? LineStarts(List<Item> items, List<Line> lines)
    {
        var starts = new int[lines.Count];
        int k = 0;
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li].Items;
            if (line.Count == 0) return null;
            while (k < items.Count && !ReferenceEquals(items[k], line[0])) k++;
            if (k == items.Count) return null;
            starts[li] = k;
        }
        return starts;
    }

    /// <summary>
    /// Where each item starts on one long line, in points, as the line breaker measures it: with the kerning between it
    /// and the item before; the last entry is the end of the last item.
    /// </summary>
    private static float[] WrapPositions(List<Item> items)
    {
        var start = new float[items.Count + 1];
        float x = 0;
        for (int k = 0; k < items.Count; k++)
        {
            if (k > 0) x += PairKern(items[k - 1], items[k]);
            start[k] = x;
            x += items[k].Width;
        }
        start[items.Count] = x;
        return start;
    }

    /// <summary>The end of a line's content when the next line starts at <paramref name="next"/>: trailing spaces hang.</summary>
    private static int ContentEnd(List<Item> items, int from, int next)
    {
        while (next > from && items[next - 1] is { Kind: ItemKind.Space } sp && (sp.Breakable || IsCollapsible(sp.Style))) next--;
        return next;
    }

    /// <summary>The width of a line from item <paramref name="from"/> up to the item the next line starts with.</summary>
    private static float LineSpan(List<Item> items, float[] pos, int from, int next) =>
        BreakEnd(items, pos, from, next) - pos[from];

    /// <summary>Where a line from <paramref name="from"/> ends when the next one starts at <paramref name="next"/>: with a hyphen after a soft hyphen.</summary>
    private static float BreakEnd(List<Item> items, float[] pos, int from, int next)
    {
        int end = ContentEnd(items, from, next);
        if (end <= from) return pos[from];
        // The end of the last item, without the kerning before the next one.
        float x = pos[end - 1] + items[end - 1].Width;
        if (next < items.Count && items[next].Kind != ItemKind.Break && items[end - 1].BreakAfter == BreakAfter.SoftHyphen) x += HyphenWidth(items[end - 1].Style);
        return x;
    }

    /// <summary>Whether a line may start at item <paramref name="k"/>: after a space that wraps, or after a hyphen or other break opportunity.</summary>
    private static bool BreaksBefore(List<Item> items, int k)
    {
        if (k == 0) return false;
        var it = items[k];
        if (it.Kind == ItemKind.Break || it.Kind == ItemKind.Space && it.Breakable) return false;
        var before = items[k - 1];
        return before.Kind == ItemKind.Space && before.Breakable || before.BreakAfter != BreakAfter.None;
    }

    /// <summary>
    /// Chrome's test of whether pretty is worth its cost for the last lines of a paragraph, lines
    /// <paramref name="from"/> to <paramref name="end"/>: the last line is shorter than a third of the line and holds a
    /// single word, or the two lines before it both end in a hyphen.
    /// </summary>
    private static bool WorthRewrapping(List<Item> items, float[] pos, int[] starts, int from, int end, int to, float availW)
    {
        int last = starts[end];
        if (LineSpan(items, pos, last, to) < availW / ShortLastLineDenominator)
        {
            bool single = true;
            for (int k = last + 1; k < to && single; k++)
                if (BreaksBefore(items, k)) single = false;
            if (single) return true;
        }
        return end - from >= 2 && EndsInHyphen(items, starts[end]) && EndsInHyphen(items, starts[end - 1]);
    }

    /// <summary>Whether the line before the one starting at <paramref name="next"/> ends at a soft hyphen or hyphenation point.</summary>
    private static bool EndsInHyphen(List<Item> items, int next) => next > 0 && items[next - 1].BreakAfter == BreakAfter.SoftHyphen;

    /// <summary>
    /// Chooses the breaks of lines that start at item <paramref name="from"/> and end before <paramref name="to"/>, as
    /// Chrome's score line breaker does: the item each line after the first starts with, or null when the best breaks
    /// do not make <paramref name="count"/> lines or there are fewer than three break opportunities to choose from.
    /// </summary>
    private static int[]? ChooseBreaks(List<Item> items, float[] pos, int from, int to, int count, ComputedStyle block,
        float availW, float firstIndent, float wrappedIndent, bool balanced)
    {
        var c = new List<WrapBreak>(16) { new() { Item = from, NoBreak = pos[from] / CssPixel, IfBreak = pos[from] / CssPixel } };
        // Chrome's penalties grow with the line's width and the font size: a hyphen costs as much as free space of the
        // square root of their product, half that in justified text, and every line twice a hyphen unless justified.
        bool justified = block.TextAlign == TextAlign.Justify;
        float widthTimesSize = availW / CssPixel * (block.FontSize / CssPixel);
        float hyphenPenalty = justified ? widthTimesSize / 2 : widthTimesSize * 2, linePenalty = justified ? 0 : hyphenPenalty * 2;
        for (int k = from + 1; k < to; k++)
        {
            if (!BreaksBefore(items, k)) continue;
            if (c.Count > MaxWrapBreaks) return null;
            bool hyphen = items[k - 1].BreakAfter == BreakAfter.SoftHyphen;
            c.Add(new WrapBreak
            {
                Item = k,
                NoBreak = pos[k] / CssPixel,
                IfBreak = BreakEnd(items, pos, from, k) / CssPixel,
                Penalty = hyphen ? hyphenPenalty : 0,
                Hyphenated = hyphen,
            });
        }
        float end = BreakEnd(items, pos, from, to) / CssPixel;
        c.Add(new WrapBreak { Item = to, NoBreak = end, IfBreak = end });
        // With the two ends counted, fewer than three break opportunities leave nothing to choose.
        if (c.Count < 5) return null;
        // A last line of one word (or one piece of a hyphenated word) is avoided.
        for (int k = c.Count - 2; k >= 0; k--)
        {
            var b = c[k];
            b.Penalty += OrphanPenalty;
            c[k] = b;
            if (!b.Hyphenated) break;
        }

        float full = (availW - wrappedIndent) / CssPixel, first = Math.Max(0, (availW - firstIndent) / CssPixel);
        var score = new float[c.Count];
        var prev = new int[c.Count];
        var line = new int[c.Count];
        int active = 0;
        for (int e = 1; e < c.Count; e++)
        {
            bool last = e == c.Count - 1;
            float best = float.MaxValue, bestHope = 0;
            int bestPrev = 0;
            int lastLine = line[active];
            float room = (lastLine == 0 ? first : full) + Eps / CssPixel;
            float startEdge = c[e].IfBreak - room;
            for (int s = active; s < e; s++)
            {
                if (line[s] != lastLine)
                {
                    lastLine = line[s];
                    float r = (lastLine == 0 ? first : full) + Eps / CssPixel;
                    if (r != room)
                    {
                        room = r;
                        startEdge = c[e].IfBreak - room;
                        bestHope = 0;
                    }
                }
                if (score[s] + bestHope >= best) continue;
                float delta = c[s].NoBreak - startEdge; // the line's free space
                float widthScore = 0, extra = 0;
                if ((last || !justified) && delta < 0) widthScore = OverfullScore;
                else if (last && !balanced) extra = LastLinePenaltyMultiplier * c[s].Penalty;
                else if (delta < 0) widthScore = OverfullScore;
                else widthScore = delta * delta;
                if (delta < 0) active = s + 1;
                else bestHope = widthScore;
                float sc = score[s] + widthScore + extra;
                if (sc <= best)
                {
                    best = sc;
                    bestPrev = s;
                }
            }
            score[e] = best + c[e].Penalty + linePenalty;
            prev[e] = bestPrev;
            line[e] = line[bestPrev] + 1;
        }
        if (line[^1] != count) return null;
        var breaks = new int[count - 1];
        for (int k = prev[^1], at = count - 2; at >= 0; k = prev[k], at--) breaks[at] = c[k].Item;
        return breaks;
    }

    /// <summary>
    /// Adds the chosen breaks of the lines from item <paramref name="from"/> to <paramref name="to"/> to the plan: a
    /// line starts at each, and nowhere else in between.
    /// </summary>
    private static byte[] Plan(byte[]? plan, int count, int from, int to, int[] breaks)
    {
        plan ??= new byte[count];
        for (int k = from + 1; k < to; k++) plan[k] = PlanKeep;
        foreach (int b in breaks) plan[b] = PlanBreak;
        return plan;
    }

    /// <summary>
    /// Chrome's other way to balance: the lines broken greedily at the narrowest width, to a CSS pixel, that still
    /// makes as many lines; they are aligned in the full width.
    /// </summary>
    private List<Line> BalanceByWidth(List<Item> items, List<Line> lines, float availW, LineIndent indent)
    {
        int n = lines.Count;
        float sum = 0;
        foreach (var l in lines) sum += l.Width;
        float lower = MathF.Round(sum / n * BalanceSearchFloor / CssPixel) * CssPixel, upper = availW;
        while (lower + CssPixel < upper)
        {
            float mid = (lower + upper) / 2;
            var trial = BreakLines(items, mid, indent);
            bool fits = trial.Count <= n;
            for (int li = 0; li < trial.Count && fits; li++)
                if (trial[li].Width > mid - indent.Of(trial, li) + Eps) fits = false;
            ReturnLines(trial);
            if (fits) upper = mid;
            else lower = mid;
        }
        if (upper >= availW) return lines;
        ReturnLines(lines);
        return BreakLines(items, upper, indent);
    }
}
