using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Text;

namespace PdfPecker.Layout;

/// <summary>
/// text-spacing-trim: fullwidth CJK punctuation sheds the blank half of its em next to other punctuation, as Chrome's
/// HanKerning applies the font's halt feature. An opening bracket after an opening bracket, a middle dot or a closing
/// bracket loses its left half; a closing bracket or dot before a closing bracket or a middle dot loses its right half.
/// With normal (the initial value) a closing character at the end of a line also loses its right half when that makes
/// it fit, space-first trims the opening character that starts every wrapped line, and trim-start the one that starts
/// any line. Only fonts with the halt feature trim, and only by what that feature takes off.
/// </summary>
internal sealed partial class LayoutEngine
{
    // A trim found between the characters of a paragraph's items: the item, the character's offset in it, whether its
    // left (start) or right (end) half goes, and by how much.
    private readonly record struct PunctuationTrim(int Item, int Offset, bool Start, float Amount);

    private readonly List<PunctuationTrim> _trims = new();
    private readonly List<Item> _trimmedItems = new();

    // OpenType features that change the widths of CJK punctuation themselves: with one of them on, Chrome leaves the
    // punctuation alone.
    private static readonly string[] ExclusiveFeatures = ["halt", "hwid", "palt", "pwid", "qwid", "twid", "valt", "vhal", "vpal"];

    /// <summary>
    /// Trims the punctuation of a paragraph's items where two meet. Characters next to each other in the text count as
    /// neighbours across inline elements, but not across spaces, breaks or inline boxes. A trimmed character ends up at
    /// the edge of an item: words are split around it, with no break opportunity between the pieces.
    /// </summary>
    private void TrimAdjacentPunctuation(List<Item> items)
    {
        if (!MayTrimPunctuation(items)) return;
        var trims = _trims;
        trims.Clear();
        var last = TextSpacingClass.Other;
        int lastItem = -1, lastOffset = 0;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (it.Kind is ItemKind.Spacer or ItemKind.Anchor) continue; // the edges of inline elements are not characters
            if (it.Kind != ItemKind.Text)
            {
                last = TextSpacingClass.Other;
                continue;
            }
            var font = it.Style.Font;
            string s = it.Source;
            for (int i = 0; i < it.Length;)
            {
                int cp = FirstCodePoint(s, it.Start + i), units = cp > 0xFFFF ? 2 : 1;
                var type = HanKerning.Classify(font, cp);
                if (ShouldTrimLast(type, last))
                {
                    var before = items[lastItem];
                    float amount = TrimOf(before, lastOffset);
                    if (amount > 0) trims.Add(new PunctuationTrim(lastItem, lastOffset, Start: false, amount));
                }
                else if (ShouldTrim(type, last) && TrimOf(it, i) is var amount and > 0)
                    trims.Add(new PunctuationTrim(k, i, Start: true, amount));
                last = type;
                lastItem = k;
                lastOffset = i;
                i += units;
            }
        }
        if (trims.Count > 0) SplitAtTrims(items, trims);
    }

    /// <summary>
    /// A text item that starts a line, with its opening punctuation trimmed as text-spacing-trim says: on a wrapped line
    /// with space-first or trim-start, on the first line of a paragraph or after a forced break only with trim-start.
    /// Without a neighbour before it on the line, a trim it had from one goes.
    /// </summary>
    private static Item TrimLineStart(Item it, bool wrapped)
    {
        var style = it.Style;
        bool trim = wrapped ? style.TextSpacingTrim is TextSpacingTrim.SpaceFirst or TextSpacingTrim.TrimStart : style.TextSpacingTrim == TextSpacingTrim.TrimStart;
        int cp = FirstCodePoint(it.Source, it.Start);
        // An opening bracket or quote, as in Chrome, and one the font draws in the right half of its em.
        float start = trim && TextSpacingCharacters.Classify(cp) is TextSpacingClass.Open or TextSpacingClass.OpenQuote
            && HanKerning.Classify(style.Font, cp) == TextSpacingClass.Open ? TrimOf(it, 0) : 0;
        return start == it.TrimStart ? it : it.Trimmed(start, it.TrimEnd);
    }

    /// <summary>Whether any text item has a character that may be fullwidth punctuation, which is rare outside CJK text.</summary>
    private static bool MayTrimPunctuation(List<Item> items)
    {
        foreach (var it in items)
        {
            if (it.Kind != ItemKind.Text) continue;
            // Most text has no character from U+2018 up, which a vectorised search rules out quickly.
            var span = it.Source.AsSpan(it.Start, it.Length);
            int i;
            while ((i = span.IndexOfAnyInRange('\u2018', '\uffff')) >= 0)
            {
                if (TextSpacingCharacters.MayBeOpenOrClose(span[i])) return true;
                span = span.Slice(i + 1);
            }
        }
        return false;
    }

    /// <summary>An opening character loses its left half after an opening, middle or closing one (Chrome's ShouldKern).</summary>
    private static bool ShouldTrim(TextSpacingClass type, TextSpacingClass last)
        => type == TextSpacingClass.Open && last is TextSpacingClass.Open or TextSpacingClass.Middle or TextSpacingClass.Close or TextSpacingClass.OpenNarrow;

    /// <summary>A closing character loses its right half before a closing or middle one (Chrome's ShouldKernLast).</summary>
    private static bool ShouldTrimLast(TextSpacingClass type, TextSpacingClass last)
        => last == TextSpacingClass.Close && type is TextSpacingClass.Close or TextSpacingClass.Middle or TextSpacingClass.CloseNarrow;

    /// <summary>
    /// What the character at <paramref name="offset"/> of a text item may lose, in points: 0 when its style keeps the
    /// space (space-all, or a feature that sets the widths of punctuation itself) or its font has no trim for it.
    /// </summary>
    private static float TrimOf(Item it, int offset)
    {
        var style = it.Style;
        if (style.TextSpacingTrim == TextSpacingTrim.SpaceAll || HasExclusiveFeature(style)) return 0;
        return HanKerning.TrimUnits(style.Font, FirstCodePoint(it.Source, it.Start + offset)) * style.TextSize / 1000f;
    }

    private static bool HasExclusiveFeature(ComputedStyle style)
    {
        if (style.FontFeatureSettings == null && style.FontVariantEastAsian == null) return false;
        if (FontFeatures.Key(null, style.FontFeatureSettings, style.FontVariantEastAsian) is not { } key) return false;
        foreach (var feature in key.Split(','))
            if (Array.IndexOf(ExclusiveFeatures, feature.Length > 4 ? feature[..4] : feature) >= 0) return true;
        return false;
    }

    /// <summary>
    /// Replaces each item with trims by pieces that have their trimmed characters at their edges: a trimmed opening
    /// character starts a piece and a trimmed closing one ends one. Only the last piece keeps the break after the item.
    /// </summary>
    private void SplitAtTrims(List<Item> items, List<PunctuationTrim> trims)
    {
        var result = _trimmedItems;
        result.Clear();
        int t = 0;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            int from = 0;
            float start = 0;
            for (; t < trims.Count && trims[t].Item == k; t++)
            {
                var trim = trims[t];
                if (trim.Start)
                {
                    if (trim.Offset > from) AddPiece(result, it, from, trim.Offset, start, 0);
                    from = trim.Offset;
                    start = trim.Amount;
                }
                else
                {
                    int cut = trim.Offset + (char.IsHighSurrogate(it.Source[it.Start + trim.Offset]) ? 2 : 1);
                    AddPiece(result, it, from, cut, start, trim.Amount);
                    from = cut;
                    start = 0;
                }
            }
            if (from < it.Length || from == 0) AddPiece(result, it, from, it.Length, start, 0);
        }
        items.Clear();
        items.AddRange(result);
        result.Clear();
    }

    /// <summary>Adds the characters of a text item from <paramref name="from"/> to <paramref name="to"/> as a piece with these trims.</summary>
    private static void AddPiece(List<Item> into, Item it, int from, int to, float start, float end)
    {
        if (from == 0 && to == it.Length)
        {
            into.Add(start == it.TrimStart && end == it.TrimEnd ? it : it.Trimmed(start, end));
            return;
        }
        var style = it.Style;
        int length = to - from;
        var piece = it.Slice(from, length, TextWidth(style.Font, it.Source, it.Start + from, length, style.TextSize, style.LetterSpacing));
        if (to < it.Length) piece.BreakAfter = BreakAfter.None;
        into.Add(piece.Trimmed(start, end));
    }
}
