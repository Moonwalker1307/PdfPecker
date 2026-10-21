using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-decoration-skip-ink: underlines and overlines leave gaps where glyphs cross them, as in Chrome, and
/// line-throughs, CJK text and the value none do not.
/// </summary>
public class TextDecorationSkipInkTests
{
    private const float PageHeight = 800;

    // A subset of Noto Sans SC (SIL Open Font License, see Fixtures/fonts) for the ideographs.
    private static readonly FontCollection Cjk = new FontCollection().Add(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansSC-Subset.woff2")), "CJK");

    private static List<DrawOp> Layout(string html, float width = 400)
    {
        var doc = HtmlParser.Parse(html);
        var fonts = FontResolver.Create([], Cjk, _ => null);
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block, Fonts = fonts });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return engine.Ops;
    }

    /// <summary>The pieces of the decoration lines, left to right.</summary>
    private static List<RectOp> Lines(string html) => Layout(html).OfType<RectOp>().Where(r => r.IsDecoration).OrderBy(r => r.Y).ThenBy(r => r.X).ToList();

    [Fact]
    public void An_underline_leaves_gaps_around_descenders()
    {
        var pieces = Lines("<p style='font-size: 20pt; margin: 0'><u>happy typography</u></p>");
        var text = Layout("<p style='font-size: 20pt; margin: 0'><u>happy typography</u></p>").OfType<TextOp>().Single();
        // p, p, y, y, p, g, p and y each cut a gap; some neighbours share one.
        Assert.True(pieces.Count >= 5, $"{pieces.Count} pieces");
        Assert.All(pieces, p => Assert.Equal(pieces[0].Y, p.Y));
        // The line still starts at the text and ends at its end, since h and the last y's right side leave ink-free ends.
        Assert.Equal(text.X, pieces[0].X, 2);
        Assert.True(pieces.Zip(pieces.Skip(1), (a, b) => b.X - (a.X + a.W)).All(gap => gap > 0));
    }

    [Fact]
    public void Text_without_descenders_keeps_one_line()
    {
        var pieces = Lines("<p style='font-size: 20pt; margin: 0'><u>alone at home</u></p>");
        Assert.Single(pieces);
    }

    [Fact]
    public void None_draws_the_whole_line()
    {
        var pieces = Lines("<p style='font-size: 20pt; margin: 0'><u style='text-decoration-skip-ink: none'>happy typography</u></p>");
        Assert.Single(pieces);
    }

    [Fact]
    public void Line_through_never_skips_ink()
    {
        var pieces = Lines("<p style='font-size: 20pt; margin: 0'><s>happy typography</s></p>");
        Assert.Single(pieces);
    }

    [Fact]
    public void An_overline_leaves_gaps_around_accents()
    {
        // The overline sits at the top of the font, above plain ascenders, so only the ring and the accents reach it.
        Assert.Single(Lines("<p style='font-size: 20pt; margin: 0'><span style='text-decoration: overline'>kill the bill</span></p>"));
        var pieces = Lines("<p style='font-size: 20pt; margin: 0'><span style='text-decoration: overline'>ÅNGSTRÖM ÉCLAIR</span></p>");
        Assert.True(pieces.Count > 1);
    }

    [Fact]
    public void Ideographs_cut_gaps_only_with_all()
    {
        // The underline is moved up through the ideographs.
        const string style = "font: 20pt CJK; margin: 0; text-underline-offset: -0.4em";
        Assert.Single(Lines($"<p style='{style}'><u>中文排版测试</u></p>"));
        Assert.True(Lines($"<p style='{style}'><u style='text-decoration-skip-ink: all'>中文排版测试</u></p>").Count > 1);
        Assert.True(Lines($"<p style='{style}'><u>Latin</u></p>").Count > 1);
    }

    [Fact]
    public void Double_underlines_leave_the_same_gaps_in_both_lines()
    {
        var pieces = Lines("<p style='font-size: 20pt; margin: 0'><u style='text-decoration-style: double'>happy typography</u></p>");
        var rows = pieces.GroupBy(p => p.Y).ToList();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Count() > 1);
        Assert.Equal(rows[0].Select(p => (p.X, p.W)), rows[1].Select(p => (p.X, p.W)));
    }

    [Fact]
    public void Skip_ink_is_inherited_and_supported()
    {
        Assert.Single(Lines("<div style='text-decoration-skip-ink: none'><p style='font-size: 20pt; margin: 0'><u>happy typography</u></p></div>"));
        Assert.True(CssSupports.Evaluate("(text-decoration-skip-ink: auto)"));
        Assert.True(CssSupports.Evaluate("(text-decoration-skip-ink: all)"));
    }
}
