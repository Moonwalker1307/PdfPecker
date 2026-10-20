using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// initial-letter: a ::first-letter that sinks into the lines of its paragraph. The expected positions are Chrome's,
/// measured in Chrome 141 with the same fonts, in points: one CSS pixel is 0.75 pt.
/// </summary>
public class InitialLetterTests
{
    private const float Width = 375, PageHeight = 800;

    private const string Text = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero, sed cursus ante "
        + "dapibus diam. Sed nisi nulla quis sem at nibh elementum imperdiet. Duis sagittis ipsum praesent mauris fusce nec tellus sed "
        + "augue semper porta mauris massa vestibulum.";

    private static string Page(string letter, string paragraph = "", string body = "")
        => $"<style>body{{margin:0;font:16px/24px serif}} p{{margin:0;{paragraph}}} p::first-letter{{{letter}}}</style>"
           + (body.Length > 0 ? body : $"<p>{Text}</p>");

    private static List<DrawOp> Layout(string html)
    {
        // The parser makes no body element of its own, which the body rules need.
        var doc = HtmlParser.Parse("<body>" + html + "</body>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return engine.Ops;
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<TextOp> Texts(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            if (op is TextOp t) yield return t;
            if (op is GroupOp g) foreach (var o in Texts(g.Ops)) yield return o;
        }
    }

    /// <summary>The letter, which is the one text drawn larger than the paragraph's, and the paragraph's lines, top to bottom.</summary>
    private static (TextOp Letter, (float X, float Baseline)[] Lines) Split(List<DrawOp> ops)
    {
        var texts = Texts(ops).ToList();
        var letter = texts.Single(t => t.Size > 12.5f);
        var lines = texts.Where(t => t != letter).GroupBy(t => t.Baseline).OrderBy(g => g.Key).Select(g => (g.Min(t => t.X), g.Key)).ToArray();
        return (letter, lines);
    }

    [Fact]
    public void Drop_cap_sits_on_the_baseline_of_its_last_line()
    {
        var (letter, lines) = Split(Layout(Page("initial-letter: 3")));
        Assert.Equal(lines[2].Baseline, letter.Baseline, 2);
        // The lines beside it start after its ink box, 48px from the edge in Chrome; the fourth is below it.
        Assert.All(lines.Take(3), l => Assert.Equal(36, l.X, 2));
        Assert.Equal(0, lines[3].X, 2);
    }

    [Fact]
    public void Font_size_makes_the_cap_height_span_the_lines()
    {
        // Two line heights plus the paragraph's cap height, over the cap height of Liberation Serif (1341/2048 em).
        var (letter, _) = Split(Layout(Page("initial-letter: 3")));
        float cap = 1341 / 2048f;
        Assert.Equal((2 * 18 + 12 * cap) / cap, letter.Size, 2);
    }

    [Fact]
    public void Raised_letter_moves_the_first_line_down()
    {
        var (dropped, _) = Split(Layout(Page("initial-letter: 3")));
        var (raised, lines) = Split(Layout(Page("initial-letter: 3 raise")));
        Assert.Equal(dropped.Baseline, raised.Baseline, 2);
        Assert.Equal(raised.Baseline, lines[0].Baseline, 2);
        Assert.Equal(36, lines[0].X, 2);
        Assert.Equal(0, lines[1].X, 2);
    }

    [Fact]
    public void Letter_sunk_deeper_than_it_is_tall_stands_on_its_last_line()
    {
        // initial-letter: 2 4 in Chrome 141: the box ends at the bottom of the fourth line and the lines beside it start
        // after its 28px.
        var (letter, lines) = Split(Layout(Page("initial-letter: 2 4", body: $"<p>{Text} {Text}</p>")));
        Assert.Equal(72, letter.Baseline, 1); // an L has no ink below the baseline
        Assert.All(lines.Take(4), l => Assert.Equal(21, l.X, 2));
        Assert.Equal(0, lines[4].X, 2);
    }

    [Fact]
    public void Text_indent_moves_the_letter_twice_as_Chrome_does()
    {
        var (_, lines) = Split(Layout(Page("initial-letter: 2", "text-indent: 20px")));
        Assert.Equal(51, lines[0].X, 2);
        Assert.Equal(51, lines[1].X, 2);
        Assert.Equal(0, lines[2].X, 2);
    }

    [Fact]
    public void Right_to_left_letter_sits_at_the_right()
    {
        var (letter, _) = Split(Layout(Page("initial-letter: 2", "direction: rtl")));
        Assert.True(letter.X > Width - 40, $"letter at {letter.X}");
    }

    [Fact]
    public void Paragraph_with_an_initial_letter_starts_below_the_one_before()
    {
        var ops = Layout(Page("initial-letter: 3", body: "<p>Short.</p><p>Next one.</p>"));
        var letters = Texts(ops).Where(t => t.Size > 12.5f).OrderBy(t => t.Baseline).ToArray();
        Assert.Equal(2, letters.Length);
        // The second paragraph starts below the first letter's box, which reaches its baseline, and drops its own letter
        // as far again.
        Assert.True(letters[1].Baseline >= 2 * letters[0].Baseline - 0.01f, $"{letters[0].Baseline} {letters[1].Baseline}");
    }

    [Fact]
    public void Paragraph_without_one_wraps_round_the_letter_above()
    {
        var ops = Layout("<style>body{margin:0;font:16px/24px serif} p{margin:0} .d::first-letter{initial-letter:3}</style>"
                         + "<p class=d>Short.</p><p>Next<br>lines<br>here<br>below</p>");
        var lines = Texts(ops).Where(t => t.Size < 12.5f).OrderBy(t => t.Baseline).ToArray();
        // "hort." and the first two lines of the next paragraph are beside the letter; "here" is below it.
        Assert.True(lines[0].X > 20);
        Assert.Equal(lines[0].X, lines[1].X, 2);
        Assert.Equal(lines[0].X, lines[2].X, 2);
        Assert.Equal(0, lines[3].X, 2);
    }

    [Fact]
    public void Cells_that_share_a_style_each_get_their_letter()
    {
        // Both cells match the same rules, so their letters start from one shared computed style.
        var ops = Layout("<style>body{margin:0;font:13px/18px sans-serif} td{width:150px} td::first-letter{initial-letter:2}</style>"
                         + "<table><tr><td>Cell one with a letter</td><td>Cell two with a letter</td></tr></table>");
        var big = Texts(ops).Where(t => t.Size > 10).ToArray();
        Assert.Equal(2, big.Length);
        Assert.Equal(big[0].Size, big[1].Size, 3);
    }

    [Fact]
    public void Initial_letter_on_other_elements_does_nothing()
    {
        var ops = Layout("<style>body{margin:0;font:16px/24px serif} span{initial-letter:3}</style><p><span>Lorem</span> ipsum</p>");
        Assert.All(Texts(ops), t => Assert.Equal(12, t.Size, 2));
    }

    [Fact]
    public void Floated_letter_takes_the_size_and_floats_as_usual()
    {
        var ops = Layout(Page("initial-letter: 3; float: right"));
        var letter = Texts(ops).Single(t => t.Size > 12.5f);
        Assert.True(letter.X > Width / 2, $"letter at {letter.X}");
    }

    [Theory]
    [InlineData("3", 3f, 0)]
    [InlineData("2.5", 2.5f, 0)]
    [InlineData("3 2", 3f, 2)]
    [InlineData("3 drop", 3f, 0)]
    [InlineData("3 raise", 3f, 1)]
    [InlineData("raise 2", 2f, 1)]
    public void Values_parse(string value, float size, int sink)
    {
        Assert.Equal((size, sink), StyleResolver.ParseInitialLetter(value));
        Assert.True(CssSupports.Evaluate($"(initial-letter: {value})"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("-2")]
    [InlineData("3 0")]
    [InlineData("3 1.5")]
    [InlineData("drop")]
    [InlineData("3 2 1")]
    [InlineData("2 raise drop")]
    [InlineData("nan")]
    public void Invalid_values_are_rejected(string value)
    {
        Assert.Null(StyleResolver.ParseInitialLetter(value));
        Assert.False(CssSupports.Evaluate($"(initial-letter: {value})"));
    }

    [Fact]
    public void Converts_to_a_valid_pdf()
    {
        var pdf = HtmlToPdf.Convert(Page("initial-letter: 3; color: #a33; background: #eee; padding: 4px"));
        AssertStructurallyValid(pdf);
    }
}

[Collection(HostileInputCollection.Name)]
public class InitialLetterHostileInputTests
{
    [Theory]
    [InlineData("1e30")]
    [InlineData("999999 999999")]
    [InlineData("1000 raise")]
    public void Huge_initial_letters_finish_quickly(string value)
    {
        var html = $"<style>p::first-letter{{initial-letter:{value}}}</style><p>Lorem ipsum dolor sit amet.</p><p>Next.</p>";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AssertStructurallyValid(HtmlToPdf.Convert(html, new PdfOptions { MaxPages = 50 }));
        }
        catch (PdfLimitException)
        {
            // Too many pages is a documented outcome.
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }
}
