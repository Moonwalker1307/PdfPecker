using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-justify and where justified lines put their free space: at spaces and no-break spaces, between CJK ideographs
/// and symbols as Chrome does, and between all characters with inter-character.
/// </summary>
public class TextJustifyTests
{
    private const float PageHeight = 800;

    private static List<TextOp> Layout(string html, float width)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return engine.Ops.OfType<TextOp>().ToList();
    }

    /// <summary>The text ops of each line, top to bottom.</summary>
    private static List<List<TextOp>> Lines(string html, float width) => Layout(html, width)
        .GroupBy(t => MathF.Round(t.Baseline, 1))
        .OrderBy(g => g.Key)
        .Select(g => g.OrderBy(t => t.X).ToList())
        .ToList();

    /// <summary>Where the last character of a run of text ends, without the letter spacing after it.</summary>
    private static float End(TextOp t) => t.X + t.Font.Measure(t.Text, t.Size) + t.CharSpacing * (t.Text.Length - 1)
        + t.WordSpacing * t.Text.Count(c => c == ' ');

    private static float Advance(string text) => new ComputedStyle().Font.Measure(text, new ComputedStyle().FontSize);

    // Every ideograph has the same advance in the fallback font, so a box a few characters wide holds exactly that many.
    private static float Chars(float n) => Advance("漢") * n + 0.01f;

    [Fact]
    public void Chinese_lines_are_justified_between_ideographs()
    {
        float width = Chars(4.5f);
        var lines = Lines($"<p style='text-align: justify; width: {width}pt; margin: 0'>我们今天去公园散步</p>", width);
        Assert.Equal(["我们今天", "去公园散", "步"], lines.Select(l => string.Concat(l.Select(t => t.Text))));
        // Three opportunities between four ideographs share the free half character; the last line is not justified.
        var first = Assert.Single(lines[0]);
        Assert.Equal(Advance("漢") / 6, first.CharSpacing, 2);
        Assert.Equal(width, End(first), 1);
        Assert.Equal(0, Assert.Single(lines[2]).CharSpacing);
    }

    [Fact]
    public void Latin_text_is_still_justified_at_its_spaces()
    {
        var lines = Lines("<p style='text-align: justify; width: 200pt; margin: 0'>The quick brown fox jumps over the lazy dog and keeps running.</p>", 200);
        var first = Assert.Single(lines[0]);
        Assert.Equal(0, first.CharSpacing);
        Assert.True(first.WordSpacing > 0);
        Assert.Equal(200, End(first), 1);
    }

    [Fact]
    public void Inter_character_spreads_the_space_between_letters()
    {
        const string text = "The quick brown fox jumps over the lazy dog and keeps running.";
        var lines = Lines($"<p style='text-align: justify; text-justify: inter-character; width: 200pt; margin: 0'>{text}</p>", 200);
        var first = Assert.Single(lines[0]);
        Assert.True(first.CharSpacing > 0);
        // The spaces get the same share as the letters, through the letter spacing alone.
        Assert.Equal(0, first.WordSpacing);
        Assert.Equal(200, End(first), 1);
        Assert.Equal(0, lines[^1].Single().CharSpacing);
    }

    [Fact]
    public void Inter_word_leaves_ideographs_alone()
    {
        float width = Chars(4.5f);
        var lines = Lines($"<p style='text-align: justify; text-justify: inter-word; width: {width}pt; margin: 0'>我们今天去公园散步</p>", width);
        Assert.All(lines, l => Assert.Equal(0, Assert.Single(l).CharSpacing));
        Assert.Equal(0, lines[0][0].X);
    }

    [Fact]
    public void None_does_not_justify()
    {
        var lines = Lines("<p style='text-align: justify; text-justify: none; width: 200pt; margin: 0'>The quick brown fox jumps over the lazy dog and keeps running.</p>", 200);
        var first = Assert.Single(lines[0]);
        Assert.Equal(0, first.X);
        Assert.Equal(0, first.WordSpacing);
        Assert.Equal(0, first.CharSpacing);
        Assert.True(End(first) < 199);
    }

    [Fact]
    public void Korean_is_justified_at_its_spaces_only()
    {
        var lines = Lines("<p style='text-align: justify; width: 60pt; margin: 0'>대한민국의 수도는 서울이며 가장 큰 도시이기도 하다</p>", 60);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.All(l, t => Assert.Equal(0, t.CharSpacing)));
        Assert.True(lines[0].Single().WordSpacing > 0);
    }

    [Fact]
    public void No_break_spaces_stretch_like_spaces()
    {
        var lines = Lines("<p style='text-align: justify; width: 200pt; margin: 0'>Invoice&nbsp;no. 1234 is due on 1&nbsp;November and must be paid by then.</p>", 200);
        var first = lines[0];
        // The line is drawn in pieces that end at its no-break spaces.
        Assert.Equal(3, first.Count);
        Assert.Equal("Invoice ", first[0].Text);
        float afterNoBreakSpace = first[1].X - first[0].X - first[0].Font.Measure(first[0].Text, first[0].Size);
        Assert.True(first[1].WordSpacing > 0);
        Assert.Equal(first[1].WordSpacing, afterNoBreakSpace, 2);
        Assert.Equal(200, End(first[^1]), 1);
    }

    [Fact]
    public void Mixed_lines_share_the_space_between_words_and_ideographs()
    {
        float width = Chars(8);
        var lines = Lines($"<p style='text-align: justify; width: {width}pt; margin: 0'>我们 PDF 今天去公园散步了一下午</p>", width);
        // Spaces, the ideographs and the boundary between them all get the same share, and the line still ends at the edge.
        var first = lines[0];
        Assert.True(first.Count > 1);
        Assert.Equal(width, End(first[^1]), 1);
        float share = first.First(t => t.CharSpacing > 0).CharSpacing;
        Assert.All(first.Where(t => t.Text.Contains(' ')), t => Assert.Equal(share, t.WordSpacing + t.CharSpacing, 2));
    }

    [Fact]
    public void Text_justify_is_inherited_and_has_the_old_distribute_name()
    {
        var lines = Lines("<div style='text-justify: distribute'><p style='text-align: justify; width: 200pt; margin: 0'>The quick brown fox jumps over the lazy dog and keeps running.</p></div>", 200);
        Assert.True(lines[0].Single().CharSpacing > 0);
        Assert.True(CssSupports.Evaluate("(text-justify: inter-character)"));
    }

    [Fact]
    public void Text_align_last_justify_justifies_the_last_line_between_ideographs()
    {
        float width = Chars(4.5f);
        var lines = Lines($"<p style='text-align: justify; text-align-last: justify; width: {width}pt; margin: 0'>我们今天去公园</p>", width);
        var last = Assert.Single(lines[^1]);
        Assert.Equal("去公园", last.Text);
        Assert.Equal(width, End(last), 1);
    }
}
