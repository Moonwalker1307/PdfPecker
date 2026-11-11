using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>Ruby: rt annotations drawn above their base at half size, rp hidden, and the room the annotations take.</summary>
public class RubyTests
{
    private const float Width = 400, PageHeight = 600;

    private static List<TextOp> Texts(string body, string css = "")
    {
        var doc = HtmlParser.Parse("<!DOCTYPE html><html><head><style>body{margin:0;font:20px/1.6 Helvetica} p{margin:0}" + css
                                   + "</style></head><body>" + body + "</body></html>");
        var sheet = new Stylesheet();
        foreach (var style in Styles(doc)) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return All(engine.Ops).OfType<TextOp>().ToList();

        static IEnumerable<Element> Styles(Element e)
        {
            foreach (var c in e.Children.OfType<Element>())
            {
                if (c.Name == "style") yield return c;
                foreach (var d in Styles(c)) yield return d;
            }
        }

        static IEnumerable<DrawOp> All(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                yield return o;
                if (o is GroupOp g)
                    foreach (var c in All(g.Ops)) yield return c;
            }
        }
    }

    private static TextOp Find(List<TextOp> texts, string text) => Assert.Single(texts, t => t.Text.Trim() == text);

    [Fact]
    public void An_annotation_is_drawn_centred_above_its_base_at_half_size()
    {
        var texts = Texts("<p>A <ruby>base<rt>annotation</rt></ruby> B</p>");
        var rb = Find(texts, "base");
        var rt = Find(texts, "annotation");
        Assert.Equal(rb.Size / 2, rt.Size, 0.01f);
        Assert.True(rt.Baseline < rb.Baseline - rb.Size * 0.7f, $"annotation baseline {rt.Baseline}, base {rb.Baseline}");
        // The base is centred under the wider annotation: both have the same middle.
        float rbW = rb.Font.Measure(rb.Text.Trim(), rb.Size), rtW = rt.Font.Measure(rt.Text.Trim(), rt.Size);
        Assert.Equal(rt.X + rtW / 2, rb.X + rbW / 2, 0.5f);
    }

    [Fact]
    public void Rp_is_not_displayed_and_each_base_gets_its_own_annotation()
    {
        var texts = Texts("<p><ruby>one<rp>(</rp><rt>1</rt><rp>)</rp>two<rt>2</rt></ruby></p>");
        Assert.DoesNotContain(texts, t => t.Text.Contains('(') || t.Text.Contains(')'));
        Assert.True(Find(texts, "1").X < Find(texts, "two").X);
        Assert.True(Find(texts, "2").X > Find(texts, "one").X);
        Assert.True(Find(texts, "2").Baseline < Find(texts, "two").Baseline);
    }

    [Fact]
    public void The_annotation_takes_the_leading_before_the_line_grows()
    {
        // 32px lines leave 6px of leading above and below the text: an annotation in the second line uses the space
        // the first leaves below it as well as its own, so the lines stay 32px apart. With line-height 1 there is no
        // leading, and the second line moves down by the annotation's height.
        string html = "<p>first line<br><ruby>second<rt>note</rt></ruby> line</p>";
        var loose = Texts(html);
        Assert.Equal(32 * 0.75f, Find(loose, "second").Baseline - Find(loose, "first line").Baseline, 0.5f);
        var tight = Texts(html, "p{line-height:1}");
        Assert.True(Find(tight, "second").Baseline - Find(tight, "first line").Baseline > 20 * 0.75f + 4);
    }

    [Fact]
    public void An_rb_element_in_the_base_does_not_lift_the_annotation()
    {
        var plain = Texts("<p><ruby>base<rt>note</rt></ruby></p>");
        var rb = Texts("<p><ruby><rb>base</rb><rt>note</rt></ruby></p>");
        Assert.Equal(Find(plain, "note").Baseline, Find(rb, "note").Baseline, 0.01f);
    }

    [Fact]
    public void A_wide_annotation_overhangs_the_text_beside_it_by_half_its_font_size_at_most()
    {
        // Without the overhang the base would start the annotation's half excess after the text; the annotation reaches
        // over that text by at most half of its 10px font size instead.
        var texts = Texts("<p>xx<ruby>i<rt>a long annotation</rt></ruby>xx</p>");
        var rt = Find(texts, "a long annotation");
        var before = texts.First(t => t.Text.StartsWith("xx", StringComparison.Ordinal));
        float beforeEnd = before.X + before.Font.Measure("xx", before.Size);
        Assert.Equal(beforeEnd - 5 * 0.75f, rt.X, 0.5f);
    }

    [Fact]
    public void Ruby_survives_a_second_style_pass()
    {
        // Container queries resolve the styles again, which unwraps and wraps the pairs: the base stays before its rt.
        var texts = Texts("<div style='container-type:inline-size'><p><ruby>base<rt>note</rt></ruby> tail</p></div>",
            "@container (min-width: 10px) { p { color: red } }");
        Assert.True(Find(texts, "note").Baseline < Find(texts, "base").Baseline);
        Assert.True(Find(texts, "base").X < Find(texts, "tail").X);
    }
}
