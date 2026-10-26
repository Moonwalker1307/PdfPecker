using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// block-ellipsis: the mark at the end of the last line of a block that line-clamp cuts short, an ellipsis by default,
/// a string of the author's, or none.
/// </summary>
public class BlockEllipsisTests
{
    private static readonly string Words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 40));

    private static List<TextOp> Texts(string style)
    {
        var doc = HtmlParser.Parse($"<p style='width: 200pt; margin: 0; {style}'>{Words}</p>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return engine.Ops.OfType<TextOp>().ToList();
    }

    /// <summary>The text of the last line drawn.</summary>
    private static string LastLine(List<TextOp> texts)
    {
        float last = texts.Max(t => t.Baseline);
        return string.Concat(texts.Where(t => t.Baseline == last).OrderBy(t => t.X).Select(t => t.Text));
    }

    [Theory]
    [InlineData("line-clamp: 2", "…")]
    [InlineData("line-clamp: 2; block-ellipsis: auto", "…")]
    [InlineData("line-clamp: 2; block-ellipsis: \"… more\"", "… more")]
    [InlineData("line-clamp: 2 \"[cut]\"", "[cut]")]
    [InlineData("-webkit-line-clamp: 2; block-ellipsis: \"»\"", "»")]
    public void The_last_line_ends_in_the_mark(string style, string mark)
    {
        var texts = Texts(style);
        Assert.Equal(2, texts.Select(t => t.Baseline).Distinct().Count());
        var line = LastLine(texts);
        Assert.EndsWith(mark, line, StringComparison.Ordinal);
        Assert.DoesNotContain("…", line.Substring(0, line.Length - mark.Length), StringComparison.Ordinal);
    }

    [Fact]
    public void None_cuts_the_lines_without_a_mark()
    {
        var texts = Texts("line-clamp: 2; block-ellipsis: none");
        Assert.Equal(2, texts.Select(t => t.Baseline).Distinct().Count());
        Assert.DoesNotContain("…", LastLine(texts), StringComparison.Ordinal);
        // The line keeps the words that fit, as an unclamped line would.
        Assert.Equal(LastLine(Texts("line-clamp: 2; block-ellipsis: \"\"")), LastLine(texts));
    }

    [Fact]
    public void A_long_mark_takes_room_from_the_line()
    {
        string plain = LastLine(Texts("line-clamp: 2; block-ellipsis: none"));
        string marked = LastLine(Texts("line-clamp: 2; block-ellipsis: \" — read the rest on the website\""));
        Assert.EndsWith("website", marked, StringComparison.Ordinal);
        Assert.True(marked.Length - " — read the rest on the website".Length < plain.Length);
    }

    [Fact]
    public void It_is_inherited_and_invalid_values_are_dropped()
    {
        var doc = HtmlParser.Parse($"<div style='block-ellipsis: \"~\"'><p style='width: 200pt; margin: 0; line-clamp: 2; block-ellipsis: 12px'>{Words}</p></div>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        Assert.EndsWith("~", LastLine(engine.Ops.OfType<TextOp>().ToList()), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_fits_gets_no_mark()
        => Assert.DoesNotContain(Texts("line-clamp: 200; block-ellipsis: \"… more\""), t => t.Text.Contains("more", StringComparison.Ordinal));

    [Fact]
    public void Supports_knows_block_ellipsis()
        => Assert.True(CssSupports.Evaluate("(block-ellipsis: \"…\")"));
}
