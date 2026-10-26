using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-underline-position: from-font draws the underline where the font says and under draws it below the em box, clear
/// of descenders. The expected offsets are Chrome's, in CSS pixels below the baseline, for 60px Helvetica (Liberation
/// Sans in Chrome).
/// </summary>
public class TextUnderlinePositionTests
{
    private const float Px = 0.75f;

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return engine.Ops;
    }

    /// <summary>The top of the underline of the paragraph, in CSS pixels below its baseline.</summary>
    private static float UnderlineTop(string style)
    {
        var ops = Layout($"<p style='font: 60px Helvetica; margin: 0; {style}'><u>xxxx</u></p>");
        var text = ops.OfType<TextOp>().Single();
        var line = ops.OfType<RectOp>().Single(r => r.IsDecoration);
        return (line.Y - text.Baseline) / Px;
    }

    [Theory]
    [InlineData("text-underline-position: from-font", 2)]
    [InlineData("text-underline-position: under", 14)]
    [InlineData("text-underline-position: under left", 14)]
    [InlineData("text-underline-position: under; text-underline-offset: 5px", 19)]
    [InlineData("text-underline-position: from-font; text-underline-offset: 3px", 5)]
    public void The_underline_is_where_chrome_draws_it(string style, float top)
        => Assert.Equal(top, UnderlineTop(style), 0.01f);

    [Theory]
    [InlineData("text-underline-position: left")]
    [InlineData("text-underline-position: auto")]
    [InlineData("text-underline-position: under from-font")]
    public void Auto_left_right_and_invalid_values_keep_the_usual_underline(string style)
        => Assert.Equal(UnderlineTop(""), UnderlineTop(style), 0.01f);

    [Fact]
    public void It_is_inherited()
    {
        var ops = Layout("<div style='text-underline-position: under'><p style='font: 60px Helvetica; margin: 0'><u>xxxx</u></p></div>");
        var text = ops.OfType<TextOp>().Single();
        Assert.Equal(14, (ops.OfType<RectOp>().Single(r => r.IsDecoration).Y - text.Baseline) / Px, 0.01f);
    }

    [Fact]
    public void Under_clears_the_descenders()
    {
        var ops = Layout("<p style='font: 60px Helvetica; margin: 0; text-underline-position: under'><u>gypsy</u></p>");
        // Skip-ink leaves no gaps, since no glyph reaches the line.
        Assert.Single(ops.OfType<RectOp>(), r => r.IsDecoration);
    }

    [Fact]
    public void Supports_knows_text_underline_position()
        => Assert.True(CssSupports.Evaluate("(text-underline-position: under)"));
}
