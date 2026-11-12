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

    [Fact]
    public void Ruby_position_under_puts_the_annotation_below_the_base_on_its_baseline()
    {
        var over = Texts("<p>A <ruby>base<rt>note</rt></ruby> B</p>");
        var under = Texts("<p>A <ruby style='ruby-position: under'>base<rt>note</rt></ruby> B</p>");
        // The base stays on the line's baseline, beside the text around it, and the annotation goes under it.
        Assert.Equal(Find(under, "A").Baseline, Find(under, "base").Baseline, 0.01f);
        Assert.True(Find(under, "note").Baseline > Find(under, "base").Baseline + 5, $"{Find(under, "note").Baseline}");
        Assert.Equal(Find(over, "note").X, Find(under, "note").X, 0.01f);
    }

    [Fact]
    public void Ruby_position_under_is_inherited_from_the_ruby_and_set_on_an_rt()
    {
        var texts = Texts("<p><ruby style='ruby-position: under'>a<rt>1</rt>b<rt style='ruby-position: over'>2</rt></ruby></p>");
        Assert.True(Find(texts, "1").Baseline > Find(texts, "a").Baseline);
        Assert.True(Find(texts, "2").Baseline < Find(texts, "b").Baseline);
    }

    [Fact]
    public void Ruby_align_places_the_narrower_of_base_and_annotation()
    {
        // A wide base under a short annotation: start puts the annotation at the base's start, center in its middle,
        // space-between at its two ends and space-around (the initial value) with half a share at each end. Latin text
        // spreads at its spaces only, so the annotation that spreads has one.
        string Html(string align, string note = "ab") => $"<p><ruby style='ruby-align: {align}'>MMMM<rt>{note}</rt></ruby></p>";
        float BaseX(List<TextOp> t) => Find(t, "MMMM").X;
        var start = Texts(Html("start"));
        Assert.Equal(BaseX(start), Find(start, "ab").X, 0.01f);
        var center = Texts(Html("center"));
        var c = Find(center, "ab");
        var m = Find(center, "MMMM");
        Assert.Equal(m.X + m.Font.Measure("MMMM", m.Size) / 2, c.X + c.Font.Measure("ab", c.Size) / 2, 0.5f);
        var between = Texts(Html("space-between", "a b"));
        var b = between.Where(t => t.Size < 15).ToList();
        Assert.Equal(BaseX(between), b.Min(t => t.X), 0.01f);
        var around = Texts(Html("space-around", "a b"));
        Assert.True(around.Where(t => t.Size < 15).Min(t => t.X) > BaseX(around) + 1);
    }

    [Fact]
    public void A_second_rt_annotates_an_empty_base_after_the_first_pair()
    {
        // As in Chrome, consecutive annotations of one base go over an empty base after it rather than into the line.
        var texts = Texts("<p><ruby>base<rt>one</rt><rt>two</rt></ruby>end</p>");
        var two = Find(texts, "two");
        Assert.Equal(Find(texts, "one").Baseline, two.Baseline, 0.01f);
        Assert.True(two.X >= Find(texts, "base").X + Find(texts, "base").Font.Measure("base", Find(texts, "base").Size) - 0.5f);
        Assert.True(Find(texts, "end").X >= two.X + two.Font.Measure("two", two.Size) - 0.5f);
    }

    [Fact]
    public void An_rt_in_an_rtc_is_ordinary_text_as_in_Chrome()
    {
        var texts = Texts("<p><ruby>base<rt>note</rt><rtc><rt>Tokyo</rt></rtc></ruby></p>");
        Assert.Equal(Find(texts, "base").Size, Find(texts, "Tokyo").Size, 0.01f);
        Assert.Equal(Find(texts, "base").Baseline, Find(texts, "Tokyo").Baseline, 0.01f);
    }
}
