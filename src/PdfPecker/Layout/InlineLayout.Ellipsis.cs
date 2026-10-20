using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>text-overflow and line-clamp: cutting a line that overflows its box short with an ellipsis.</summary>
internal sealed partial class LayoutEngine
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Cuts a line that overflows its clipping box (text-overflow: ellipsis or a string): characters and inline boxes at
    /// the end are dropped until "…" (or the string), in the block's font, fits within <paramref name="room"/>. The line's items are in logical
    /// order, or in visual order when <paramref name="visual"/> is set; a right-to-left line ends on the left.
    /// </summary>
    private void Ellipsize(Line line, ComputedStyle block, float room, bool visual = false, bool rtl = false, string? mark = null)
    {
        string ellipsis = mark ?? block.TextOverflow ?? Ellipsis;
        float ew = TextWidth(block.Font, ellipsis, 0, ellipsis.Length, block.TextSize, block.LetterSpacing);
        float limit = room - ew, used = 0;
        var items = line.Items;
        byte para = items.Count > 0 ? items[0].Para : (byte)0;
        // A right-to-left line is cut at its left end: it is worked on from right to left and turned back at the end.
        bool reversed = visual && rtl;
        if (reversed) items.Reverse();
        var kept = new List<Item>(items.Count + 1);
        int k = 0;
        for (; k < items.Count; k++)
        {
            var it = items[k];
            if (used + it.Width <= limit + Eps) { kept.Add(it); used += it.Width; continue; }
            if (it.IsText && it.Length > 0)
            {
                // As many of the word's characters as fit, from the side the line starts at: in visual order, a word
                // running against the line keeps its logical end.
                bool keepEnd = visual && ((it.Level & 1) != 0) != rtl;
                int fit = FittingCharacters(it, used, limit, keepEnd, out float fw);
                if (fit > 0) { kept.Add(it.Slice(keepEnd ? it.Length - fit : 0, fit, fw)); used += fw; }
            }
            break;
        }
        // Anchors and positioned boxes on the cut part still need their positions.
        for (; k < items.Count; k++)
            if (items[k].Kind is ItemKind.Anchor or ItemKind.Absolute) kept.Add(items[k]);
        while (kept.Count > 0 && kept[^1].Kind == ItemKind.Space) { used -= kept[^1].Width; kept.RemoveAt(kept.Count - 1); }
        var dots = Rent(ItemKind.Text, block);
        dots.Tag = kept.LastOrDefault(k => k.Tag != null)?.Tag;
        dots.Source = ellipsis;
        dots.Length = ellipsis.Length;
        dots.Width = ew;
        dots.Level = dots.Para = para;
        // The ellipsis takes the place of the last visible item, after any anchors kept at the end.
        int at = kept.Count;
        while (at > 0 && kept[at - 1].Kind is ItemKind.Anchor or ItemKind.Absolute) at--;
        kept.Insert(at, dots);
        if (reversed) kept.Reverse();
        items.Clear();
        items.AddRange(kept);
        line.Width = used + ew;
    }

    /// <summary>
    /// How many characters of a word fit after <paramref name="used"/> within <paramref name="limit"/>, from its start
    /// or, with <paramref name="keepEnd"/>, from its end; <paramref name="width"/> is what they take.
    /// </summary>
    private static int FittingCharacters(Item it, float used, float limit, bool keepEnd, out float width)
    {
        var f = it.Style.Font;
        float size = it.Style.TextSize, ls = it.Style.LetterSpacing;
        int fit = 0;
        width = 0;
        if (it.Level == 0 && !keepEnd)
        {
            while (fit < it.Length)
            {
                float cw = CharAdvance(f, it.Source[it.Start + fit], size, ls);
                if (used + width + cw > limit + Eps) break;
                width += cw;
                fit++;
            }
        }
        else
        {
            // Shaped text is measured as a whole, since joining changes the widths of its letters.
            while (fit < it.Length)
            {
                int from = keepEnd ? it.Start + it.Length - fit - 1 : it.Start;
                float cw = TextWidth(f, it.Source, from, fit + 1, size, ls);
                if (used + cw > limit + Eps) break;
                width = cw;
                fit++;
            }
        }
        return fit;
    }
}
