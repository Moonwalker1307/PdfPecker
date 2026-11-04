using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Gap decorations (CSS Gaps 1): column and row rules in grid and flex containers, their lists, breaks, insets,
/// visibility and overlap, how they split on pages, and the new values on multi-column boxes. Positions are written
/// in CSS pixels and compared in points.
/// </summary>
public class GapDecorationTests
{
    private const float Width = 500, PageHeight = 800, Px = 0.75f;
    private const uint Blue = 0xFF0000FF, Red = 0xFFFF0000, Green = 0xFF008000;

    private static ComputedStyle StyleOf(string style)
    {
        var doc = HtmlParser.Parse($"<div style='{style}'></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc.FindFirst("div")!.Style!;
    }

    private static List<DrawOp> Layout(string html, float pageHeight = PageHeight)
    {
        var doc = HtmlParser.Parse("<style>body { margin: 0 } .g > div, .f > div { background: #cfc }</style>" + html);
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(doc.FindFirst("style")!.InnerText(), sheet);
        new StyleResolver(sheet, Width, pageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(pageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops);
    }

    private static List<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        var list = new List<DrawOp>();
        foreach (var o in ops)
        {
            if (o is GroupOp g) list.AddRange(Flatten(g.Ops));
            else list.Add(o);
        }
        return list;
    }

    /// <summary>The solid rules of one colour as (x, y, w, h) in CSS pixels, in paint order.</summary>
    private static List<(float X, float Y, float W, float H)> Rules(List<DrawOp> ops, uint color)
        => ops.OfType<RectOp>().Where(r => r.Color == color).Select(r => (r.X / Px, r.Y / Px, r.W / Px, r.H / Px)).ToList();

    private static void Near(float expected, float actual, float tolerance = 0.1f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    private static void Rule((float X, float Y, float W, float H) rule, float x, float y, float w, float h)
    {
        Near(x, rule.X);
        Near(y, rule.Y);
        Near(w, rule.W);
        Near(h, rule.H);
    }

    // Three 40px columns and 30px rows with 10px gaps: column gaps centred at 45 and 95, row gaps at 35 and 75.
    private const string Grid = "display: grid; grid-template-columns: repeat(3, 40px); grid-auto-rows: 30px; gap: 10px; "
        + "column-rule: 4px solid #00f; row-rule: 4px solid #f00";

    private static string Cells(int n) => string.Concat(Enumerable.Repeat("<div></div>", n));

    // ---- properties ----

    [Fact]
    public void Rule_shorthand_sets_columns_and_rows()
    {
        var s = StyleOf("rule: 3px dashed red");
        Assert.Equal(BorderStyle.Dashed, s.ColumnRuleStyle);
        Near(2.25f, s.ColumnRuleWidth);
        Assert.Equal(Red, s.ColumnRuleColor);
        var rules = s.GapRules!;
        Assert.Equal(BorderStyle.Dashed, rules.RowStyles!.First);
        Near(2.25f, rules.RowWidths!.First);
        Assert.Equal(Red, rules.RowColors!.First);
    }

    [Fact]
    public void A_single_column_rule_keeps_no_gap_rules()
    {
        // Plain column rules, as multi-column boxes have always used them, add nothing to the rarely used group.
        var s = StyleOf("column-rule: 2px solid blue");
        Assert.Null(s.GapRules);
        Assert.Equal(BorderStyle.Solid, s.ColumnRuleStyle);
    }

    [Fact]
    public void Lists_expand_repeats_and_give_each_gap_its_value()
    {
        var colors = StyleOf("column-rule-color: gray, repeat(2, red, blue), gray").GapRules!.ColumnColors!;
        uint gray = 0xFF808080;
        Assert.Equal([gray, Red, Blue, Red, Blue, gray, gray], Enumerable.Range(0, 7).Select(i => colors.For(i, 7)).ToArray());

        // An auto repeat fills the gaps between the values before and after it, whatever their number.
        var auto = StyleOf("column-rule-color: gray, repeat(auto, red, blue), green").GapRules!.ColumnColors!;
        Assert.Equal([gray, Red, Blue, Red, Green], Enumerable.Range(0, 5).Select(i => auto.For(i, 5)).ToArray());
        Assert.Equal([gray, Green], Enumerable.Range(0, 2).Select(i => auto.For(i, 2)).ToArray());
        Assert.Equal(gray, auto.For(0, 1));
    }

    [Fact]
    public void Gap_rule_lists_spread_over_the_longhands()
    {
        var s = StyleOf("row-rule: 1px solid red, repeat(2, 3px dotted blue)");
        var rules = s.GapRules!;
        Assert.Equal([BorderStyle.Solid, BorderStyle.Dotted, BorderStyle.Dotted], Enumerable.Range(0, 3).Select(i => rules.RowStyles!.For(i, 3)).ToArray());
        Assert.Equal([0.75f, 2.25f, 2.25f], Enumerable.Range(0, 3).Select(i => rules.RowWidths!.For(i, 3)).ToArray());
        Assert.Equal(Blue, rules.RowColors!.For(2, 3));
    }

    [Fact]
    public void Invalid_lists_are_ignored()
    {
        Assert.Null(StyleOf("row-rule-style: solid, wavy").GapRules);
        Assert.Null(StyleOf("row-rule-color: repeat(auto, red), repeat(auto, blue)").GapRules);
        Assert.Null(StyleOf("row-rule-width: repeat(0, 1px)").GapRules);
        // A repeat that would make an enormous list is refused instead of allocated.
        Assert.Null(StyleOf("row-rule-color: repeat(2000000000, red, blue)").GapRules);
        Assert.Equal(BorderStyle.Solid, StyleOf("column-rule-style: solid; column-rule-style: solid, wavy").ColumnRuleStyle);
    }

    [Fact]
    public void Invalid_rule_lists_set_no_longhand()
    {
        // As in Chrome, a shorthand list with one bad entry is dropped whole, not applied to the longhands that accept it.
        Assert.Null(StyleOf("column-rule: 2px solid red, foo").GapRules);
        Assert.Null(StyleOf("row-rule: 2px solid red,").GapRules);
        Assert.Null(StyleOf("rule: repeat(2, repeat(2, 1px solid red))").GapRules);
        Assert.Null(StyleOf("row-rule: 1px solid red, solid dashed").GapRules);
        Assert.Null(StyleOf("row-rule: repeat(2, 1px solid red,)").GapRules);
        Assert.NotNull(StyleOf("row-rule: 1px solid red, repeat(2, thin dotted currentColor, calc(2px) double #00f)").GapRules);
    }

    [Fact]
    public void A_single_value_replaces_a_list()
    {
        var s = StyleOf("column-rule-color: red, blue; column-rule-color: green");
        Assert.Equal(Green, s.ColumnRuleColor);
        Assert.Null(s.GapRules!.ColumnColors);
    }

    [Fact]
    public void Break_visibility_and_overlap_keywords()
    {
        var rules = StyleOf("rule-break: intersection; row-rule-break: none; rule-visibility-items: between; rule-overlap: column-over-row").GapRules!;
        Assert.Equal(RuleBreak.Intersection, rules.ColumnBreak);
        Assert.Equal(RuleBreak.None, rules.RowBreak);
        Assert.Equal(RuleVisibility.Between, rules.ColumnVisibility);
        Assert.Equal(RuleVisibility.Between, rules.RowVisibility);
        Assert.True(rules.ColumnOverRow);
        // The older spanning-item keyword is no longer in the specification or in Chrome.
        Assert.Null(StyleOf("rule-break: spanning-item").GapRules);
    }

    [Fact]
    public void Inset_shorthands_fill_the_cap_and_junction_longhands()
    {
        var rules = StyleOf("rule-inset: 3px 5px / 10% overlap-join").GapRules!;
        foreach (var insets in new[] { rules.ColumnInsets, rules.RowInsets })
        {
            Near(2.25f, insets.CapStart.Length.Value);
            Near(3.75f, insets.CapEnd.Length.Value);
            Assert.True(insets.JunctionStart.Length.IsPercent);
            Assert.True(insets.JunctionEnd.OverlapJoin);
        }
        var only = StyleOf("column-rule-inset: 4px; row-rule-inset-junction: 1px 2px; column-rule-inset-end: 6px").GapRules!;
        Near(3, only.ColumnInsets.CapStart.Length.Value);
        Near(3, only.ColumnInsets.JunctionStart.Length.Value);
        Near(4.5f, only.ColumnInsets.CapEnd.Length.Value);
        Near(4.5f, only.ColumnInsets.JunctionEnd.Length.Value);
        Near(0.75f, only.RowInsets.JunctionStart.Length.Value);
        Near(1.5f, only.RowInsets.JunctionEnd.Length.Value);
        Near(0, only.RowInsets.CapStart.Length.Value);
    }

    [Fact]
    public void Css_wide_keywords_copy_one_property()
    {
        var doc = HtmlParser.Parse("<div style='row-rule: 2px solid red; rule-break: none; row-rule-inset-cap-start: 4px'>"
            + "<p style='row-rule-color: inherit; rule-break: inherit; row-rule-inset-cap-start: inherit'></p></div>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var rules = doc.FindFirst("p")!.Style!.GapRules!;
        Assert.Equal(Red, rules.RowColors!.First);
        Assert.Null(rules.RowStyles);
        Assert.Equal(RuleBreak.None, rules.ColumnBreak);
        Near(3, rules.RowInsets.CapStart.Length.Value);
    }

    [Fact]
    public void Supports_knows_the_gap_decoration_properties()
    {
        Assert.True(CssSupports.Evaluate("(row-rule: 1px solid red)"));
        Assert.True(CssSupports.Evaluate("(column-rule-color: red, repeat(auto, blue))"));
        Assert.True(CssSupports.Evaluate("(rule-inset: 2px / -50%)"));
        Assert.True(CssSupports.Evaluate("(rule-break: intersection)"));
        Assert.True(CssSupports.Evaluate("(rule-visibility-items: between)"));
        Assert.True(CssSupports.Evaluate("(rule-overlap: column-over-row)"));
        Assert.False(CssSupports.Evaluate("(rule-break: spanning-item)"));
        Assert.False(CssSupports.Evaluate("(row-rule-style: wavy)"));
        Assert.False(CssSupports.Evaluate("(column-rule-color: red, nocolor)"));
    }

    // ---- grid ----

    [Fact]
    public void Grid_rules_sit_in_the_middle_of_the_gaps()
    {
        var ops = Layout($"<div class='g' style='{Grid}'>{Cells(6)}</div>");
        var columns = Rules(ops, Blue);
        Assert.Equal(2, columns.Count);
        Rule(columns[0], 43, 0, 4, 70);
        Rule(columns[1], 93, 0, 4, 70);
        var rows = Rules(ops, Red);
        Rule(Assert.Single(rows), 0, 33, 140, 4);
    }

    [Fact]
    public void Grid_rules_paint_below_the_items_and_rows_over_columns()
    {
        var ops = Layout($"<div class='g' style='{Grid}'>{Cells(6)}</div>");
        int lastRule = ops.FindLastIndex(o => o is RectOp r && (r.Color == Blue || r.Color == Red));
        int firstItem = ops.FindIndex(o => o is RectOp r && r.Color == 0xFFCCFFCC);
        Assert.True(lastRule < firstItem);
        Assert.True(ops.FindIndex(o => o is RectOp r && r.Color == Blue) < ops.FindIndex(o => o is RectOp r && r.Color == Red));

        var over = Layout($"<div class='g' style='{Grid}; rule-overlap: column-over-row'>{Cells(6)}</div>");
        Assert.True(over.FindIndex(o => o is RectOp r && r.Color == Red) < over.FindIndex(o => o is RectOp r && r.Color == Blue));
    }

    [Fact]
    public void A_spanning_item_breaks_the_rule_it_covers()
    {
        // The first item spans the first two columns: the first column gap's rule starts below it, at the end of the
        // row gap, while the row rule runs through.
        var ops = Layout($"<div class='g' style='{Grid}'><div style='grid-column: span 2'></div>{Cells(4)}</div>");
        var columns = Rules(ops, Blue);
        Rule(columns[0], 43, 40, 4, 30);
        Rule(columns[1], 93, 0, 4, 70);

        // With rule-break: none the rule runs the whole gap, behind the item.
        var none = Rules(Layout($"<div class='g' style='{Grid}; rule-break: none'><div style='grid-column: span 2'></div>{Cells(4)}</div>"), Blue);
        Rule(none[0], 43, 0, 4, 70);
    }

    [Fact]
    public void Intersection_breaks_at_every_crossing()
    {
        var ops = Layout($"<div class='g' style='{Grid}; rule-break: intersection'>{Cells(6)}</div>");
        var columns = Rules(ops, Blue);
        Assert.Equal(4, columns.Count);
        Rule(columns[0], 43, 0, 4, 30);
        Rule(columns[1], 43, 40, 4, 30);
        var rows = Rules(ops, Red);
        Assert.Equal(3, rows.Count);
        Rule(rows[1], 50, 33, 40, 4);
    }

    [Fact]
    public void Visibility_leaves_out_parts_next_to_empty_cells()
    {
        // Items in the first row only and one in the second row's last cell.
        string html(string visibility) => $"<div class='g' style='{Grid}; rule-visibility-items: {visibility}'>{Cells(3)}<div style='grid-column: 3'></div></div>";
        var between = Layout(html("between"));
        Assert.Equal(2, Rules(between, Blue).Count);
        Rule(Rules(between, Blue)[0], 43, 0, 4, 30);
        Rule(Assert.Single(Rules(between, Red)), 100, 33, 40, 4);

        var around = Layout(html("around"));
        Rule(Rules(around, Blue)[1], 93, 0, 4, 70);
        Rule(Assert.Single(Rules(around, Red)), 0, 33, 140, 4);

        Assert.Equal(2, Rules(Layout(html("all")), Blue).Count);
    }

    [Fact]
    public void Insets_move_the_ends_at_caps_and_junctions()
    {
        var ops = Layout($"<div class='g' style='{Grid}; rule-break: intersection; column-rule-inset: 2px / -50%; row-rule: none'>{Cells(6)}</div>");
        var columns = Rules(ops, Blue);
        // The junction inset of -50% reaches the middle of the row gap from both sides; the caps move in by 2px.
        Rule(columns[0], 43, 2, 4, 33);
        Rule(columns[1], 43, 35, 4, 33);
    }

    [Fact]
    public void Negative_cap_insets_stop_at_the_content_box()
    {
        // Huge negative caps reach the content edges of the padded grid and no further.
        var ops = Layout($"<div class='g' style='{Grid}; padding: 20px; rule-inset: -1e6px; column-rule-inset-cap-end: -1e30px'>{Cells(9)}</div>");
        Rule(Rules(ops, Blue)[0], 63, 20, 4, 110);
        // Rows run across the content box, the full width less the padding.
        Rule(Rules(ops, Red)[0], 20, 53, Width / Px - 40, 4);
        var flex = Layout("<div class='f' style='display: flex; flex-wrap: wrap; width: 200px; gap: 10px; column-rule: 4px solid #00f; rule-inset: -1e6px'>"
            + "<div style='width: 60px; height: 30px'></div><div style='width: 60px; height: 30px'></div></div>");
        Rule(Assert.Single(Rules(flex, Blue)), 63, 0, 4, 30);
    }

    [Fact]
    public void Overlap_join_reaches_past_the_crossing_rule()
    {
        var ops = Layout($"<div class='g' style='{Grid}; rule-break: intersection; row-rule-inset: overlap-join'>{Cells(6)}</div>");
        var rows = Rules(ops, Red);
        // Half the 10px gap and half the 4px column rule beyond each junction; nothing at the container's edges.
        Rule(rows[0], 0, 33, 47, 4);
        Rule(rows[1], 43, 33, 54, 4);
        Rule(rows[2], 93, 33, 47, 4);
    }

    [Fact]
    public void Lists_alternate_over_the_gaps()
    {
        var ops = Layout($"<div class='g' style='{Grid}; column-rule-color: blue, green'>{Cells(6)}</div>");
        Near(43, Assert.Single(Rules(ops, Blue)).X);
        Near(93, Assert.Single(Rules(ops, Green)).X);
    }

    [Fact]
    public void Right_to_left_grids_mirror_their_rules()
    {
        var ops = Layout($"<div class='g' style='{Grid}; width: 140px; direction: rtl; row-rule-inset-start: 6px; column-rule-color: blue, green'>{Cells(6)}</div>");
        // The first gap in placement order is on the right, and the row rule starts there.
        Near(93, Assert.Single(Rules(ops, Blue)).X);
        Near(43, Assert.Single(Rules(ops, Green)).X);
        Rule(Assert.Single(Rules(ops, Red)), 0, 33, 134, 4);
    }

    [Fact]
    public void A_row_gap_on_a_page_break_disappears()
    {
        // Pages of 100px: the third row moves to the second page, and the row gap before it goes with the break.
        var cells = string.Concat(Enumerable.Repeat("<div style='break-inside: avoid'></div>", 9));
        var ops = Layout($"<div class='g' style='{Grid}; column-rule-inset: 3px'>{cells}</div>", pageHeight: 100 * Px);
        Assert.Single(Rules(ops, Red));
        var first = Rules(ops, Blue).Where(r => r.X < 50).ToList();
        Assert.Equal(2, first.Count);
        // Each piece is capped at the page's end and start as at the container's edges.
        Rule(first[0], 43, 3, 4, 94);
        Rule(first[1], 43, 103, 4, 24);
    }

    [Fact]
    public void Grids_without_rules_draw_nothing_extra()
    {
        var plain = Layout($"<div class='g' style='display: grid; grid-template-columns: 1fr 1fr; gap: 10px'>{Cells(4)}</div>");
        var ruled = Layout($"<div class='g' style='display: grid; grid-template-columns: 1fr 1fr; gap: 10px; rule-break: intersection; rule-inset: 3px'>{Cells(4)}</div>");
        Assert.Equal(plain.Count, ruled.Count);
    }

    // ---- flex ----

    [Fact]
    public void Flex_rules_run_between_the_items_of_each_line_and_between_the_lines()
    {
        var ops = Layout("<div class='f' style='display: flex; flex-wrap: wrap; width: 200px; gap: 10px; column-rule: 4px solid #00f; row-rule: 4px solid #f00'>"
            + "<div style='width: 60px; height: 30px'></div><div style='width: 60px; height: 20px'></div><div style='width: 100px; height: 40px'></div>"
            + "<div style='width: 50px; height: 30px'></div></div>");
        var columns = Rules(ops, Blue);
        Assert.Equal(2, columns.Count);
        // As tall as each line: the first line is 30px, the second 40px from y = 40.
        Rule(columns[0], 63, 0, 4, 30);
        Rule(columns[1], 103, 40, 4, 40);
        // The row rule spans the container, through the column gaps (rule-break: normal is none in flex).
        Rule(Assert.Single(Rules(ops, Red)), 0, 33, 200, 4);
    }

    [Fact]
    public void Flex_intersection_breaks_rows_at_the_column_gaps_of_both_lines()
    {
        var ops = Layout("<div class='f' style='display: flex; flex-wrap: wrap; width: 200px; gap: 10px; row-rule: 4px solid #f00; rule-break: intersection'>"
            + "<div style='width: 60px; height: 30px'></div><div style='width: 60px; height: 20px'></div><div style='width: 100px; height: 40px'></div>"
            + "<div style='width: 50px; height: 30px'></div></div>");
        var rows = Rules(ops, Red);
        Assert.Equal(3, rows.Count);
        Rule(rows[0], 0, 33, 60, 4);
        Rule(rows[1], 70, 33, 30, 4);
        Rule(rows[2], 110, 33, 90, 4);
    }

    [Fact]
    public void Column_flex_rules_separate_the_items()
    {
        var ops = Layout("<div class='f' style='display: flex; flex-direction: column; width: 100px; gap: 10px; row-rule: 2px solid #f00; row-rule-inset: 5px'>"
            + "<div style='height: 20px'></div><div style='height: 30px'></div><div style='height: 20px'></div></div>");
        var rows = Rules(ops, Red);
        Assert.Equal(2, rows.Count);
        Rule(rows[0], 5, 24, 90, 2);
        Rule(rows[1], 5, 64, 90, 2);
    }

    [Fact]
    public void Wrapped_column_flex_rules_run_between_the_lines()
    {
        var ops = Layout("<div class='f' style='display: flex; flex-direction: column; flex-wrap: wrap; height: 70px; width: 200px; gap: 10px 20px; column-rule: 2px solid #00f; row-rule: 2px solid #f00'>"
            + "<div style='height: 30px; width: 50px'></div><div style='height: 30px; width: 50px'></div><div style='height: 40px; width: 50px'></div></div>");
        var column = Assert.Single(Rules(ops, Blue));
        Near(0, column.Y);
        Near(70, column.H);
        Assert.Single(Rules(ops, Red));
    }

    // ---- multi-column ----

    [Fact]
    public void Multicol_rules_take_lists_and_insets()
    {
        var ops = Layout("<div style='columns: 3; column-gap: 20px; column-rule: 2px solid #00f; column-rule-color: blue, green; column-rule-inset: 5px'>"
            + "<p style='height: 40px; margin: 0'>a</p><p style='height: 40px; margin: 0'>b</p><p style='height: 40px; margin: 0'>c</p></div>");
        var blue = Assert.Single(Rules(ops, Blue));
        Assert.Single(Rules(ops, Green));
        Near(5, blue.Y);
        Near(30, blue.H);
    }

    [Fact]
    public void Multicol_negative_insets_stop_at_the_column_box()
    {
        var ops = Layout("<div style='columns: 3; column-gap: 20px; column-rule: 2px solid #00f; column-rule-color: blue, green; column-rule-inset: -1e6px'>"
            + "<p style='height: 40px; margin: 0'>a</p><p style='height: 40px; margin: 0'>b</p><p style='height: 40px; margin: 0'>c</p></div>");
        var blue = Assert.Single(Rules(ops, Blue));
        Near(0, blue.Y);
        Near(40, blue.H);
    }
}
