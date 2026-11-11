using System.Globalization;

namespace PdfPecker.Text;

/// <summary>The bidirectional character types of the Unicode Bidirectional Algorithm (UAX #9).</summary>
internal enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON,
    LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>How a character joins its neighbours in cursive scripts (Arabic, Syriac, N'Ko, ...).</summary>
internal enum JoiningType : byte { U, L, R, D, T, Alaph, DalathRish }

/// <summary>
/// The Line_Break classes of UAX #14 that the line breaker tells apart, in the order tools/UnicodeTables writes them.
/// XX stands for every other class, alphabetic letters among them; emoji bases are ID and emoji modifiers CM.
/// </summary>
internal enum LineBreakClass : byte
{
    XX, NU, PR, PO, IS, SY, HY, BA, BB, B2, GL, WJ, ZW, CM, ZWJ, QU,
    EX, IN, OP, CL, CP, NS, CJ, ID, H2, H3, JL, JV, JT,
}

/// <summary>
/// The Vertical_Orientation values of UAX #50, in the order tools/UnicodeTables writes them: R is turned sideways in
/// vertical text, U stands upright, Tu and Tr take the font's vertical form and otherwise stand upright (Tu) or are
/// turned (Tr).
/// </summary>
internal enum VerticalOrientation : byte { R, U, Tu, Tr }

/// <summary>Lookups in the generated Unicode tables.</summary>
internal static partial class UnicodeTables
{
    private static byte[]? s_bmpBidi; // Bidi_Class of the BMP, expanded on first use

    public static BidiClass Bidi(int cp)
    {
        if ((uint)cp < 0x10000)
        {
            var bmp = s_bmpBidi ??= ExpandBmp();
            return (BidiClass)bmp[cp];
        }
        return (BidiClass)BidiClassValues[FindRange(BidiClassStarts, cp)];
    }

    private static byte[] ExpandBmp()
    {
        var t = new byte[0x10000];
        var starts = BidiClassStarts;
        var values = BidiClassValues;
        for (int k = 0; k < starts.Length && starts[k] < 0x10000; k++)
        {
            int end = k + 1 < starts.Length ? Math.Min(starts[k + 1], 0x10000) : 0x10000;
            t.AsSpan(starts[k], end - starts[k]).Fill(values[k]);
        }
        return t;
    }

    // The index of the last start at or below cp.
    private static int FindRange(ReadOnlySpan<int> starts, int cp)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= cp) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The Line_Break class of a character, and whether it is East Asian wide or fullwidth.</summary>
    public static LineBreakClass LineBreak(int cp, out bool wide)
    {
        int v = LineBreakValues[FindRange(LineBreakStarts, cp)];
        wide = (v & 0x80) != 0;
        return (LineBreakClass)(v & 0x7F);
    }

    /// <summary>The Vertical_Orientation of a character (UAX #50).</summary>
    public static VerticalOrientation Vertical(int cp)
        => cp < 0xA7 ? VerticalOrientation.R : (VerticalOrientation)VerticalOrientationValues[FindRange(VerticalOrientationStarts, cp)];

    /// <summary>True for characters that are strong right-to-left, Arabic numbers, or explicit bidi controls.</summary>
    public static bool NeedsBidi(int cp)
    {
        if (cp < 0x0590) return false;
        var c = Bidi(cp);
        return c is BidiClass.R or BidiClass.AL or BidiClass.AN || c >= BidiClass.LRE;
    }

    /// <summary>The Bidi_Mirroring_Glyph of a character, or the character itself.</summary>
    public static int Mirror(int cp)
    {
        var pairs = MirrorPairs;
        int lo = 0, hi = pairs.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, v = pairs[mid * 2];
            if (v == cp) return pairs[mid * 2 + 1];
            if (v < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return cp;
    }

    /// <summary>
    /// The paired bracket of a character (canonical equivalents of U+2329/U+232A and U+3008/U+3009 are unified), with
    /// whether it opens; 0 when it is not a bracket.
    /// </summary>
    public static int Bracket(int cp, out bool opening)
    {
        opening = false;
        var t = BracketPairs;
        int lo = 0, hi = t.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, v = t[mid * 3];
            if (v == cp)
            {
                opening = t[mid * 3 + 2] == 1;
                return t[mid * 3 + 1];
            }
            if (v < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>The canonical form of a bracket for matching pairs: U+2329/U+232A match U+3008/U+3009.</summary>
    public static int CanonicalBracket(int cp) => cp switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => cp };

    public static JoiningType Joining(int cp)
    {
        if (cp >= 0x0600)
        {
            var t = JoiningRanges;
            int lo = 0, hi = t.Length / 3 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < t[mid * 3]) hi = mid - 1;
                else if (cp > t[mid * 3 + 1]) lo = mid + 1;
                else return (JoiningType)t[mid * 3 + 2];
            }
        }
        return IsMark(cp) || CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.Format ? JoiningType.T : JoiningType.U;
    }

    /// <summary>The Canonical_Combining_Class of a character (0 for starters).</summary>
    public static int CombiningClass(int cp)
    {
        if (cp < 0x0300) return 0;
        var t = CombiningClassRanges;
        int lo = 0, hi = t.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < t[mid * 3]) hi = mid - 1;
            else if (cp > t[mid * 3 + 1]) lo = mid + 1;
            else return t[mid * 3 + 2];
        }
        return 0;
    }

    // HarfBuzz's modified combining classes 10-35: Hebrew points in the order of the SBL Hebrew manual, and Arabic shadda
    // before the other harakat, which is the order fonts expect for their mark ligatures and positioning.
    private static ReadOnlySpan<byte> ModifiedClasses =>
    [
        22, 15, 16, 17, 23, 18, 19, 20, 21, 14, 24, 12, 25, 13, 10, 11, 26, // 10-26 Hebrew
        28, 29, 30, 31, 32, 33, 27, 34, 35, // 27-35 Arabic
    ];

    /// <summary>
    /// The combining class that marks are ordered by before shaping. Besides the Hebrew and Arabic classes, HarfBuzz
    /// moves the Telugu length marks before the other marks, and Thai Sara U and Sara Uu before Phinthu, as Uniscribe
    /// orders them. In Tibetan it puts the vowel sign U before I, which Dzongkha's stacked vowels need, Tsa-phru before
    /// U and Padma after the vowel signs, and Tai Tham's Sakot after the tone marks.
    /// </summary>
    public static int ModifiedCombiningClass(int cp)
    {
        if (cp == 0x0F39) return 127;
        if (cp is 0x0FC6 or 0x1A60) return 254;
        int c = CombiningClass(cp);
        return c switch
        {
            >= 10 and <= 35 => ModifiedClasses[c - 10],
            84 => 4,   // Telugu length mark
            91 => 5,   // Telugu ai length mark
            103 => 3,  // Thai Sara U and Sara Uu
            130 => 132, // Tibetan vowel sign I
            132 => 131, // Tibetan vowel sign U
            _ => c,
        };
    }

    // DecompositionStarts: where a mapping starts in DecompositionData, with this bit set for a compatibility mapping.
    private const int CompatibilityMapping = 0x8000, MappingStart = 0x7FFF;

    /// <summary>
    /// The index of a code point's decomposition mapping, one level deep, or -1 when it has none (or only a compatibility
    /// mapping and those are not wanted). Hangul syllables are not in the table.
    /// </summary>
    public static int FindDecomposition(int cp, bool compatibility)
    {
        var keys = DecompositionKeys;
        int lo = 0, hi = keys.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, v = keys[mid];
            if (v == cp) return compatibility || (DecompositionStarts[mid] & CompatibilityMapping) == 0 ? mid : -1;
            if (v < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Where the mapping at an index of the decomposition table starts and ends in DecompositionData.</summary>
    public static (int Start, int End) DecompositionMapping(int index)
        => (DecompositionStarts[index] & MappingStart, DecompositionStarts[index + 1] & MappingStart);

    /// <summary>The code point at an index of DecompositionData, which is UTF-16; moves the index past it.</summary>
    public static int MappingCodePoint(ref int index)
    {
        var data = DecompositionData;
        char c = (char)data[index++];
        return char.IsHighSurrogate(c) && index < data.Length && char.IsLowSurrogate((char)data[index])
            ? char.ConvertToUtf32(c, (char)data[index++]) : c;
    }

    /// <summary>
    /// The primary composite of two characters, the one whose canonical mapping they are and that normalization
    /// composes (composition exclusions are not listed), or 0. Hangul syllables are left to the caller.
    /// </summary>
    public static int Composite(int first, int second)
    {
        var compositions = Compositions;
        int lo = 0, hi = compositions.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, index = compositions[mid];
            int i = DecompositionStarts[index] & MappingStart;
            int a = MappingCodePoint(ref i), b = MappingCodePoint(ref i);
            int order = a != first ? a.CompareTo(first) : b.CompareTo(second);
            if (order == 0) return DecompositionKeys[index];
            if (order < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>The canonical decomposition of a Hebrew or Arabic letter (second is 0 for one character), or false.</summary>
    public static bool Decompose(int cp, out int first, out int second)
    {
        first = second = 0;
        return cp is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFB4F) && DecomposeCanonical(cp, out first, out second);
    }

    /// <summary>The canonical decomposition of a character, one level deep (second is 0 for one character), or false.</summary>
    public static bool DecomposeCanonical(int cp, out int first, out int second)
    {
        first = second = 0;
        int k = FindDecomposition(cp, compatibility: false);
        if (k < 0) return false;
        var (i, end) = DecompositionMapping(k);
        first = MappingCodePoint(ref i);
        if (i < end) second = MappingCodePoint(ref i);
        return true;
    }

    /// <summary>True for non-spacing and enclosing combining marks.</summary>
    public static bool IsMark(int cp)
        => cp >= 0x0300 && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    /// <summary>The Arabic presentation form of a letter (form 0 isolated, 1 final, 2 initial, 3 medial), or 0.</summary>
    public static int PresentationForm(int cp, int form) => Find(PresentationForms, cp << 2 | form);

    /// <summary>The presentation form of lam followed by an alef (form 0 isolated, 1 final), or 0.</summary>
    public static int LamAlef(int alef, int form) => Find(LamAlefForms, alef << 2 | form);

    private static int Find(ReadOnlySpan<int> pairs, int key)
    {
        int lo = 0, hi = pairs.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, v = pairs[mid * 2];
            if (v == key) return pairs[mid * 2 + 1];
            if (v < key) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>
    /// Default ignorable characters that are never drawn: bidi controls, zero-width joiners and spaces, and the like.
    /// </summary>
    public static bool IsDefaultIgnorable(int cp)
        => cp is 0x00AD or 0x034F or 0x061C or (>= 0x115F and <= 0x1160) or (>= 0x17B4 and <= 0x17B5) or (>= 0x180B and <= 0x180F)
            or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or 0x3164 or (>= 0xFE00 and <= 0xFE0F)
            or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A)
            or (>= 0xE0000 and <= 0xE0FFF);
}
