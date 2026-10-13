using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Custom properties, flexbox, grid, multi-column, inline SVG, viewport units and running header/footer bands.</summary>
public class LayoutFeatureTests
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
        return engine.Ops;
    }

    private static Element Styled(string html, float width = Width, float height = PageHeight)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, width, height).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static Element ById(Element root, string id)
        => Descendants(root).First(e => e.Id == id);

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static RectOp Box(List<DrawOp> ops, string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return Flatten(ops).OfType<RectOp>().Single(r => r.Color == c);
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    // ---- Custom properties ----

    [Fact]
    public void Var_resolves_inherited_values_fallbacks_and_nesting()
    {
        var doc = Styled("""
            <style>
              :root { --main: #ff0000; --pad: 10px; --Mixed: 3pt }
              .a { color: var(--main); padding: var(--pad) var(--missing, 4px); margin-left: var(--Mixed) }
              .b { --main: #0000ff; color: var(--nope, var(--main)) }
              .c { color: var(--undefined-without-fallback) }
            </style>
            <div class="a" id="a"><p class="b" id="b">x</p><p class="c" id="c">y</p></div>
            """);
        var a = ById(doc, "a").Style!;
        Assert.True(CssColor.TryParse("#ff0000", 0, out var red));
        Assert.True(CssColor.TryParse("#0000ff", 0, out var blue));
        Assert.Equal(red, a.Color);
        Near(7.5f, a.PaddingTop.Value);   // 10px
        Near(3f, a.PaddingRight.Value);   // 4px fallback
        Near(3f, a.MarginLeft.Value);     // custom property names are case-sensitive
        Assert.Equal(blue, ById(doc, "b").Style!.Color);
        Assert.Equal(red, ById(doc, "c").Style!.Color); // invalid at computed time: inherits
    }

    [Fact]
    public void Var_inside_shorthands_is_expanded_after_substitution()
    {
        var doc = Styled("<style>:root{--b: 3pt solid #00ff00} div{border: var(--b); margin: var(--m, 1pt 2pt)}</style><div id='d'>x</div>");
        var s = ById(doc, "d").Style!;
        Near(3f, s.BorderTopWidth);
        Near(1f, s.MarginTop.Value);
        Near(2f, s.MarginRight.Value);
    }

    // ---- Flexbox ----

    [Fact]
    public void Flex_row_distributes_free_space_by_grow()
    {
        var ops = Layout("""
            <div style="display:flex; gap:10pt">
              <div style="flex:1; height:20pt; background:#ff0000"></div>
              <div style="flex:3; height:20pt; background:#00ff00"></div>
              <div style="width:90pt; height:20pt; background:#0000ff"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00"); var c = Box(ops, "#0000ff");
        // 500 - 2 gaps - 90 leaves 390 to share 1:3
        Near(0, a.X); Near(97.5f, a.W);
        Near(107.5f, b.X); Near(292.5f, b.W);
        Near(410, c.X); Near(90, c.W);
        Near(a.Y, c.Y);
    }

    [Fact]
    public void Flex_justify_and_align_items()
    {
        var ops = Layout("""
            <div style="display:flex; justify-content:space-between; align-items:center; height:100pt">
              <div style="width:50pt; height:20pt; background:#ff0000"></div>
              <div style="width:50pt; height:40pt; background:#00ff00"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00");
        Near(0, a.X); Near(450, b.X);
        Near(40, a.Y); Near(30, b.Y);
    }

    [Fact]
    public void Flex_items_stretch_to_the_tallest_and_wrap()
    {
        var ops = Layout("""
            <div style="display:flex; flex-wrap:wrap">
              <div style="width:300pt; background:#ff0000"><div style="height:50pt"></div></div>
              <div style="width:150pt; background:#00ff00">short</div>
              <div style="width:100pt; height:10pt; background:#0000ff"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00"); var c = Box(ops, "#0000ff");
        Near(50, b.H);              // stretched to line height
        Near(300, b.X);
        Near(0, c.X); Near(50, c.Y); // wrapped to second line
        Near(a.Y, b.Y);
    }

    [Fact]
    public void Flex_column_grows_inside_a_definite_height_and_respects_order()
    {
        var ops = Layout("""
            <div style="display:flex; flex-direction:column; height:200pt">
              <div style="height:20pt; background:#ff0000; order:2"></div>
              <div style="flex-grow:1; background:#00ff00"></div>
              <div style="height:30pt; background:#0000ff; order:-1"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00"); var c = Box(ops, "#0000ff");
        Near(0, c.Y); Near(30, b.Y); Near(150, b.H); Near(180, a.Y);
        Near(500, b.W);
    }

    [Fact]
    public void Margin_auto_pushes_flex_items_apart()
    {
        var ops = Layout("""
            <div style="display:flex">
              <div style="width:50pt; height:10pt; background:#ff0000"></div>
              <div style="width:50pt; height:10pt; background:#00ff00; margin-left:auto"></div>
            </div>
            """);
        Near(450, Box(ops, "#00ff00").X);
    }

    // ---- Grid ----

    [Fact]
    public void Grid_sizes_fixed_fr_and_auto_tracks()
    {
        var ops = Layout("""
            <div style="display:grid; grid-template-columns: 100pt 1fr 2fr; column-gap:10pt; row-gap:5pt">
              <div style="height:20pt; background:#ff0000"></div>
              <div style="height:30pt; background:#00ff00"></div>
              <div style="background:#0000ff">x</div>
              <div style="height:10pt; background:#ffff00"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00"); var c = Box(ops, "#0000ff"); var d = Box(ops, "#ffff00");
        Near(0, a.X); Near(100, a.W);
        // 500 - 100 - 2 gaps leaves 380 to share 1:2
        Near(110, b.X); Near(126.67f, b.W);
        Near(246.67f, c.X); Near(253.33f, c.W);
        Near(30, c.H);               // stretched to the row
        Near(0, d.X); Near(35, d.Y); // auto-placed on row 2 after the gap
    }

    [Fact]
    public void Inline_block_in_a_fr_column_contributes_its_min_content_width()
    {
        // Like Chrome: the inline-block can wrap, so it does not widen its 1fr column to its unwrapped width.
        var ops = Layout("""
            <div style="display:grid; grid-template-columns: 1fr 1fr 1fr">
              <div style="background:#ff0000">one two three four five six seven eight nine ten</div>
              <div style="background:#00ff00; text-align:center">
                <div style="display:inline-block; text-align:left">eleven twelve thirteen fourteen fifteen sixteen seventeen</div>
              </div>
              <div style="background:#0000ff; text-align:right">10.242,50</div>
            </div>
            """);
        Near(0, Box(ops, "#ff0000").X);
        Near(500 / 3f, Box(ops, "#ff0000").W);
        Near(500 / 3f, Box(ops, "#00ff00").X);
        Near(500 / 3f, Box(ops, "#00ff00").W);
        Near(1000 / 3f, Box(ops, "#0000ff").X);
    }

    [Fact]
    public void Grid_explicit_placement_spans_and_repeat()
    {
        var ops = Layout("""
            <div style="display:grid; grid-template-columns: repeat(4, 1fr); grid-auto-rows: 20pt">
              <div style="grid-column: 2 / span 2; background:#ff0000"></div>
              <div style="grid-row: 1 / 3; grid-column: 4; background:#00ff00"></div>
              <div style="background:#0000ff"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00"); var c = Box(ops, "#0000ff");
        Near(125, a.X); Near(250, a.W);
        Near(375, b.X); Near(40, b.H);
        // sparse auto-placement: the cursor is past the first item, so the next free cell is row 2, column 1
        Near(0, c.X); Near(20, c.Y);
    }

    [Fact]
    public void Grid_auto_fill_minmax_and_self_alignment()
    {
        var ops = Layout("""
            <div style="display:grid; grid-template-columns: repeat(auto-fill, minmax(120pt, 1fr)); justify-items:center">
              <div style="width:20pt; height:10pt; background:#ff0000"></div>
              <div style="width:20pt; height:10pt; background:#00ff00; justify-self:end"></div>
            </div>
            """);
        // 4 columns of 125pt
        Near(52.5f, Box(ops, "#ff0000").X);
        Near(230, Box(ops, "#00ff00").X);
    }

    [Fact]
    public void Grid_rows_move_to_the_next_page_as_a_unit()
    {
        var ops = Layout("""
            <div style="height:770pt"></div>
            <div style="display:grid; grid-template-columns:1fr 1fr">
              <div style="height:40pt; background:#ff0000"></div>
              <div style="height:20pt; background:#00ff00"></div>
            </div>
            """);
        Near(PageHeight, Box(ops, "#ff0000").Y);
        Near(PageHeight, Box(ops, "#00ff00").Y);
    }

    // ---- Multi-column ----

    [Fact]
    public void Column_count_balances_children_into_columns()
    {
        var ops = Layout("""
            <div style="column-count:2; column-gap:20pt">
              <div style="height:30pt; background:#ff0000"></div>
              <div style="height:30pt; background:#00ff00"></div>
              <div style="height:30pt; background:#0000ff"></div>
              <div style="height:30pt; background:#ffff00"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var c = Box(ops, "#0000ff"); var d = Box(ops, "#ffff00");
        Near(240, a.W);
        Near(0, a.X); Near(260, c.X); Near(0, c.Y); Near(30, d.Y);
    }

    // ---- SVG ----

    [Fact]
    public void Svg_paths_parse_all_commands()
    {
        var p = PathParser.Parse("M10 10 h20 v20 H10 Z m5 5 l1-1 c1 1 2 2 3 3 s1 1 2 2 q1 1 2 2 t2 2 a5 5 0 0 1 10 0 A 1 1 0 1 0 50 50z")!;
        Assert.Equal(SvgShape.MoveTo, p.Verbs[0]);
        Assert.Contains(SvgShape.CurveTo, p.Verbs);
        Assert.Equal(SvgShape.Close, p.Verbs[^1]);
        // h20 from (10,10) → (30,10)
        Near(30, p.Points[2]); Near(10, p.Points[3]);
        // compact numbers like "1-1" and ".5.5" split correctly
        var q = PathParser.Parse("M.5.5L1e1-2")!;
        Near(0.5f, q.Points[0]); Near(0.5f, q.Points[1]); Near(10, q.Points[2]); Near(-2, q.Points[3]);
    }

    [Fact]
    public void Svg_builds_shapes_with_transforms_and_styles()
    {
        var doc = Styled("""
            <svg id="s" width="100" height="50" viewBox="0 0 200 100" style="fill: #00ff00">
              <rect x="10" y="10" width="20" height="20" fill="#ff0000"/>
              <g transform="translate(100 0) scale(2)"><circle cx="5" cy="5" r="5" stroke="#0000ff" stroke-width="2"/></g>
              <path d="M0 0L10 10" fill="none"/>
              <text x="0" y="0">drawn as text</text>
            </svg>
            """);
        var svg = SvgImage.Build(ById(doc, "s"));
        Near(75, svg.Width!.Value); Near(37.5f, svg.Height!.Value);
        Assert.True(svg.HasViewBox);
        Assert.Equal(2, svg.Shapes.Count(s => s.Kind == SvgShape.KindPath && s.Fill != null));
        Assert.Single(svg.Shapes, s => s.Kind == SvgShape.KindText);
        var circle = svg.Shapes.Single(s => s.Stroke != null);
        Near(4, circle.StrokeWidth);  // scaled by the group transform
        Assert.True(CssColor.TryParse("#00ff00", 0, out var green));
        Assert.Equal(green, circle.Fill);  // inherited from the svg's CSS
        Assert.True(circle.Points.Max() >= 120 - 0.01f); // 100 + 2*10
    }

    [Fact]
    public void Inline_svg_is_drawn_into_the_pdf()
    {
        var pdf = ConvertUncompressed("<p>Logo <svg width='40' height='20' viewBox='0 0 40 20'><rect width='40' height='20' fill='#ff0000'/></svg> end</p>");
        var content = AllContent(pdf);
        Assert.Contains(" cm", content);
        Assert.Contains("1 0 0 rg", content);
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int red = 0;
        for (int i = 0; i < img.Width * img.Height; i++)
            if (img.Rgba[i * 4] > 240 && img.Rgba[i * 4 + 1] < 20 && img.Rgba[i * 4 + 2] < 20) red++;
        Assert.InRange(red, 30 * 15 - 60, 30 * 15 + 60); // 40x20 px = 30x15 pt
    }

    // ---- Viewport units ----

    [Fact]
    public void Vh_refers_to_the_page_content_area()
    {
        var doc = Styled("<div id='d' style='height:50vh; width:10vw; min-height:100vh'></div>", 500, 800);
        var s = ById(doc, "d").Style!;
        // The viewport is rounded down to whole CSS pixels, as in Chrome: 800pt is 1066.67px, so 100vh is 1066px.
        Near(399.75f, s.Height.Value);
        Near(49.95f, s.Width.Value);
        var pdf = HtmlToPdf.Convert("<body style='margin:0'><div style='min-height:100vh;background:#eee'>x</div></body>");
        Assert.Equal(1, PageCount(pdf));
    }

    // ---- Running header and footer ----

    [Fact]
    public void Sticky_header_and_footer_repeat_with_page_numbers()
    {
        var html = """
            <header data-sticky-pdf-header height="15mm">Company header</header>
            <main>
            """ + string.Concat(Enumerable.Range(1, 120).Select(i => $"<p>Line {i}</p>")) + """
            </main>
            <footer data-sticky-pdf-footer height="10mm">Side {page} af {total-pages}</footer>
            """;
        var pdf = ConvertUncompressed(html);
        int pages = PageCount(pdf);
        Assert.True(pages >= 2);
        var content = AllContent(pdf);
        Assert.Equal(pages, CountOf(content, "(Company header) Tj"));
        for (int p = 1; p <= pages; p++) Assert.Contains($"(Side {p} af {pages}) Tj", content);
        Assert.DoesNotContain("{page}", content);
    }

    [Fact]
    public void TruncateBandsToWholePoints_rounds_the_declared_header_height_down()
    {
        // height="17mm" is 48.19pt; rounded down to 48pt, the flowing content starts 0.19pt higher.
        const string html = """
            <body style="margin:0"><header data-sticky-pdf-header height="17mm">H</header><p style="margin:0">Body</p></body>
            """;
        float exact = BodyBaseline(new PdfOptions());
        float truncated = BodyBaseline(new PdfOptions { TruncateBandsToWholePoints = true });
        Assert.Equal(17 / 25.4f * 72 - 48, truncated - exact, 0.02f);

        // A height that is already whole on paper is kept.
        Assert.Equal(BodyBaseline(new PdfOptions(), "48pt"), BodyBaseline(new PdfOptions { TruncateBandsToWholePoints = true }, "48pt"));

        float BodyBaseline(PdfOptions options, string height = "17mm")
        {
            var content = AllContent(ConvertUncompressed(html.Replace("17mm", height), options));
            var m = Regex.Match(content, @"(-?[\d.]+) Tm\s*(?:\[\(Body\)|\(Body\))");
            Assert.True(m.Success, content);
            return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public void Single_page_with_footer_fits_min_height_100vh()
    {
        // The paragraph has no margin: a default <p> margin would collapse through the
        // min-height:100vh div and push it past the page, which Chrome also does.
        var pdf = HtmlToPdf.Convert("""
            <body style="margin:0"><div style="min-height:100vh">
              <header data-sticky-pdf-header height="17mm">H</header>
              <p style="margin:0">Body</p>
              <footer data-sticky-pdf-footer height="12mm">Side {page} af {total-pages}</footer>
            </div></body>
            """);
        Assert.Equal(1, PageCount(pdf));
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
