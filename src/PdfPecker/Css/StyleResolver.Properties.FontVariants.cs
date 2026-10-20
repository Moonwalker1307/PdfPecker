using PdfPecker.Fonts;

namespace PdfPecker.Css;

/// <summary>
/// The font properties that choose how glyphs are picked and sized rather than which font is used: kerning, synthesis,
/// size adjustment and the east asian and position variants.
/// </summary>
internal sealed partial class StyleResolver
{
    private static void ApplyFontKerning(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        switch (d.Lower)
        {
            case "inherit" or "unset": s.NoKerning = parent.NoKerning; break;
            case "none": s.NoKerning = true; break;
            case "auto" or "normal" or "initial": s.NoKerning = false; break;
        }
    }

    /// <summary>font-synthesis: none, or any of weight, style, small-caps and position (the ones left out are turned off).</summary>
    private static void ApplyFontSynthesis(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        switch (d.Lower)
        {
            case "inherit" or "unset": s.Synthesis = parent.Synthesis; return;
            case "initial": s.Synthesis = FontSynthesis.All; return;
            case "none": s.Synthesis = FontSynthesis.None; return;
        }
        var allowed = FontSynthesis.None;
        foreach (var token in d.Lower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            allowed |= token switch
            {
                "weight" => FontSynthesis.Weight,
                "style" => FontSynthesis.Style,
                "small-caps" => FontSynthesis.SmallCaps,
                "position" => FontSynthesis.Position,
                _ => (FontSynthesis)0x80, // not a keyword: the declaration is invalid
            };
        }
        if (allowed != FontSynthesis.None && (allowed & (FontSynthesis)0x80) == 0) s.Synthesis = allowed;
    }

    /// <summary>One of the font-synthesis-weight, -style, -small-caps and -position longhands: auto allows, none forbids.</summary>
    private static void ApplyFontSynthesisPart(ComputedStyle s, ComputedStyle parent, FontSynthesis part, in DeclaredValue d)
    {
        switch (d.Lower)
        {
            case "auto" or "initial": s.Synthesis |= part; break;
            case "none": s.Synthesis &= ~part; break;
            case "inherit" or "unset": s.Synthesis = s.Synthesis & ~part | parent.Synthesis & part; break;
        }
    }

    private static void ApplyFontSizeAdjust(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit || d.Lower == "unset") s.SizeAdjust = parent.SizeAdjust;
        else if (d.Lower == "initial") s.SizeAdjust = default;
        else if (FontSizeAdjust.Parse(d.Lower) is { } adjust) s.SizeAdjust = adjust;
    }

    private static void ApplyFontVariantEastAsian(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit || d.Lower == "unset") s.FontVariantEastAsian = parent.FontVariantEastAsian;
        else if (d.Lower is "normal" or "initial") s.FontVariantEastAsian = null;
        else if (FontFeatures.ValidEastAsian(d.Lower)) s.FontVariantEastAsian = d.Lower;
    }

    private static void ApplyFontVariantPosition(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit || d.Lower == "unset") s.FontVariantPosition = parent.FontVariantPosition;
        else if (d.Lower is "normal" or "initial") s.FontVariantPosition = null;
        else if (d.Lower is "sub" or "super") s.FontVariantPosition = d.Lower;
    }
}
