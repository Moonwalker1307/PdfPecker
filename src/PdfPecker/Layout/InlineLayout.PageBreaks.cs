using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>Orphans and widows: which lines of a paragraph start a new page so that neither is left alone.</summary>
internal sealed partial class LayoutEngine
{
    // Each forced break can move the lines after it onto other pages, so the plan is made again until it settles, at
    // most this many times.
    private const int MaxBreakPlans = 8;

    /// <summary>
    /// Chooses the lines of a paragraph that start a new page so that at least <paramref name="orphans"/> lines stay at the
    /// bottom of a page and at least <paramref name="widows"/> lines go to the top of the next. Returns null when the
    /// natural breaks already satisfy both.
    /// </summary>
    private bool[]? PlanBreaks(int n, float[] lineA, float[] lineD, float top, int orphans, int widows)
    {
        if (orphans <= 1 && widows <= 1) return null;
        int visibleCount = 0;
        float total = 0;
        for (int li = 0; li < n; li++)
            if (!float.IsNaN(lineA[li])) { visibleCount++; total += lineA[li] + lineD[li]; }
        if (visibleCount < 2 || !CrossesPage(top, top + total)) return null;

        bool[]? forced = null;
        // The plan only tries breaks: a multi-column box balancing its columns must not take them for real ones.
        var notes = _columnNotes;
        bool paused = notes is { Paused: true };
        if (notes != null) notes.Paused = true;
        try
        {
            for (int attempt = 0; attempt < MaxBreakPlans; attempt++)
            {
                if (!ForceOneBreak(n, lineA, lineD, top, orphans, widows, visibleCount, ref forced)) break;
            }
        }
        finally
        {
            if (notes != null) notes.Paused = paused;
        }
        return forced;
    }

    /// <summary>
    /// Walks the lines through the pages with the breaks planned so far and forces one more where a page break
    /// leaves too few lines behind or ahead. Returns whether a break was added.
    /// </summary>
    private bool ForceOneBreak(int n, float[] lineA, float[] lineD, float top, int orphans, int widows, int visibleCount, ref bool[]? forced)
    {
        float y = top;
        int chunkStart = -1, chunkLines = 0, seen = 0, chunkPage = 0;
        for (int li = 0; li < n; li++)
        {
            if (float.IsNaN(lineA[li])) continue;
            float h = lineA[li] + lineD[li];
            float fy = FitOnPage(y, h);
            if (forced != null && forced[li]) fy = NextPage(fy);
            if (chunkStart >= 0 && PageOf(fy) > chunkPage)
            {
                // A page break before line li: chunkLines lines stay behind, visibleCount - seen lines move on.
                // Only the paragraph's first chunk can move as a whole: later chunks already start at a page top.
                bool canMoveChunk = chunkLines == seen && !AtPageTop(top);
                int after = visibleCount - seen;
                if (chunkLines < orphans && canMoveChunk)
                {
                    (forced ??= new bool[n])[chunkStart] = true;
                    return true;
                }
                if (after < widows)
                {
                    int need = widows - after, keep = chunkLines - need;
                    if (keep >= orphans || keep <= 0 && canMoveChunk)
                    {
                        int target = keep <= 0 ? chunkStart : NthVisible(lineA, n, chunkStart, keep);
                        if (target >= 0 && !(forced?[target] ?? false))
                        {
                            (forced ??= new bool[n])[target] = true;
                            return true;
                        }
                    }
                }
                chunkStart = -1;
            }
            if (chunkStart < 0) { chunkStart = li; chunkLines = 0; chunkPage = PageOf(fy); }
            chunkLines++;
            seen++;
            y = fy + h;
        }
        return false;
    }

    /// <summary>The lines that start a new page to keep orphans and widows, as PlaceLines will choose them; null for none.</summary>
    private bool[]? PlanLineBreaks(List<Line> lines, ComputedStyle block, float top)
    {
        int n = lines.Count;
        if (n < 2 || block.LineClamp > 0) return null;
        var a = new float[n];
        var d = new float[n];
        for (int li = 0; li < n; li++) LineExtent(lines[li], block, out a[li], out d[li]);
        return PlanBreaks(n, a, d, top, block.Orphans, block.Widows);
    }

    private static bool SamePlan(bool[]? a, bool[]? b)
    {
        int n = Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        for (int i = 0; i < n; i++)
            if ((a != null && i < a.Length && a[i]) != (b != null && i < b.Length && b[i])) return false;
        return true;
    }

    /// <summary>The index of the visible line <paramref name="k"/> lines after visible line <paramref name="from"/>.</summary>
    private static int NthVisible(float[] lineA, int n, int from, int k)
    {
        for (int li = from + 1; li < n; li++)
            if (!float.IsNaN(lineA[li]) && --k == 0) return li;
        return -1;
    }
}
