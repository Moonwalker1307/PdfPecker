using System.Globalization;

namespace PdfPecker.Text;

/// <summary>How strictly Chinese and Japanese line breaking rules apply (CSS line-break).</summary>
internal enum LineBreakStrictness : byte { Strict, Normal, Loose }

/// <summary>
/// Line break opportunities inside a run of text without spaces, as UAX #14 and CSS Text give them for Chinese, Japanese
/// and Korean: between ideographs, kana and Hangul syllables, but never before closing punctuation or small kana (under
/// strict rules) and never after opening brackets.
/// </summary>
/// <remarks>
/// Only pairs with an East Asian wide or fullwidth character (or a Hangul jamo) on either side are considered, so text in
/// other scripts keeps breaking at spaces and hyphens only, exactly as before. Spaces never reach the breaker: the
/// layout splits words at them first.
/// </remarks>
internal static class LineBreaker
{
    /// <summary>Whether a word has a character next to which the breaker may find an opportunity.</summary>
    public static bool HasEastAsian(ReadOnlySpan<char> s)
    {
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] < 0x1100) continue; // nothing below the Hangul jamo is wide
            int cp = CodePointAt(s, k);
            var c = UnicodeTables.LineBreak(cp, out bool wide);
            if (wide || IsJamo(c)) return true;
            if (cp > 0xFFFF) k++;
        }
        return false;
    }

    /// <summary>
    /// The index of the first break opportunity after <paramref name="from"/> in <paramref name="s"/>, or its length when
    /// there is none before its end.
    /// </summary>
    public static int NextBreak(ReadOnlySpan<char> s, int from, bool keepAll, LineBreakStrictness strictness)
    {
        int k = from;
        int prevCp = CodePointAt(s, k);
        var prev = Resolve(prevCp, out bool prevWide, strictness);
        // A combining mark with nothing before it is treated as a letter (LB10).
        if (prev is LineBreakClass.CM or LineBreakClass.ZWJ) { prev = LineBreakClass.XX; prevWide = false; }
        bool afterZwj = false;
        k += prevCp > 0xFFFF ? 2 : 1;
        while (k < s.Length)
        {
            int cp = CodePointAt(s, k);
            var cls = Resolve(cp, out bool wide, strictness);
            int len = cp > 0xFFFF ? 2 : 1;
            // Combining marks and emoji modifiers stay with the character they follow and take nothing from it (LB9).
            if (cls is LineBreakClass.CM or LineBreakClass.ZWJ)
            {
                afterZwj = cls == LineBreakClass.ZWJ;
                k += len;
                continue;
            }
            // Nor does an emoji zero-width-joiner sequence break (LB8a).
            if (!afterZwj && BreakBetween(prevCp, prev, prevWide, cp, cls, wide, keepAll, strictness)) return k;
            afterZwj = false;
            prevCp = cp;
            prev = cls;
            prevWide = wide;
            k += len;
        }
        return s.Length;
    }

    /// <summary>Whether a line may break between two characters that are next to each other.</summary>
    public static bool BreakBetween(int before, int after, bool keepAll, LineBreakStrictness strictness)
    {
        var a = Resolve(before, out bool aw, strictness);
        var b = Resolve(after, out bool bw, strictness);
        // The character a mark belongs to is not known here, so no break is assumed next to marks.
        if (a is LineBreakClass.CM or LineBreakClass.ZWJ || b is LineBreakClass.CM or LineBreakClass.ZWJ) return false;
        return BreakBetween(before, a, aw, after, b, bw, keepAll, strictness);
    }

    private static bool BreakBetween(int acp, LineBreakClass a, bool aw, int bcp, LineBreakClass b, bool bw, bool keepAll, LineBreakStrictness strictness)
    {
        if (!(aw || bw || IsJamo(a) || IsJamo(b))) return false;
        // Word joiners and no-break characters glue to both neighbours (LB11, LB12, LB12a).
        if (a is LineBreakClass.WJ or LineBreakClass.GL || b is LineBreakClass.WJ) return false;
        if (a is LineBreakClass.ZW) return true;
        if (b is LineBreakClass.GL && a is not (LineBreakClass.BA or LineBreakClass.HY)) return false;
        // Nothing breaks after an opening bracket or quotation mark (LB14).
        if (a is LineBreakClass.OP) return false;
        // CSS line-break: normal and loose allow a break before the Japanese and Chinese hyphens, which strict forbids.
        if (strictness != LineBreakStrictness.Strict && bcp is 0x2010 or 0x2013 or 0x301C or 0x30A0) return true;
        if (strictness == LineBreakStrictness.Loose)
        {
            if (IsLooseBreakBefore(bcp)) return true;
            // Loose also breaks between an ideograph and a postfix or prefix, which keep to numbers otherwise.
            if (b is LineBreakClass.PO && a is LineBreakClass.ID || a is LineBreakClass.PR && b is LineBreakClass.ID) return true;
        }
        // Closing punctuation, exclamation and question marks and infix separators never start a line (LB13).
        if (b is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY) return false;
        // Loose breaks on both sides of the ellipsis and two-dot leader; otherwise they stay with what precedes them.
        if (strictness == LineBreakStrictness.Loose && (acp is 0x2025 or 0x2026 || bcp is 0x2025 or 0x2026)) return true;
        // Quotation marks whose direction is not known stay with both neighbours (LB19).
        if (a is LineBreakClass.QU || b is LineBreakClass.QU) return false;
        if (a is LineBreakClass.B2 && b is LineBreakClass.B2) return false;
        // Nonstarters (iteration marks, prolonged sound marks, small kana under strict rules), break-after punctuation
        // and inseparable characters do not start a line (LB21, LB22).
        if (b is LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.NS or LineBreakClass.IN) return false;
        if (a is LineBreakClass.BB) return false;
        // Prefixes, postfixes and numbers hold together (LB23 to LB25).
        if (a is LineBreakClass.PR && b is LineBreakClass.ID or LineBreakClass.NU or LineBreakClass.XX or LineBreakClass.OP || a is LineBreakClass.PR && IsHangul(b)) return false;
        if (b is LineBreakClass.PO && (a is LineBreakClass.ID or LineBreakClass.NU or LineBreakClass.XX or LineBreakClass.CL or LineBreakClass.CP || IsHangul(a))) return false;
        if (b is LineBreakClass.PR && a is LineBreakClass.NU or LineBreakClass.XX) return false;
        if (b is LineBreakClass.NU && a is LineBreakClass.HY or LineBreakClass.IS or LineBreakClass.SY) return false;
        // Hangul syllables are not broken apart into their jamo (LB26).
        if (a is LineBreakClass.JL && b is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3) return false;
        if (a is LineBreakClass.JV or LineBreakClass.H2 && b is LineBreakClass.JV or LineBreakClass.JT) return false;
        if (a is LineBreakClass.JT or LineBreakClass.H3 && b is LineBreakClass.JT) return false;
        // Letters and digits that are not ideographs, fullwidth Latin among them, form words (LB23, LB28, LB29).
        if (a is LineBreakClass.XX or LineBreakClass.NU && b is LineBreakClass.XX or LineBreakClass.NU) return false;
        if (a is LineBreakClass.IS && b is LineBreakClass.XX) return false;
        // Narrow brackets cling to the letters inside them (LB30).
        if (a is LineBreakClass.XX or LineBreakClass.NU && b is LineBreakClass.OP && !bw) return false;
        if (a is LineBreakClass.CP && !aw && b is LineBreakClass.XX or LineBreakClass.NU) return false;
        // word-break: keep-all keeps runs of letters, ideographs and Hangul together; punctuation still breaks.
        if (keepAll && IsLetter(a) && IsLetter(b)) return false;
        return true;
    }

    // The class with the choices of CSS line-break and of the quotation marks made: small kana and the prolonged sound
    // mark (CJ) are nonstarters under strict rules and ideographs otherwise, and a quotation mark that is an initial or
    // final punctuation opens or closes, as it does next to Chinese and Japanese text.
    private static LineBreakClass Resolve(int cp, out bool wide, LineBreakStrictness strictness)
    {
        var c = UnicodeTables.LineBreak(cp, out wide);
        return c switch
        {
            LineBreakClass.CJ => strictness == LineBreakStrictness.Strict ? LineBreakClass.NS : LineBreakClass.ID,
            LineBreakClass.QU => CharUnicodeInfo.GetUnicodeCategory(cp) switch
            {
                UnicodeCategory.InitialQuotePunctuation => LineBreakClass.OP,
                UnicodeCategory.FinalQuotePunctuation => LineBreakClass.CL,
                _ => c,
            },
            _ => c,
        };
    }

    // line-break: loose breaks before iteration marks and the centred punctuation of Chinese and Japanese.
    private static bool IsLooseBreakBefore(int cp) =>
        cp is 0x3005 or 0x303B or 0x309D or 0x309E or 0x30FD or 0x30FE
            or 0x30FB or 0xFF1A or 0xFF1B or 0xFF65 or 0x203C or 0x2047 or 0x2048 or 0x2049 or 0xFF01 or 0xFF1F;

    private static bool IsJamo(LineBreakClass c) => c is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT;

    private static bool IsHangul(LineBreakClass c) => c is >= LineBreakClass.H2 and <= LineBreakClass.JT;

    private static bool IsLetter(LineBreakClass c) => c is LineBreakClass.XX or LineBreakClass.NU or LineBreakClass.ID || IsHangul(c);

    private static int CodePointAt(ReadOnlySpan<char> s, int k)
    {
        char c = s[k];
        return char.IsHighSurrogate(c) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]) ? char.ConvertToUtf32(c, s[k + 1]) : c;
    }
}
