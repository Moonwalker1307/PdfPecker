using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Tests;

public class HtmlParserTests
{
    private static Element Body(string html) => HtmlParser.Parse(html);

    private static IEnumerable<Element> Elements(Element e, string name)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce) continue;
            if (ce.Name == name) yield return ce;
            foreach (var d in Elements(ce, name)) yield return d;
        }
    }

    [Fact]
    public void Decodes_named_and_numeric_entities()
    {
        var doc = Body("<p>&lt;a&gt; &amp; &copy; &#169; &#xA9; &unknown; &amp</p>");
        Assert.Equal("<a> & \u00A9 \u00A9 \u00A9 &unknown; &", doc.FindFirst("p")!.InnerText());
    }

    [Fact]
    public void Unclosed_paragraphs_close_implicitly()
    {
        var doc = Body("<p>one<p>two<div>three</div>");
        var ps = Elements(doc, "p").ToList();
        Assert.Equal(2, ps.Count);
        Assert.Equal("one", ps[0].InnerText());
        Assert.Equal("two", ps[1].InnerText());
        Assert.Equal("#document", doc.FindFirst("div")!.Parent!.Name);
    }

    [Fact]
    public void List_items_and_table_cells_close_implicitly()
    {
        var doc = Body("<ul><li>a<li>b</ul><table><tr><td>1<td>2<tr><td>3</table>");
        Assert.Equal(2, Elements(doc, "li").Count());
        Assert.Equal(2, Elements(doc, "tr").Count());
        Assert.Equal(3, Elements(doc, "td").Count());
        Assert.Equal("tbody", doc.FindFirst("tr")!.Parent!.Name);
    }

    [Fact]
    public void Void_and_self_closing_elements_have_no_children()
    {
        var doc = Body("<p>a<br>b<img src=x.png>c<hr/>d</p>");
        Assert.Empty(doc.FindFirst("br")!.Children);
        Assert.Equal("x.png", doc.FindFirst("img")!.GetAttribute("src"));
    }

    [Fact]
    public void Script_and_style_content_is_raw_text()
    {
        var doc = Body("<style>p > a { color: red }</style><script>if (a < b && c) {}</script>");
        Assert.Equal("p > a { color: red }", doc.FindFirst("style")!.InnerText());
        Assert.Equal("if (a < b && c) {}", doc.FindFirst("script")!.InnerText());
    }

    [Fact]
    public void Attributes_support_all_quote_styles()
    {
        var a = Body("<a HREF=\"x\" title='y' data-z=w disabled>t</a>").FindFirst("a")!;
        Assert.Equal("x", a.GetAttribute("href"));
        Assert.Equal("y", a.GetAttribute("title"));
        Assert.Equal("w", a.GetAttribute("data-z"));
        Assert.Equal("", a.GetAttribute("disabled"));
    }

    [Fact]
    public void Content_written_between_table_rows_moves_in_front_of_the_table()
    {
        // Browsers foster-parent what a table cannot hold; left in the table, the table layout would drop it.
        var doc = Body("<div><table>Note<input value=\"a\"><tr><span>b</span><td>1</td></tr><input type=hidden name=h></table></div>");
        var div = doc.FindFirst("div")!;
        Assert.Equal(new[] { "#text", "input", "span", "table" }, div.Children.Select(c => c is Element e ? e.Name : "#text"));
        Assert.Equal("1", doc.FindFirst("td")!.InnerText());
        Assert.Equal("tr", doc.FindFirst("td")!.Parent!.Name);
        Assert.Equal("tbody", Elements(doc, "input").Last().Parent!.Name); // a hidden field stays where it was written
    }

    [Fact]
    public void A_form_inside_a_row_keeps_its_cells_in_the_row()
    {
        var doc = Body("<table><tr><form><td>a</td><td><input name=x></td></form></tr></table>");
        var tr = doc.FindFirst("tr")!;
        Assert.Equal(2, tr.Children.OfType<Element>().Count(e => e.Name == "td"));
        Assert.NotNull(doc.FindFirst("form"));
    }

    [Fact]
    public void A_form_inside_a_form_is_ignored()
    {
        var doc = Body("<form><p><form><input name=x></form><input name=y></form>");
        Assert.Single(Elements(doc, "form"));
        Assert.Equal("form", doc.FindFirst("p")!.Parent!.Name);
    }

    [Fact]
    public void A_field_closes_an_open_select()
    {
        var doc = Body("<p><select><option>a<input value=x></p>");
        Assert.Equal("p", doc.FindFirst("input")!.Parent!.Name);
    }

    [Fact]
    public void Comments_and_doctype_are_ignored()
    {
        var doc = Body("<!DOCTYPE html><!-- <p>no</p> --><p>yes</p>");
        Assert.Single(Elements(doc, "p"));
    }
}

public class StyleTests
{
    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
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
        foreach (var c in root.Children.OfType<Element>())
        {
            if (c.Id == id) return c;
            var found = Search(c, id);
            if (found != null) return found;
        }
        throw new InvalidOperationException(id);

        static Element? Search(Element e, string id)
        {
            foreach (var c in e.Children.OfType<Element>())
            {
                if (c.Id == id) return c;
                var f = Search(c, id);
                if (f != null) return f;
            }
            return null;
        }
    }

    [Fact]
    public void Specificity_order_and_importance_are_respected()
    {
        var doc = Styled(@"<style>
            p { color: red }
            .c { color: green }
            #x { color: blue }
            p.c { color: yellow !important }
          </style>
          <p id=x class=c>t</p><p id=y class=c style='color: black'>u</p><p id=z>v</p>");
        Assert.Equal(0xFFFFFF00u, ById(doc, "x").Style.Color);
        Assert.Equal(0xFFFFFF00u, ById(doc, "y").Style.Color);
        Assert.Equal(0xFFFF0000u, ById(doc, "z").Style.Color);
    }

    [Fact]
    public void Inline_style_beats_id_selector()
    {
        var doc = Styled("<style>#a { color: red }</style><p id=a style='color: #00f'>t</p>");
        Assert.Equal(0xFF0000FFu, ById(doc, "a").Style.Color);
    }

    [Fact]
    public void Inherited_and_non_inherited_properties()
    {
        var doc = Styled("<div id=d style='color: red; border: 1px solid; font-size: 20px'><span id=s>t</span></div>");
        var span = ById(doc, "s");
        Assert.Equal(0xFFFF0000u, span.Style.Color);
        Assert.Equal(15f, span.Style.FontSize);
        Assert.False(span.Style.HasBorder);
        Assert.True(ById(doc, "d").Style.HasBorder);
    }

    [Fact]
    public void Display_initial_unset_and_inherit()
    {
        // display is not inherited, so initial and unset are its initial value, inline, for any element.
        var doc = Styled(@"<style>
            .h { display: none }
            .i { display: initial }
            .u { display: unset }
          </style>
          <span id=a class='h i'>a</span><div id=b class='h u'>b</div>
          <section style='display: flex'><span id=c style='display: inherit'>c</span></section>");
        Assert.Equal(Display.Inline, ById(doc, "a").Style.Display);
        Assert.Equal(Display.Inline, ById(doc, "b").Style.Display);
        Assert.Equal(Display.Flex, ById(doc, "c").Style.Display);
    }

    [Fact]
    public void Em_units_resolve_against_font_size()
    {
        var doc = Styled("<div style='font-size: 10pt'><p id=p style='font-size: 2em; margin-top: 1em; padding-left: 50%'>t</p></div>");
        var s = ById(doc, "p").Style;
        Assert.Equal(20f, s.FontSize);
        // Lengths are stored in Chrome's 1/64 px layout units, so 20pt becomes 19.99pt.
        Assert.Equal(20f, s.MarginTop.Value, 0.05f);
        Assert.True(s.PaddingLeft.IsPercent);
    }

    [Fact]
    public void Combinators_and_pseudo_classes_match()
    {
        var doc = Styled(@"<style>
            ul > li:first-child { color: red }
            li + li { color: green }
            li:nth-child(3) { color: blue }
            div li:last-child { font-weight: bold }
            a[href^='http'] { color: #123456 }
            li:not(.skip) { text-align: center }
          </style>
          <div><ul><li id=a>1</li><li id=b class=skip>2</li><li id=c>3</li></ul></div><a id=l href='https://x'>x</a>");
        Assert.Equal(0xFFFF0000u, ById(doc, "a").Style.Color);
        Assert.Equal(0xFF008000u, ById(doc, "b").Style.Color);
        Assert.Equal(0xFF0000FFu, ById(doc, "c").Style.Color);
        Assert.True(ById(doc, "c").Style.Bold);
        Assert.False(ById(doc, "b").Style.Bold);
        Assert.Equal(0xFF123456u, ById(doc, "l").Style.Color);
        Assert.Equal(TextAlign.Center, ById(doc, "a").Style.TextAlign);
        Assert.Equal(TextAlign.Start, ById(doc, "b").Style.TextAlign);
    }

    [Fact]
    public void Shorthands_expand()
    {
        var doc = Styled("<p id=p style='margin: 1px 2px 3px; padding: 4px; border-left: 3px dashed #f00; font: italic bold 16px/2 Georgia, serif; background: #eee url(x.png) no-repeat'>t</p>");
        var s = ById(doc, "p").Style;
        Assert.Equal(0.75f, s.MarginTop.Value);
        Assert.Equal(1.5f, s.MarginRight.Value);
        Assert.Equal(2.25f, s.MarginBottom.Value);
        Assert.Equal(1.5f, s.MarginLeft.Value);
        Assert.Equal(3f, s.PaddingTop.Value);
        Assert.Equal(BorderStyle.Dashed, s.BorderLeftStyle);
        Assert.Equal(2.25f, s.BorderLeft);
        Assert.Equal(0f, s.BorderTop);
        Assert.True(s.Bold);
        Assert.True(s.Italic);
        Assert.Equal(12f, s.FontSize);
        Assert.Equal(24f, s.LineHeight);
        Assert.Equal(Fonts.FontFamily.Times, s.FontFamily);
        Assert.Equal(0xFFEEEEEEu, s.BackgroundColor);
    }

    [Fact]
    public void Media_print_rules_apply_and_screen_rules_do_not()
    {
        var doc = Styled("<style>@media print { #p { color: red } } @media screen { #p { font-weight: bold } }</style><p id=p>t</p>");
        Assert.Equal(0xFFFF0000u, ById(doc, "p").Style.Color);
        Assert.False(ById(doc, "p").Style.Bold);
    }

    [Theory]
    [InlineData("#abc", 0xFFAABBCCu)]
    [InlineData("#a1b2c3", 0xFFA1B2C3u)]
    [InlineData("rgb(255, 0, 0)", 0xFFFF0000u)]
    [InlineData("rgb(0 128 0)", 0xFF008000u)]
    [InlineData("hsl(240, 100%, 50%)", 0xFF0000FFu)]
    [InlineData("rebeccapurple", 0xFF663399u)]
    [InlineData("transparent", 0u)]
    public void Parses_colors(string css, uint expected)
    {
        Assert.True(CssColor.TryParse(css, CssColor.Black, out var c));
        Assert.Equal(expected, c);
    }
}
