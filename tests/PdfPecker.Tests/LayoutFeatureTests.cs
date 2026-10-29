using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;
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
    public void Flex_items_with_min_width_zero_shrink_below_their_content()
    {
        // min-width: 0 lets an item shrink below its longest word; min-width: auto keeps the word whole.
        var ops = Layout("""
            <div style="display:flex; width:300pt">
              <div style="flex:1; background:#ff0000"><span style="display:inline-block; width:200pt; height:10pt"></span></div>
              <div style="flex:1; min-width:0; background:#00ff00"><span style="display:inline-block; width:200pt; height:10pt"></span></div>
            </div>
            """);
        Near(200, Box(ops, "#ff0000").W);
        Near(100, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void Wrapping_column_flex_box_starts_a_new_column_when_full()
    {
        // Three 25pt items fit in neither one 60pt column: the third starts a second column, and the columns share the
        // width (align-content: normal stretches the lines).
        var ops = Layout("""
            <div style="display:flex; flex-direction:column; flex-wrap:wrap; height:60pt; width:200pt">
              <div style="height:25pt; background:#ff0000"></div>
              <div style="height:25pt; background:#00ff00"></div>
              <div style="height:25pt; background:#0000ff"></div>
            </div>
            """);
        var a = Box(ops, "#ff0000"); var b = Box(ops, "#00ff00"); var c = Box(ops, "#0000ff");
        Near(0, a.X); Near(0, b.X); Near(25, b.Y);
        Near(100, c.X); Near(a.Y, c.Y);
        Near(100, a.W); Near(100, c.W);
    }

    [Fact]
    public void Intrinsic_width_keywords_size_the_box_by_its_content()
    {
        const string words = "<span style='display:inline-block; width:80pt; height:10pt'></span> <span style='display:inline-block; width:80pt; height:10pt'></span>";
        var ops = Layout($"""
            <div style="width:120pt">
              <div style="width:fit-content; background:#ff0000">{words}</div>
              <div style="width:min-content; background:#00ff00">{words}</div>
              <div style="width:max-content; background:#0000ff">{words}</div>
              <div style="width:fit-content; padding:0 5pt; background:#ffff00"><span style="display:inline-block; width:30pt; height:10pt"></span></div>
            </div>
            """);
        Near(120, Box(ops, "#ff0000").W);   // fit-content: the available width, which is less than the max-content width
        Near(80, Box(ops, "#00ff00").W);    // min-content: the widest word
        Assert.True(Box(ops, "#0000ff").W > 160); // max-content: both words on one line, wider than the container
        Near(40, Box(ops, "#ffff00").W);    // fit-content: the content when it is narrower, plus padding
    }

    [Fact]
    public void Grid_fit_content_track_is_capped_at_its_length()
    {
        var ops = Layout("""
            <div style="display:grid; grid-template-columns:fit-content(100pt) 1fr; width:400pt">
              <div style="background:#ff0000"><span style="display:inline-block; width:60pt; height:10pt"></span> <span style="display:inline-block; width:60pt; height:10pt"></span></div>
              <div style="background:#00ff00">x</div>
            </div>
            """);
        Near(100, Box(ops, "#ff0000").W);
        Near(100, Box(ops, "#00ff00").X);
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

    [Theory]
    [InlineData("a <span style='display:inline-block'>b</span> cccccccccccccccccccc")]
    [InlineData("a <span style='display:inline-block'>b <span style='display:inline-block'>c</span></span> dddd")]
    public void Words_around_an_inline_block_keep_their_intrinsic_widths(string content)
    {
        // Laying out the inline-block while measuring its paragraph handed that paragraph's pooled words out again, so
        // the words after the box were counted in place of the words before it: here "a" as wide as the long word.
        static (float Min, float Max) Measure(string html)
        {
            var div = Find(Styled($"<div>{html}</div>"), "div").First();
            return new LayoutEngine(PageHeight, new ImageLoader(null, null, false)).MeasureIntrinsic(div);
        }
        var block = Measure(content);
        var inline = Measure(content.Replace("display:inline-block", "display:inline"));
        Near(inline.Min, block.Min);
        Near(inline.Max, block.Max);
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
    public void Grid_rows_break_across_pages_with_their_items()
    {
        // As in Chrome, a row that fits on a page but not in the room left on this one is not moved whole: its items
        // break at the page boundary.
        var ops = Layout("""
            <div style="height:770pt"></div>
            <div style="display:grid; grid-template-columns:1fr 1fr">
              <div style="height:40pt; background:#ff0000"></div>
              <div style="height:20pt; background:#00ff00"></div>
            </div>
            """);
        Near(770, Box(ops, "#ff0000").Y);
        Near(770, Box(ops, "#00ff00").Y);
    }

    [Fact]
    public void Grid_rows_with_an_item_that_avoids_breaks_move_to_the_next_page_as_a_unit()
    {
        var ops = Layout("""
            <div style="height:770pt"></div>
            <div style="display:grid; grid-template-columns:1fr 1fr">
              <div style="height:40pt; background:#ff0000; break-inside:avoid"></div>
              <div style="height:20pt; background:#00ff00"></div>
            </div>
            """);
        Near(PageHeight, Box(ops, "#ff0000").Y);
        Near(PageHeight, Box(ops, "#00ff00").Y);
    }

    [Theory]
    [InlineData(750, false)] // room for two lines: both items start on the first page
    [InlineData(770, true)]  // room for one line: the paragraph's orphans would leave nothing of it there
    public void Grid_rows_move_whole_when_an_item_would_leave_no_content_on_the_page(float spacer, bool moves)
    {
        var ops = Layout($"""
            <div style="height:{spacer}pt"></div>
            <div style="display:grid; grid-template-columns:1fr 1fr; font-size:10pt; line-height:20pt">
              <div><p style="margin:0">Alone</p><p style="margin:0">More</p></div>
              <div>First<br>Second<br>Third<br>Fourth</div>
            </div>
            """);
        var texts = Flatten(ops).OfType<TextOp>().ToList();
        float alone = texts.First(t => t.Text.Contains("Alone")).LineTop, first = texts.First(t => t.Text.Contains("First")).LineTop;
        Near(moves ? PageHeight : spacer, alone);
        Near(moves ? PageHeight : spacer, first);
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

    [Fact]
    public void Balanced_columns_keep_a_break_inside_avoid_column_grid_whole_in_the_first_column()
    {
        // Chrome starts balancing at the height of the tallest box that must stay whole, so the only grid is not split
        // into two 40pt columns: it fills the first column and the second stays empty (values measured in Chromium).
        var ops = Layout("""
            <div style="column-count:2; column-gap:20pt">
              <div style="display:grid; break-inside:avoid-column">
                <div style="height:20pt; background:#ff0000"></div><div style="height:20pt"></div>
                <div style="height:20pt"></div><div style="height:20pt; background:#0000ff"></div>
              </div>
            </div>
            <div style="height:5pt; background:#ffff00"></div>
            """);
        var last = Box(ops, "#0000ff");
        Near(0, last.X); Near(60, last.Y);
        Near(80, Box(ops, "#ffff00").Y);
    }

    [Fact]
    public void Balanced_columns_are_as_tall_as_the_tallest_box_that_avoids_a_column_break()
    {
        // Three short grids fill the first column and the 70pt one goes whole to the second, as in Chrome; growing the
        // columns from half the content by the shortages of its rows split it or made the columns taller than 70pt.
        var ops = Layout("""
            <style>.k { display: grid; break-inside: avoid-column }</style>
            <div style="column-count:2; column-gap:20pt">
              <div class="k"><div style="height:20pt; background:#ff0000"></div></div>
              <div class="k"><div style="height:20pt"></div></div>
              <div class="k"><div style="height:20pt"></div></div>
              <div class="k"><div style="height:30pt; background:#00ff00"></div><div style="height:40pt; background:#0000ff"></div></div>
            </div>
            <div style="height:5pt; background:#ffff00"></div>
            """);
        var top = Box(ops, "#00ff00"); var bottom = Box(ops, "#0000ff");
        Near(260, top.X); Near(0, top.Y);
        Near(260, bottom.X); Near(30, bottom.Y);
        Near(70, Box(ops, "#ffff00").Y);
    }

    [Fact]
    public void Grid_that_avoids_a_column_break_moves_whole_though_its_first_row_fits_the_first_column()
    {
        // Split at the end of the first column, the grid measured the room it left there as well and looked taller
        // than the second column, so it stayed split with its first row alone; Chrome moves it whole.
        var ops = Layout("""
            <style>.k { display: grid; break-inside: avoid-column }</style>
            <div style="column-count:2; column-gap:20pt">
              <div class="k"><div style="height:20pt"></div></div>
              <div class="k"><div style="height:20pt"></div></div>
              <div class="k"><div style="height:10pt"></div></div>
              <div class="k"><div style="height:10pt; background:#00ff00"></div><div style="height:60pt; background:#0000ff"></div></div>
            </div>
            <div style="height:5pt; background:#ffff00"></div>
            """);
        var head = Box(ops, "#00ff00"); var rows = Box(ops, "#0000ff");
        Near(260, head.X); Near(0, head.Y);
        Near(260, rows.X); Near(10, rows.Y);
        Near(70, Box(ops, "#ffff00").Y);
    }

    [Fact]
    public void Balanced_columns_grow_by_what_a_box_moved_on_whole_was_short_of()
    {
        // The third grid misses the first column by 10pt and moves on whole: the columns grow by those 10pt to 90pt, as
        // in Chrome, not by the 20pt the last grid's second row missed by.
        var ops = Layout("""
            <style>.k { display: grid; break-inside: avoid-column }</style>
            <div style="column-count:2; column-gap:20pt">
              <div class="k"><div style="height:30pt"></div></div>
              <div class="k"><div style="height:30pt"></div></div>
              <div class="k"><div style="height:30pt; background:#ff0000"></div></div>
              <div class="k"><div style="height:35pt; background:#00ff00"></div><div style="height:35pt"></div></div>
            </div>
            <div style="height:5pt; background:#ffff00"></div>
            """);
        var third = Box(ops, "#ff0000"); var last = Box(ops, "#00ff00");
        Near(0, third.X); Near(60, third.Y);
        Near(260, last.X); Near(0, last.Y);
        Near(90, Box(ops, "#ffff00").Y);
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

    // ---- Percentage heights (the expected boxes were measured in Chromium's PDF output of the same HTML) ----

    private static RectOp Rect(string html, string color, float pageHeight = PageHeight)
        => Box(Layout("<style>body { margin: 0 }</style>" + html, pageHeight: pageHeight), color);

    private static void NearBox(RectOp r, float x, float y, float w, float h)
    {
        Near(x, r.X);
        Near(y, r.Y);
        Near(w, r.W);
        Near(h, r.H);
    }

    [Fact]
    public void Percentage_height_through_html_and_body_fills_the_page()
    {
        var ops = Layout("""
            <style>html, body { height: 100%; margin: 0 } .cover { height: 100%; background: #ff0000 }
            .after { height: 40pt; background: #0000ff }</style>
            <div class="cover">Cover</div><div class="after"></div>
            """);
        NearBox(Box(ops, "#ff0000"), 0, 0, 500, 800);
        NearBox(Box(ops, "#0000ff"), 0, 800, 500, 40); // the next box starts the second page
    }

    [Fact]
    public void Percentage_chain_halves_at_every_level()
    {
        var ops = Layout("""
            <style>html, body { height: 100%; margin: 0 } .w { height: 50%; background: #888888 }
            .c { height: 50%; background: #ff0000 } .m { min-height: 10%; background: #0000ff }</style>
            <div class="w"><div class="c"></div><div class="m"></div></div>
            """);
        NearBox(Box(ops, "#888888"), 0, 0, 500, 400);
        NearBox(Box(ops, "#ff0000"), 0, 0, 500, 200);
        NearBox(Box(ops, "#0000ff"), 0, 200, 500, 40);
    }

    [Fact]
    public void Percentage_heights_in_a_fixed_height_card_resolve_against_its_content_box()
    {
        var html = """
            <style>.card { height: 200pt; width: 200pt; border: 6pt solid #000000; padding: 10pt; background: #888888 }
            .a { height: 50%; background: #ff0000 } .b { height: 25%; margin-top: 10pt; background: #0000ff }
            .c { height: calc(25% - 20pt); background: #00ff00 }
            .d { height: 50%; box-sizing: border-box; padding: 10pt; background: #ffff00 }</style>
            <div class="card"><div class="a"></div><div class="b"></div><div class="c"></div></div>
            <div class="card"><div class="d"></div></div>
            """;
        NearBox(Rect(html, "#ff0000"), 16, 16, 200, 100);
        NearBox(Rect(html, "#0000ff"), 16, 126, 200, 50);
        NearBox(Rect(html, "#00ff00"), 16, 176, 200, 30);
        NearBox(Rect(html, "#ffff00"), 16, 248, 200, 100); // border-box: the padding is inside the 50%
    }

    [Fact]
    public void Percentage_heights_against_an_auto_height_behave_as_auto()
    {
        var html = """
            <style>.p { background: #888888 } .a { height: 50%; background: #ff0000 } .b { min-height: 50%; background: #0000ff }
            .c { max-height: 10%; background: #00ff00 }</style>
            <div class="p"><div class="a">a</div><div class="b">b</div><div class="c">c<br>c<br>c</div></div>
            """;
        float line = Rect(html, "#ff0000").H;
        Assert.True(line is > 5 and < 20, $"one line: {line}");
        Near(line, Rect(html, "#0000ff").H);
        Near(3 * line, Rect(html, "#00ff00").H, 0.5f);
    }

    [Fact]
    public void Sidebar_with_height_100_percent_in_a_fixed_height_flex_row()
    {
        var html = """
            <style>.row { display: flex; height: 200pt; background: #888888 } .side { width: 80pt; height: 100%; background: #ff0000 }
            .side .in { height: 50%; background: #0000ff } .main { flex: 1; background: #00ff00 }
            .main .m { height: 40%; background: #ffff00 } .top { width: 60pt; height: 30%; align-self: flex-start; background: #ff00ff }</style>
            <div class="row"><div class="side"><div class="in"></div></div><div class="main"><div class="m"></div></div><div class="top"></div></div>
            """;
        NearBox(Rect(html, "#ff0000"), 0, 0, 80, 200);
        NearBox(Rect(html, "#0000ff"), 0, 0, 80, 100);
        NearBox(Rect(html, "#ffff00"), 80, 0, 360, 80); // a stretched item's height is definite
        NearBox(Rect(html, "#ff00ff"), 440, 0, 60, 60);
    }

    [Fact]
    public void Stretched_flex_item_of_an_auto_height_row_is_definite()
    {
        var html = """
            <style>.row { display: flex } .t { width: 50pt; height: 100pt; background: #ff0000 }
            .q { width: 50pt; background: #00ff00 } .q .in { height: 50%; background: #ffff00 }</style>
            <div class="row"><div class="t"></div><div class="q"><div class="in"></div></div></div>
            """;
        NearBox(Rect(html, "#ffff00"), 50, 0, 50, 50);
    }

    [Fact]
    public void Grown_item_of_a_fixed_height_flex_column_is_definite()
    {
        var html = """
            <style>.col { display: flex; flex-direction: column; height: 200pt; width: 200pt } .top { height: 25%; background: #ff0000 }
            .grow { flex: 1; background: #0000ff } .grow .in { height: 50%; width: 50pt; background: #ffff00 }</style>
            <div class="col"><div class="top"></div><div class="grow"><div class="in"></div></div></div>
            """;
        NearBox(Rect(html, "#ff0000"), 0, 0, 200, 50);
        NearBox(Rect(html, "#ffff00"), 0, 50, 50, 75);
        // With only a min-height the container's height is not definite, and neither is the grown item's.
        var minOnly = html.Replace("height: 200pt; width", "min-height: 200pt; width", StringComparison.Ordinal).Replace("top { height: 25%", "top { height: 50pt", StringComparison.Ordinal);
        var grown = Rect(minOnly, "#0000ff");
        NearBox(grown, 0, 50, 200, 150);
        Assert.True(CssColor.TryParse("#ffff00", 0, out var yellow));
        Assert.DoesNotContain(Flatten(Layout("<style>body { margin: 0 }</style>" + minOnly)).OfType<RectOp>(), r => r.Color == yellow && r.H > 0.01f);
    }

    [Fact]
    public void Image_with_a_percentage_height_in_a_fixed_height_flex_row_takes_its_width_from_that_height()
    {
        var ops = Layout("""
            <style>body { margin: 0 } .row { display: flex; height: 150pt } .row div { width: 60pt; background: #00ff00 } svg { height: 100% }</style>
            <div class="row"><div></div><svg viewBox="0 0 2 1"><rect width="2" height="1" fill="#f00"/></svg></div>
            """);
        var svg = Flatten(ops).OfType<SvgOp>().Single();
        Near(60, svg.X);
        Near(0, svg.Y);
        Near(300, svg.W);
        Near(150, svg.H);
    }

    [Fact]
    public void App_shell_of_flex_columns_and_rows_fills_the_page()
    {
        var ops = Layout("""
            <style>html, body { height: 100%; margin: 0 } .app { display: flex; flex-direction: column; height: 100% }
            header { height: 40pt; background: #ff0000 } main { flex: 1; display: flex } nav { width: 80pt; background: #0000ff }
            nav .in { height: 50%; background: #00ff00 } .content { flex: 1 } .content .in { height: 100%; width: 50pt; background: #ffff00 }</style>
            <div class="app"><header></header><main><nav><div class="in"></div></nav><div class="content"><div class="in"></div></div></main></div>
            """);
        NearBox(Box(ops, "#0000ff"), 0, 40, 80, 760);
        NearBox(Box(ops, "#00ff00"), 0, 40, 80, 380);
        NearBox(Box(ops, "#ffff00"), 80, 40, 50, 760);
    }

    [Fact]
    public void Stretched_grid_item_is_definite_and_one_aligned_to_the_start_is_not()
    {
        var html = """
            <style>.g { display: grid; height: 200pt; grid-template-rows: 1fr 1fr; grid-template-columns: 100pt 100pt }
            .i1 { background: #ff0000 } .i1 .in { height: 50%; background: #0000ff } .i2 { height: 50%; background: #00ff00 }
            .i3 { align-self: start; background: #ff00ff } .i3 .in { height: 50%; background: #ffff00 }</style>
            <div class="g"><div class="i1"><div class="in"></div></div><div class="i2"></div><div class="i3">t<div class="in"></div></div></div>
            """;
        NearBox(Rect(html, "#0000ff"), 0, 0, 100, 50);
        NearBox(Rect(html, "#00ff00"), 100, 0, 100, 50); // the item's own percentage refers to its grid area
        var ops = Layout("<style>body { margin: 0 }</style>" + html);
        Assert.True(CssColor.TryParse("#ffff00", 0, out var yellow));
        Assert.DoesNotContain(Flatten(ops).OfType<RectOp>(), r => r.Color == yellow && r.H > 0.01f);
    }

    [Fact]
    public void Absolutely_positioned_boxes_resolve_against_the_padding_box()
    {
        var html = """
            <style>.rel { position: relative; height: 200pt; width: 300pt; padding: 10pt }
            .x { position: absolute; left: 0; top: 0; width: 50pt; height: 50%; background: #ff0000 }
            .y { position: absolute; left: 100pt; top: 0; bottom: 0; width: 50pt; background: #0000ff } .y .in { height: 50%; background: #ffff00 }
            .z { position: absolute; left: 200pt; top: 10pt; width: 50pt; min-height: 30%; max-height: 20%; background: #00ff00 }</style>
            <div class="rel"><div class="x"></div><div class="y"><div class="in"></div></div><div class="z"></div></div>
            """;
        NearBox(Rect(html, "#ff0000"), 0, 0, 50, 110);
        NearBox(Rect(html, "#0000ff"), 100, 0, 50, 220);
        NearBox(Rect(html, "#ffff00"), 100, 0, 50, 110);
        NearBox(Rect(html, "#00ff00"), 200, 10, 50, 66); // min-height wins over max-height
    }

    [Fact]
    public void Aspect_ratio_with_a_percentage_height_and_as_the_source_of_a_definite_height()
    {
        var html = """
            <style>.box { height: 200pt } .a { height: 50%; aspect-ratio: 2; background: #ff0000 }
            .r { width: 200pt; aspect-ratio: 2; background: #0000ff } .r .in { height: 50%; width: 50pt; background: #ffff00 }</style>
            <div class="box"><div class="a"></div></div><div class="r"><div class="in"></div></div>
            """;
        NearBox(Rect(html, "#ff0000"), 0, 0, 200, 100);
        NearBox(Rect(html, "#ffff00"), 0, 200, 50, 50);
    }

    [Fact]
    public void Images_inline_blocks_and_floats_resolve_against_their_containing_block()
    {
        var ops = Layout("""
            <style>body { margin: 0 } .box { height: 150pt } svg { height: 100%; display: block }
            .fl { float: right; width: 40pt; height: 30%; background: #00ff00 }
            .w { height: 100pt } .ib { display: inline-block; width: 40pt; height: 50%; background: #0000ff }</style>
            <div class="box"><div class="fl"></div><svg viewBox="0 0 2 1"><rect width="2" height="1" fill="#f00"/></svg></div>
            <div class="w">x<span class="ib"></span></div>
            """);
        var svg = Flatten(ops).OfType<SvgOp>().Single();
        Near(150, svg.H);
        Near(300, svg.W);
        NearBox(Box(ops, "#00ff00"), 460, 0, 40, 45);
        Near(50, Box(ops, "#0000ff").H);
    }

    [Fact]
    public void Table_cells_are_definite_only_with_a_fixed_height_or_a_table_height()
    {
        // The table is 50% of its 200pt container. The first cell's child fills the row (100pt), not just the cell's
        // own 60pt; the second cell's child is half the row because the table's height is definite.
        var html = """
            <style>.w { height: 200pt } table { height: 50%; border-spacing: 0; background: #888888 } td { padding: 0; width: 100pt }
            .in { height: 100%; width: 30pt; background: #0000ff } .in2 { height: 50%; width: 30pt; background: #ffff00 }</style>
            <div class="w"><table><tr><td style="height: 60pt"><div class="in"></div></td><td><div class="in2">x</div></td></tr></table></div>
            """;
        NearBox(Rect(html, "#888888"), 0, 0, 200, 100);
        NearBox(Rect(html, "#0000ff"), 0, 0, 30, 100);
        NearBox(Rect(html, "#ffff00"), 100, 25, 30, 50); // vertically centred in the row
        // A row's height alone does not make its cells definite.
        var rowOnly = Layout("""
            <style>body { margin: 0 } table { border-spacing: 0 } td { padding: 0; width: 60pt }</style>
            <table><tr style="height: 80pt"><td><div style="height: 50%; background: #ff0000"></div></td><td>a</td></tr></table>
            """);
        Assert.True(CssColor.TryParse("#ff0000", 0, out var red));
        Assert.DoesNotContain(Flatten(rowOnly).OfType<RectOp>(), r => r.Color == red && r.H > 0.01f);
    }

    [Fact]
    public void Percentage_heights_in_a_definite_cell_spanning_rows_resolve_against_all_its_rows()
    {
        // As in Chrome: the cell is definite through its tiny height and spans two rows of 30pt and 40pt, so its child
        // fills 70pt, and a child of 50% in a cell spanning three rows is half of them, centred like the cell's content.
        var html = """
            <style>body { margin: 0 } table { border-spacing: 0 } td { padding: 0; width: 100pt }
            .a { height: 30pt } .b { height: 40pt } .c { height: 20pt }</style>
            <table><tr><td rowspan="2" style="height: 1px"><div style="height: 100%; background: #0000ff">x</div></td><td><div class="a"></div></td></tr>
            <tr><td><div class="b"></div></td></tr></table>
            <table><tr><td rowspan="3" style="height: 1px"><div style="height: 50%; background: #ff0000"></div></td><td><div class="c"></div></td></tr>
            <tr><td><div class="c"></div></td></tr><tr><td><div class="c"></div></td></tr></table>
            """;
        NearBox(Rect(html, "#0000ff"), 0, 0, 100, 70);
        NearBox(Rect(html, "#ff0000"), 0, 85, 100, 30);
    }

    [Fact]
    public void Tall_percentage_box_breaks_across_pages_and_its_children_resolve_against_it()
    {
        var ops = Layout("""
            <style>html, body { height: 100%; margin: 0 } .tall { height: 250%; width: 100pt; background: #ff0000 }
            .q { height: 20%; width: 50pt; background: #0000ff } .after { height: 30pt; background: #00ff00 }</style>
            <div class="tall"><div class="q"></div></div><div class="after"></div>
            """, pageHeight: 260);
        NearBox(Box(ops, "#ff0000"), 0, 0, 100, 650);
        NearBox(Box(ops, "#0000ff"), 0, 0, 50, 130);
        NearBox(Box(ops, "#00ff00"), 0, 650, 500, 30);
    }

    [Fact]
    public void Percentages_of_the_page_area_resolve_against_the_page_each_box_starts_on()
    {
        // The first page's flow is 200pt tall, the others 260pt, as with @page :first { margin: 50pt }. A box resolves
        // the chain up to the page area against the page it starts on, so a box that starts on page 2 gets its height.
        var doc = HtmlParser.Parse("""
            <style>html, body { height: 100%; margin: 0 } .a { height: 100%; background: #ff0000 } .b { height: 50%; background: #0000ff }</style>
            <div class="a"></div><div class="b"></div>
            """);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, 300, 200).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var first = new PageSetup { Width = 400, Height = 300, MarginTop = 50, MarginBottom = 50, MarginLeft = 50, MarginRight = 50 };
        var other = new PageSetup { Width = 400, Height = 300, MarginTop = 20, MarginBottom = 20, MarginLeft = 50, MarginRight = 50 };
        var engine = new LayoutEngine(new PageGrid(first, other, other), new ImageLoader(null, null, false));
        engine.Layout(doc, 300);
        NearBox(Box(engine.Ops, "#ff0000"), 0, 0, 300, 200);
        NearBox(Box(engine.Ops, "#0000ff"), 0, 200, 300, 130);
    }

    // ---- Fieldset and legend (as Chromium lays them out) ----

    private const string FieldsetCss = """
        <style>body { margin: 0 } fieldset { margin: 0; border: 3pt solid #0000ff; background: #ff0000; padding: 3pt 10pt 5pt }
        legend { height: 20pt; width: 60pt; padding: 0; background: #00ff00 } .c { height: 10pt; background: #ffff00 }</style>
        """;

    /// <summary>The clip that cuts the legend's gap out of the fieldset's border.</summary>
    private static ClipOp BorderGap(IEnumerable<DrawOp> ops)
    {
        static IEnumerable<ClipOp> Clips(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops.OfType<GroupOp>())
            {
                if (o is ClipOp { Hole: not null } c) yield return c;
                foreach (var inner in Clips(o.Ops)) yield return inner;
            }
        }
        return Clips(ops).Single();
    }

    private static void NearGap(ClipOp clip, float x, float y, float w, float h)
    {
        var (gx, gy, gw, gh) = clip.Hole!.Value;
        Near(x, gx);
        Near(y, gy);
        Near(w, gw);
        Near(h, gh);
    }

    [Fact]
    public void Legend_sits_on_the_top_border_which_is_cut_behind_it()
    {
        var ops = Layout(FieldsetCss + "<fieldset><legend></legend><div class=c></div></fieldset>");
        var legend = Box(ops, "#00ff00");
        var fieldset = Box(ops, "#ff0000");
        var content = Box(ops, "#ffff00");
        NearBox(legend, 13, 0, 60, 20); // after the border and the padding
        // The border moves down to the legend's middle, and the background with it.
        NearBox(fieldset, 0, 8.5f, 500, 20 + 3 + 10 + 5 + 3 - 8.5f);
        Near(23, content.Y); // below the legend and the top padding
        var clip = BorderGap(ops);
        NearGap(clip, 13, 0, 60, 20);
        Assert.Contains(Flatten(clip.Ops), o => o is BorderRectOp);
    }

    [Fact]
    public void Legend_thinner_than_the_border_is_centred_in_it()
    {
        var ops = Layout(FieldsetCss + "<fieldset style='border-width:30pt'><legend></legend><div class=c></div></fieldset>");
        NearBox(Box(ops, "#ff0000"), 0, 0, 500, 30 + 3 + 10 + 5 + 30);
        Near(5, Box(ops, "#00ff00").Y);
        Near(33, Box(ops, "#ffff00").Y);
        NearGap(BorderGap(ops), 40, 0, 60, 30);
    }

    [Theory]
    [InlineData("<fieldset><legend style='text-align:right'></legend></fieldset>", 500 - 13 - 60)]
    [InlineData("<fieldset><legend align=center></legend></fieldset>", 13 + (474 - 60) / 2f)]
    [InlineData("<fieldset dir=rtl><legend></legend></fieldset>", 500 - 13 - 60)]
    [InlineData("<fieldset><legend style='margin-left:auto'></legend></fieldset>", 500 - 13 - 60)]
    [InlineData("<fieldset><legend style='margin:0 7pt'></legend></fieldset>", 20)]
    public void Legend_is_placed_by_its_auto_margins_or_text_align(string html, float x)
        => Near(x, Box(Layout(FieldsetCss + html), "#00ff00").X);

    [Fact]
    public void Floated_legend_is_an_ordinary_box_and_the_next_legend_is_rendered()
    {
        var ops = Layout(FieldsetCss + """
            <fieldset><legend style="float:left;background:#00ffff"></legend><legend></legend><div class=c></div></fieldset>
            """);
        Near(0, Box(ops, "#00ff00").Y);
        Near(23, Box(ops, "#00ffff").Y); // in the content, beside the yellow box
        Near(23, Box(ops, "#ffff00").Y);
    }

    [Fact]
    public void Height_of_a_fieldset_includes_its_legend_below_the_border()
    {
        // Chromium makes a fieldset with height: 100pt as tall as without a legend, from its legend's top: the legend
        // takes room from the content box.
        var ops = Layout(FieldsetCss + "<fieldset style='height:100pt'><legend></legend></fieldset>");
        NearBox(Box(ops, "#ff0000"), 0, 8.5f, 500, 3 + 3 + 100 + 5 + 3 - 8.5f);
        ops = Layout(FieldsetCss + "<fieldset style='height:100pt;box-sizing:border-box'><legend></legend></fieldset>");
        NearBox(Box(ops, "#ff0000"), 0, 8.5f, 500, 100 - 8.5f);
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void Text_fields_stretch_in_flex_columns_and_grids_but_keep_their_width_elsewhere()
    {
        var doc = Styled("""
            <style>.col { display: flex; flex-direction: column } .grid { display: grid } .start { align-items: start }</style>
            <p><input id="inline"></p>
            <div class="col"><input id="col"><input id="sized" size="5"><input id="set" style="width: 50px"></div>
            <div class="col start"><input id="start"></div>
            <div class="grid"><input id="grid"><textarea id="area"></textarea></div>
            """);
        Element El(string id) => Find(doc, "input").Concat(Find(doc, "textarea")).First(e => e.Id == id);
        Assert.False(El("inline").Style.Width.IsAuto);
        Assert.True(El("col").Style.Width.IsAuto);
        Assert.True(El("sized").Style.Width.IsAuto);
        Assert.False(El("set").Style.Width.IsAuto);
        Assert.False(El("start").Style.Width.IsAuto);
        Assert.True(El("grid").Style.Width.IsAuto);
        Assert.True(El("area").Style.Width.IsAuto);
    }
}
