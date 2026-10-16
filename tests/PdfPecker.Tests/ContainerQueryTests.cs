using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Container queries: container-type, container-name, @container conditions and the cq units. Expected sizes were
/// measured in Chromium for the same HTML.
/// </summary>
public class ContainerQueryTests
{
    private const float Width = 500, PageHeight = 800;

    /// <summary>Resolves and lays out until the container sizes settle, as HtmlToPdf does.</summary>
    private static List<DrawOp> Layout(string html, float width = Width)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        LayoutEngine engine;
        int passes = 0;
        while (true)
        {
            var resolver = new StyleResolver(sheet, width, PageHeight);
            resolver.ResolveTree(doc, new ComputedStyle { Display = Display.Block });
            engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
            engine.Layout(doc, width);
            if (!resolver.ContainerSizesChanged() || ++passes > 6) break;
        }
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
            else if (o is ClipOp c) { foreach (var inner in Flatten(c.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static uint Color(string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return c;
    }

    private static RectOp Box(List<DrawOp> ops, string color) => ops.OfType<RectOp>().Single(r => r.Color == Color(color));

    private static bool Has(List<DrawOp> ops, string color) => ops.OfType<RectOp>().Any(r => r.Color == Color(color));

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    private const string Probe = "<div class='x' style='height: 10px'></div>";

    [Theory]
    [InlineData("(min-width: 200px)", 300, true)]
    [InlineData("(min-width: 200px)", 150, false)]
    [InlineData("(max-width: 300px)", 300, true)]
    [InlineData("(width: 300px)", 300, true)]
    [InlineData("(width > 300px)", 300, false)]
    [InlineData("(width >= 300px)", 300, true)]
    [InlineData("(200px < width <= 300px)", 300, true)]
    [InlineData("(200px < width <= 300px)", 200, false)]
    [InlineData("(400px > width > 100px)", 300, true)]
    [InlineData("(100px < width)", 300, true)]
    [InlineData("(inline-size > 250px)", 300, true)]
    [InlineData("not (width < 200px)", 300, true)]
    [InlineData("not (width < 400px)", 300, false)]
    [InlineData("(width > 100px) and (width < 200px)", 300, false)]
    [InlineData("(width > 100px) and (width < 400px)", 300, true)]
    [InlineData("(width < 100px) or (width > 250px)", 300, true)]
    [InlineData("((width < 100px) or (width > 250px)) and (not (width = 200px))", 300, true)]
    [InlineData("(min-width: 20em)", 300, false)]
    [InlineData("(min-width: 18em)", 300, true)]
    [InlineData("(width)", 300, true)]
    [InlineData("(width > 100px) and (width < 400px) or (width = 1px)", 300, false)] // and with or: invalid
    [InlineData("(color)", 300, false)]
    [InlineData("style(--x: 1)", 300, false)]
    [InlineData("(width < 1000px) or (color)", 300, false)]        // an unknown feature makes the query invalid
    [InlineData("(width < 1000px) or foo(bar)", 300, false)]
    [InlineData("(width < 1000px) or (width < 50%)", 300, false)]  // so does a percentage
    [InlineData("(width < 1000px) or (height > 0px)", 300, false)] // no size container to answer the height part
    [InlineData("(width: 300.01px)", 300, true)]                   // equality allows for 1/64 px, as in Chrome
    [InlineData("(width: 300.02px)", 300, false)]
    [InlineData("(width > 299.99px)", 300, true)]
    [InlineData("(width < 300.01px)", 300, true)]
    [InlineData("(width >= 300.01px)", 300, true)]
    [InlineData("(width >= 300.1px)", 300, false)]
    [InlineData("(WIDTH > 100PX) AND (width < 400px)", 300, true)]
    [InlineData("NOT (width < 100px)", 300, true)]
    [InlineData("(min-width: calc(100px + 1em))", 300, true)]
    public void Width_conditions(string condition, int containerPx, bool matches)
    {
        var ops = Layout($@"<style>
            .c {{ container-type: inline-size; width: {containerPx}px }}
            @container {condition} {{ .x {{ background: #ff0000 }} }}
            </style><div class='c'>{Probe}</div>");
        Assert.Equal(matches, Has(ops, "#ff0000"));
    }

    [Fact]
    public void Em_in_a_query_is_the_containers_font_size()
    {
        // 300px at a 20px font is 15em.
        var ops = Layout(@"<style>
            .c { container-type: inline-size; width: 300px; font-size: 20px }
            @container (width >= 15em) { .x { background: #ff0000 } }
            @container (width > 15em) { .x { background: #0000ff } }
            </style><div class='c'><div class='x' style='height: 10px; font-size: 40px'></div></div>");
        Assert.True(Has(ops, "#ff0000"));
        Assert.False(Has(ops, "#0000ff"));
    }

    [Fact]
    public void A_query_without_a_container_fails()
    {
        var ops = Layout("<style>@container (width >= 0px) { .x { background: #ff0000 } } @container not (width < 0px) { .x { border-top: 1px solid #00ff00 } }</style>" + Probe);
        Assert.False(Has(ops, "#ff0000"));
        Assert.False(Has(ops, "#00ff00"));
    }

    [Fact]
    public void The_query_container_is_the_nearest_ancestor_not_the_element_itself()
    {
        var ops = Layout(@"<style>
            .outer { container-type: inline-size; width: 400px }
            .inner { container-type: inline-size; width: 200px; height: 10px }
            @container (width > 300px) { .inner { background: #ff0000 } .x { background: #0000ff } }
            </style><div class='outer'><div class='inner'><div class='x' style='height: 5px'></div></div></div>");
        Assert.True(Has(ops, "#ff0000"));  // the inner box asks the outer one
        Assert.False(Has(ops, "#0000ff")); // its child asks the inner one
    }

    [Fact]
    public void Named_queries_skip_containers_with_other_names()
    {
        var ops = Layout(@"<style>
            .card { container: card / inline-size; width: 400px }
            .side { container-name: side other; container-type: inline-size; width: 100px }
            @container card (width > 300px) { .x { background: #ff0000 } }
            @container other (width > 300px) { .x { border-top: 1px solid #00ff00 } }
            @container nothing (width > 0px) { .x { border-bottom: 1px solid #0000ff } }
            </style><div class='card'><div class='side'>" + Probe + "</div></div>");
        Assert.True(Has(ops, "#ff0000"));
        Assert.False(Has(ops, "#00ff00"));
        Assert.False(Has(ops, "#0000ff"));
    }

    [Fact]
    public void Height_queries_need_a_size_container()
    {
        // The inline-size container cannot answer a height query, so the size container around it does.
        var ops = Layout(@"<style>
            .outer { container-type: size; width: 400px; height: 100px }
            .inner { container-type: inline-size; width: 200px }
            @container (height = 100px) { .x { background: #ff0000 } }
            @container (orientation: landscape) and (aspect-ratio > 3) { .x { border-top: 1px solid #00ff00 } }
            @container (aspect-ratio: 4 / 1) { .x { border-bottom: 1px solid #0000ff } }
            @container (orientation: portrait) { .x { border-left: 1px solid #ffff00 } }
            </style><div class='outer'><div class='inner'>" + Probe + "</div></div>");
        Assert.True(Has(ops, "#ff0000"));
        Assert.True(Has(ops, "#00ff00"));
        Assert.True(Has(ops, "#0000ff"));
        Assert.False(Has(ops, "#ffff00"));
    }

    [Fact]
    public void Container_units_refer_to_the_nearest_container()
    {
        var ops = Layout(@"<style>
            .c { container-type: size; width: 400px; height: 200px }
            .a { width: 50cqw; height: 10cqh; background: #ff0000 }
            .b { width: calc(25cqi + 10px); height: 5cqb; background: #00ff00 }
            .d { width: 10cqmin; height: 10cqmax; background: #0000ff }
            </style><div class='c'><div class='a'></div><div class='b'></div><div class='d'></div></div>");
        Near(150, Box(ops, "#ff0000").W);
        Near(15, Box(ops, "#ff0000").H);
        Near(82.5f, Box(ops, "#00ff00").W);
        Near(7.5f, Box(ops, "#00ff00").H);
        Near(15, Box(ops, "#0000ff").W);
        Near(30, Box(ops, "#0000ff").H);
    }

    [Fact]
    public void Container_units_differ_between_containers_with_the_same_styles()
    {
        var ops = Layout(@"<style>
            .c { container-type: inline-size }
            .x { width: 50cqw; height: 10px }
            .one { width: 200px } .two { width: 400px }
            .one .x { background: #ff0000 } .two .x { background: #00ff00 }
            </style><div class='c one'><div class='x'></div></div><div class='c two'><div class='x'></div></div>");
        Near(75, Box(ops, "#ff0000").W);
        Near(150, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void Container_units_without_a_container_are_viewport_units()
    {
        var ops = Layout("<div style='width: 10cqw; height: 10px; background: #ff0000'></div>");
        Near(Box(ops, "#ff0000").W, Layout("<div style='width: 10vw; height: 10px; background: #ff0000'></div>").OfType<RectOp>().Single().W);
        // An inline-size container gives the width but not the height: cqh falls back to vh.
        var cqh = Layout("<div style='container-type: inline-size; width: 100px'><div style='height: 10cqh; width: 10cqw; background: #ff0000'></div></div>");
        var vh = Layout("<div style='height: 10vh; background: #00ff00'></div>");
        Near(Box(vh, "#00ff00").H, Box(cqh, "#ff0000").H);
        Near(7.5f, Box(cqh, "#ff0000").W);
    }

    [Fact]
    public void Size_containment_ignores_the_content_height()
    {
        var ops = Layout(@"<div style='container-type: size; background: #ff0000; padding: 5px'><div style='height: 50px'></div></div>
                           <div style='container-type: size; min-height: 20px; background: #00ff00'><div style='height: 50px'></div></div>");
        Near(7.5f, Box(ops, "#ff0000").H);
        Near(15, Box(ops, "#00ff00").H);
    }

    [Fact]
    public void Inline_size_containment_ignores_the_content_width()
    {
        // Shrink-to-fit boxes (floats, inline blocks, flex items) that are containers come out as narrow as their
        // padding, as in Chrome.
        var ops = Layout(@"<div style='float: left; container-type: inline-size; padding: 4px; background: #ff0000'>some text here</div>
                           <div style='display: flex'><div style='container-type: inline-size; padding-left: 3px; background: #00ff00'>words</div></div>");
        Near(6, Box(ops, "#ff0000").W);
        Near(2.25f, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void Nested_containers_settle_over_several_passes()
    {
        // The inner container gets its width from a query on the outer one, and its child queries that width.
        var ops = Layout(@"<style>
            .outer { container-type: inline-size; width: 400px }
            .inner { container-type: inline-size }
            @container (width > 300px) { .inner { width: 250px } }
            @container (width < 300px) { .x { background: #ff0000; width: 50cqw } }
            </style><div class='outer'><div class='inner'>" + Probe + "</div></div>");
        Near(125 * 0.75f, Box(ops, "#ff0000").W);
    }

    [Fact]
    public void Pseudo_elements_query_their_host()
    {
        var ops = Layout(@"<style>
            .c { container-type: inline-size; width: 300px }
            @container (width > 200px) { .c::before { content: ''; display: block; height: 10px; background: #ff0000 } }
            </style><div class='c'></div>");
        Near(225, Box(ops, "#ff0000").W);
    }

    [Fact]
    public void Container_rules_nest_with_media_supports_layers_and_style_rules()
    {
        var ops = Layout(@"<style>
            .c { container-type: inline-size; width: 300px }
            @media print { @container (width > 200px) { @container (width < 400px) { .x { background: #ff0000 } } } }
            .x { @container (width > 200px) { border-top: 2px solid #00ff00 } }
            @layer base { @container (width > 200px) { .x { border-bottom: 2px solid #0000ff } } }
            .x { border-bottom-color: #ffff00 }
            </style><div class='c'>" + Probe + "</div>");
        Assert.True(Has(ops, "#ff0000"));
        Assert.True(Has(ops, "#00ff00"));
        Assert.False(Has(ops, "#0000ff")); // the unlayered colour wins
        Assert.True(Has(ops, "#ffff00"));
    }

    [Fact]
    public void Invalid_preludes_drop_the_block()
    {
        foreach (var prelude in new[] { "none (width > 0px)", "(width > 0px) (width > 0px)", "and (width > 0px)", "" })
        {
            var ops = Layout($"<style>.c {{ container-type: inline-size }} @container {prelude} {{ .x {{ background: #ff0000 }} }}</style><div class='c'>{Probe}</div>");
            Assert.False(Has(ops, "#ff0000"), prelude);
        }
    }

    [Fact]
    public void A_name_alone_is_not_a_query_and_normal_containers_answer_no_size_query()
    {
        var ops = Layout(@"<style>
            .c { container-name: card }
            @container card { .x { background: #ff0000 } }
            @container card (width > 0px) { .x { border-top: 1px solid #00ff00 } }
            </style><div class='c'>" + Probe + "</div>");
        Assert.False(Has(ops, "#ff0000"));
        Assert.False(Has(ops, "#00ff00"));
    }

    [Theory]
    [InlineData("style(--theme: dark)", true)]
    [InlineData("style(--theme:   dark )", true)]
    [InlineData("style(--theme: Dark)", false)]
    [InlineData("style(--theme)", true)]
    [InlineData("style(--other)", false)]
    [InlineData("not style(--theme: light)", true)]
    [InlineData("style((--theme: dark) and (--size: 2))", true)]
    [InlineData("style((--theme: light) or (--size: 2))", true)]
    [InlineData("style(color: red)", false)]
    [InlineData("not style(color: red)", false)]
    [InlineData("(width < 1000px) or style(color: red)", false)] // only custom properties can be queried
    [InlineData("panel style(--theme: dark)", false)]            // the panel does not have --theme
    [InlineData("panel style(--size: 2)", true)]
    [InlineData("other style(--size: 2)", false)]
    [InlineData("style(--size: 2)", true)]                       // inherited by the parent
    [InlineData("panel (width > 100px) and style(--size: 2)", true)]
    public void Style_queries_test_the_custom_properties_of_the_container(string condition, bool matches)
    {
        // A style query without a name asks the parent, which needs no container-type.
        var ops = Layout($@"<style>
            .outer {{ container: panel / inline-size; width: 300px; --size: 2 }}
            .c {{ --theme: dark }}
            @container {condition} {{ .x {{ background: #ff0000 }} }}
            </style><div class='outer'><div class='c'>{Probe}</div></div>");
        Assert.Equal(matches, Has(ops, "#ff0000"));
    }

    [Fact]
    public void Containers_that_are_inline_boxes_or_table_parts_answer_no_size_query()
    {
        // Size containment does not apply to them, so they are no size containers (as in Chrome).
        foreach (var html in new[]
                 {
                     "<span class='c'><span class='x'>a</span></span>",
                     "<table><tr><td class='c'><div class='x'>a</div></td></tr></table>",
                     "<table class='c'><tr><td><div class='x'>a</div></td></tr></table>",
                 })
        {
            var ops = Layout("<style>.c { container-type: inline-size; width: 100px } @container (width >= 0px) { .x { background: #ff0000 } }</style>" + html);
            Assert.False(Has(ops, "#ff0000"), html);
        }
    }

    [Fact]
    public void Flex_and_grid_items_are_containers_at_their_used_width()
    {
        var ops = Layout(@"<div style='display: flex; width: 400px'><div style='container-type: inline-size; flex: 1'><div style='width: 50cqw; height: 10px; background: #ff0000'></div></div><div style='width: 100px'></div></div>
            <div style='display: grid; grid-template-columns: 1fr 3fr; width: 400px'><div style='container-type: inline-size'><div style='width: 50cqw; height: 10px; background: #00ff00'></div></div><div style='container-type: inline-size'><div style='width: 50cqw; height: 10px; background: #0000ff'></div></div></div>");
        Near(112.5f, Box(ops, "#ff0000").W);
        Near(37.5f, Box(ops, "#00ff00").W);
        Near(112.5f, Box(ops, "#0000ff").W);
    }

    [Fact]
    public void Font_size_in_cq_units()
    {
        var ops = Layout("<div style='container-type: inline-size; width: 400px'><div style='font-size: 5cqi; line-height: 1; background: #ff0000'>x</div></div>");
        Near(15, Box(ops, "#ff0000").H);
    }

    [Fact]
    public void Container_shorthand_and_supports()
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(".a { container: a b / size } .b { container: none } .c { container: x / bogus }", sheet);
        Assert.Equal(new[] { "container-name", "container-type" }, sheet.Rules[0].Declarations.Select(d => d.Property));
        Assert.Equal("a b", sheet.Rules[0].Declarations[0].Value);
        Assert.Equal("size", sheet.Rules[0].Declarations[1].Value);
        Assert.Equal("normal", sheet.Rules[1].Declarations[1].Value);
        Assert.Equal(2, sheet.Rules.Count);
        Assert.True(CssSupports.Evaluate("(container-type: inline-size)"));
        Assert.True(CssSupports.Evaluate("(container: card / size)"));
        Assert.True(CssSupports.Evaluate("(width: 10cqi)"));
        Assert.False(CssSupports.Evaluate("(container-type: scroll-state)"));
        Assert.True(CssSupports.Evaluate("at-rule(@container)"));
    }

    [Fact]
    public void HtmlToPdf_resolves_container_queries()
    {
        var html = @"<style>
            .c { container-type: inline-size; width: 300px }
            @container (width > 200px) { .x { background: #ff0000 } }
            @container (width > 400px) { .x { background: #0000ff } }
            </style><div class='c'><div class='x' style='height: 10px'></div></div>";
        var content = AllContent(ConvertUncompressed(html));
        Assert.Contains("1 0 0 rg", content);
        Assert.DoesNotContain("0 0 1 rg", content);
    }
}
