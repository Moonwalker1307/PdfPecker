using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Tests;

/// <summary>
/// @scope. The expected colours are Chrome's: rules match below each scope root and above its limits, :scope and
/// &amp; are the root, a selector without them does not match the root itself, nested roots inside a limit are scopes
/// of their own, and the closest root wins between rules of equal specificity.
/// </summary>
public class ScopeTests
{
    private static Element Resolve(string body)
    {
        var doc = HtmlParser.Parse("<html><body>" + body + "</body></html>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style"))
        {
            sheet.CurrentScopeRoot = style.Parent;
            CssParser.ParseStylesheet(style.InnerText(), sheet);
        }
        sheet.CurrentScopeRoot = null;
        new StyleResolver(sheet, 400, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
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

    private static uint Red(Element doc, string id) => (Find(doc, "*").Concat(All(doc)).First(x => x.GetAttribute("id") == id).Style.Color >> 16) & 0xFF;

    private static IEnumerable<Element> All(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    private const string Cards = """
        <style>
        @scope (.card) to (.content) {
          .card { color: rgb(1,0,0) }
          :scope { color: rgb(5,0,0) }
          .t { color: rgb(2,0,0) }
          & .u { color: rgb(3,0,0) }
        }
        </style>
        <div class=card id=card><span class=t id=t1>t</span><span class=u id=u1>u</span>
          <div class=content><span class=t id=t2>t</span><div class=card id=card2><span class=t id=t3>t</span></div></div></div>
        <span class=t id=t4>outside</span>
        """;

    [Theory]
    [InlineData("card", 5)]   // :scope is the root; ".card" alone does not match it
    [InlineData("t1", 2)]
    [InlineData("u1", 3)]     // & is the root
    [InlineData("t2", 5)]     // below the limit: .t does not apply, the colour is the card's
    [InlineData("card2", 5)]  // a root of its own inside the outer card's limit
    [InlineData("t3", 2)]
    [InlineData("t4", 0)]     // outside every root
    public void Rules_apply_between_the_root_and_its_limits(string id, int red)
        => Assert.Equal((uint)red, Red(Resolve(Cards), id));

    [Fact]
    public void The_closest_root_wins_between_equal_specificities()
    {
        var doc = Resolve("""
            <style>@scope (.light) { p { color: rgb(10,0,0) } } @scope (.dark) { p { color: rgb(20,0,0) } }</style>
            <div class=dark><div class=light><p id=p1>p</p></div></div>
            <div class=light><div class=dark><p id=p2>p</p></div></div>
            """);
        Assert.Equal(10u, Red(doc, "p1"));
        Assert.Equal(20u, Red(doc, "p2"));
    }

    [Fact]
    public void A_scoped_rule_wins_over_an_unscoped_one_of_equal_specificity()
        => Assert.Equal(31u, Red(Resolve("<style>@scope (.x) { :scope { color: rgb(31,0,0) } } .x { color: rgb(30,0,0) }</style><div class=x id=x>x</div>"), "x"));

    [Fact]
    public void Without_a_start_the_root_is_the_style_elements_parent()
    {
        var doc = Resolve("""
            <div id=host><style>@scope { p { color: rgb(40,0,0) } :scope { color: rgb(41,0,0) } }</style><p id=p3>p</p></div><p id=p4>p</p>
            """);
        Assert.Equal(41u, Red(doc, "host"));
        Assert.Equal(40u, Red(doc, "p3"));
        Assert.Equal(0u, Red(doc, "p4"));
    }

    [Fact]
    public void Nested_scopes_start_inside_the_outer_scope()
    {
        var doc = Resolve("""
            <style>@scope (.a) { @scope (.b) { span { color: rgb(50,0,0) } } }</style>
            <div class=a><div class=b><span id=in>x</span></div></div><div class=b><span id=out>x</span></div>
            """);
        Assert.Equal(50u, Red(doc, "in"));
        Assert.Equal(0u, Red(doc, "out"));
    }

    [Fact]
    public void An_invalid_prelude_drops_the_block()
        => Assert.Equal(0u, Red(Resolve("<style>@scope (.a) until (.b) { p { color: rgb(60,0,0) } }</style><div class=a><p id=p>p</p></div>"), "p"));

    [Fact]
    public void A_limit_that_names_scope_is_matched_for_each_root()
    {
        // The limit is a .content child of the root: .content deeper down limits nothing.
        var doc = Resolve("<style>@scope (.card) to (:scope > .content) { p { color: rgb(70,0,0) } }</style>"
            + "<div class=card><div class=content><p id=a>a</p></div><div><div class=content><p id=b>b</p></div></div></div>");
        Assert.Equal(0u, Red(doc, "a"));
        Assert.Equal(70u, Red(doc, "b"));
    }

    [Fact]
    public void Deep_nesting_stays_fast()
    {
        // Every element is a root and a limit; matching walks a bounded number of generations.
        string open = string.Concat(Enumerable.Repeat("<div class=s>", 2000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Resolve("<style>@scope (.s) to (.s) { div { color: red } } @scope (.s) { @scope (.s) { div { color: blue } } }</style>" + open + "x");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }
}
