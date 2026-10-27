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

    [Theory]
    [InlineData("<p>x", false)]
    [InlineData("<!DOCTYPE html><p>x", true)]
    [InlineData("\n\n<!doctype HTML><p>x", true)]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\"><p>x", true)]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\"><p>x", false)]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\" \"http://www.w3.org/TR/html4/loose.dtd\"><p>x", true)]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 3.2 Final//EN\"><p>x", false)]
    [InlineData("<!DOCTYPE svg><p>x", false)]
    [InlineData("<p>x<!DOCTYPE html>", false)]
    public void A_doctype_sets_standards_mode_as_in_browsers(string html, bool standards)
    {
        HtmlParser.Parse(html, out var used);
        Assert.Equal(standards, (used & NamesUsed.StandardsMode) != 0);
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
        Assert.Equal("body", doc.FindFirst("div")!.Parent!.Name);
    }

    [Fact]
    public void A_stray_end_p_makes_an_empty_paragraph()
    {
        // Acid2's markup: the table closes the first paragraph, so the </p> after it opens and closes a new one.
        var doc = Body("<div><p><table><tr><td></td></tr></table></p><p class=bad>x</p></div>");
        var names = doc.FindFirst("div")!.Children.OfType<Element>().Select(e => e.Name + (e.GetAttribute("class") is { } c ? "." + c : ""));
        Assert.Equal(new[] { "p", "table", "p", "p.bad" }, names);
        Assert.Empty(Elements(doc, "p").ElementAt(1).Children);
        // A </p> inside a cell, a button or an object makes one there and closes nothing outside.
        var cell = Body("<p>a<table><tr><td>b</p>c</td></tr></table>");
        Assert.Equal("p", cell.FindFirst("td")!.Children.OfType<Element>().Single().Name);
        var button = Body("<p>a<button>b</p>c</button>d</p>");
        Assert.Equal("p", button.FindFirst("button")!.Children.OfType<Element>().Single().Name);
        Assert.Equal("abcd", Elements(button, "p").First().InnerText());
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

    [Theory]
    [InlineData("<!DOCTYPE html>", 24f)]
    [InlineData("", 150f)]
    public void Unitless_lengths_count_as_pixels_only_in_quirks_mode(string doctype, float width)
    {
        // Acid2 sets width: 2em and then width: 200, which a page with a doctype must ignore.
        var doc = HtmlParser.Parse(doctype + "<p id=p>x</p>", out var used);
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet("p { width: 2em; width: 200; margin: 0 7 }", sheet);
        new StyleResolver(sheet) { StandardsMode = (used & NamesUsed.StandardsMode) != 0 }.ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var p = ById(doc, "p");
        Assert.Equal(width, p.Style.Width.Value, 0.01f);
        Assert.Equal(width == 150f ? 5.25f : 0f, p.Style.MarginLeft.Value, 0.01f);
    }

    [Fact]
    public void Invalid_css_is_dropped_as_in_browsers()
    {
        // Acid2's parser tests, each against a valid declaration before it.
        var doc = Styled(@"<style>
            p { background: yellow; height: 10px; margin-left: 4px }
            #a { error: \}; height: 20px }
            ;
            #b { height: 30px }
            #c { background: red pink }
            #d { border: 5px solid red ! error }
            #e { background: url(a.png) red, blue }
            #f { content: ""!"" ; margin-left: 8px }
          </style>
          <p id=a>t</p><p id=b>t</p><p id=c>t</p><p id=d>t</p><p id=e>t</p><p id=f>t</p>");
        Assert.Equal(15f, ById(doc, "a").Style.Height.Value, 2);  // an escaped brace does not end the rule
        Assert.Equal(7.5f, ById(doc, "b").Style.Height.Value, 2); // the stray ';' makes the next selector invalid
        Assert.Equal(0xFFFFFF00u, ById(doc, "c").Style.BackgroundColor); // two colours
        Assert.Equal(0f, ById(doc, "d").Style.BorderLeft);                 // '!' that is not !important
        Assert.Equal(0xFFFFFF00u, ById(doc, "e").Style.BackgroundColor); // a colour before the last layer
        Assert.Equal(6f, ById(doc, "f").Style.MarginLeft.Value, 2);      // '!' in a string is fine
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

    [Fact]
    public void Border_inherit_copies_the_parents_border()
    {
        var doc = Styled(@"<style>div { border: 2px solid green } span { border: inherit } i { border: 1px solid red; border: initial }</style>
          <div><span id=s>x</span><i id=i>y</i></div>");
        var s = ById(doc, "s").Style;
        Assert.Equal(BorderStyle.Solid, s.BorderTopStyle);
        Assert.Equal(BorderStyle.Solid, s.BorderLeftStyle);
        Assert.Equal(1.5f, s.BorderTopWidth);
        Assert.Equal(0xFF008000u, s.BorderBottomColor);
        Assert.Equal(BorderStyle.None, ById(doc, "i").Style.BorderTopStyle);
    }

    [Fact]
    public void All_unset_drops_earlier_declarations_but_keeps_later_ones()
    {
        var doc = Styled(@"<style>.b { display: block; padding: 3px; border: 1px solid; color: red }
            .u { all: unset } .later { all: unset; margin-left: 4px }</style>
          <div style='color: blue'><div id=u class='b u'>x</div><div id=l class='b later'>y</div></div>");
        var u = ById(doc, "u").Style;
        Assert.Equal(Display.Inline, u.Display);
        Assert.Equal(BorderStyle.None, u.BorderTopStyle);
        Assert.Equal(0f, u.PaddingTop.Value);
        Assert.Equal(0xFF0000FFu, u.Color); // color is inherited, so unset takes the parent's
        Assert.Equal(3f, ById(doc, "l").Style.MarginLeft.Value);
    }

    [Fact]
    public void All_initial_resets_inherited_properties_and_revert_keeps_the_user_agents()
    {
        var doc = Styled(@"<style>.c { color: red; font-weight: bold } .i { all: initial } .r { padding: 5px; all: revert }</style>
          <div class=c><p id=i class=i>x</p><p id=r class=r>y</p></div>");
        var i = ById(doc, "i").Style;
        Assert.Equal(Display.Inline, i.Display);
        Assert.Equal(0xFF000000u, i.Color);
        Assert.False(i.Bold);
        var r = ById(doc, "r").Style;
        Assert.Equal(Display.Block, r.Display); // the user agent's p { display: block } stays
        Assert.Equal(0f, r.PaddingTop.Value);
    }

    [Fact]
    public void Author_blocks_of_the_same_text_share_their_declarations_but_not_with_the_user_agent()
    {
        var first = new Stylesheet { ShareDeclarations = true };
        var second = new Stylesheet { ShareDeclarations = true };
        CssParser.ParseStylesheet("p { margin: 0 1px; color: red }", first);
        CssParser.ParseStylesheet("@media print { div { margin: 0 1px; color: red } }", second);
        Assert.Same(first.Rules[0].Declarations, second.Rules[0].Declarations);
        Assert.Equal(["margin-top", "margin-right", "margin-bottom", "margin-left", "color"], first.Rules[0].Declarations.Select(d => d.Property));

        var own = new Stylesheet();
        CssParser.ParseStylesheet("p { margin: 0 1px; color: red }", own);
        Assert.NotSame(first.Rules[0].Declarations[0], own.Rules[0].Declarations[0]);
    }

    [Fact]
    public void All_revert_tells_an_author_block_from_the_user_agents_of_the_same_text()
    {
        // ".x" has the same block as the user agent's "b, strong { font-weight: bold; }". If they shared declarations,
        // the span and the b would share one computed style, though revert drops the span's bold and keeps the b's.
        var doc = HtmlParser.Parse("<style>.x { font-weight: bold; } .r { all: revert }</style><div><span id=s class='x r'>a</span><b id=b class=r>b</b></div>");
        var sheet = new Stylesheet { ShareDeclarations = true };
        CssParser.ParseStylesheet(".x { font-weight: bold; } .r { all: revert }", sheet);
        new StyleResolver(sheet).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        Assert.False(ById(doc, "s").Style.Bold);
        Assert.True(ById(doc, "b").Style.Bold);
    }
}
