namespace PdfPecker.Css;

/// <summary>
/// Applying a declared value to a computed style. One switch on the property name hands each declaration to the method
/// for its property; the methods live in StyleResolver.Properties.*.cs, grouped by concern.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>
    /// A declared value prepared once for whichever property reads it, so that every property sees the same trimmed,
    /// folded and lower-cased text.
    /// </summary>
    private readonly struct DeclaredValue
    {
        public DeclaredValue(string raw, float em)
        {
            string v = raw.Trim();
            // Math functions that produce plain numbers (opacity: calc(1 / 2)) become the number; lengths are parsed later.
            if (CssMath.HasMath(v)) v = CssMath.FoldNumbers(v);
            Value = v;
            Lower = v.ToLowerInvariant();
            Em = em;
            Inherit = Lower == "inherit";
        }

        /// <summary>The trimmed value in its written case, which strings, URLs, names and feature tags need.</summary>
        public string Value { get; }

        /// <summary>The trimmed value in lower case, for keywords and numbers.</summary>
        public string Lower { get; }

        /// <summary>The element's font size before this declaration, which em units resolve against.</summary>
        public float Em { get; }

        /// <summary>Whether the value is the inherit keyword.</summary>
        public bool Inherit { get; }
    }

    private void Apply(ComputedStyle s, ComputedStyle parent, string prop, string raw)
    {
        var d = new DeclaredValue(raw, s.FontSize);
        if (d.Lower is "inherit" or "initial" or "unset" && ApplyCssWideKeyword(s, parent, prop, d.Lower)) return;

        // A string switch compiles to a hash jump, so one flat switch is the cheapest way to find the property, and this
        // runs for every declaration of every element. Single calls of a shared helper stay inline.
        switch (prop)
        {
            // ---- box generation, positioning and sizing (Properties.Box.cs)
            case "display": ApplyDisplay(s, parent, in d); break;
            case "float": ApplyFloat(s, parent, in d); break;
            case "clear": ApplyClear(s, parent, in d); break;
            case "shape-outside": ApplyShapeOutside(s, parent, in d); break;
            case "shape-margin": ApplyShapeMargin(s, parent, in d); break;
            case "shape-image-threshold": ApplyShapeImageThreshold(s, parent, in d); break;
            case "position": ApplyPosition(s, parent, in d); break;
            case "top": SetLength(ref s.Top, d.Lower, d.Em, d.Inherit ? parent.Top : null); break;
            case "right": SetLength(ref s.Right, d.Lower, d.Em, d.Inherit ? parent.Right : null); break;
            case "bottom": SetLength(ref s.Bottom, d.Lower, d.Em, d.Inherit ? parent.Bottom : null); break;
            case "left": SetLength(ref s.Left, d.Lower, d.Em, d.Inherit ? parent.Left : null); break;
            case "z-index": ApplyZIndex(s, parent, in d); break;
            case "overflow":
            case "overflow-x":
            case "overflow-y":
                ApplyOverflow(s, parent, prop, in d);
                break;
            case "visibility": s.Visible = d.Lower is not ("hidden" or "collapse"); break;
            case "box-sizing": s.BorderBox = d.Lower == "border-box"; break;
            case "margin-top": SetLength(ref s.MarginTop, d.Lower, d.Em, d.Inherit ? parent.MarginTop : null); break;
            case "margin-right": SetLength(ref s.MarginRight, d.Lower, d.Em, d.Inherit ? parent.MarginRight : null); break;
            case "margin-bottom": SetLength(ref s.MarginBottom, d.Lower, d.Em, d.Inherit ? parent.MarginBottom : null); break;
            case "margin-left": SetLength(ref s.MarginLeft, d.Lower, d.Em, d.Inherit ? parent.MarginLeft : null); break;
            case "padding-top": SetLength(ref s.PaddingTop, d.Lower, d.Em, d.Inherit ? parent.PaddingTop : null, noAuto: true); break;
            case "padding-right": SetLength(ref s.PaddingRight, d.Lower, d.Em, d.Inherit ? parent.PaddingRight : null, noAuto: true); break;
            case "padding-bottom": SetLength(ref s.PaddingBottom, d.Lower, d.Em, d.Inherit ? parent.PaddingBottom : null, noAuto: true); break;
            case "padding-left": SetLength(ref s.PaddingLeft, d.Lower, d.Em, d.Inherit ? parent.PaddingLeft : null, noAuto: true); break;
            // Logical properties map to the physical side the direction gives.
            case "margin-inline-start": Apply(s, parent, s.Rtl ? "margin-right" : "margin-left", raw); break;
            case "margin-inline-end": Apply(s, parent, s.Rtl ? "margin-left" : "margin-right", raw); break;
            case "padding-inline-start": Apply(s, parent, s.Rtl ? "padding-right" : "padding-left", raw); break;
            case "padding-inline-end": Apply(s, parent, s.Rtl ? "padding-left" : "padding-right", raw); break;
            case "margin-block-start": Apply(s, parent, "margin-top", raw); break;
            case "margin-block-end": Apply(s, parent, "margin-bottom", raw); break;
            case "padding-block-start": Apply(s, parent, "padding-top", raw); break;
            case "padding-block-end": Apply(s, parent, "padding-bottom", raw); break;
            case "inset-inline-start": Apply(s, parent, s.Rtl ? "right" : "left", raw); break;
            case "inset-inline-end": Apply(s, parent, s.Rtl ? "left" : "right", raw); break;
            case "inset-block-start": Apply(s, parent, "top", raw); break;
            case "inset-block-end": Apply(s, parent, "bottom", raw); break;
            // Every box is horizontal, so the inline axis is the width and the block axis the height.
            case "inline-size": Apply(s, parent, "width", raw); break;
            case "block-size": Apply(s, parent, "height", raw); break;
            case "min-inline-size": Apply(s, parent, "min-width", raw); break;
            case "min-block-size": Apply(s, parent, "min-height", raw); break;
            case "max-inline-size": Apply(s, parent, "max-width", raw); break;
            case "max-block-size": Apply(s, parent, "max-height", raw); break;
            case "width": ApplyWidth(s, parent, in d); break;
            case "height": SetLength(ref s.Height, d.Lower, d.Em, d.Inherit ? parent.Height : null); break;
            case "min-width": SetLength(ref s.MinWidth, d.Lower, d.Em, null); break;
            case "min-height": SetLength(ref s.MinHeight, d.Lower, d.Em, null); break;
            case "max-width": SetLength(ref s.MaxWidth, d.Lower == "none" ? "auto" : d.Lower, d.Em, null); break;
            case "max-height": SetLength(ref s.MaxHeight, d.Lower == "none" ? "auto" : d.Lower, d.Em, null); break;
            case "aspect-ratio": ApplyAspectRatio(s, parent, in d); break;
            case "object-fit": ApplyObjectFit(s, parent, in d); break;
            case "object-position": ApplyObjectPosition(s, parent, in d); break;
            case "container-type": ApplyContainerType(s, parent, in d); break;
            case "container-name": ApplyContainerName(s, parent, in d); break;
            case "table-layout": s.TableLayoutFixed = d.Lower == "fixed"; break;
            case "border-collapse": s.BorderCollapse = d.Lower == "collapse"; break;
            case "caption-side": s.CaptionBottom = d.Lower == "bottom"; break;
            case "empty-cells": s.EmptyCellsHide = d.Lower == "hide"; break;
            case "border-spacing-h": ApplyBorderSpacing(ref s.BorderSpacingH, in d); break;
            case "border-spacing-v": ApplyBorderSpacing(ref s.BorderSpacingV, in d); break;

            // ---- fonts and text (Properties.Text.cs)
            case "font-family": ApplyFontFamily(s, parent, in d); break;
            case "font-weight": ApplyFontWeight(s, parent, in d); break;
            case "font-stretch": ApplyFontStretch(s, parent, in d); break;
            case "font-style": ApplyFontStyle(s, parent, in d); break;
            case "font-variation-settings": ApplyFontVariationSettings(s, parent, in d); break;
            case "font-optical-sizing": s.FontOpticalSizingNone = d.Inherit ? parent.FontOpticalSizingNone : d.Lower == "none"; break;
            case "font-variant-caps": ApplyFontVariantCaps(s, parent, in d); break;
            case "font-variant-ligatures": ApplyFontVariantLigatures(s, parent, in d); break;
            case "font-variant-numeric": ApplyFontVariantNumeric(s, parent, in d); break;
            case "font-feature-settings": ApplyFontFeatureSettings(s, parent, in d); break;
            case "font-kerning": ApplyFontKerning(s, parent, in d); break;
            case "font-synthesis": ApplyFontSynthesis(s, parent, in d); break;
            case "font-synthesis-weight": ApplyFontSynthesisPart(s, parent, FontSynthesis.Weight, in d); break;
            case "font-synthesis-style": ApplyFontSynthesisPart(s, parent, FontSynthesis.Style, in d); break;
            case "font-synthesis-small-caps": ApplyFontSynthesisPart(s, parent, FontSynthesis.SmallCaps, in d); break;
            case "font-synthesis-position": ApplyFontSynthesisPart(s, parent, FontSynthesis.Position, in d); break;
            case "font-size-adjust": ApplyFontSizeAdjust(s, parent, in d); break;
            case "font-variant-east-asian": ApplyFontVariantEastAsian(s, parent, in d); break;
            case "font-variant-position": ApplyFontVariantPosition(s, parent, in d); break;
            case "line-height": ApplyLineHeight(s, parent, in d); break;
            case "text-align": ApplyTextAlign(s, parent, in d); break;
            case "text-align-last": ApplyTextAlignLast(s, parent, in d); break;
            case "text-justify": ApplyTextJustify(s, parent, in d); break;
            case "text-spacing-trim": ApplyTextSpacingTrim(s, parent, in d); break;
            case "text-indent": ApplyTextIndent(s, in d); break;
            case "text-transform": ApplyTextTransform(s, parent, in d); break;
            case "text-decoration-line": ApplyTextDecorationLine(s, parent, in d); break;
            case "text-decoration-color": ApplyTextDecorationColor(s, parent, in d); break;
            case "text-decoration-style": ApplyTextDecorationStyle(s, parent, in d); break;
            case "text-decoration-thickness": ApplyTextDecorationThickness(s, parent, in d); break;
            case "text-underline-offset": ApplyTextUnderlineOffset(s, parent, in d); break;
            case "text-decoration-skip-ink": ApplySkipInk(s, parent, in d); break;
            case "text-overflow": ApplyTextOverflow(s, in d); break;
            case "letter-spacing": ApplyLetterSpacing(s, in d); break;
            case "word-spacing": ApplyWordSpacing(s, in d); break;
            case "tab-size":
            case "-moz-tab-size":
                ApplyTabSize(s, parent, in d);
                break;
            case "white-space": ApplyWhiteSpace(s, parent, in d); break;
            case "text-wrap": ApplyTextWrap(s, parent, in d); break;
            case "text-wrap-mode": ApplyTextWrapMode(s, parent, in d); break;
            case "text-wrap-style": ApplyTextWrapStyle(s, parent, in d); break;
            case "overflow-wrap": ApplyOverflowWrap(s, parent, in d); break;
            case "word-break": ApplyWordBreak(s, parent, in d); break;
            case "line-break": ApplyLineBreak(s, parent, in d); break;
            case "hyphens":
            case "-webkit-hyphens":
                ApplyHyphens(s, parent, in d);
                break;
            case "hyphenate-character":
            case "-webkit-hyphenate-character":
                ApplyHyphenateCharacter(s, parent, in d);
                break;
            case "quotes": ApplyQuotes(s, parent, in d); break;
            case "hyphenate-limit-chars": ApplyHyphenateLimitChars(s, parent, in d); break;
            case "-webkit-line-clamp":
            case "line-clamp":
                ApplyLineClamp(s, in d);
                break;
            case "vertical-align": ApplyVerticalAlign(s, parent, in d); break;
            case "direction": ApplyDirection(s, parent, in d); break;
            case "unicode-bidi": ApplyUnicodeBidi(s, parent, in d); break;
            case "writing-mode":
            case "-webkit-writing-mode":
                ApplyWritingMode(s, parent, in d);
                break;
            case LangProperty: s.Lang = d.Value; break;
            case ControlWidthProperty: SetLength(ref s.ControlWidth, d.Lower, d.Em, null); break;

            // ---- flexbox, grid and multi-column layout (Properties.Layout.cs)
            case "flex-direction": ApplyFlexDirection(s, in d); break;
            case "flex-wrap": ApplyFlexWrap(s, in d); break;
            case "flex-grow": ApplyFlexGrow(s, in d); break;
            case "flex-shrink": ApplyFlexShrink(s, in d); break;
            case "flex-basis": ApplyFlexBasis(s, in d); break;
            case "order": ApplyOrder(s, in d); break;
            case "justify-content": s.JustifyContent = ParseAlign(d.Lower, s.JustifyContent); break;
            case "align-items": s.AlignItems = ParseAlign(d.Lower, s.AlignItems); break;
            case "align-content": s.AlignContent = ParseAlign(d.Lower, s.AlignContent); break;
            case "justify-items": s.JustifyItems = ParseAlign(d.Lower, s.JustifyItems); break;
            case "align-self": s.AlignSelf = ParseAlign(d.Lower, s.AlignSelf); break;
            case "justify-self": s.JustifySelf = ParseAlign(d.Lower, s.JustifySelf); break;
            case "row-gap":
            case "column-gap":
                ApplyGap(s, prop, in d);
                break;
            case "grid-template-columns": s.GridTemplateColumns = d.Lower == "none" ? null : ParseTrackList(d.Lower, d.Em); break;
            case "grid-template-rows": s.GridTemplateRows = d.Lower == "none" ? null : ParseTrackList(d.Lower, d.Em); break;
            case "grid-auto-columns": s.GridAutoColumns = ParseAutoTracks(in d); break;
            case "grid-auto-rows": s.GridAutoRows = ParseAutoTracks(in d); break;
            case "grid-auto-flow": ApplyGridAutoFlow(s, in d); break;
            case "grid-template-areas": s.GridTemplateAreas = ParseGridAreas(d.Value); break;
            case "grid-column-start": s.GridColumnStart = ParseGridLine(d.Lower); break;
            case "grid-column-end": s.GridColumnEnd = ParseGridLine(d.Lower); break;
            case "grid-row-start": s.GridRowStart = ParseGridLine(d.Lower); break;
            case "grid-row-end": s.GridRowEnd = ParseGridLine(d.Lower); break;
            case "column-count": ApplyColumnCount(s, in d); break;
            case "column-width": ApplyColumnWidth(s, in d); break;
            case "column-rule-style": s.ColumnRuleStyle = ParseBorderStyle(d.Lower, s.ColumnRuleStyle); break;
            case "column-rule-width": s.ColumnRuleWidth = BorderWidth(d.Lower, d.Em, s.ColumnRuleWidth); break;
            case "column-rule-color": s.ColumnRuleColor = BorderColor(d.Value, s); break;
            case "column-span": s.ColumnSpanAll = d.Lower == "all"; break;
            case "column-fill": s.ColumnFillAuto = d.Lower == "auto"; break;

            // ---- colours, backgrounds, borders, outlines and shadows (Properties.Paint.cs)
            case "color": ApplyColor(s, parent, in d); break;
            case "opacity": ApplyOpacity(s, in d); break;
            case "background-color": ApplyBackgroundColor(s, parent, in d); break;
            case "background-image": s.BackgroundImages = d.Inherit ? parent.BackgroundImages : ParseBackgroundImage(d.Value, s); break;
            case "background-repeat": ApplyBackgroundRepeat(s, parent, in d); break;
            case "background-size": ApplyBackgroundSize(s, parent, in d); break;
            case "background-position": ApplyBackgroundPosition(s, parent, in d); break;
            case "background-position-x": ApplyBackgroundPositionX(s, parent, in d); break;
            case "background-position-y": ApplyBackgroundPositionY(s, parent, in d); break;
            case "background-origin": ApplyBackgroundOrigin(s, parent, in d); break;
            case "background-clip": ApplyBackgroundClip(s, parent, in d); break;
            case "border-top-width": s.BorderTopWidth = BorderWidth(d.Lower, d.Em, s.BorderTopWidth); break;
            case "border-right-width": s.BorderRightWidth = BorderWidth(d.Lower, d.Em, s.BorderRightWidth); break;
            case "border-bottom-width": s.BorderBottomWidth = BorderWidth(d.Lower, d.Em, s.BorderBottomWidth); break;
            case "border-left-width": s.BorderLeftWidth = BorderWidth(d.Lower, d.Em, s.BorderLeftWidth); break;
            case "border-top-style": s.BorderTopStyle = ParseBorderStyle(d.Lower, s.BorderTopStyle); break;
            case "border-right-style": s.BorderRightStyle = ParseBorderStyle(d.Lower, s.BorderRightStyle); break;
            case "border-bottom-style": s.BorderBottomStyle = ParseBorderStyle(d.Lower, s.BorderBottomStyle); break;
            case "border-left-style": s.BorderLeftStyle = ParseBorderStyle(d.Lower, s.BorderLeftStyle); break;
            case "border-top-color": s.BorderTopColor = BorderColor(d.Value, s); break;
            case "border-right-color": s.BorderRightColor = BorderColor(d.Value, s); break;
            case "border-bottom-color": s.BorderBottomColor = BorderColor(d.Value, s); break;
            case "border-left-color": s.BorderLeftColor = BorderColor(d.Value, s); break;
            case "border-top-left-radius": SetRadius(s, 0, d.Lower, d.Em, d.Inherit ? parent : null); break;
            case "border-top-right-radius": SetRadius(s, 1, d.Lower, d.Em, d.Inherit ? parent : null); break;
            case "border-bottom-right-radius": SetRadius(s, 2, d.Lower, d.Em, d.Inherit ? parent : null); break;
            case "border-bottom-left-radius": SetRadius(s, 3, d.Lower, d.Em, d.Inherit ? parent : null); break;
            case "border-image-source": ApplyBorderImageSource(s, parent, in d); break;
            case "border-image-slice": ApplyBorderImageSlice(s, parent, in d); break;
            case "border-image-width": ApplyBorderImageWidth(s, parent, in d); break;
            case "border-image-outset": ApplyBorderImageOutset(s, parent, in d); break;
            case "border-image-repeat": ApplyBorderImageRepeat(s, parent, in d); break;
            case "outline-style": s.OutlineStyle = d.Lower == "auto" ? BorderStyle.Solid : ParseBorderStyle(d.Lower, s.OutlineStyle); break;
            case "outline-width": s.OutlineWidth = BorderWidth(d.Lower, d.Em, s.OutlineWidth); break;
            // invert is drawn as the text colour, as browsers without inversion do.
            case "outline-color": s.OutlineColor = d.Lower == "invert" ? null : BorderColor(d.Value, s); break;
            case "outline-offset": ApplyOutlineOffset(s, in d); break;
            case "box-shadow": ApplyBoxShadow(s, parent, in d); break;
            case "text-shadow": ApplyTextShadow(s, parent, in d); break;
            case "-webkit-text-fill-color": ApplyTextFillColor(s, parent, in d); break;
            case "-webkit-text-stroke-width": ApplyTextStrokeWidth(s, parent, in d); break;
            case "-webkit-text-stroke-color": ApplyTextStrokeColor(s, parent, in d); break;
            case "paint-order": ApplyPaintOrder(s, parent, in d); break;

            // ---- transforms, clipping, filters and compositing (Properties.Effects.cs)
            case "transform": ApplyTransform(s, parent, in d); break;
            case "translate": ApplyTranslate(s, parent, in d); break;
            case "rotate": ApplyRotate(s, parent, in d); break;
            case "scale": ApplyScale(s, parent, in d); break;
            case "transform-origin": ApplyTransformOrigin(s, parent, in d); break;
            case "perspective": ApplyPerspective(s, parent, in d); break;
            case "perspective-origin": ApplyPerspectiveOrigin(s, parent, in d); break;
            // Chrome prints a flattened 3D scene whatever these say, so they are accepted and have no effect.
            case "transform-style":
            case "backface-visibility":
                break;
            case "clip-path": ApplyClipPath(s, parent, in d); break;
            case "filter": ApplyFilter(s, parent, in d); break;
            case "backdrop-filter": ApplyBackdropFilter(s, parent, in d); break;
            case "mask-image": ApplyMaskImage(s, parent, in d); break;
            case "mask-repeat": s.MaskRepeats = MaskList(s.MaskRepeats, parent.MaskRepeats, in d, CssBackground.Repeat); break;
            case "mask-size": ApplyMaskSize(s, parent, in d); break;
            case "mask-position": ApplyMaskPosition(s, parent, in d); break;
            case "mask-position-x": ApplyMaskPositionAxis(s, parent, in d, horizontal: true); break;
            case "mask-position-y": ApplyMaskPositionAxis(s, parent, in d, horizontal: false); break;
            case "mask-origin": s.MaskOrigins = MaskList(s.MaskOrigins, parent.MaskOrigins, in d, CssMask.Box); break;
            case "mask-clip": s.MaskClips = MaskList(s.MaskClips, parent.MaskClips, in d, CssMask.Box); break;
            case "mask-mode": s.MaskModes = MaskList(s.MaskModes, parent.MaskModes, in d, CssMask.Mode); break;
            case "mask-composite": s.MaskComposites = MaskList(s.MaskComposites, parent.MaskComposites, in d, CssMask.Composite); break;
            case "mix-blend-mode": ApplyMixBlendMode(s, parent, in d); break;
            case "background-blend-mode": ApplyBackgroundBlendMode(s, parent, in d); break;
            case "isolation": s.Isolate = d.Lower == "isolate" || d.Inherit && parent.Isolate; break;
            case "box-decoration-break": s.BoxDecorationClone = d.Lower == "clone" || d.Inherit && parent.BoxDecorationClone; break;

            // ---- SVG presentation properties (Properties.Svg.cs)
            case "fill": ApplyFill(s, parent, in d); break;
            case "stroke": ApplyStroke(s, parent, in d); break;
            case "stroke-width": ApplyStrokeWidth(s, in d); break;
            case "fill-opacity": ApplyFillOpacity(s, in d); break;
            case "stroke-opacity": ApplyStrokeOpacity(s, in d); break;
            case "fill-rule": s.SvgEvenOdd = d.Lower == "evenodd"; break;
            case "clip-rule": s.SvgClipEvenOdd = d.Lower == "evenodd"; break;
            case "stroke-linecap": ApplyStrokeLineCap(s, in d); break;
            case "stroke-linejoin": ApplyStrokeLineJoin(s, in d); break;
            case "stroke-miterlimit": ApplyStrokeMiterLimit(s, in d); break;
            case "stroke-dasharray": ApplyStrokeDashArray(s, parent, in d); break;
            case "stroke-dashoffset": ApplyStrokeDashOffset(s, parent, in d); break;
            case "mask": s.SvgMask = SvgUrl(d.Value, d.Lower); break;
            case "mask-type": s.SvgMaskAlpha = d.Lower == "alpha"; break;
            case "marker": s.SvgMarkerStart = s.SvgMarkerMid = s.SvgMarkerEnd = d.Inherit ? parent.SvgMarkerStart : SvgUrl(d.Value, d.Lower); break;
            case "marker-start": s.SvgMarkerStart = d.Inherit ? parent.SvgMarkerStart : SvgUrl(d.Value, d.Lower); break;
            case "marker-mid": s.SvgMarkerMid = d.Inherit ? parent.SvgMarkerMid : SvgUrl(d.Value, d.Lower); break;
            case "marker-end": s.SvgMarkerEnd = d.Inherit ? parent.SvgMarkerEnd : SvgUrl(d.Value, d.Lower); break;
            case "flood-color": ApplyFloodColor(s, in d); break;
            case "flood-opacity": ApplyFloodOpacity(s, in d); break;
            case "lighting-color": ApplyLightingColor(s, in d); break;
            case "color-interpolation-filters": ApplyColorInterpolationFilters(s, parent, in d); break;
            case "text-anchor": ApplyTextAnchor(s, parent, in d); break;
            case "dominant-baseline":
            case "alignment-baseline":
                ApplySvgBaseline(s, parent, in d);
                break;

            // ---- fragmentation and paged media (Properties.Paged.cs)
            case "break-before": ApplyBreakBefore(s, in d); break;
            case "break-after": ApplyBreakAfter(s, in d); break;
            case "break-inside": ApplyBreakInside(s, in d); break;
            case "orphans": ApplyOrphans(s, parent, in d); break;
            case "widows": ApplyWidows(s, parent, in d); break;
            case "page": ApplyPage(s, parent, in d); break;
            case "string-set": s.StringSet = d.Lower is "none" ? null : d.Inherit ? parent.StringSet : d.Value; break;
            case "footnote-display": ApplyFootnoteDisplay(s, parent, in d); break;
            case "footnote-policy": ApplyFootnotePolicy(s, parent, in d); break;

            // ---- generated content, counters and list markers (Properties.Lists.cs)
            case "content": s.Content = d.Lower is "normal" or "none" ? null : d.Inherit ? parent.Content : d.Value; break;
            case "counter-reset": s.CounterReset = d.Inherit ? parent.CounterReset : ParseCounters(d.Value, 0); break;
            case "counter-set": s.CounterSet = d.Inherit ? parent.CounterSet : ParseCounters(d.Value, 0); break;
            case "counter-increment": s.CounterIncrement = d.Inherit ? parent.CounterIncrement : ParseCounters(d.Value, 1); break;
            case "list-style-type": ApplyListStyleType(s, parent, in d); break;
            case "list-style-image": ApplyListStyleImage(s, parent, in d); break;
            case "list-style-position": s.ListStylePositionInside = d.Lower == "inside"; break;
        }
    }
}
