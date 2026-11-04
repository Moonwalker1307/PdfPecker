namespace PdfPecker.Css;

/// <summary>
/// The gap decoration properties (CSS Gaps 1): column and row rules in the gaps of grid, flex and multi-column
/// containers, with lists of values, breaks, insets, visibility and overlap. A single column rule colour, style and
/// width stay where the multi-column properties keep them; everything else is in <see cref="GapRules"/>.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>column-rule-style, -width and -color: one value as before, or a list of values for the gaps in turn.</summary>
    private void ApplyColumnRule(ComputedStyle s, string prop, in DeclaredValue d)
    {
        if (!GapRules.IsList(d.Lower))
        {
            switch (prop)
            {
                case "column-rule-style": s.ColumnRuleStyle = ParseBorderStyle(d.Lower, s.ColumnRuleStyle); break;
                case "column-rule-width": s.ColumnRuleWidth = BorderWidth(d.Lower, d.Em, s.ColumnRuleWidth); break;
                default: s.ColumnRuleColor = BorderColor(d.Value, s); break;
            }
            // A single value replaces a list set before it.
            if (s.GapRules is { } old && (prop switch
                {
                    "column-rule-style" => old.ColumnStyles != null,
                    "column-rule-width" => old.ColumnWidths != null,
                    _ => old.ColumnColors != null,
                }))
            {
                var rules = s.ChangeGapRules();
                switch (prop)
                {
                    case "column-rule-style": rules.ColumnStyles = null; break;
                    case "column-rule-width": rules.ColumnWidths = null; break;
                    default: rules.ColumnColors = null; break;
                }
            }
            return;
        }
        switch (prop)
        {
            case "column-rule-style":
                if (StyleList(d.Lower) is { } styles) (s.ColumnRuleStyle, s.ChangeGapRules().ColumnStyles) = (styles.First, styles);
                break;
            case "column-rule-width":
                if (WidthList(in d) is { } widths) (s.ColumnRuleWidth, s.ChangeGapRules().ColumnWidths) = (widths.First, widths);
                break;
            default:
                if (ColorList(d.Value, s) is { } colors) (s.ColumnRuleColor, s.ChangeGapRules().ColumnColors) = (colors.First, colors);
                break;
        }
    }

    /// <summary>The longhands of the gap decoration properties other than the column rule's own three.</summary>
    private void ApplyGapRule(ComputedStyle s, string prop, in DeclaredValue d)
    {
        switch (prop)
        {
            case "row-rule-style":
                if (StyleList(d.Lower) is { } styles) s.ChangeGapRules().RowStyles = styles;
                break;
            case "row-rule-width":
                if (WidthList(in d) is { } widths) s.ChangeGapRules().RowWidths = widths;
                break;
            case "row-rule-color":
                if (ColorList(d.Value, s) is { } colors) s.ChangeGapRules().RowColors = colors;
                break;
            case "column-rule-break":
                if (GapRules.ParseBreak(d.Lower) is { } cb) s.ChangeGapRules().ColumnBreak = cb;
                break;
            case "row-rule-break":
                if (GapRules.ParseBreak(d.Lower) is { } rb) s.ChangeGapRules().RowBreak = rb;
                break;
            case "column-rule-visibility-items":
                if (GapRules.ParseVisibility(d.Lower) is { } cv) s.ChangeGapRules().ColumnVisibility = cv;
                break;
            case "row-rule-visibility-items":
                if (GapRules.ParseVisibility(d.Lower) is { } rv) s.ChangeGapRules().RowVisibility = rv;
                break;
            case "rule-overlap":
                if (d.Lower is "row-over-column" or "column-over-row") s.ChangeGapRules().ColumnOverRow = d.Lower == "column-over-row";
                break;
            default:
            {
                // The eight inset longhands, column- or row-rule-inset- then cap- or junction- then start or end.
                if (ParseRuleInset(in d) is not { } inset) break;
                bool column = prop.StartsWith("column-", StringComparison.Ordinal);
                var rules = s.ChangeGapRules();
                var insets = column ? rules.ColumnInsets : rules.RowInsets;
                insets = prop.AsSpan(prop.IndexOf("-inset-", StringComparison.Ordinal) + 7) switch
                {
                    "cap-start" => insets with { CapStart = inset },
                    "cap-end" => insets with { CapEnd = inset },
                    "junction-start" => insets with { JunctionStart = inset },
                    _ => insets with { JunctionEnd = inset },
                };
                if (column) rules.ColumnInsets = insets;
                else rules.RowInsets = insets;
                break;
            }
        }
    }

    private static GapValues<BorderStyle>? StyleList(string lower)
        => GapRules.ParseList(lower, static (string text, out BorderStyle style) =>
        {
            style = ParseBorderStyle(text, (BorderStyle)255);
            return style != (BorderStyle)255;
        });

    private GapValues<float>? WidthList(in DeclaredValue d)
    {
        float em = d.Em;
        return GapRules.ParseList(d.Lower, (string text, out float width) =>
        {
            width = BorderWidth(text, em, float.NaN);
            return !float.IsNaN(width) && width >= 0;
        });
    }

    private static GapValues<uint?>? ColorList(string value, ComputedStyle s)
        => GapRules.ParseList(value, (string text, out uint? color) =>
        {
            color = null;
            if (text.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) return true;
            if (!CssColor.TryParse(text, s.Color, out var c)) return false;
            color = c;
            return true;
        });

    /// <summary>A rule-inset longhand: a length, a percentage of the crossing gap, or overlap-join; null when not valid.</summary>
    private RuleInset? ParseRuleInset(in DeclaredValue d)
    {
        if (d.Lower == "overlap-join") return new RuleInset(Length.Zero, true);
        if (!TryParseLength(d.Lower, d.Em, _rootFontSize, out var len, _vw, _vh) || len.IsAuto) return null;
        return new RuleInset(ToLayoutUnits(len), false);
    }

    /// <summary>Copies one gap decoration property for inherit, initial and unset.</summary>
    private static void CopyGapRule(ComputedStyle s, ComputedStyle from, string prop)
    {
        var source = from.GapRules ?? new GapRules();
        switch (prop)
        {
            case "column-rule-style": s.ColumnRuleStyle = from.ColumnRuleStyle; break;
            case "column-rule-width": s.ColumnRuleWidth = from.ColumnRuleWidth; break;
            case "column-rule-color": s.ColumnRuleColor = from.ColumnRuleColor; break;
        }
        if (s.GapRules == null && from.GapRules == null) return;
        var rules = s.ChangeGapRules();
        switch (prop)
        {
            case "column-rule-style": rules.ColumnStyles = source.ColumnStyles; break;
            case "column-rule-width": rules.ColumnWidths = source.ColumnWidths; break;
            case "column-rule-color": rules.ColumnColors = source.ColumnColors; break;
            case "row-rule-style": rules.RowStyles = source.RowStyles; break;
            case "row-rule-width": rules.RowWidths = source.RowWidths; break;
            case "row-rule-color": rules.RowColors = source.RowColors; break;
            case "column-rule-break": rules.ColumnBreak = source.ColumnBreak; break;
            case "row-rule-break": rules.RowBreak = source.RowBreak; break;
            case "column-rule-visibility-items": rules.ColumnVisibility = source.ColumnVisibility; break;
            case "row-rule-visibility-items": rules.RowVisibility = source.RowVisibility; break;
            case "rule-overlap": rules.ColumnOverRow = source.ColumnOverRow; break;
            default:
                // An inset longhand: copying all four of its direction would undo the others, so only this one is copied.
                bool column = prop.StartsWith("column-", StringComparison.Ordinal);
                var to = column ? rules.ColumnInsets : rules.RowInsets;
                var of = column ? source.ColumnInsets : source.RowInsets;
                to = prop.AsSpan(prop.IndexOf("-inset-", StringComparison.Ordinal) + 7) switch
                {
                    "cap-start" => to with { CapStart = of.CapStart },
                    "cap-end" => to with { CapEnd = of.CapEnd },
                    "junction-start" => to with { JunctionStart = of.JunctionStart },
                    _ => to with { JunctionEnd = of.JunctionEnd },
                };
                if (column) rules.ColumnInsets = to;
                else rules.RowInsets = to;
                break;
        }
    }
}
