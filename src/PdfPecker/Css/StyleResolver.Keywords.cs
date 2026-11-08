namespace PdfPecker.Css;

/// <summary>
/// The CSS-wide keywords: inherit, initial and unset on single properties, revert and revert-layer, which roll the
/// cascade back, and the all shorthand, which applies one of them to every property but direction, unicode-bidi and
/// custom properties.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>The document's root style, whose child has every property at its initial value.</summary>
    private ComputedStyle? _rootStyle;

    private ComputedStyle? _initialStyle;

    /// <summary>A style with every property at its initial value.</summary>
    private ComputedStyle InitialStyle => _initialStyle ??= (_rootStyle ?? new ComputedStyle()).CreateChild();

    /// <summary>What the all shorthand does to the declarations that come before it in the cascade.</summary>
    private enum AllKeyword : byte { None, Unset, Initial, Inherit, Revert, RevertLayer }

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
                "revert" => AllKeyword.Revert,
                "revert-layer" => AllKeyword.RevertLayer,
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
        if (!ReachedByAll(prop)) return false;
        return all.Keyword switch
        {
            AllKeyword.Revert => !RevertKeeps(i, all.Index, layer: false),
            AllKeyword.RevertLayer => !RevertKeeps(i, all.Index, layer: true),
            _ => true,
        };
    }

    private static bool ReachedByAll(string prop)
        => prop is not ("direction" or "unicode-bidi" or LangProperty or ControlWidthProperty) && !prop.StartsWith("--", StringComparison.Ordinal);

    /// <summary>
    /// The index in the working buffer of the declaration being applied, which revert and revert-layer roll back from;
    /// -1 outside the cascade.
    /// </summary>
    private int _applyIndex = -1;

    /// <summary>
    /// While a property is rolled back, the only property <see cref="Apply"/> sets; the declarations applied again for it
    /// leave every other property alone.
    /// </summary>
    private string? _revertFilter;

    /// <summary>Set when a declaration applied again for <see cref="_revertFilter"/> set that property.</summary>
    private bool _revertHit;

    /// <summary>
    /// Set when a declaration applied again for <see cref="_revertFilter"/> reverts the property itself: 1 for revert, 2
    /// for revert-layer. The rollback then goes on from that declaration rather than recursing.
    /// </summary>
    private byte _pendingRevert;

    private bool[] _overridden = new bool[64];
    private readonly HashSet<string> _laterProperties = new(StringComparer.Ordinal);
    private bool _overriddenKnown;

    /// <summary>
    /// Whether the declaration at <paramref name="i"/> reverts its property while a later declaration of the same
    /// property follows, which sets it anyway. The cascade skips such a revert: rolling back once for every one of many
    /// layers that revert a property would cost the square of the declarations.
    /// </summary>
    private bool RevertOverridden(int i)
    {
        var d = _decls[i];
        if (d.HasVar || _attrUsed && d.UsesAttr || !IsRevert(d.Value, out _)) return false;
        if (!_overriddenKnown)
        {
            _overriddenKnown = true;
            if (_overridden.Length < _count) _overridden = new bool[Math.Max(_count, _overridden.Length * 2)];
            _laterProperties.Clear();
            for (int j = _count - 1; j >= 0; j--) _overridden[j] = !_laterProperties.Add(_decls[j].Property);
        }
        return _overridden[i];
    }

    /// <summary>Whether a value is revert or revert-layer, and which.</summary>
    private static bool IsRevert(string value, out bool layer)
    {
        var v = value.AsSpan().Trim();
        layer = v.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);
        return layer || v.Equals("revert", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the declaration at <paramref name="j"/> still counts after the one at <paramref name="at"/> reverts the
    /// property. revert rolls back to the user agent's declarations, as if the page had none for the property (the dir
    /// attribute counts as the user agent's, as in HTML's style sheet; other presentational attributes are the page's).
    /// revert-layer rolls back to the declarations of other origins and other cascade layers; a style attribute is a
    /// layer of its own above the page's style sheets.
    /// </summary>
    private bool RevertKeeps(int j, int at, bool layer)
    {
        long tierJ = _keys[j] >> 60, tierAt = _keys[at] >> 60;
        if (!layer)
        {
            if (tierAt is TierUa or TierUaImportant) return false;
            return tierJ is TierUa or TierUaImportant || tierJ == TierHint && _decls[j].Property is "direction" or "unicode-bidi";
        }
        return Origin(tierJ) != Origin(tierAt) || LayerRank(j) != LayerRank(at);

        static int Origin(long tier) => tier switch
        {
            TierUa or TierUaImportant => 0,
            TierHint => 1,
            TierAuthor or TierAuthorImportant => 2,
            _ => 3,
        };
    }

    /// <summary>The cascade layer of a declaration in the working buffer; important declarations store it reversed.</summary>
    private int LayerRank(int i)
    {
        int bits = (int)((_keys[i] >> 52) & 0xFF);
        long tier = _keys[i] >> 60;
        return tier is TierAuthorImportant or TierInlineImportant or TierUaImportant ? UnlayeredRank - bits : bits;
    }

    /// <summary>
    /// revert and revert-layer on one property (a longhand, after logical properties are mapped): the property starts
    /// unset, and the last declaration before the reverting one that still counts sets it again. Declarations are
    /// looked at from the reverting one backwards, and the first that sets the property is the one that wins. One that
    /// reverts the property again moves the starting point back to itself, so a chain of reverts costs one pass.
    /// </summary>
    private void Revert(ComputedStyle s, ComputedStyle parent, string prop, bool layer)
    {
        int at = _applyIndex, origin = at;
        Apply(s, parent, prop, "unset");
        if (at <= 0) return;
        _revertFilter = prop;
        try
        {
            for (int j = at - 1; j >= 0; j--)
            {
                if (!RevertKeeps(j, at, layer)) continue;
                var d = _decls[j];
                if (d.Property == "font-size" || d.Property.StartsWith("--", StringComparison.Ordinal)) continue;
                _revertHit = false;
                _pendingRevert = 0;
                _applyIndex = j;
                if (d.Property != "all") ApplyDeclaration(s, parent, d);
                else if (ReachedByAll(prop) && AllValue(d.Value) is { } keyword) Apply(s, parent, prop, keyword);
                if (_pendingRevert != 0)
                {
                    layer = _pendingRevert == 2;
                    at = j;
                    _pendingRevert = 0;
                    Apply(s, parent, prop, "unset");
                }
                else if (_revertHit) break;
            }
        }
        finally
        {
            _revertFilter = null;
            _pendingRevert = 0;
            _applyIndex = origin;
        }
    }

    /// <summary>The CSS-wide keyword of an all declaration, or null for a value all does not take.</summary>
    private static string? AllValue(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v is "unset" or "initial" or "inherit" or "revert" or "revert-layer" ? v : null;
    }

    /// <summary>
    /// The value of a property whose winning declaration is at <paramref name="i"/>, for the properties the cascade
    /// computes before the others (direction, writing-mode, font-size and font-family): revert and revert-layer are
    /// followed back to the declaration they roll back to, and "unset" stands for none. Null when var() or if() fails,
    /// which makes the property unset too.
    /// </summary>
    private string? WinningValue(int i, ComputedStyle s)
    {
        string prop = _decls[i].Property;
        while (true)
        {
            var value = Substituted(_decls[i], s);
            if (value == null || !IsRevert(value, out bool layer)) return value;
            int j = i - 1;
            for (; j >= 0; j--)
            {
                if (!RevertKeeps(j, i, layer)) continue;
                var p = _decls[j].Property;
                if (p == prop || (p is "writing-mode" or "-webkit-writing-mode" && prop is "writing-mode" or "-webkit-writing-mode")
                    || (p == "all" && ReachedByAll(prop) && AllValue(_decls[j].Value) != null)) break;
            }
            if (j < 0) return "unset";
            i = j;
        }
    }

    /// <summary>
    /// The value of the custom property declared at <paramref name="i"/>: revert and revert-layer are followed back, and
    /// unset is inherit, as custom properties inherit, unless the property is registered as not inherited, when it is
    /// the registered initial value.
    /// </summary>
    private string CustomValue(int i)
    {
        string prop = _decls[i].Property;
        while (true)
        {
            var value = _decls[i].Value;
            if (!IsRevert(value, out bool layer))
            {
                if (!value.AsSpan().Trim().Equals("unset", StringComparison.OrdinalIgnoreCase)) return value;
                break;
            }
            int j = i - 1;
            while (j >= 0 && (_decls[j].Property != prop || !RevertKeeps(j, i, layer))) j--;
            if (j < 0) break;
            i = j;
        }
        foreach (var registered in _registeredEach)
            if (registered.Property == prop) return registered.Value;
        return "inherit";
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
                s.Quirks = parent.Quirks;
                s.Hyphenation = parent.Hyphenation;
                s.Fonts = parent.Fonts;
                s.MissingFonts = parent.MissingFonts;
                s.PageName = parent.PageName;
                s.CustomProperties = parent.CustomProperties;
                s.Zoom = parent.Zoom;
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
            case "visibility": s.Visible = from.Visible; s.Collapsed = from.Collapsed; break;
            case "image-orientation": s.ImageOrientationNone = from.ImageOrientationNone; break;
            case "image-rendering": s.ImagePixelated = from.ImagePixelated; break;
            case "-pdfpecker-image-quality": s.ImageQuality = from.ImageQuality; break;
            case "-pdfpecker-image-max-resolution": s.ImageMaxDpi = from.ImageMaxDpi; break;
            case "accent-color": s.AccentColor = from.AccentColor; break;
            case "appearance": s.AppearanceNone = from.AppearanceNone; break;
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
            case "text-indent": s.TextIndent = from.TextIndent; s.TextIndentHanging = from.TextIndentHanging; s.TextIndentEachLine = from.TextIndentEachLine; break;
            case "initial-letter": s.InitialLetterSize = from.InitialLetterSize; s.InitialLetterSink = from.InitialLetterSink; break;
            case "text-justify": s.TextJustify = from.TextJustify; break;
            case "text-spacing-trim": s.TextSpacingTrim = from.TextSpacingTrim; break;
            case "text-decoration-skip-ink": s.SkipInk = from.SkipInk; break;
            case "text-underline-position": s.UnderlinePosition = from.UnderlinePosition; break;
            case "text-emphasis-style": s.EmphasisShape = from.EmphasisShape; s.EmphasisOpen = from.EmphasisOpen; s.EmphasisString = from.EmphasisString; break;
            case "text-emphasis-color": s.EmphasisColor = from.EmphasisColor; break;
            case "text-emphasis-position": s.EmphasisUnder = from.EmphasisUnder; break;
            case "text-box-trim": s.TextBoxTrim = from.TextBoxTrim; break;
            case "text-box-edge": s.TextEdgeOver = from.TextEdgeOver; s.TextEdgeUnder = from.TextEdgeUnder; break;
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
            case "column-rule-style":
            case "column-rule-width":
            case "column-rule-color":
            case "row-rule-style":
            case "row-rule-width":
            case "row-rule-color":
            case "column-rule-break":
            case "row-rule-break":
            case "column-rule-visibility-items":
            case "row-rule-visibility-items":
            case "rule-overlap":
            case "column-rule-inset-cap-start":
            case "column-rule-inset-cap-end":
            case "column-rule-inset-junction-start":
            case "column-rule-inset-junction-end":
            case "row-rule-inset-cap-start":
            case "row-rule-inset-cap-end":
            case "row-rule-inset-junction-start":
            case "row-rule-inset-junction-end":
                CopyGapRule(s, from, prop);
                break;
            default: return false;
        }
        return true;
    }
}
