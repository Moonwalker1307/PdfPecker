using System.Globalization;

namespace PdfPecker.Text;

/// <summary>
/// The text spacing character classes of CSS Text 4, as Chrome assigns them (HanKerningCharType): fullwidth opening and
/// closing punctuation, middle dots, the narrow brackets of other scripts, and the punctuation whose class depends on
/// where the font draws it (dots, colons, semicolons and quotes).
/// </summary>
internal enum TextSpacingClass : byte
{
    Other,
    Open,
    Close,
    Middle,
    OpenNarrow,
    CloseNarrow,
    Dot,
    Colon,
    Semicolon,
    OpenQuote,
    CloseQuote,
}

/// <summary>The character classes that text-spacing-trim looks at.</summary>
internal static class TextSpacingCharacters
{
    private const int CjkSymbolsFirst = 0x3000, CjkSymbolsLast = 0x303F;

    /// <summary>The class of a character, before the font settles the dots, colons, semicolons and quotes.</summary>
    public static TextSpacingClass Classify(int cp)
    {
        switch (cp)
        {
            case 0x2018 or 0x201C: return TextSpacingClass.OpenQuote;
            case 0x2019 or 0x201D: return TextSpacingClass.CloseQuote;
            // Ideographic space, middle dot, hyphenation point and katakana middle dot.
            case 0x3000 or 0x00B7 or 0x2027 or 0x30FB: return TextSpacingClass.Middle;
            // Ideographic comma and full stop, fullwidth comma and full stop.
            case 0x3001 or 0x3002 or 0xFF0C or 0xFF0E: return TextSpacingClass.Dot;
            case 0xFF1A: return TextSpacingClass.Colon;
            case 0xFF1B: return TextSpacingClass.Semicolon;
        }
        if (cp < 0x28) return TextSpacingClass.Other;
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (category is not (UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation)) return TextSpacingClass.Other;
        bool open = category == UnicodeCategory.OpenPunctuation;
        if (cp is >= CjkSymbolsFirst and <= CjkSymbolsLast || IsFullwidth(cp)) return open ? TextSpacingClass.Open : TextSpacingClass.Close;
        return open ? TextSpacingClass.OpenNarrow : TextSpacingClass.CloseNarrow;
    }

    /// <summary>
    /// Whether a character may be fullwidth opening or closing punctuation, which is what a text needs to have before
    /// its punctuation is trimmed at all; quotes count, since a font can draw them fullwidth.
    /// </summary>
    public static bool MayBeOpenOrClose(int cp) => cp >= 0x2018 && Classify(cp) is TextSpacingClass.Open or TextSpacingClass.Close
        or TextSpacingClass.Dot or TextSpacingClass.Colon or TextSpacingClass.Semicolon or TextSpacingClass.OpenQuote or TextSpacingClass.CloseQuote;

    /// <summary>Whether a character's East Asian Width is Fullwidth: the ideographic space and the fullwidth forms.</summary>
    private static bool IsFullwidth(int cp) => cp == 0x3000 || cp is >= 0xFF01 and <= 0xFF60 || cp is >= 0xFFE0 and <= 0xFFE6;
}
