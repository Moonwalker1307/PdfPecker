using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Typed attr() in any property (CSS Values 5), checked against what Chrome computes for the same declarations: the
/// types, fallbacks, cycles, inheritance, pseudo-elements and the rule that no attribute may reach a url().
/// </summary>
public class CssAttrTests
{
    private const float Width = 400; // 400pt: a block's auto width
    private const uint Red = 0xFFFF0000, Blue = 0xFF0000FF, Lime = 0xFF00FF00, Black = 0xFF000000;

    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse("<!doctype html>" + html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, 800) { StandardsMode = true }.ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;

        static IEnumerable<Element> Find(Element e, string name)
        {
            foreach (var c in e.Children.OfType<Element>())
            {
                if (c.Name == name) yield return c;
                foreach (var d in Find(c, name)) yield return d;
            }
        }
    }

    private static Element ById(Element root, string id)
    {
        if (root.Id == id) return root;
        foreach (var c in root.Children)
            if (c is Element e && ById(e, id) is { } found) return found;
        return null!;
    }

    /// <summary>The style of a div with data-v set to <paramref name="attr"/> (left out when null) under the rule.</summary>
    private static ComputedStyle Probe(string declaration, string? attr, string? b = null)
    {
        var a = attr == null ? "" : $" data-v=\"{System.Net.WebUtility.HtmlEncode(attr)}\"";
        if (b != null) a += $" data-a=\"{System.Net.WebUtility.HtmlEncode(attr)}\" data-b=\"{System.Net.WebUtility.HtmlEncode(b)}\"";
        var doc = Styled($"<style>#outer{{width:300px;color:black}} #h{{width:7px;font-size:10px}} #h{{{declaration}}}</style>"
                         + $"<div id=outer><div id=h data-w=8{a}></div></div>");
        return ById(doc, "h").Style;
    }

    private static float? WidthPx(string declaration, string? attr)
    {
        var w = Probe(declaration, attr).Width;
        return w.IsAuto ? null : w.Resolve(300 * 0.75f) / 0.75f;
    }

    // Each row was checked in Chrome 153; null is width: auto (300px in Chrome's probe), the unset value.
    [Theory]
    [InlineData("width: attr(data-v px)", "50", 50f)]
    [InlineData("width: attr(data-v px)", "50px", null)]
    [InlineData("width: attr(data-v px)", " 50 ", null)]
    [InlineData("width: attr(data-v px)", "5e1", 50f)]
    [InlineData("width: attr(data-v px)", "+5", 5f)]
    [InlineData("width: attr(data-v px)", ".5", 0.5f)]
    [InlineData("width: attr(data-v px)", "calc(5)", null)]
    [InlineData("width: attr(data-v PX)", "46", 46f)]
    [InlineData("width: attr(DATA-V px)", "33", 33f)]
    [InlineData("width: attr( data-v  px )", "45", 45f)]
    [InlineData("width: attr(data-v %)", "40", 120f)]
    [InlineData("width: attr(data-v em)", "2", 20f)]
    [InlineData("width: attr(data-v deg)", "10", null)]
    [InlineData("width: attr(data-v foo)", "5", null)]
    [InlineData("width: attr(data-v type(<length>))", "3em", 30f)]
    [InlineData("width: attr(data-v type(<length>))", "  13px  ", 13f)]
    [InlineData("width: attr(data-v type(<length>))", "10PX", 10f)]
    [InlineData("width: attr(data-v type(<length>))", "calc(10px + 5px)", 15f)]
    [InlineData("width: attr(data-v type(<length>))", "13px;", null)]
    [InlineData("width: attr(data-v type(<length>))", "13px !important", null)]
    [InlineData("width: attr(data-v type(<length>))", "10px 20px", null)]
    [InlineData("width: attr(data-v type(<length-percentage>))", "30%", 90f)]
    [InlineData("width: attr(data-v type(<length>|<percentage>))", "13%", 39f)]
    [InlineData("width: attr(data-v type(*))", "calc(10px + 6px)", 16f)]
    [InlineData("width: attr(data-v type(*))", "inherit", null)]
    [InlineData("width: attr(data-v)", "50px", null)]
    [InlineData("width: attr(data-v raw-string)", "30px", null)]
    [InlineData("width: attr(data-v number)", "50", null)]
    [InlineData("width: calc(attr(data-v number) * 1px)", "50", 50f)]
    [InlineData("width: calc(attr(data-v number) * 1px)", " 50 ", null)]
    [InlineData("width: calc(attr(data-v type(<number>)) * 2px)", " 50 ", 100f)]
    [InlineData("width: calc(attr(data-v %) + 10px)", "10", 40f)]
    [InlineData("width: attr(data-missing px, 10px)", "50", 10f)]
    [InlineData("width: attr(data-v px, 10px)", "abc", 10f)]
    [InlineData("width: attr(data-v px, 10px)", "", 10f)]
    [InlineData("width: attr(data-v px)", "", null)]
    [InlineData("width: attr(data-v type(<length>), attr(data-w px))", "x", 8f)]
    [InlineData("width: attr(data-v type(<length>),)", "zz", null)]
    [InlineData("width: attr(data-v type(*), 5px)", "", null)]
    [InlineData("width: attr(data-v type(<length>))", "10px/**/", 10f)]
    [InlineData("width: attr(data-v type(<length>))", "/* c */ 10px", 10f)]
    [InlineData("width: attr(data-v type(<length>))", "1\\70 x", 1f)]
    [InlineData("width: attr(data-v px)", "5.", 5f)]
    [InlineData("width: attr(data-v px)", ".", null)]
    [InlineData("width: attr(data-v type(<length>", "12px", 12f)]
    [InlineData("width: attr(data-v px", "13", 13f)]
    [InlineData("width: attr(data-v px, 4px", "x", 4f)]
    public void Typed_values_and_fallbacks_match_chrome(string declaration, string? attr, float? expected)
    {
        var actual = WidthPx(declaration, attr);
        if (expected == null) Assert.Null(actual);
        else Assert.Equal(expected.Value, actual!.Value, 2);
    }

    [Theory]
    [InlineData("width: attr(data-v px px)")]
    [InlineData("width: attr()")]
    [InlineData("width: attr(data-v type(<foo>))")]
    [InlineData("width: attr(data-v type(<url>))")]
    [InlineData("width: attr(data-v type(<length>) px)")]
    [InlineData("width: attr(|data-v px)")]
    [InlineData("width: attr(*|data-v px)")]
    public void A_malformed_attr_drops_the_declaration_when_parsed(string declaration)
    {
        // The earlier width: 7px stays, as in Chrome, where the declaration never enters the cascade.
        Assert.Equal(7f, WidthPx(declaration, "5")!.Value, 2);
        Assert.False(CssSupports.Evaluate($"({declaration})"));
    }

    [Theory]
    [InlineData("(width: attr(data-x px))", true)]
    [InlineData("(width: attr(data-x type(<length>), 10px))", true)]
    [InlineData("(color: attr(data-c type(<color>)))", true)]
    [InlineData("(content: attr(title))", true)]
    [InlineData("(--x: attr(data-x))", true)]
    [InlineData("(width: attr(data-x px px))", false)]
    public void Supports_accepts_well_formed_attr(string condition, bool expected)
        => Assert.Equal(expected, CssSupports.Evaluate(condition));

    [Theory]
    [InlineData("color: attr(data-v type(<color>))", "red", Red)]
    [InlineData("color: attr(data-v type(<color>))", "#00f", Blue)]
    [InlineData("color: attr(data-v type(<color>))", "rgb(0 255 0)", Lime)]
    [InlineData("color: attr(data-v type(<color>))", "nonsense", Black)]
    [InlineData("color: attr(data-v type(<color>), blue)", "nonsense", Blue)]
    [InlineData("color: attr(data-v type(<custom-ident>))", "red", Red)]
    [InlineData("color: attr(data-v type(<custom-ident>))", "inherit", Black)]
    [InlineData("color: attr(data-v type(red | blue))", "blue", Blue)]
    [InlineData("color: attr(data-v type(red | blue))", "green", Black)]
    [InlineData("color: attr(data-v)", "red", Black)]
    [InlineData("color: attr(data-v type(*))", "red", Red)]
    [InlineData("color: red; color: attr(data-v type(<color>))", "nonsense", Black)]
    public void Colours_from_attributes(string declaration, string attr, uint expected)
        => Assert.Equal(expected, Probe(declaration, attr).Color);

    [Fact]
    public void Background_colour_and_shorthands_take_attr()
    {
        var s = Probe("background-color: attr(data-v type(<color>), red); margin: attr(data-v px)", "x");
        Assert.Equal(Red, s.BackgroundColor);
        Assert.True(s.MarginTop.IsAuto || s.MarginTop.Value == 0); // margin unset: 0
        s = Probe("margin: attr(data-v type(<length>+))", "1px 2px 3px");
        Assert.Equal(0.75f, s.MarginTop.Value, 3);
        Assert.Equal(1.5f, s.MarginRight.Value, 3);
        Assert.Equal(2.25f, s.MarginBottom.Value, 3);
        Assert.Equal(1.5f, s.MarginLeft.Value, 3);
        s = Probe("border-left: attr(data-v px) solid red", "4");
        Assert.Equal(3f, s.BorderLeftWidth, 3);
        Assert.Equal(Red, s.BorderLeftColor);
    }

    [Fact]
    public void Font_size_opacity_and_numbers()
    {
        Assert.Equal(15f, Probe("font-size: attr(data-v px)", "20").FontSize, 3);
        Assert.Equal(24f, Probe("font-size: attr(data-v type(<length>))", "2em").FontSize, 3); // 2 x the parent's 16px
        Assert.Equal(0.5f, Probe("opacity: attr(data-v number)", "0.5").Opacity, 3);
        Assert.Equal(0.5f, Probe("opacity: attr(data-v %)", "50").Opacity, 3);
        Assert.Equal(0.3f, Probe("opacity: attr(data-v number, 0.3)", "x").Opacity, 3);
        Assert.Equal(3, Probe("z-index: attr(data-v type(<integer>))", "3").ZIndex);
    }

    [Theory]
    [InlineData("width: attr(data-v type(<length>))", "var(--w)", 21f)]
    [InlineData("width: attr(data-v type(*))", "var(--w)", 21f)]
    [InlineData("width: attr(data-v type(<length>))", "var(--nope)", null)]
    [InlineData("width: attr(data-v type(<length>))", "var(--nope, 5px)", 5f)]
    [InlineData("width: attr(data-v type(<length>), 6px)", "var(--nope)", 6f)]
    [InlineData("width: var(--q, attr(data-v px))", "11", 11f)]
    [InlineData("width: attr(data-v px, var(--q, 12px))", "x", 12f)]
    [InlineData("width: attr(data-v type(*))", "attr(data-w px)", 8f)]
    public void Var_and_attr_nest_in_both_directions(string declaration, string attr, float? expected)
    {
        var actual = WidthPx("--w: 21px; " + declaration, attr);
        if (expected == null) Assert.Null(actual);
        else Assert.Equal(expected.Value, actual!.Value, 2);
    }

    [Theory]
    [InlineData("width: attr(data-v type(*))", "attr(data-v px)", null)]
    [InlineData("width: attr(data-v type(*), 4px)", "attr(data-v type(*))", 4f)]
    [InlineData("width: attr(data-v type(*), attr(data-v px))", "attr(data-v type(*))", null)]
    [InlineData("width: attr(data-v px, attr(data-v px, 3px))", "x", null)]
    public void Attribute_cycles_are_invalid_as_in_chrome(string declaration, string attr, float? expected)
    {
        var actual = WidthPx(declaration, attr);
        if (expected == null) Assert.Null(actual);
        else Assert.Equal(expected.Value, actual!.Value, 2);
    }

    [Fact]
    public void Custom_properties_take_the_declaring_elements_attributes()
    {
        // --x is computed on the outer element, from its own data-v, and inherited as that value.
        var doc = Styled("<style>#o{--x: attr(data-v px); --s: attr(data-v)} #i{width: var(--x)} #i::before{content: var(--s)}</style>"
                         + "<div id=o data-v=40><div id=i data-v=90>x</div></div>");
        var inner = ById(doc, "i");
        Assert.Equal(30f, inner.Style.Width.Value, 3);
        Assert.Equal("\"40\"", inner.Style.CustomProperties!.TryGetValue("--s", out var s) ? s : null);
        var before = inner.Children.OfType<Element>().First(c => c.Name == "::before");
        Assert.Equal("40", ((TextNode)before.Children[0]).Text);
    }

    [Fact]
    public void A_string_from_attr_is_not_read_as_css_again()
    {
        var doc = Styled("<style>#o{--w: 5px; --s: attr(data-v)} #o::before{content: var(--s)}</style>"
                         + "<div id=o data-v='var(--w) \"q\" \\'>x</div>");
        var before = ById(doc, "o").Children.OfType<Element>().First(c => c.Name == "::before");
        Assert.Equal("var(--w) \"q\" \\", ((TextNode)before.Children[0]).Text);
    }

    [Fact]
    public void Pseudo_elements_read_the_originating_elements_attributes()
    {
        var doc = Styled("<style>p::before{content: attr(data-label type(<string>), 'none'); display: inline-block;"
                         + " width: attr(data-w px); color: attr(data-c type(<color>))}</style>"
                         + "<p id=p data-w=20 data-c=blue data-label='\"Hi\"'>x</p>");
        var before = ById(doc, "p").Children.OfType<Element>().First(c => c.Name == "::before");
        Assert.Equal(15f, before.Style.Width.Value, 3);
        Assert.Equal(Blue, before.Style.Color);
        Assert.Equal("Hi", ((TextNode)before.Children[0]).Text);
    }

    [Fact]
    public void A_fallback_that_is_not_a_string_leaves_content_without_a_box()
    {
        var doc = Styled("<style>p::before{content: attr(data-x, 3)}</style><p id=a>a</p><p id=b data-x=X>b</p>");
        Assert.DoesNotContain(ById(doc, "a").Children, c => c is Element { Name: "::before" });
        Assert.Contains(ById(doc, "b").Children, c => c is Element { Name: "::before" });
    }

    [Fact]
    public void An_unclosed_attr_at_the_end_of_a_style_attribute_is_closed()
    {
        var doc = Styled("<div id=a style='width: attr(data-w type(<length>' data-w=20px></div><div id=b style='width: attr(data-w px' data-w=40></div>");
        Assert.Equal(15f, ById(doc, "a").Style.Width.Value, 3);
        Assert.Equal(30f, ById(doc, "b").Style.Width.Value, 3);
    }

    [Fact]
    public void Untyped_attr_in_content_still_works_with_typed_ones_beside_it()
    {
        var doc = Styled("<style>p::before{content: attr(data-a) \"-\" attr(data-b type(<string>)) attr(data-missing, \"!\")}</style>"
                         + "<p id=p data-a=A data-b='\"B\"'>x</p>");
        var before = ById(doc, "p").Children.OfType<Element>().First(c => c.Name == "::before");
        Assert.Equal("A-B!", ((TextNode)before.Children[0]).Text);
    }

    [Fact]
    public void Inherited_values_are_the_parents_computed_ones()
    {
        var doc = Styled("<style>.c{color: attr(data-c type(<color>))} .w{width: attr(data-w px)} .w > div{width: inherit}</style>"
                         + "<div class=c data-c=red><p id=a>a</p><p id=b class=c data-c=bogus>b</p></div>"
                         + "<div class=w data-w=40><div id=d data-w=90></div></div>");
        Assert.Equal(Red, ById(doc, "a").Style.Color);
        Assert.Equal(Red, ById(doc, "b").Style.Color); // invalid: color is unset, which inherits
        Assert.Equal(30f, ById(doc, "d").Style.Width.Value, 3);
    }

    [Fact]
    public void Styles_with_attr_are_not_shared_between_elements()
    {
        var doc = Styled("<style>.bar{width: attr(data-v %)}</style><div class=bar id=a data-v=10></div><div class=bar id=b data-v=60></div>");
        Assert.Equal(40f, ById(doc, "a").Style.Width.Resolve(400), 3);
        Assert.Equal(240f, ById(doc, "b").Style.Width.Resolve(400), 3);
    }

    // Chrome forbids values from attributes in url() and the other functions that load something, the fallback included.
    [Theory]
    [InlineData("background-image: attr(data-v type(<image>))", "url(a.png)", false)]
    [InlineData("background-image: attr(data-v type(*))", "url(a.png)", false)]
    [InlineData("background-image: image-set(attr(data-v) 1x)", "a.png", false)]
    [InlineData("background-image: image-set(attr(data-v type(*)))", "url(a.png)", false)]
    [InlineData("background-image: attr(data-missing type(<image>), url(a.png))", "x", false)]
    [InlineData("--u: attr(data-v type(*)); background-image: var(--u)", "url(a.png)", false)]
    [InlineData("--u: attr(data-v); background-image: image-set(var(--u))", "a.png", false)]
    [InlineData("list-style-image: attr(data-v type(*))", "url(a.png)", false)]
    [InlineData("background-image: attr(data-v type(<image>))", "linear-gradient(red, blue)", true)]
    [InlineData("background-image: url(a.png), linear-gradient(attr(data-v type(<color>)), blue)", "red", true)]
    // Pieces that are harmless one by one but join into a resource function, which Chrome's token boundaries prevent.
    [InlineData("background-image: attr(data-a type(*))attr(data-b type(*))", "url", false, "(G1.png)")]
    [InlineData("background-image: attr(data-a type(<custom-ident>))(G2.png)", "url", false, "x")]
    [InlineData("--p: attr(data-a type(*)); --q: attr(data-b type(*)); background-image: var(--p)var(--q)", "url", false, "(G3.png)")]
    [InlineData("background-image: attr(data-a type(*))attr(data-b type(*))", "image-set", false, "(\"G5.png\" 1x)")]
    [InlineData("background-image: url(a.png), attr(data-a type(*))", "linear-gradient(red, blue)", true, "x")]
    public void Attribute_values_never_reach_a_url(string declaration, string attr, bool image, string? b = null)
    {
        var s = Probe(declaration, attr, b);
        if (declaration.StartsWith("list-style-image", StringComparison.Ordinal)) Assert.Null(s.ListStyleImage);
        else Assert.Equal(image, s.BackgroundImages is { } layers && Array.Exists(layers, l => l != null));
    }

    // The same through a whole conversion: the resource resolver is asked only for the file the style sheet names.
    [Theory]
    [InlineData("div{background-image: attr(data-a type(*))attr(data-b type(*))}", "url", "(G1.png)")]
    [InlineData("div{background-image: attr(data-a type(<custom-ident>))(G2.png)}", "url", "x")]
    [InlineData("div{--p: attr(data-a type(*)); --q: attr(data-b type(*)); background-image: var(--p)var(--q)}", "url", "(G3.png)")]
    [InlineData("div{background-image: attr(data-a type(*))attr(data-b type(*))}", "image-set", "(\"G5.png\" 1x)")]
    [InlineData("div::before{content: attr(data-a type(*))attr(data-b type(*))}", "url", "(G6.png)")]
    [InlineData("div::before{content: url(attr(data-a))}", "G7.png", "x")]
    [InlineData("div::before{content: image-set(attr(data-a))}", "G8.png", "x")]
    public void Joined_attribute_pieces_load_nothing(string css, string a, string b)
    {
        var fetched = new List<string>();
        var html = $"<!doctype html><style>div{{width:50px;height:50px}} p{{background-image:url(CTRL.png)}} {css}</style>"
                   + $"<div data-a=\"{System.Net.WebUtility.HtmlEncode(a)}\" data-b=\"{System.Net.WebUtility.HtmlEncode(b)}\">x</div><p>p</p>";
        var options = new PdfOptions { ResourceResolver = u => { lock (fetched) fetched.Add(u); return null; } };
        HtmlToPdf.Convert(html, options);
        Assert.Contains(fetched, u => u.Contains("CTRL.png", StringComparison.Ordinal));
        Assert.DoesNotContain(fetched, u => u.Contains("G", StringComparison.Ordinal) && !u.Contains("CTRL", StringComparison.Ordinal));
    }

    [Fact]
    public void An_untyped_attr_in_url_makes_no_box()
    {
        var doc = Styled("<style>p::before{content: url(attr(data-x))} i::before{content: \"a\" attr(data-x)}</style><p id=p data-x=a.png>p</p><i id=i data-x=b>i</i>");
        Assert.DoesNotContain(ById(doc, "p").Children, c => c is Element { Name: "::before" });
        Assert.Contains(ById(doc, "i").Children, c => c is Element { Name: "::before" });
    }

    [Fact]
    public void A_type_star_value_cannot_add_declarations()
    {
        var s = Probe("color: attr(data-v type(*))", "red; background-color: blue");
        Assert.Equal(Black, s.Color);
        Assert.Equal(0u, s.BackgroundColor);
    }

    [Fact]
    public void Untyped_attr_strings_escape_quotes_and_backslashes()
    {
        Assert.Equal("\"a\\\"b\\\\c\"", CssAttr.Quote("a\"b\\c"));
        Assert.Equal("\"x\\a y\"", CssAttr.Quote("x\ny"));
    }

    [Fact]
    public void Documents_without_attr_take_no_new_path()
    {
        Assert.False(CssAttr.Used("width", "var(--x)"));
        Assert.False(CssAttr.Used("width", "calc(1px + 2px)"));
        Assert.False(CssAttr.Used("content", "attr(title)"));
        Assert.False(CssAttr.Used("width", "myattr(x)"));
        Assert.True(CssAttr.Used("content", "attr(data-n type(<string>))"));
        Assert.True(CssAttr.Used("--x", "attr(title)"));
    }

    [Fact]
    public void A_bar_chart_from_data_attributes_lays_out()
    {
        var html = "<!doctype html><style>.bar{height:10px;margin:2px 0;width:attr(data-pct %);"
                   + "background:attr(data-color type(<color>), gray)}</style>"
                   + "<div class=bar data-pct=25 data-color=red></div><div class=bar data-pct=75></div>";
        var pdf = HtmlToPdf.Convert(html);
        Assert.NotEmpty(pdf);
    }
}
