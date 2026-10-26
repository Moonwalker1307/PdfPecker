namespace PdfPecker.Css;

/// <summary>
/// The -webkit- property names that Chrome still takes as other names for standard properties. Older templates and
/// CSS frameworks write -webkit-transform, -webkit-border-radius or -webkit-flex, often without the standard name, and
/// Chrome treats each as the property it stands for, so a later declaration of either name wins.
/// </summary>
internal static class PrefixedProperties
{
    /// <summary>The standard property a -webkit- name stands for, or null when it is not one of Chrome's aliases.</summary>
    public static string? Unprefixed(string name) => !name.StartsWith("-webkit-", StringComparison.Ordinal) ? null : name switch
    {
        "-webkit-align-content" => "align-content",
        "-webkit-align-items" => "align-items",
        "-webkit-align-self" => "align-self",
        "-webkit-backface-visibility" => "backface-visibility",
        "-webkit-background-origin" => "background-origin",
        "-webkit-background-size" => "background-size",
        "-webkit-border-after" => "border-block-end",
        "-webkit-border-after-color" => "border-block-end-color",
        "-webkit-border-after-style" => "border-block-end-style",
        "-webkit-border-after-width" => "border-block-end-width",
        "-webkit-border-before" => "border-block-start",
        "-webkit-border-before-color" => "border-block-start-color",
        "-webkit-border-before-style" => "border-block-start-style",
        "-webkit-border-before-width" => "border-block-start-width",
        "-webkit-border-bottom-left-radius" => "border-bottom-left-radius",
        "-webkit-border-bottom-right-radius" => "border-bottom-right-radius",
        "-webkit-border-end" => "border-inline-end",
        "-webkit-border-end-color" => "border-inline-end-color",
        "-webkit-border-end-style" => "border-inline-end-style",
        "-webkit-border-end-width" => "border-inline-end-width",
        "-webkit-border-radius" => "border-radius",
        "-webkit-border-start" => "border-inline-start",
        "-webkit-border-start-color" => "border-inline-start-color",
        "-webkit-border-start-style" => "border-inline-start-style",
        "-webkit-border-start-width" => "border-inline-start-width",
        "-webkit-border-top-left-radius" => "border-top-left-radius",
        "-webkit-border-top-right-radius" => "border-top-right-radius",
        "-webkit-box-shadow" => "box-shadow",
        "-webkit-box-sizing" => "box-sizing",
        "-webkit-clip-path" => "clip-path",
        "-webkit-column-count" => "column-count",
        "-webkit-column-gap" => "column-gap",
        "-webkit-column-rule" => "column-rule",
        "-webkit-column-rule-color" => "column-rule-color",
        "-webkit-column-rule-style" => "column-rule-style",
        "-webkit-column-rule-width" => "column-rule-width",
        "-webkit-column-span" => "column-span",
        "-webkit-column-width" => "column-width",
        "-webkit-columns" => "columns",
        "-webkit-filter" => "filter",
        "-webkit-flex" => "flex",
        "-webkit-flex-basis" => "flex-basis",
        "-webkit-flex-direction" => "flex-direction",
        "-webkit-flex-flow" => "flex-flow",
        "-webkit-flex-grow" => "flex-grow",
        "-webkit-flex-shrink" => "flex-shrink",
        "-webkit-flex-wrap" => "flex-wrap",
        "-webkit-font-feature-settings" => "font-feature-settings",
        "-webkit-hyphenate-character" => "hyphenate-character",
        "-webkit-justify-content" => "justify-content",
        "-webkit-logical-height" => "block-size",
        "-webkit-logical-width" => "inline-size",
        "-webkit-margin-after" => "margin-block-end",
        "-webkit-margin-before" => "margin-block-start",
        "-webkit-margin-end" => "margin-inline-end",
        "-webkit-margin-start" => "margin-inline-start",
        "-webkit-max-logical-height" => "max-block-size",
        "-webkit-max-logical-width" => "max-inline-size",
        "-webkit-min-logical-height" => "min-block-size",
        "-webkit-min-logical-width" => "min-inline-size",
        "-webkit-opacity" => "opacity",
        "-webkit-order" => "order",
        "-webkit-padding-after" => "padding-block-end",
        "-webkit-padding-before" => "padding-block-start",
        "-webkit-padding-end" => "padding-inline-end",
        "-webkit-padding-start" => "padding-inline-start",
        "-webkit-perspective" => "perspective",
        "-webkit-perspective-origin" => "perspective-origin",
        "-webkit-shape-image-threshold" => "shape-image-threshold",
        "-webkit-shape-margin" => "shape-margin",
        "-webkit-shape-outside" => "shape-outside",
        "-webkit-transform" => "transform",
        "-webkit-transform-origin" => "transform-origin",
        "-webkit-transform-style" => "transform-style",
        _ => null,
    };

    /// <summary>
    /// The break-before, break-after or break-inside value of a -webkit-column-break- value, where always means a
    /// column break and avoid a column avoid; null for a value the legacy property does not take.
    /// </summary>
    public static string? ColumnBreak(string value) => value.Trim().ToLowerInvariant() switch
    {
        "always" => "column",
        "avoid" => "avoid-column",
        "auto" => "auto",
        var wide when CssParser.IsCssWideKeyword(wide) => wide,
        _ => null,
    };
}
