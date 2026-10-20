using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Page-margin boxes, text decoration, multi-column rules and spans, grid packing and the other CSS3 layout features.</summary>
public class Css3LayoutTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html, float width = Width, float pageHeight = PageHeight)
    {
        var doc = HtmlParser.Parse(html, impliedStructure: false);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, width, pageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(pageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return Flatten(engine.Ops).ToList();
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static RectOp Box(List<DrawOp> ops, string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return ops.OfType<RectOp>().Single(r => r.Color == c);
    }

    private static TextOp Text(List<DrawOp> ops, string text) => ops.OfType<TextOp>().First(t => t.Text.Contains(text));

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    // ---- page-margin boxes ----

    [Fact]
    public void Margin_boxes_show_page_numbers_on_every_page()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { size: A4; margin: 20mm;
              @top-right { content: 'Page ' counter(page) ' of ' counter(pages) }
              @bottom-center { content: counter(page, upper-roman); color: red } }
            @page :first { @top-right { content: none } }
          </style><p>one</p><p style='break-before: page'>two</p><p style='break-before: page'>three</p>");
        Assert.Equal(3, PageCount(pdf));
        var content = AllContent(pdf);
        Assert.DoesNotContain("Page 1 of 3", content);
        Assert.Contains("Page 2 of 3", content);
        Assert.Contains("Page 3 of 3", content);
        Assert.Contains("(III)", content);
        Assert.Contains("1 0 0 rg", content);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Margin_box_widths_keep_the_centre_box_centred()
    {
        // Only a centre box: it gets the whole width. Left and right of different sizes: shared by content.
        var (a, b, c) = PageMarginBoxes.Distribute(new[] { 0f, 50f, 0f }, false, true, false, 500);
        Assert.Equal(0, a); Near(500, b); Assert.Equal(0, c);
        (a, b, c) = PageMarginBoxes.Distribute(new[] { 100f, 50f, 20f }, true, true, true, 500);
        Near(500 * 50f / 250f, b);
        Near(a, c);
        Near(500, a + b + c);
        (a, _, c) = PageMarginBoxes.Distribute(new[] { 300f, 0f, 100f }, true, false, true, 500);
        Near(375, a); Near(125, c);
    }

    [Fact]
    public void Margin_boxes_are_positioned_in_the_page_margins()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { size: 400pt 400pt; margin: 50pt; @top-left { content: 'TL' } @bottom-right-corner { content: 'C' } }
          </style><p>x</p>");
        var content = AllContent(pdf);
        // top-left starts at the left margin edge (x = 50pt), vertically centred in the 50pt top margin.
        Assert.Matches(@"\b50 3[67]\d(\.\d+)? Tm \(TL\)", content);
        // The bottom-right corner box is the 50pt square at the page's corner; its text is left-aligned there.
        Assert.Matches(@"\b350 [12]\d(\.\d+)? Tm \(C\)", content);
    }

    // ---- text decoration ----

    [Fact]
    public void Text_decoration_colour_style_thickness_and_offset()
    {
        var ops = Layout(@"<p style='font-size:20pt; text-decoration: underline wavy red 2pt; text-underline-offset: 4pt'>wave</p>
            <p style='font-size:20pt; text-decoration: underline double blue'>double</p>
            <p style='font-size:20pt; text-decoration-line: line-through; text-decoration-style: dotted'>dots</p>");
        var wave = ops.OfType<LineOp>().Where(l => l.Solid).ToList();
        Assert.True(wave.Count > 4);
        Assert.All(wave, l => { Assert.Equal(0xFFFF0000u, l.Color); Assert.Equal(2, l.Width); });
        var waveText = Text(ops, "wave");
        Assert.True(wave.Min(l => Math.Min(l.Y1, l.Y2)) > waveText.Baseline + 3.9f);
        var blue = ops.OfType<RectOp>().Where(r => r.Color == 0xFF0000FF).ToList();
        Assert.Equal(2, blue.Count);
        Assert.Contains(ops.OfType<LineOp>(), l => l.Dotted);
    }

    [Fact]
    public void Underline_keeps_the_colour_of_the_box_that_set_it()
    {
        var ops = Layout("<p style='color: blue; text-decoration: underline'>a <span style='color: red'>b</span></p>");
        var lines = ops.OfType<RectOp>().Where(r => r.IsDecoration).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, r => Assert.Equal(0xFF0000FFu, r.Color));
    }

    // ---- multi-column ----

    [Fact]
    public void Column_rules_sit_in_the_gaps()
    {
        var ops = Layout(@"<div style='columns: 3; column-gap: 20pt; column-rule: 2pt solid #00ff00'>
            <p style='height: 30pt; margin: 0'>a</p><p style='height: 30pt; margin: 0'>b</p><p style='height: 30pt; margin: 0'>c</p></div>");
        var rules = ops.OfType<RectOp>().Where(r => r.Color == 0xFF00FF00).OrderBy(r => r.X).ToList();
        Assert.Equal(2, rules.Count);
        float colW = (500 - 40) / 3f;
        Near(colW + 10 - 1, rules[0].X, 0.5f);
        Near(2 * colW + 30 - 1, rules[1].X, 0.5f);
        Near(30, rules[0].H);
    }

    [Fact]
    public void Column_span_all_interrupts_the_columns()
    {
        var ops = Layout(@"<div style='columns: 2; column-gap: 0'>
            <p style='height: 20pt; margin: 0; background: #ff0000'></p><p style='height: 20pt; margin: 0; background: #ff0001'></p>
            <h2 style='column-span: all; height: 10pt; margin: 0; background: #00ff00'></h2>
            <p style='height: 20pt; margin: 0; background: #0000ff'></p><p style='height: 20pt; margin: 0; background: #0000fe'></p></div>");
        var span = Box(ops, "#00ff00");
        Near(0, span.X); Near(500, span.W); Near(20, span.Y);
        Near(250, Box(ops, "#ff0001").X);
        var after = Box(ops, "#0000ff");
        Near(30, after.Y);
        Near(250, Box(ops, "#0000fe").X);
    }

    // Lines w0..w{n-1} of one paragraph, 10pt apart.
    private static string Lines(int n, string style = "")
        => $"<p style='margin: 0; font: 8pt/10pt sans-serif; {style}'>" + string.Join("<br>", Enumerable.Range(0, n).Select(i => $"w{i}x")) + "</p>";

    // Blocks b0..b{n-1}, each 9pt (12px, so that layout units keep it exact) tall with its own background colour.
    private static string Blocks(int n, int from = 0)
        => string.Concat(Enumerable.Range(from, n).Select(i => $"<div style='height: 9pt; background: #0000{i:x2}'></div>"));

    [Fact]
    public void A_paragraph_continues_in_the_next_column()
    {
        var ops = Layout("<div style='columns: 2; column-gap: 0'>" + Lines(10) + "</div><p style='margin: 0'>after</p>");
        // Balanced: five lines in each column, the second column starting level with the first.
        Near(Text(ops, "w0x").LineTop, Text(ops, "w5x").LineTop);
        Near(250, Text(ops, "w5x").X, 0.5f);
        Near(0, Text(ops, "w4x").X, 0.5f);
        Near(50, Text(ops, "after").LineTop, 0.5f);
    }

    [Fact]
    public void Columns_across_pages_fill_every_column_on_each_page()
    {
        var ops = Layout("<div style='columns: 2; column-gap: 0'>" + Blocks(30) + "</div>", pageHeight: 90);
        // Page 1 holds b0..b19 in two full columns; page 2 balances the last ten over both columns.
        Near(0, Box(ops, "#000009").X); Near(81, Box(ops, "#000009").Y);
        Near(250, Box(ops, "#00000a").X); Near(0, Box(ops, "#00000a").Y);
        Near(250, Box(ops, "#000013").X); Near(81, Box(ops, "#000013").Y);
        Near(0, Box(ops, "#000014").X); Near(90, Box(ops, "#000014").Y);
        Near(250, Box(ops, "#000019").X); Near(90, Box(ops, "#000019").Y);
    }

    [Fact]
    public void Break_before_column_starts_the_next_column()
    {
        var ops = Layout("<div style='columns: 3; column-gap: 0'>" + Blocks(2)
                         + "<div style='break-before: column; height: 9pt; background: #000002'></div></div>");
        // The forced break leaves two columns to the content before it, which balances over them.
        Near(500 / 3f, Box(ops, "#000001").X, 0.5f);
        Near(1000 / 3f, Box(ops, "#000002").X, 0.5f);
        Near(0, Box(ops, "#000002").Y);
    }

    [Fact]
    public void Break_inside_avoid_keeps_a_box_in_one_column()
    {
        var ops = Layout("<div style='columns: 2; column-gap: 0'><div style='height: 20pt; background: #ff0000'></div>"
                         + "<div style='break-inside: avoid'><div style='height: 20pt; background: #00ff00'></div><div style='height: 20pt; background: #0000ff'></div></div></div>");
        Near(250, Box(ops, "#00ff00").X); Near(0, Box(ops, "#00ff00").Y);
        Near(250, Box(ops, "#0000ff").X); Near(20, Box(ops, "#0000ff").Y);
    }

    [Fact]
    public void Orphans_make_the_balanced_columns_taller()
    {
        // Two lines a column would leave fewer than three at the first column's end.
        var ops = Layout("<div style='columns: 2; column-gap: 0'>" + Lines(4, "orphans: 3; widows: 1") + "</div>");
        Near(0, Text(ops, "w2x").X, 0.5f);
        Near(250, Text(ops, "w3x").X, 0.5f);
        Near(Text(ops, "w0x").LineTop, Text(ops, "w3x").LineTop);
    }

    [Fact]
    public void Column_fill_auto_fills_the_columns_in_turn()
    {
        var ops = Layout("<div style='columns: 2; column-gap: 0; column-fill: auto; height: 18pt'>" + Blocks(3) + "</div>");
        Near(0, Box(ops, "#000001").X); Near(9, Box(ops, "#000001").Y);
        Near(250, Box(ops, "#000002").X); Near(0, Box(ops, "#000002").Y);
    }

    [Fact]
    public void Right_to_left_columns_start_on_the_right()
    {
        var ops = Layout("<div style='columns: 2; column-gap: 0; direction: rtl'>" + Blocks(2) + "</div>");
        Near(250, Box(ops, "#000000").X);
        Near(0, Box(ops, "#000001").X); Near(0, Box(ops, "#000001").Y);
    }

    [Fact]
    public void A_table_header_repeats_at_the_top_of_each_column()
    {
        string rows = string.Concat(Enumerable.Range(0, 12).Select(i => $"<tr><td>r{i}x</td></tr>"));
        var ops = Layout("<div style='columns: 2; column-gap: 0; font: 8pt/10pt sans-serif'><table style='border-collapse: collapse'>"
                         + "<thead><tr><th>headx</th></tr></thead>" + rows + "</table></div>");
        var heads = ops.OfType<TextOp>().Where(t => t.Text.Contains("headx")).OrderBy(t => t.X).ToList();
        Assert.Equal(2, heads.Count);
        Near(heads[0].LineTop, heads[1].LineTop);
        Assert.True(heads[1].X >= 250);
    }

    [Fact]
    public void Nested_columns_split_inside_the_outer_columns()
    {
        var ops = Layout("<div style='columns: 2; column-gap: 0'><div style='columns: 2; column-gap: 0'>" + Blocks(4) + "</div></div>");
        // The inner box's columns are 125pt wide, and the outer balancing splits the inner box between the outer
        // columns: one block in each inner column.
        Near(0, Box(ops, "#000000").X);
        Near(125, Box(ops, "#000001").X); Near(0, Box(ops, "#000001").Y);
        Near(250, Box(ops, "#000002").X); Near(0, Box(ops, "#000002").Y);
        Near(375, Box(ops, "#000003").X); Near(0, Box(ops, "#000003").Y);
    }

    // ---- grid ----

    [Fact]
    public void Dense_packing_fills_earlier_holes()
    {
        const string items = @"<div style='grid-column: span 2; background:#ff0000'></div><div style='grid-column: span 2; background:#00ff00'></div>
            <div style='background:#0000ff'></div>";
        var sparse = Layout("<div style='display:grid; grid-template-columns: repeat(3, 100pt); grid-auto-rows: 10pt'>" + items + "</div>");
        var dense = Layout("<div style='display:grid; grid-template-columns: repeat(3, 100pt); grid-auto-rows: 10pt; grid-auto-flow: row dense'>" + items + "</div>");
        // Sparse: the small item goes after the second wide one (row 2, column 3). Dense: into the hole at row 1, column 3.
        Near(200, Box(sparse, "#0000ff").X); Near(10, Box(sparse, "#0000ff").Y);
        Near(200, Box(dense, "#0000ff").X); Near(0, Box(dense, "#0000ff").Y);
    }

    [Fact]
    public void Column_flow_fills_columns_first()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-rows: 10pt 10pt; grid-auto-columns: 50pt; grid-auto-flow: column'>
            <div style='background:#ff0000'></div><div style='background:#00ff00'></div><div style='background:#0000ff'></div></div>");
        Near(0, Box(ops, "#00ff00").X); Near(10, Box(ops, "#00ff00").Y);
        Near(50, Box(ops, "#0000ff").X); Near(0, Box(ops, "#0000ff").Y);
    }

    [Fact]
    public void Span_to_a_named_line()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: [a] 50pt 50pt [b] 50pt [b] 50pt; grid-auto-rows: 10pt'>
            <div style='grid-column: 1 / span b; background:#ff0000'></div>
            <div style='grid-column: 1 / span 2 b; grid-row: 2; background:#00ff00'></div></div>");
        Near(100, Box(ops, "#ff0000").W);
        Near(150, Box(ops, "#00ff00").W);
    }

    // ---- subgrid ----

    [Fact]
    public void Subgrid_columns_are_the_parent_tracks_it_spans()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: 100pt 1fr 50pt'>
            <div style='grid-column: 1 / 4; display:grid; grid-template-columns: subgrid; grid-auto-rows: 10pt'>
              <div style='background:#ff0000'></div><div style='background:#00ff00'></div><div style='background:#0000ff'></div>
            </div></div>");
        Near(0, Box(ops, "#ff0000").X); Near(100, Box(ops, "#ff0000").W);
        Near(100, Box(ops, "#00ff00").X); Near(350, Box(ops, "#00ff00").W);
        Near(450, Box(ops, "#0000ff").X); Near(50, Box(ops, "#0000ff").W);
    }

    [Fact]
    public void Subgrid_padding_border_and_margin_shrink_its_edge_tracks()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: 100pt 100pt 100pt'>
            <div style='grid-column: 1 / 4; display:grid; grid-template-columns: subgrid; grid-auto-rows: 10pt; padding: 0 20pt 0 5pt; border-left: 3pt solid #000; margin-left: 2pt'>
              <div style='background:#ff0000'></div><div style='background:#00ff00'></div><div style='background:#0000ff'></div>
            </div></div>");
        Near(10, Box(ops, "#ff0000").X); Near(90, Box(ops, "#ff0000").W);
        Near(100, Box(ops, "#00ff00").X); Near(100, Box(ops, "#00ff00").W);
        Near(200, Box(ops, "#0000ff").X); Near(80, Box(ops, "#0000ff").W);
    }

    [Fact]
    public void Subgrid_items_size_the_parent_tracks()
    {
        // The parent's auto column is as wide as the subgrid's first item plus the subgrid's left padding.
        var ops = Layout(@"<div style='display:grid; grid-template-columns: auto 1fr; grid-auto-rows: 10pt'>
            <div style='grid-column: 1 / 3; display:grid; grid-template-columns: subgrid; padding-left: 10pt'>
              <div style='width: 120pt; background:#ff0000'></div><div style='background:#0000ff'></div>
            </div>
            <div style='grid-column: 1; background:#00ff00'></div></div>");
        Near(130, Box(ops, "#00ff00").W);
        Near(10, Box(ops, "#ff0000").X); Near(120, Box(ops, "#ff0000").W);
        Near(130, Box(ops, "#0000ff").X); Near(370, Box(ops, "#0000ff").W);
    }

    [Fact]
    public void Subgrid_gap_moves_the_inner_lines_by_half_the_difference()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: 100pt 100pt; column-gap: 10pt'>
            <div style='grid-column: 1 / 3; display:grid; grid-template-columns: subgrid; column-gap: 2pt; grid-auto-rows: 10pt'>
              <div style='background:#ff0000'></div><div style='background:#00ff00'></div>
            </div></div>");
        Near(0, Box(ops, "#ff0000").X); Near(104, Box(ops, "#ff0000").W);
        Near(106, Box(ops, "#00ff00").X); Near(104, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void Subgrid_rows_line_the_cards_items_up()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: 1fr 1fr; column-gap: 10pt'>
            <div style='grid-row: span 2; display:grid; grid-template-rows: subgrid'>
              <div style='height:10pt; background:#ff0000'></div><div style='height:30pt; background:#00ff00'></div>
            </div>
            <div style='grid-row: span 2; display:grid; grid-template-rows: subgrid'>
              <div style='height:25pt; background:#0000ff'></div><div style='height:5pt; background:#ffff00'></div>
            </div>
            <div style='background:#00ffff; height: 10pt'></div></div>");
        // The parent's first row is 25pt (the tallest first item), its second 30pt, so the second items start together.
        Near(0, Box(ops, "#ff0000").Y); Near(25, Box(ops, "#00ff00").Y);
        Near(0, Box(ops, "#0000ff").Y); Near(25, Box(ops, "#ffff00").Y);
        Near(55, Box(ops, "#00ffff").Y);
    }

    [Fact]
    public void Subgrid_lines_carry_the_parents_names_and_its_own()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: [a] 100pt [b] 100pt [c] 100pt [d]'>
            <div style='grid-column: b / d; display:grid; grid-template-columns: subgrid [x] [y] [z]; grid-auto-rows: 10pt'>
              <div style='grid-column: c; background:#ff0000'></div>
              <div style='grid-column: x / z; background:#00ff00'></div>
            </div></div>");
        Near(200, Box(ops, "#ff0000").X); Near(100, Box(ops, "#ff0000").W);
        Near(100, Box(ops, "#00ff00").X); Near(200, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void Subgrid_spans_its_named_lines_and_clamps_items_to_its_tracks()
    {
        // Three name lists give an auto-placed subgrid a span of two; an item placed past its tracks lands in the last one.
        var ops = Layout(@"<div style='display:grid; grid-template-columns: repeat(4, 50pt)'>
            <div style='display:grid; grid-template-columns: subgrid [a] [b] [c]; grid-auto-rows: 10pt'>
              <div style='background:#ff0000'></div><div style='grid-column: 5; background:#00ff00'></div>
              <div style='grid-column: 2 / span 3; background:#0000ff'></div>
            </div>
            <div style='background:#ffff00; height: 10pt'></div></div>");
        Near(0, Box(ops, "#ff0000").X);
        Near(50, Box(ops, "#00ff00").X); Near(50, Box(ops, "#00ff00").W);
        Near(50, Box(ops, "#0000ff").X); Near(50, Box(ops, "#0000ff").W);
        Near(100, Box(ops, "#ffff00").X);
    }

    [Fact]
    public void Nested_subgrids_share_the_outer_tracks()
    {
        var ops = Layout(@"<div style='display:grid; grid-template-columns: 100pt 100pt 100pt'>
            <div style='grid-column: 1 / 4; display:grid; grid-template-columns: subgrid'>
              <div style='grid-column: 2 / 4; display:grid; grid-template-columns: subgrid; grid-auto-rows: 10pt'>
                <div style='grid-column: 2; background:#ff0000'></div>
              </div>
            </div></div>");
        Near(200, Box(ops, "#ff0000").X); Near(100, Box(ops, "#ff0000").W);
    }

    [Fact]
    public void Subgrid_outside_a_grid_falls_back_to_none()
    {
        var ops = Layout(@"<div><div style='display:grid; grid-template-columns: subgrid [a] [b]; grid-auto-rows: 10pt'>
            <div style='background:#ff0000'></div><div style='background:#00ff00'></div></div></div>");
        Near(500, Box(ops, "#ff0000").W);
        Near(0, Box(ops, "#00ff00").X); Near(10, Box(ops, "#00ff00").Y); Near(500, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void Subgrid_rows_paginate_with_the_parents_rows()
    {
        // The second card's two rows do not fit under the first card's, so the parent moves them to the next page whole.
        var ops = Layout(@"<div style='display:grid; grid-template-columns: 1fr'>
            <div style='grid-row: span 2; display:grid; grid-template-rows: subgrid'>
              <div style='height:300pt; background:#ff0000'></div><div style='height:300pt; background:#00ff00'></div>
            </div>
            <div style='grid-row: span 2; display:grid; grid-template-rows: subgrid'>
              <div style='height:300pt; background:#0000ff'></div><div style='height:300pt; background:#ffff00'></div>
            </div></div>");
        Near(0, Box(ops, "#ff0000").Y); Near(300, Box(ops, "#00ff00").Y);
        Near(800, Box(ops, "#0000ff").Y); Near(1100, Box(ops, "#ffff00").Y);
    }

    [Fact]
    public void Subgrid_name_lists_fill_the_lines()
    {
        var doc = HtmlParser.Parse("<div style='grid-template-columns: subgrid [a b] repeat(auto-fill, [c]) [d]; grid-template-rows: subgrid repeat(2, [r] [s])'>x</div>");
        new StyleResolver(new Stylesheet()).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var s = doc.FindFirst("div")!.Style;
        Assert.True(s.GridTemplateColumns!.Subgrid);
        Assert.Equal(new[] { "a b", "c", "c", "c", "d" }, s.GridTemplateColumns.SubgridLineNames(5).Select(n => string.Join(' ', n!)));
        Assert.Equal(new[] { "a b", "d" }, s.GridTemplateColumns.SubgridLineNames(2).Select(n => string.Join(' ', n!)));
        Assert.Equal(new[] { "r", "s", "r", "s" }, s.GridTemplateRows!.SubgridLineNames(4).Select(n => string.Join(' ', n!)));
        Assert.Equal(4, s.GridTemplateRows.SubgridNamedLines);
    }

    // ---- transforms ----

    [Fact]
    public void Individual_transform_properties_combine_with_transform()
    {
        var doc = HtmlParser.Parse("<div id=a style='translate: 10px 20px; rotate: 45deg; scale: 2; transform: skewX(10deg)'>x</div>");
        new StyleResolver(new Stylesheet()).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var t = doc.FindFirst("div")!.Style.Transform!;
        Assert.Equal(new[] { TransformKind.Translate, TransformKind.Rotate, TransformKind.Scale, TransformKind.Skew }, t.Select(f => f.Kind));
    }

    // ---- inline text ----

    [Fact]
    public void Line_clamp_keeps_the_first_lines_and_adds_an_ellipsis()
    {
        var words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 40));
        var ops = Layout($"<p style='display:-webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; width: 200pt'>{words}</p>");
        var lines = ops.OfType<TextOp>().Select(t => t.Baseline).Distinct().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains(ops.OfType<TextOp>(), t => t.Text.EndsWith("…", StringComparison.Ordinal));
    }

    [Fact]
    public void Small_caps_are_synthesized_from_capitals()
    {
        var ops = Layout("<p style='font-variant: small-caps; font-size: 20pt'>Hello</p>");
        var texts = ops.OfType<TextOp>().ToList();
        Assert.Contains(texts, t => t.Text == "H" && t.Size == 20);
        Assert.Contains(texts, t => t.Text == "ELLO" && Math.Abs(t.Size - 14) < 0.01f);
    }

    [Fact]
    public void Text_align_last_aligns_the_final_line()
    {
        var ops = Layout("<p style='width: 300pt; text-align: justify; text-align-last: right'>word word word word word word word word word word word word word word word word word</p>");
        var last = ops.OfType<TextOp>().OrderBy(t => t.Baseline).Last();
        float right = last.X + last.Font.Measure(last.Text, last.Size);
        Near(300, right, 1);
    }

    [Fact]
    public void Tab_size_and_hyphens_none()
    {
        var tabs = Layout("<pre style='tab-size: 2'>\tx</pre><pre>\ty</pre>");
        Assert.Equal("  x", Text(tabs, "x").Text);
        Assert.Equal("        y", Text(tabs, "y").Text);
        var noHyphen = Layout("<p style='width: 40pt; hyphens: none'>extra­ordinary</p>");
        Assert.DoesNotContain(noHyphen.OfType<TextOp>(), t => t.Text.EndsWith("-", StringComparison.Ordinal));
        var hyphen = Layout("<p style='width: 40pt'>extra­ordinary</p>");
        Assert.Contains(hyphen.OfType<TextOp>(), t => t.Text.EndsWith("-", StringComparison.Ordinal));
    }

    [Fact]
    public void Marker_pseudo_element_and_string_list_markers()
    {
        var ops = Layout(@"<style>.m li::marker { color: red; content: '\2192  ' } .s { list-style-type: '\2014 ' }</style>
            <ul class=m><li>a</li></ul><ul class=s><li>b</li></ul>");
        Assert.Contains(ops.OfType<TextOp>(), t => t.Text.StartsWith("→", StringComparison.Ordinal) && t.Color == 0xFFFF0000);
        Assert.Contains(ops.OfType<TextOp>(), t => t.Text.StartsWith("—", StringComparison.Ordinal));
    }

    [Fact]
    public void Aspect_ratio_with_a_definite_height_gives_the_width_in_a_flex_row()
    {
        var ops = Layout("<div style='display:flex'><div style='height: 40pt; aspect-ratio: 2; background:#ff0000'></div><div style='background:#00ff00'>x</div></div>");
        Near(80, Box(ops, "#ff0000").W);
        Near(80, Box(ops, "#00ff00").X);
    }

    [Fact]
    public void A_stretched_flex_item_taller_than_a_fixed_height_row_keeps_its_place()
    {
        // A Bootstrap progress bar: a 14px flex row whose bar has a 15.75px text line. The bar used to be moved down by
        // its own offset when the line was placed a second time, below the row and out of its clip.
        var ops = Layout("<div style='height:60pt'></div><div style='display:flex;height:10.5pt;overflow:hidden;border-radius:4pt;background:#e9ecef'>"
                         + "<div style='width:72%;background:#0d6efd;font-size:15pt'>72%</div></div>");
        var bar = Box(ops, "#0d6efd");
        Near(60, bar.Y);
        Near(10.5f, bar.H);
    }
}
