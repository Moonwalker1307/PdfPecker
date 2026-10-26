using System.Globalization;

namespace PdfPecker.Css;

/// <summary>Font and text properties: fonts, alignment, decoration, spacing, white space, line breaking and bidi.</summary>
internal sealed partial class StyleResolver
{
    private void ApplyFontSize(ComputedStyle s, ComputedStyle parent, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        float px;
        switch (v)
        {
            case "xx-small": px = 9; break;
            case "x-small": px = 10; break;
            case "small": px = 13; break;
            case "medium": px = 16; break;
            case "large": px = 18; break;
            case "x-large": px = 24; break;
            case "xx-large": px = 32; break;
            case "xxx-large": px = 48; break;
            case "smaller": s.FontSize = parent.FontSize / 1.2f; return;
            case "larger": s.FontSize = parent.FontSize * 1.2f; return;
            case "inherit" or "unset": s.FontSize = parent.FontSize; return;
            case "initial": s.FontSize = 12; return;
            default:
                if (TryParseLength(v, parent.FontSize, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto)
                    s.FontSize = Math.Max(0, len.Resolve(parent.FontSize));
                return;
        }
        s.FontSize = px * 0.75f;
    }

    private static void ApplyFontFamily(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.FontFamily = parent.FontFamily; s.FontFamilyName = parent.FontFamilyName; return; }
        s.FontFamily = MapFontFamily(d.Value, s.FontFamily);
        s.FontFamilyName = d.Value;
    }

    private static void ApplyFontWeight(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        s.FontWeight = d.Lower switch
        {
            "bold" => 700,
            "normal" => 400,
            "bolder" => parent.FontWeight < 350 ? 400 : parent.FontWeight < 550 ? 700 : Math.Max(900, parent.FontWeight),
            "lighter" => parent.FontWeight < 100 ? parent.FontWeight : parent.FontWeight < 550 ? 100 : parent.FontWeight < 750 ? 400 : 700,
            "inherit" => parent.FontWeight,
            _ => Invariant.TryParseInt(d.Lower, out int w) && w >= 1 && w <= 1000 ? w : s.FontWeight,
        };
        s.Bold = s.FontWeight >= 600;
    }

    private static void ApplyFontStretch(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.FontStretch = parent.FontStretch;
        else if (FontStretchPercent(d.Lower) is float stretch) s.FontStretch = stretch;
    }

    private static void ApplyFontStyle(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.Italic = d.Lower switch { "italic" or "oblique" => true, "normal" => false, "inherit" => parent.Italic, _ => s.Italic };

    private static void ApplyFontVariationSettings(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.FontVariationSettings = d.Inherit ? parent.FontVariationSettings : d.Lower is "normal" or "initial" or "unset" ? null : d.Value;

    private static void ApplyFontVariantCaps(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.SmallCaps = d.Lower switch
        {
            "small-caps" or "petite-caps" => 1,
            "all-small-caps" or "all-petite-caps" => 2,
            "inherit" => parent.SmallCaps,
            _ => 0,
        };

    private static void ApplyFontVariantLigatures(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        string lower = d.Lower;
        if (d.Inherit) s.NoLigatures = parent.NoLigatures;
        else if (lower is "normal" or "initial" or "unset") s.NoLigatures = false;
        else if (lower == "none") s.NoLigatures = true;
        else if (lower.Contains("no-common-ligatures")) s.NoLigatures = true;
        else if (lower.Contains("common-ligatures")) s.NoLigatures = false;
    }

    private static void ApplyFontVariantNumeric(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.FontVariantNumeric = d.Lower switch
        {
            "inherit" => parent.FontVariantNumeric,
            "normal" or "initial" or "unset" => null,
            _ => d.Lower,
        };

    private static void ApplyFontFeatureSettings(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit)
        {
            s.NoLigatures = parent.NoLigatures;
            s.NoKerning = parent.NoKerning;
            s.FontFeatureSettings = parent.FontFeatureSettings;
            return;
        }
        if (FeatureSetting(d.Lower, "liga") is bool liga) s.NoLigatures = !liga;
        if (FeatureSetting(d.Lower, "kern") is bool kern) s.NoKerning = !kern;
        // Tags are case-sensitive, so the value is kept as written.
        s.FontFeatureSettings = d.Lower is "normal" or "initial" or "unset" ? null : d.Value;
    }

    private void ApplyLineHeight(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.LineHeightKind = parent.LineHeightKind; s.LineHeightValue = parent.LineHeightValue; return; }
        if (d.Lower == "normal") { s.LineHeightKind = LineHeightKind.Normal; return; }
        // "Infinity" and "NaN" parse as floats but are not CSS numbers; kept, they make every line's height NaN.
        if (float.TryParse(d.Lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float factor))
        {
            if (!float.IsFinite(factor)) return;
            s.LineHeightKind = LineHeightKind.Factor;
            s.LineHeightValue = factor;
        }
        else if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var lh, _vw, _vh) && !lh.IsAuto)
        {
            s.LineHeightKind = LineHeightKind.Absolute;
            s.LineHeightValue = lh.Resolve(d.Em);
        }
    }

    private static void ApplyTextAlign(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        s.TextAlign = d.Lower switch
        {
            "start" => TextAlign.Start,
            "end" => TextAlign.End,
            "left" or "-webkit-left" => TextAlign.Left,
            "right" or "-webkit-right" => TextAlign.Right,
            "center" or "middle" or "-webkit-center" => TextAlign.Center,
            "justify" or "justify-all" => TextAlign.Justify,
            "inherit" => parent.TextAlign,
            _ => s.TextAlign,
        };
        if (d.Lower == "justify-all") s.TextAlignLast = TextAlign.Justify;
        // The legacy values that align attributes and the center element map to also move child blocks narrower than
        // their container, as in Chrome; every other value aligns only the lines.
        s.BlockAlign = d.Lower switch
        {
            "-webkit-left" => TextAlign.Left,
            "-webkit-right" => TextAlign.Right,
            "-webkit-center" => TextAlign.Center,
            "inherit" => parent.BlockAlign,
            "start" or "end" or "left" or "right" or "center" or "middle" or "justify" or "justify-all" => null,
            _ => s.BlockAlign,
        };
    }

    private static void ApplyTextAlignLast(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.TextAlignLast = d.Lower switch
        {
            "auto" or "initial" or "unset" => null,
            "start" => TextAlign.Start,
            "end" => TextAlign.End,
            "left" => TextAlign.Left,
            "right" => TextAlign.Right,
            "center" => TextAlign.Center,
            "justify" => TextAlign.Justify,
            "inherit" => parent.TextAlignLast,
            _ => s.TextAlignLast,
        };

    private static void ApplyTextJustify(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.TextJustify = d.Lower switch
        {
            "auto" => TextJustify.Auto,
            "none" => TextJustify.None,
            "inter-word" => TextJustify.InterWord,
            "inter-character" or "distribute" => TextJustify.InterCharacter,
            "inherit" => parent.TextJustify,
            _ => s.TextJustify,
        };

    private static void ApplyTextSpacingTrim(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.TextSpacingTrim = d.Lower switch
        {
            "normal" => TextSpacingTrim.Normal,
            "space-all" => TextSpacingTrim.SpaceAll,
            "space-first" => TextSpacingTrim.SpaceFirst,
            "trim-start" => TextSpacingTrim.TrimStart,
            "inherit" => parent.TextSpacingTrim,
            _ => s.TextSpacingTrim,
        };

    private void ApplyTextIndent(ComputedStyle s, in DeclaredValue d)
    {
        if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var ti, _vw, _vh)) s.TextIndent = ti.IsPercent ? 0 : ti.Resolve(0);
    }

    private static void ApplyTextTransform(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.TextTransform = d.Lower switch
        {
            "uppercase" => TextTransform.Uppercase,
            "lowercase" => TextTransform.Lowercase,
            "capitalize" => TextTransform.Capitalize,
            "none" => TextTransform.None,
            "inherit" => parent.TextTransform,
            _ => s.TextTransform,
        };

    private void ApplyTextDecorationLine(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.TextDecoration = parent.TextDecoration; return; }
        var td = TextDecoration.None;
        foreach (var t in CssParser.Tokens(d.Lower))
        {
            if (t == "underline") td |= TextDecoration.Underline;
            else if (t == "line-through") td |= TextDecoration.LineThrough;
            else if (t == "overline") td |= TextDecoration.Overline;
        }
        s.TextDecoration = td;
        _decorationLineSet = true;
    }

    private void ApplyTextDecorationColor(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.DecorationColor = parent.DecorationColor;
        else if (d.Lower == "currentcolor") { s.DecorationColor = null; _decorationLineSet = true; }
        else if (CssColor.TryParse(d.Value, s.Color, out var dc)) s.DecorationColor = dc;
    }

    private static void ApplyTextDecorationStyle(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.DecorationStyle = d.Lower switch
        {
            "solid" => DecorationStyle.Solid,
            "double" => DecorationStyle.Double,
            "dotted" => DecorationStyle.Dotted,
            "dashed" => DecorationStyle.Dashed,
            "wavy" => DecorationStyle.Wavy,
            "inherit" => parent.DecorationStyle,
            _ => s.DecorationStyle,
        };

    private static void ApplySkipInk(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.SkipInk = d.Lower switch
        {
            "auto" => SkipInk.Auto,
            "none" => SkipInk.None,
            "all" => SkipInk.All,
            "inherit" => parent.SkipInk,
            _ => s.SkipInk,
        };

    /// <summary>text-underline-position: auto | from-font | [ under || [ left | right ] ]; anything else is dropped.</summary>
    private static void ApplyUnderlinePosition(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        switch (d.Lower)
        {
            case "inherit": s.UnderlinePosition = parent.UnderlinePosition; return;
            case "auto" or "initial" or "unset" or "left" or "right": s.UnderlinePosition = UnderlinePosition.Auto; return;
            case "from-font": s.UnderlinePosition = UnderlinePosition.FromFont; return;
            case "under" or "under left" or "under right" or "left under" or "right under": s.UnderlinePosition = UnderlinePosition.Under; return;
        }
    }

    private void ApplyTextDecorationThickness(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.DecorationThickness = parent.DecorationThickness;
        else if (d.Lower is "auto" or "from-font" or "initial" or "unset") s.DecorationThickness = null;
        else if (TryParseLength(d.Value, d.Em, _rootFontSize, out var dt, _vw, _vh) && !dt.IsAuto) s.DecorationThickness = dt;
    }

    private void ApplyTextUnderlineOffset(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.UnderlineOffset = parent.UnderlineOffset;
        else if (d.Lower is "auto" or "initial" or "unset") s.UnderlineOffset = null;
        else if (TryParseLength(d.Value, d.Em, _rootFontSize, out var uo, _vw, _vh) && !uo.IsAuto) s.UnderlineOffset = uo;
    }

    private static void ApplyTextOverflow(ComputedStyle s, in DeclaredValue d)
    {
        // One value for both ends, or start and end: only the end of the line is cut, as in Chrome.
        var tov = CssParser.Tokens(d.Value.Trim());
        if (d.Inherit || tov.Count is not (1 or 2)) return;
        var end = tov[^1];
        if (GeneratedContent.UnquoteMark(end) is { } str) s.TextOverflow = str;
        else if (end.Equals("ellipsis", StringComparison.OrdinalIgnoreCase)) s.TextOverflow = "…";
        else if (end.Equals("clip", StringComparison.OrdinalIgnoreCase) || end.Equals("initial", StringComparison.OrdinalIgnoreCase)) s.TextOverflow = null;
    }

    private void ApplyLetterSpacing(ComputedStyle s, in DeclaredValue d)
    {
        if (d.Lower == "normal") s.LetterSpacing = 0;
        else if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var ls, _vw, _vh)) s.LetterSpacing = ls.Resolve(0);
    }

    private void ApplyWordSpacing(ComputedStyle s, in DeclaredValue d)
    {
        if (d.Lower == "normal") s.WordSpacing = 0;
        else if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var ws, _vw, _vh)) s.WordSpacing = ws.Resolve(0);
    }

    private void ApplyTabSize(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.TabSize = parent.TabSize;
        else if (int.TryParse(d.Lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tabs) && tabs >= 0) s.TabSize = Math.Min(tabs, 64);
        else if (TryParseLength(d.Value, d.Em, _rootFontSize, out var tabLen, _vw, _vh) && !tabLen.IsAuto && !tabLen.IsPercent)
        {
            // A length is turned into whole spaces of the current font.
            float space = Math.Max(0.01f, s.Font.CharWidth(' ', s.FontSize) + s.LetterSpacing + s.WordSpacing);
            s.TabSize = Math.Clamp((int)MathF.Round(tabLen.Value / space), 0, 64);
        }
    }

    private static void ApplyWhiteSpace(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.WhiteSpace = d.Lower switch
        {
            "normal" => WhiteSpace.Normal,
            "nowrap" => WhiteSpace.NoWrap,
            "pre" => WhiteSpace.Pre,
            "pre-wrap" or "break-spaces" => WhiteSpace.PreWrap,
            "pre-line" => WhiteSpace.PreLine,
            "inherit" => parent.WhiteSpace,
            _ => s.WhiteSpace,
        };

    /// <summary>
    /// text-wrap, the shorthand for text-wrap-mode and text-wrap-style: either may be left out, and is then reset to
    /// wrap or auto, so text-wrap: balance also makes nowrap text wrap again.
    /// </summary>
    private static void ApplyTextWrap(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit)
        {
            s.WhiteSpace = parent.WhiteSpace;
            s.TextWrap = parent.TextWrap;
            return;
        }
        bool? wrap = null;
        TextWrapStyle? style = null;
        foreach (var t in CssParser.Tokens(d.Lower))
        {
            if (wrap == null && WrapMode(t) is { } w) wrap = w;
            else if (style == null && WrapStyle(t) is { } ws) style = ws;
            else return; // invalid: the declaration is ignored
        }
        if (wrap == null && style == null) return;
        s.WhiteSpace = WithWrapMode(s.WhiteSpace, wrap ?? true);
        s.TextWrap = style ?? TextWrapStyle.Auto;
    }

    private static void ApplyTextWrapMode(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.WhiteSpace = WithWrapMode(s.WhiteSpace, Wraps(parent.WhiteSpace));
        else if (WrapMode(d.Lower) is { } wrap) s.WhiteSpace = WithWrapMode(s.WhiteSpace, wrap);
    }

    private static void ApplyTextWrapStyle(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.TextWrap = parent.TextWrap;
        else if (WrapStyle(d.Lower) is { } style) s.TextWrap = style;
    }

    /// <summary>A text-wrap-mode keyword: true for wrap, false for nowrap, null for anything else.</summary>
    private static bool? WrapMode(string t) => t switch
    {
        "wrap" => true,
        "nowrap" => false,
        _ => null,
    };

    private static TextWrapStyle? WrapStyle(string t) => t switch
    {
        "auto" or "stable" => TextWrapStyle.Auto,
        "balance" => TextWrapStyle.Balance,
        "pretty" => TextWrapStyle.Pretty,
        _ => null,
    };

    /// <summary>Whether lines may wrap with this white-space: the text-wrap-mode part of it.</summary>
    private static bool Wraps(WhiteSpace ws) => ws is WhiteSpace.Normal or WhiteSpace.PreWrap or WhiteSpace.PreLine;

    /// <summary>
    /// white-space with its text-wrap-mode part set: white-space is a shorthand for how white space collapses and for
    /// text-wrap-mode, so the collapsing stays as it was.
    /// </summary>
    private static WhiteSpace WithWrapMode(WhiteSpace ws, bool wrap) => ws switch
    {
        WhiteSpace.Normal or WhiteSpace.NoWrap => wrap ? WhiteSpace.Normal : WhiteSpace.NoWrap,
        WhiteSpace.Pre or WhiteSpace.PreWrap => wrap ? WhiteSpace.PreWrap : WhiteSpace.Pre,
        _ => wrap ? WhiteSpace.PreLine : WhiteSpace.PreLineNoWrap,
    };

    private static void ApplyOverflowWrap(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.OverflowWrap = d.Lower switch
        {
            "normal" => OverflowWrap.Normal,
            "break-word" => OverflowWrap.BreakWord,
            "anywhere" => OverflowWrap.Anywhere,
            "inherit" => parent.OverflowWrap,
            _ => s.OverflowWrap,
        };

    private static void ApplyWordBreak(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        switch (d.Lower)
        {
            case "normal": s.WordBreak = WordBreak.Normal; break;
            case "break-all": s.WordBreak = WordBreak.BreakAll; break;
            case "keep-all": s.WordBreak = WordBreak.KeepAll; break;
            // Deprecated: the same as word-break: normal with overflow-wrap: anywhere.
            case "break-word": s.WordBreak = WordBreak.Normal; s.OverflowWrap = OverflowWrap.Anywhere; break;
            case "inherit": s.WordBreak = parent.WordBreak; break;
        }
    }

    private static void ApplyLineBreak(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.LineBreak = d.Lower switch
        {
            "auto" => LineBreak.Auto,
            "loose" => LineBreak.Loose,
            "normal" => LineBreak.Normal,
            "strict" => LineBreak.Strict,
            "anywhere" => LineBreak.Anywhere,
            "inherit" => parent.LineBreak,
            _ => s.LineBreak,
        };

    private static void ApplyHyphens(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        s.NoHyphens = d.Inherit ? parent.NoHyphens : d.Lower == "none";
        s.HyphensAuto = d.Inherit ? parent.HyphensAuto : d.Lower == "auto";
    }

    private static void ApplyHyphenateCharacter(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.HyphenateCharacter = parent.HyphenateCharacter;
        else if (d.Lower == "auto" || d.Lower == "initial" || d.Lower == "unset") s.HyphenateCharacter = null;
        else if (GeneratedContent.UnquoteMark(d.Value) is { } hyphen) s.HyphenateCharacter = hyphen;
    }

    // quotes: none, auto, or pairs of strings for the nesting levels. A list with an odd number of strings is invalid.
    private static void ApplyQuotes(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit || d.Lower == "unset") { s.Quotes = parent.Quotes; return; }
        if (d.Lower is "auto" or "initial") { s.Quotes = null; return; }
        if (d.Lower == "none") { s.Quotes = Array.Empty<string>(); return; }
        var tokens = CssParser.Tokens(d.Value);
        if (tokens.Count == 0 || tokens.Count % 2 != 0 || tokens.Count > MaxQuoteStrings) return;
        var strings = new string[tokens.Count];
        for (int i = 0; i < strings.Length; i++)
            if (GeneratedContent.UnquoteMark(tokens[i]) is { } str) strings[i] = str;
            else return;
        s.Quotes = strings;
    }

    private const int MaxQuoteStrings = 64;

    private static void ApplyHyphenateLimitChars(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit || d.Lower == "unset") s.HyphenateLimitChars = parent.HyphenateLimitChars;
        else if (d.Lower == "initial") s.HyphenateLimitChars = default;
        else if (ParseHyphenateLimitChars(d.Lower) is { } limits) s.HyphenateLimitChars = limits;
    }

    /// <summary>
    /// Parses hyphenate-limit-chars: up to three values, each auto or a positive integer, for the shortest word that is
    /// hyphenated and the fewest characters before and after a hyphen. A missing after value is the same as the before
    /// value, and a missing before value is auto. Anything else is an invalid declaration (null).
    /// </summary>
    internal static HyphenateLimits? ParseHyphenateLimitChars(string lower)
    {
        Span<int> values = stackalloc int[3];
        int count = 0;
        foreach (var token in lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (count == 3) return null;
            if (token == "auto") values[count++] = 0;
            else if (HyphenateLimit(token) is { } n) values[count++] = n;
            else return null;
        }
        if (count == 0) return null;
        if (count == 1) values[1] = 0;
        if (count < 3) values[2] = values[1];
        return new HyphenateLimits(values[0], values[1], values[2]);
    }

    // A positive CSS integer. Huge values are clamped rather than rejected, as Chrome does; no word is that long anyway.
    private static int? HyphenateLimit(string token)
    {
        var digits = token.AsSpan(token.StartsWith('+') ? 1 : 0);
        if (digits.IsEmpty) return null;
        foreach (char c in digits)
            if (c is < '0' or > '9') return null;
        int n = Invariant.TryParseInt(digits, out int v) ? Math.Min(v, HyphenateLimits.Max) : HyphenateLimits.Max;
        return n >= 1 ? n : null;
    }

    private static void ApplyLineClamp(ComputedStyle s, in DeclaredValue d)
        => s.LineClamp = int.TryParse(d.Lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int clamp) && clamp > 0 ? Math.Min(clamp, 10000) : 0;

    private void ApplyVerticalAlign(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        // A length raises the box by that much (a negative one lowers it); a percentage is of the box's line height.
        if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var shift, _vw, _vh) && !shift.IsAuto)
        {
            s.VerticalAlign = VerticalAlign.Length;
            s.VerticalAlignLength = shift;
            return;
        }
        if (d.Lower == "inherit") s.VerticalAlignLength = parent.VerticalAlignLength;
        s.VerticalAlign = d.Lower switch
        {
            "baseline" => VerticalAlign.Baseline,
            "sub" => VerticalAlign.Sub,
            "super" => VerticalAlign.Super,
            "top" => VerticalAlign.Top,
            "middle" => VerticalAlign.Middle,
            "bottom" => VerticalAlign.Bottom,
            "text-top" => VerticalAlign.TextTop,
            "text-bottom" => VerticalAlign.TextBottom,
            "inherit" => parent.VerticalAlign,
            _ => s.VerticalAlign,
        };
    }

    private static void ApplyDirection(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.Rtl = d.Lower switch { "rtl" => true, "ltr" => false, "inherit" => parent.Rtl, "initial" => false, _ => s.Rtl };

    private static void ApplyWritingMode(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.WritingMode = d.Lower switch
        {
            "horizontal-tb" or "lr" or "lr-tb" or "rl" or "rl-tb" or "initial" => WritingMode.HorizontalTb,
            "vertical-rl" or "tb" or "tb-rl" => WritingMode.VerticalRl,
            "vertical-lr" => WritingMode.VerticalLr,
            "sideways-rl" => WritingMode.SidewaysRl,
            "sideways-lr" => WritingMode.SidewaysLr,
            "inherit" or "unset" => parent.WritingMode,
            _ => s.WritingMode,
        };

    private static void ApplyUnicodeBidi(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.UnicodeBidi = d.Lower switch
        {
            "normal" => UnicodeBidi.Normal,
            "embed" => UnicodeBidi.Embed,
            "isolate" or "-webkit-isolate" => UnicodeBidi.Isolate,
            "bidi-override" => UnicodeBidi.BidiOverride,
            "isolate-override" or "-webkit-isolate-override" => UnicodeBidi.IsolateOverride,
            "plaintext" or "-webkit-plaintext" => UnicodeBidi.Plaintext,
            "inherit" => parent.UnicodeBidi,
            _ => s.UnicodeBidi,
        };
}
