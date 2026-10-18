namespace PdfPecker.Css;

/// <summary>Fragmentation and paged media: breaks, orphans and widows, named pages and footnotes.</summary>
internal sealed partial class StyleResolver
{
    private static void ApplyBreakBefore(ComputedStyle s, in DeclaredValue d)
    {
        string lower = d.Lower;
        s.PageBreakBefore = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
        s.AvoidBreakBefore = lower is "avoid" or "avoid-page";
        s.ColumnBreakBefore = lower == "column";
        s.AvoidColumnBreakBefore = lower == "avoid-column";
    }

    private static void ApplyBreakAfter(ComputedStyle s, in DeclaredValue d)
    {
        string lower = d.Lower;
        s.PageBreakAfter = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
        s.AvoidBreakAfter = lower is "avoid" or "avoid-page";
        s.ColumnBreakAfter = lower == "column";
        s.AvoidColumnBreakAfter = lower == "avoid-column";
    }

    private static void ApplyBreakInside(ComputedStyle s, in DeclaredValue d)
    {
        s.AvoidBreakInside = d.Lower is "avoid" or "avoid-page";
        s.AvoidColumnBreakInside = d.Lower == "avoid-column";
    }

    private static void ApplyOrphans(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.Orphans = parent.Orphans;
        else if (Invariant.TryParseInt(d.Lower, out int orphans) && orphans > 0) s.Orphans = orphans;
    }

    private static void ApplyWidows(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.Widows = parent.Widows;
        else if (Invariant.TryParseInt(d.Lower, out int widows) && widows > 0) s.Widows = widows;
    }

    private void ApplyPage(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit || d.Lower == "auto") s.PageName = parent.PageName;
        else if (CssParser.Tokens(d.Value) is { Count: 1 } name && CssParser.IsPageName(name[0]))
        {
            s.PageName = name[0];
            UsedPageNames = true;
        }
    }

    private static void ApplyFootnoteDisplay(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.FootnoteDisplay = d.Inherit ? parent.FootnoteDisplay : d.Lower switch
        {
            "block" => FootnoteDisplay.Block,
            "inline" => FootnoteDisplay.Inline,
            "compact" => FootnoteDisplay.Compact,
            _ => s.FootnoteDisplay,
        };

    private static void ApplyFootnotePolicy(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.FootnotePolicy = d.Inherit ? parent.FootnotePolicy : d.Lower switch
        {
            "auto" => FootnotePolicy.Auto,
            "line" => FootnotePolicy.Line,
            "block" => FootnotePolicy.Block,
            _ => s.FootnotePolicy,
        };
}
