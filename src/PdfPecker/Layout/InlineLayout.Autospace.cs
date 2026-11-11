using System.Globalization;
using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>
/// text-autospace: an eighth of an em between an ideograph (a Chinese, Japanese or Korean letter) and a letter of
/// another script or a digit next to it, as Chrome adds it with text-autospace: normal. The space goes before the
/// character after the boundary, as a start trim of minus that amount, so it is dropped where that character starts a
/// line (see <see cref="TrimLineStart"/>). Characters count as neighbours across inline elements, not across spaces,
/// breaks, punctuation or inline boxes, and the space is an eighth of the smaller of the two fonts.
/// </summary>
internal sealed partial class InlineLayout
{
    private enum AutospaceClass : byte { Other, Ideograph, Alpha, Numeric }

    private readonly List<(int Item, int Offset, float Amount)> _autospaces = new(); // in the order of the text

    private void Autospace(List<Item> items)
    {
        if (!MayAutospace(items)) return;
        var spaces = _autospaces;
        spaces.Clear();
        var last = AutospaceClass.Other;
        ComputedStyle? lastStyle = null;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (it.Kind is ItemKind.Spacer or ItemKind.Anchor) continue; // the edges of inline elements are not characters
            if (it.Kind != ItemKind.Text || it.Upright)
            {
                last = AutospaceClass.Other;
                continue;
            }
            string s = it.Source;
            for (int i = 0; i < it.Length;)
            {
                int cp = FirstCodePoint(s, it.Start + i), units = cp > 0xFFFF ? 2 : 1;
                var type = ClassifyAutospace(cp);
                if (lastStyle != null && type != AutospaceClass.Other && last != AutospaceClass.Other && (type == AutospaceClass.Ideograph) != (last == AutospaceClass.Ideograph))
                {
                    var other = type == AutospaceClass.Ideograph ? last : type;
                    var wanted = other == AutospaceClass.Numeric ? TextAutospace.IdeographNumeric : TextAutospace.IdeographAlpha;
                    // Both sides ask for it, as the property is inherited and a boundary between elements is in both.
                    if ((it.Style.TextAutospace & wanted) != 0 && (lastStyle.TextAutospace & wanted) != 0)
                        spaces.Add((k, i, Math.Min(it.Style.TextSize, lastStyle.TextSize) / 8));
                }
                last = type;
                lastStyle = it.Style;
                i += units;
            }
        }
        if (spaces.Count == 0) return;
        // Items with spaces inside are split there; the list is built anew, so that many spaces in one word (with
        // word-break: keep-all) stay linear.
        var result = _trimmedItems;
        result.Clear();
        int t = 0;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            int from = 0;
            float start = it.TrimStart;
            for (; t < spaces.Count && spaces[t].Item == k; t++)
            {
                var (_, offset, amount) = spaces[t];
                if (offset > from)
                {
                    var head = it.Slice(from, offset - from, ItemWidth(it, it.Start + from, offset - from));
                    head.BreakAfter = BreakAfter.None;
                    result.Add(head.Trimmed(start, 0));
                    start = 0;
                }
                from = offset;
                start -= amount;
            }
            if (from == 0) result.Add(start == it.TrimStart ? it : it.Trimmed(start, it.TrimEnd));
            else
            {
                var tail = it.Slice(from, it.Length - from, ItemWidth(it, it.Start + from, it.Length - from));
                result.Add(tail.Trimmed(start, tail.TrimEnd));
            }
        }
        items.Clear();
        items.AddRange(result);
        result.Clear();
    }

    /// <summary>Whether any text item asks for autospace, which almost none does: it is off unless a style sets it.</summary>
    private static bool MayAutospace(List<Item> items)
    {
        foreach (var it in items)
            if (it.Kind == ItemKind.Text && it.Style.TextAutospace != TextAutospace.None) return true;
        return false;
    }

    /// <summary>
    /// An ideograph is a letter of Han, Hiragana, Katakana, Bopomofo or Hangul that is wide (not the halfwidth forms);
    /// other letters, marks and digits count unless they are fullwidth or halfwidth forms, as in Chrome.
    /// </summary>
    private static AutospaceClass ClassifyAutospace(int cp)
    {
        if (cp < 0x80)
            return cp is >= '0' and <= '9' ? AutospaceClass.Numeric : cp is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? AutospaceClass.Alpha : AutospaceClass.Other;
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        bool letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
        if (IsWideEastAsian(cp)) return letter || cp == 0x3007 ? AutospaceClass.Ideograph : AutospaceClass.Other;
        if (cp is >= 0xFF00 and <= 0xFFEF) return AutospaceClass.Other; // fullwidth and halfwidth forms
        if (category == UnicodeCategory.DecimalDigitNumber) return AutospaceClass.Numeric;
        return letter || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ? AutospaceClass.Alpha : AutospaceClass.Other;
    }

    /// <summary>The blocks of wide Han, kana, Bopomofo and Hangul characters.</summary>
    private static bool IsWideEastAsian(int cp)
        => cp is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x2FDF or >= 0x3005 and <= 0x3007 or >= 0x3021 and <= 0x3029
            or >= 0x3031 and <= 0x3035 or >= 0x3038 and <= 0x303C or >= 0x3041 and <= 0x31FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x3FFFD;
}
