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
        var doc = HtmlParser.Parse(html);
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
}
