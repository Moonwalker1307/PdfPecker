namespace PdfPecker.Css;

/// <summary>
/// The CSS-wide keywords: inherit, initial and unset on single properties, and the all shorthand, which applies one of
/// them (or revert) to every property but direction, unicode-bidi and custom properties.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>The document's root style, whose child has every property at its initial value.</summary>
    private ComputedStyle? _rootStyle;

    private ComputedStyle? _initialStyle;

    /// <summary>A style with every property at its initial value.</summary>
    private ComputedStyle InitialStyle => _initialStyle ??= (_rootStyle ?? new ComputedStyle()).CreateChild();

    /// <summary>What the all shorthand does to the declarations that come before it in the cascade.</summary>
    private enum AllKeyword : byte { None, Unset, Initial, Inherit, Revert }

    /// <summary>
    /// The last all declaration in the working buffer and its keyword. Declarations sorted before it lose, except
    /// those all does not reach; revert keeps the user agent's.
    /// </summary>
    private (int Index, AllKeyword Keyword) FindAll()
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property != "all") continue;
            var keyword = _decls[i].Value.Trim().ToLowerInvariant() switch
            {
                "unset" => AllKeyword.Unset,
                "initial" => AllKeyword.Initial,
                "inherit" => AllKeyword.Inherit,
                // revert-layer is taken as revert: layered author rules are rare where all is used.
                "revert" or "revert-layer" => AllKeyword.Revert,
                _ => AllKeyword.None,
            };
            if (keyword != AllKeyword.None) return (i, keyword);
        }
        return (-1, AllKeyword.None);
    }

    /// <summary>Whether the all declaration at <paramref name="all"/> overrides the declaration at <paramref name="i"/>.</summary>
    private bool ResetByAll(int i, (int Index, AllKeyword Keyword) all)
    {
        if (i >= all.Index) return false;
        var prop = _decls[i].Property;
        if (prop is "direction" or "unicode-bidi" or LangProperty or ControlWidthProperty || prop.StartsWith("--", StringComparison.Ordinal)) return false;
        if (all.Keyword != AllKeyword.Revert) return true;
        long tier = _keys[i] >> 60;
        return tier is not (TierUa or TierUaImportant);
    }

    /// <summary>The style an element starts from before its declarations, after an all declaration.</summary>
    private ComputedStyle StartingStyle(ComputedStyle parent, AllKeyword all)
    {
        switch (all)
        {
            case AllKeyword.Initial:
            {
                var s = InitialStyle.Clone();
                s.ParentFontSize = parent.FontSize;
                s.ParentBaselineShift = parent.ChildBaselineShift;
                s.ParentStyle = parent;
                // all leaves direction alone, and the language and the document's fonts are not properties.
                s.Rtl = parent.Rtl;
                s.Lang = parent.Lang;
                s.Hyphenation = parent.Hyphenation;
                s.Fonts = parent.Fonts;
                s.MissingFonts = parent.MissingFonts;
                s.PageName = parent.PageName;
                s.CustomProperties = parent.CustomProperties;
                return s;
            }
            case AllKeyword.Inherit:
            {
                var s = parent.Clone();
                s.ParentFontSize = parent.FontSize;
                s.ParentBaselineShift = parent.ChildBaselineShift;
                s.ParentStyle = parent;
                s.UnicodeBidi = UnicodeBidi.Normal;
                s.CanvasBackground = false;
                return s;
            }
            default:
                return parent.CreateChild();
        }
    }

    /// <summary>
    /// Applies inherit, initial or unset to one property by copying its computed value from the parent, from the
    /// initial style, or from whichever of the two the property takes when nothing sets it. Returns false for
    /// properties this does not cover, which handle the keywords themselves or ignore them.
    /// </summary>
    private bool ApplyCssWideKeyword(ComputedStyle s, ComputedStyle parent, string prop, string lower)
    {
        ComputedStyle from;
        switch (lower)
        {
            case "inherit": from = parent; break;
            case "initial": from = InitialStyle; break;
            // A child's starting style holds the parent's value for inherited properties and the initial value for the rest.
            case "unset": from = parent.CreateChild(); break;
            default: return false;
        }
        return CopyProperty(s, from, prop);
    }

    /// <summary>Copies the computed value of one property; false when the property is not one this covers.</summary>
    private static bool CopyProperty(ComputedStyle s, ComputedStyle from, string prop)
    {
        switch (prop)
        {
            case "margin-top": s.MarginTop = from.MarginTop; break;
            case "margin-right": s.MarginRight = from.MarginRight; break;
            case "margin-bottom": s.MarginBottom = from.MarginBottom; break;
            case "margin-left": s.MarginLeft = from.MarginLeft; break;
            case "padding-top": s.PaddingTop = from.PaddingTop; break;
            case "padding-right": s.PaddingRight = from.PaddingRight; break;
            case "padding-bottom": s.PaddingBottom = from.PaddingBottom; break;
            case "padding-left": s.PaddingLeft = from.PaddingLeft; break;
            case "width": s.Width = from.Width; break;
            case "height": s.Height = from.Height; break;
            case "min-width": s.MinWidth = from.MinWidth; break;
            case "min-height": s.MinHeight = from.MinHeight; break;
            case "max-width": s.MaxWidth = from.MaxWidth; break;
            case "max-height": s.MaxHeight = from.MaxHeight; break;
            case "top": s.Top = from.Top; break;
            case "right": s.Right = from.Right; break;
            case "bottom": s.Bottom = from.Bottom; break;
            case "left": s.Left = from.Left; break;
            case "box-sizing": s.BorderBox = from.BorderBox; break;
            case "visibility": s.Visible = from.Visible; break;
            case "image-orientation": s.ImageOrientationNone = from.ImageOrientationNone; break;
            case "border-top-width": s.BorderTopWidth = from.BorderTopWidth; break;
            case "border-right-width": s.BorderRightWidth = from.BorderRightWidth; break;
            case "border-bottom-width": s.BorderBottomWidth = from.BorderBottomWidth; break;
            case "border-left-width": s.BorderLeftWidth = from.BorderLeftWidth; break;
            case "border-top-style": s.BorderTopStyle = from.BorderTopStyle; break;
            case "border-right-style": s.BorderRightStyle = from.BorderRightStyle; break;
            case "border-bottom-style": s.BorderBottomStyle = from.BorderBottomStyle; break;
            case "border-left-style": s.BorderLeftStyle = from.BorderLeftStyle; break;
            case "border-top-color": s.BorderTopColor = from.BorderTopColor; break;
            case "border-right-color": s.BorderRightColor = from.BorderRightColor; break;
            case "border-bottom-color": s.BorderBottomColor = from.BorderBottomColor; break;
            case "border-left-color": s.BorderLeftColor = from.BorderLeftColor; break;
            case "outline-style": (s.OutlineStyle, s.OutlineAuto) = (from.OutlineStyle, from.OutlineAuto); break;
            case "outline-width": s.OutlineWidth = from.OutlineWidth; break;
            case "outline-color": s.OutlineColor = from.OutlineColor; break;
            case "outline-offset": s.OutlineOffset = from.OutlineOffset; break;
            case "color": s.Color = from.Color; break;
            case "background-color": s.BackgroundColor = from.BackgroundColor; break;
            case "opacity": s.Opacity = from.Opacity; break;
            case "box-shadow": s.BoxShadows = from.BoxShadows; break;
            case "text-shadow": s.TextShadows = from.TextShadows; break;
            case "-webkit-text-fill-color": s.TextFillColor = from.TextFillColor; break;
            case "-webkit-text-stroke-width": s.TextStrokeWidth = from.TextStrokeWidth; break;
            case "-webkit-text-stroke-color": s.TextStrokeColor = from.TextStrokeColor; break;
            case "paint-order": s.PaintStrokeFirst = from.PaintStrokeFirst; break;
            case "font-family": s.FontFamily = from.FontFamily; s.FontFamilyName = from.FontFamilyName; break;
            case "font-weight": s.FontWeight = from.FontWeight; s.Bold = from.Bold; break;
            case "font-style": s.Italic = from.Italic; break;
            case "line-height": s.LineHeightKind = from.LineHeightKind; s.LineHeightValue = from.LineHeightValue; break;
            case "text-align": s.TextAlign = from.TextAlign; s.BlockAlign = from.BlockAlign; break;
            case "text-indent": s.TextIndent = from.TextIndent; break;
            case "initial-letter": s.InitialLetterSize = from.InitialLetterSize; s.InitialLetterSink = from.InitialLetterSink; break;
            case "text-justify": s.TextJustify = from.TextJustify; break;
            case "text-spacing-trim": s.TextSpacingTrim = from.TextSpacingTrim; break;
            case "text-decoration-skip-ink": s.SkipInk = from.SkipInk; break;
            case "text-underline-position": s.UnderlinePosition = from.UnderlinePosition; break;
            case "text-transform": s.TextTransform = from.TextTransform; break;
            case "letter-spacing": s.LetterSpacing = from.LetterSpacing; break;
            case "word-spacing": s.WordSpacing = from.WordSpacing; break;
            case "white-space": s.WhiteSpace = from.WhiteSpace; break;
            case "vertical-align": s.VerticalAlign = from.VerticalAlign; s.VerticalAlignLength = from.VerticalAlignLength; break;
            case "list-style-position": s.ListStylePositionInside = from.ListStylePositionInside; break;
            case "flex-grow": s.FlexGrow = from.FlexGrow; break;
            case "flex-shrink": s.FlexShrink = from.FlexShrink; break;
            case "flex-basis": s.FlexBasis = from.FlexBasis; break;
            case "order": s.Order = from.Order; break;
            case "row-gap": s.RowGap = from.RowGap; break;
            case "column-gap": s.ColumnGap = from.ColumnGap; break;
            default: return false;
        }
        return true;
    }
}
