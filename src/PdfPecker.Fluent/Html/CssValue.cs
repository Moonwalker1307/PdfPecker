using System.Globalization;
using System.Text;

namespace PdfPecker.Fluent.Html;

/// <summary>Formats CSS values. Every number is written with the invariant culture, so output never depends on the machine.</summary>
internal static class CssValue
{
    public static string Pt(float points) => Number(points) + "pt";

    public static string Number(float value)
    {
        // Four decimals are far below what a page can show and keep float noise (0.30000001) out of the output.
        var s = MathF.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    public static string Color(Color color) => color.ToString();

    /// <summary>
    /// A quoted CSS string. Besides quotes and backslashes, &lt; &gt; and &amp; are escaped too, because the string sits
    /// in a style element, where &lt;/style would end the style sheet.
    /// </summary>
    public static string String(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\' or '<' or '>' or '&' or '\n' or '\r' or '\f' || char.IsControl(c))
                sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            else
                sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// The declarations a text style sets. A box (a paragraph, a slot, the page) takes only the inherited font
    /// properties; the background and superscript position belong to the spans of text.
    /// </summary>
    public static void AddTextStyle(List<(string, string)> declarations, TextStyle style, bool forBox)
    {
        float? size = style.Size;
        if (forBox)
        {
            if (size is { } s) declarations.Add(("font-size", Pt(s)));
        }
        else if (style.Position is TextPosition.Superscript or TextPosition.Subscript)
        {
            declarations.Add(("vertical-align", style.Position == TextPosition.Superscript ? "super" : "sub"));
            declarations.Add(("font-size", size is { } s ? Pt(s * 0.66f) : "0.66em"));
        }
        else
        {
            if (style.Position == TextPosition.Normal) declarations.Add(("vertical-align", "baseline"));
            if (size is { } s) declarations.Add(("font-size", Pt(s)));
        }
        if (style.Families is { } families) declarations.Add(("font-family", FontFamilies(families)));
        if (style.Weight is { } weight) declarations.Add(("font-weight", ((int)weight).ToString(CultureInfo.InvariantCulture)));
        if (style.IsItalic is { } italic) declarations.Add(("font-style", italic ? "italic" : "normal"));
        if (style.IsUnderline != null || style.IsStrikethrough != null)
        {
            bool under = style.IsUnderline == true, through = style.IsStrikethrough == true;
            declarations.Add(("text-decoration-line", under && through ? "underline line-through" : under ? "underline" : through ? "line-through" : "none"));
        }
        if (style.DecorationStyle is { } decoration)
            declarations.Add(("text-decoration-style", decoration switch
            {
                TextDecorationStyle.Double => "double",
                TextDecorationStyle.Wavy => "wavy",
                TextDecorationStyle.Dotted => "dotted",
                TextDecorationStyle.Dashed => "dashed",
                _ => "solid",
            }));
        if (style.DecorationTint is { } decorationColor) declarations.Add(("text-decoration-color", Color(decorationColor)));
        // Fonts draw their underline at about a fourteenth of an em, which a factor of 1 keeps.
        if (style.DecorationWidth is { } thickness) declarations.Add(("text-decoration-thickness", Number(thickness * 0.07f) + "em"));
        if (style.Color is { } color) declarations.Add(("color", Color(color)));
        if (!forBox && style.Background is { } background) declarations.Add(("background-color", Color(background)));
        if (style.Leading is { } leading) declarations.Add(("line-height", Number(leading)));
        if (style.Tracking is { } tracking) declarations.Add(("letter-spacing", Number(tracking) + "em"));
        if (style.WordGap is { } wordGap) declarations.Add(("word-spacing", Number(wordGap) + "em"));
        if (style.Features is { } features)
        {
            var sb = new StringBuilder();
            foreach (var (tag, on) in features)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append('"').Append(tag).Append("\" ").Append(on ? '1' : '0');
            }
            declarations.Add(("font-feature-settings", sb.ToString()));
        }
    }

    /// <summary>The span-only part of a style (background and position), for a span whose paragraph carries the font.</summary>
    public static void AddSpanOnlyStyle(List<(string, string)> declarations, TextStyle style)
    {
        if (style.Position is TextPosition.Superscript or TextPosition.Subscript)
        {
            declarations.Add(("vertical-align", style.Position == TextPosition.Superscript ? "super" : "sub"));
            declarations.Add(("font-size", "0.66em"));
        }
        if (style.Background is { } background) declarations.Add(("background-color", Color(background)));
    }

    private static string FontFamilies(string[] families)
    {
        var sb = new StringBuilder();
        foreach (var family in families)
        {
            if (sb.Length > 0) sb.Append(',');
            var name = family.Trim();
            // Generic families are keywords and must stay unquoted; everything else is quoted, so a name is never read as CSS.
            if (name is "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy" or "system-ui") sb.Append(name);
            else sb.Append(String(name));
        }
        return sb.ToString();
    }
}
