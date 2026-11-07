using PdfPecker.Css;
using PdfPecker.Html;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>The tree-counting functions sibling-index() and sibling-count() (CSS Values 5, 9), as Chrome computes them.</summary>
public class SiblingIndexTests
{
    private static Element Styled(string html, out Element doc)
    {
        doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, 600, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static List<Element> Children(Element doc, string tag) => Find(doc, tag).ToList();

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children)
            if (c is Element el)
            {
                if (el.Name == name) yield return el;
                foreach (var d in Find(el, name)) yield return d;
            }
    }

    [Fact]
    public void Each_sibling_gets_its_own_position_and_the_count()
    {
        Styled("<style>li { z-index: sibling-index(); order: sibling-count() }</style><ul><li>a</li><li>b</li><li>c</li></ul>", out var doc);
        var items = Children(doc, "li");
        Assert.Equal(new int?[] { 1, 2, 3 }, items.Select(e => e.Style.ZIndex));
        Assert.All(items, e => Assert.Equal(3, e.Style.Order));
    }

    [Fact]
    public void Text_nodes_do_not_count_and_math_uses_the_numbers()
    {
        Styled("<style>p { width: calc(sibling-index() * 10pt); height: calc(100pt / sibling-count()) }</style><div>x<p></p> y <p></p></div>", out var doc);
        var ps = Children(doc, "p");
        Assert.Equal(10, ps[0].Style.Width.Value, 1);
        Assert.Equal(20, ps[1].Style.Width.Value, 1);
        Assert.Equal(50, ps[1].Style.Height.Value, 1);
    }

    [Fact]
    public void A_custom_property_resolves_on_the_element_that_declares_it()
    {
        Styled("<style>ul { --i: sibling-index() } li { z-index: var(--i) } li.own { --i: sibling-index() }</style>" +
               "<div><span></span><ul><li></li><li class=own></li></ul></div>", out var doc);
        var items = Children(doc, "li");
        Assert.Equal(2, items[0].Style.ZIndex); // the ul's index, inherited
        Assert.Equal(2, items[1].Style.ZIndex); // its own
    }

    [Fact]
    public void A_pseudo_element_counts_its_originating_element()
    {
        var pdf = ConvertUncompressed("<style>p::before { content: 'x'; color: rgb(calc(sibling-index() * 100) 0 0) }</style><div><p>a</p><p>b</p></div>");
        var content = AllContent(pdf);
        Assert.Contains("0.392 0 0 rg", content);
        Assert.Contains("0.784 0 0 rg", content);
    }

    [Theory]
    [InlineData("sibling-index(1)")]
    [InlineData("sibling-indexx()")]
    [InlineData("xsibling-index()")]
    public void Malformed_calls_are_invalid(string value)
    {
        Styled($"<style>li {{ z-index: 7 }} li {{ z-index: {value} }}</style><ul><li></li><li></li></ul>", out var doc);
        Assert.All(Children(doc, "li"), e => Assert.NotEqual(2, e.Style.ZIndex));
    }

    [Fact]
    public void Strings_are_left_alone()
    {
        Assert.Equal("'sibling-index()' 2 \"sibling-count()\"",
            CssAttr.ReplaceTreeCounting("'sibling-index()' sibling-index( ) \"sibling-count()\"", new Element("p") { ElementIndex = 2, ElementSiblingCount = 3 }));
    }

    [Fact]
    public void The_root_element_is_the_first_of_one()
    {
        Styled("<style>html { z-index: sibling-index(); order: sibling-count() }</style><p></p>", out var doc);
        var html = doc.FindFirst("html")!;
        Assert.Equal(1, html.Style.ZIndex);
        Assert.Equal(1, html.Style.Order);
    }

    [Fact]
    public void Supports_accepts_the_functions()
    {
        var pdf = ConvertUncompressed("<style>@supports (width: calc(sibling-index() * 1px)) { p { color: #ff0000 } }</style><p>a</p>");
        Assert.Contains("1 0 0 rg", AllContent(pdf));
    }

    [Fact]
    public void A_shorthand_is_expanded_per_element()
    {
        Styled("<style>p { margin: calc(sibling-index() * 1pt) 0 }</style><div><p></p><p></p></div>", out var doc);
        var ps = Children(doc, "p");
        Assert.Equal(1, ps[0].Style.MarginTop.Value, 1);
        Assert.Equal(2, ps[1].Style.MarginBottom.Value, 1);
    }
}
