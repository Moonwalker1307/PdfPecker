using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>text-autospace: an eighth of an em between ideographs and Latin letters or digits, as Chrome adds it.</summary>
public class TextAutospaceTests
{
    private const float Width = 400, PageHeight = 600;

    private static List<TextOp> Texts(string body, string css = "")
    {
        var doc = HtmlParser.Parse("<!DOCTYPE html><html><head><style>body{margin:0;font:16px Helvetica} p{margin:0}" + css
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

    /// <summary>Where the text <paramref name="word"/> starts, in points.</summary>
    private static float XOf(List<TextOp> texts, string word)
    {
        foreach (var t in texts)
        {
            int i = t.Text.IndexOf(word, StringComparison.Ordinal);
            if (i >= 0) return t.X + t.Font.Measure(t.Text[..i], t.Size) + t.CharSpacing * i;
        }
        throw new InvalidOperationException(word + " not drawn");
    }

    [Theory]
    [InlineData("normal", "English", 2)]
    [InlineData("auto", "English", 2)]
    [InlineData("ideograph-alpha", "English", 2)]
    [InlineData("ideograph-numeric", "English", 0)]
    [InlineData("normal", "2026", 6)]
    [InlineData("ideograph-numeric", "2026", 2)]
    [InlineData("ideograph-alpha", "2026", 4)]
    [InlineData("no-autospace", "English", 0)]
    public void An_eighth_of_an_em_goes_between_ideographs_and_other_letters_or_digits(string value, string word, float px)
    {
        string html = "<p>日本English日本2026日本</p>";
        float on = XOf(Texts(html, $"p{{text-autospace:{value}}}"), word), off = XOf(Texts(html), word);
        // 16px / 8 = 2px at each boundary before the word: 本|E, then h|日 and 本|2 before the digits.
        Assert.Equal(px * 0.75f, on - off, 0.05f);
    }

    [Fact]
    public void Punctuation_spaces_and_fullwidth_letters_get_no_space()
    {
        string html = "<p>日本、English 日本Ｅ日本</p>";
        var on = Texts(html, "p{text-autospace:normal}");
        var off = Texts(html);
        Assert.Equal(XOf(off, "English"), XOf(on, "English"), 0.01f);
        Assert.Equal(XOf(off, "Ｅ"), XOf(on, "Ｅ"), 0.01f);
    }

    [Fact]
    public void The_space_crosses_inline_elements_with_the_smaller_font()
    {
        float on = XOf(Texts("<p>日本<small>English</small></p>", "p{text-autospace:normal}"), "English");
        float off = XOf(Texts("<p>日本<small>English</small></p>"), "English");
        Assert.Equal(13.333f / 8 * 0.75f, on - off, 0.05f);
    }

    [Fact]
    public void A_line_does_not_start_with_the_space()
    {
        // The paragraph breaks between the ideographs and the Latin word, which starts the second line at its edge.
        var texts = Texts("<p style='width:40px'>日本日本English</p>", "p{text-autospace:normal}");
        Assert.Equal(0, XOf(texts, "English"), 0.01f);
    }

    [Theory]
    [InlineData("normal", true)]
    [InlineData("ideograph-alpha ideograph-numeric insert", true)]
    [InlineData("punctuation replace", true)]
    [InlineData("ideograph-alpha ideograph-alpha", false)]
    [InlineData("insert", false)]
    [InlineData("nonsense", false)]
    public void Supports_reads_the_value(string value, bool valid)
        => Assert.Equal(valid, CssSupports.Evaluate($"(text-autospace: {value})"));
}
