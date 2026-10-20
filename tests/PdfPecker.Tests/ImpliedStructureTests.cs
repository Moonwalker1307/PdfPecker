using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// The html, head and body elements a document leaves out are made by the parser, as in browsers, so that rules for
/// them (body { margin: 30px }) reach a fragment too.
/// </summary>
public class ImpliedStructureTests
{
    private static string Shape(Element e)
        => e.Name + (e.Children.OfType<Element>().Any() ? "(" + string.Join(" ", e.Children.OfType<Element>().Select(Shape)) + ")" : "");

    private static string Tree(string html) => Shape(HtmlParser.Parse(html).Children.OfType<Element>().Single());

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false)) { FixedAreaHeight = 800 };
        engine.Layout(doc, 500);
        return engine.Ops;
    }

    private static TextOp Text(string html, string word)
        => Layout(html).OfType<TextOp>().First(t => t.Text.Contains(word, StringComparison.Ordinal));

    [Theory]
    [InlineData("<p>x", "html(head body(p))")]
    [InlineData("text only", "html(head body)")]
    [InlineData("<title>t</title><style>p{}</style><p>x", "html(head(title style) body(p))")]
    [InlineData("<html><p>x", "html(head body(p))")]
    [InlineData("<html><head><title>t</title></head><p>x</p>", "html(head(title) body(p))")]
    [InlineData("<head><meta><body><p>x", "html(head(meta) body(p))")]
    [InlineData("<html><head></head><body><p>x</p></body></html>", "html(head body(p))")]
    [InlineData("<p>x</p></body></html><div>y</div>", "html(head body(p div))")]
    [InlineData("<!-- c -->\n<!DOCTYPE html>\n<html>\n<head>\n</head>\n<body>\n</body>\n</html>\n", "html(head body)")]
    [InlineData("<title>only a title</title>", "html(head(title) body)")]
    public void Missing_parts_are_created(string html, string expected) => Assert.Equal(expected, Tree(html));

    [Fact]
    public void Head_content_after_an_explicit_head_end_goes_back_into_the_head()
        => Assert.Equal("html(head(meta) body(p))", Tree("<html><head></head><meta charset=utf-8><p>x"));

    [Fact]
    public void Head_content_inside_the_body_stays_there()
        => Assert.Equal("html(head body(p style))", Tree("<p>x</p><style>p{}</style>"));

    [Fact]
    public void A_second_body_or_html_tag_adds_only_the_missing_attributes()
    {
        var doc = HtmlParser.Parse("<p>x<html lang=da><body class=a style='margin:1px'><html lang=en dir=rtl><body class=b id=late>");
        var html = doc.FindFirst("html")!;
        var body = doc.FindFirst("body")!;
        Assert.Equal("da", html.GetAttribute("lang"));
        Assert.Equal("rtl", html.GetAttribute("dir"));
        Assert.Equal("a", body.GetAttribute("class"));
        Assert.Equal("late", body.GetAttribute("id"));
        Assert.Single(doc.Children.OfType<Element>().Single().Children.OfType<Element>(), c => c.Name == "body");
        Assert.Equal("body", doc.FindFirst("p")!.Parent!.Name);
    }

    [Fact]
    public void An_empty_document_stays_empty() => Assert.Empty(HtmlParser.Parse("  \n ").Children);

    [Fact]
    public void Svg_files_are_parsed_without_the_extra_elements()
    {
        var doc = HtmlParser.Parse("<svg><rect/></svg>", impliedStructure: false);
        Assert.Equal("svg", Assert.Single(doc.Children.OfType<Element>()).Name);
    }

    [Fact]
    public void Body_rules_apply_to_a_fragment()
    {
        var plain = Text("<p style='margin:0'>word</p>", "word");
        var padded = Text("<style>body { margin: 40px; padding-left: 10px }</style><p style='margin:0'>word</p>", "word");
        Assert.Equal(8 * 0.75f, plain.X, 0.01f);
        Assert.Equal(50 * 0.75f, padded.X, 0.01f);
    }

    [Fact]
    public void Html_rules_apply_to_a_fragment()
    {
        var text = Text("<style>html { font-size: 40px } body { margin: 0 }</style><p style='margin:0'>word</p>", "word");
        Assert.Equal(30f, text.Size, 0.01f);
    }

    [Fact]
    public void A_body_selector_with_a_child_combinator_matches_a_fragment()
    {
        var text = Text("<style>body > p { margin-left: 100px } body { margin: 0 }</style><p>word</p>", "word");
        Assert.Equal(75f, text.X, 0.01f);
    }
}
