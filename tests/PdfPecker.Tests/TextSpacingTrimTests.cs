using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-spacing-trim: fullwidth CJK punctuation loses the blank half of its em next to other punctuation and at line
/// edges, as Chrome trims it with the font's halt feature.
/// </summary>
public class TextSpacingTrimTests
{
    private const float PageHeight = 800;

    // A subset of Noto Sans SC (SIL Open Font License, see Fixtures/fonts), which has the halt feature. Its ideographs and
    // punctuation are all one em wide, and halt takes half an em off the punctuation.
    private static readonly FontCollection Cjk = new FontCollection().Add(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansSC-Subset.woff2")), "CJK");

    private static List<TextOp> Layout(string html, float width = 400)
    {
        var doc = HtmlParser.Parse(html);
        var fonts = FontResolver.Create([], Cjk, _ => null);
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block, Fonts = fonts });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return engine.Ops.OfType<TextOp>().ToList();
    }

    /// <summary>The text ops of each line, top to bottom, left to right.</summary>
    private static List<List<TextOp>> Lines(string html, float width = 400) => Layout(html, width)
        .GroupBy(t => MathF.Round(t.Baseline, 1))
        .OrderBy(g => g.Key)
        .Select(g => g.OrderBy(t => t.X).ToList())
        .ToList();

    private static string P(string text, string style = "") => $"<p style='font: 10pt CJK; margin: 0; {style}'>{text}</p>";

    private static string Text(List<TextOp> line) => string.Concat(line.Select(t => t.Text));

    [Fact]
    public void Punctuation_next_to_punctuation_loses_its_blank_half()
    {
        var line = Assert.Single(Lines(P("（规则）。例如：「这是」")));
        Assert.Equal("（规则）。例如：「这是」", Text(line));
        // The closing bracket before the full stop ends its run half an em early, and the opening corner bracket after
        // the colon is drawn half an em to the left.
        Assert.Equal(["（规则）", "。例如：", "「这是」"], line.Select(t => t.Text));
        Assert.Equal(0, line[0].X, 2);
        Assert.Equal(35, line[1].X, 2);
        Assert.Equal(70, line[2].X, 2);
    }

    [Fact]
    public void Space_all_keeps_every_character_a_full_em()
    {
        var line = Assert.Single(Lines(P("（规则）。例如：「这是」", "text-spacing-trim: space-all")));
        Assert.Equal("（规则）。例如：「这是」", Assert.Single(line).Text);
    }

    [Fact]
    public void Font_features_that_set_punctuation_widths_turn_trimming_off()
    {
        var line = Assert.Single(Lines(P("（规则）。", "font-feature-settings: \"palt\"")));
        Assert.Single(line);
    }

    [Fact]
    public void A_closing_bracket_at_the_end_of_a_line_is_trimmed_when_that_makes_it_fit()
    {
        // Nine ideographs and a closing bracket are 100pt wide; trimmed, 95pt.
        var lines = Lines(P("中文文本在任意两个」汉字"), 96);
        Assert.Equal(["中文文本在任意两个」", "汉字"], lines.Select(Text));
        lines = Lines(P("中文文本在任意两个」汉字", "text-spacing-trim: space-all"), 96);
        Assert.Equal(["中文文本在任意两", "个」汉字"], lines.Select(Text));
    }

    [Fact]
    public void A_comma_at_the_end_of_a_line_keeps_its_space_as_in_Chrome()
    {
        var lines = Lines(P("中文文本在任意两个，汉字"), 96);
        Assert.Equal(["中文文本在任意两", "个，汉字"], lines.Select(Text));
    }

    [Fact]
    public void Normal_leaves_an_opening_bracket_that_starts_a_line()
    {
        var lines = Lines(P("「中文文本在」「任意两个汉字"), 70.5f);
        Assert.Equal(["「中文文本在」", "「任意两个汉字"], lines.Select(Text));
        Assert.Equal(0, lines[0][0].X, 2);
        // The second bracket had its left half trimmed after the closing one, but starting a line it has no neighbour.
        Assert.Equal(0, lines[1][0].X, 2);
    }

    [Fact]
    public void Space_first_trims_the_brackets_that_start_wrapped_lines()
    {
        var lines = Lines(P("「中文文本在任意两个「汉字", "text-spacing-trim: space-first"), 50);
        Assert.Equal(["「中文文本", "在任意两个", "「汉字"], lines.Select(Text));
        Assert.Equal(0, lines[0][0].X, 2);
        Assert.Equal(-5, lines[2][0].X, 2);
    }

    [Fact]
    public void Trim_start_also_trims_the_first_line()
    {
        var lines = Lines(P("「中文文本在任意两个", "text-spacing-trim: trim-start"), 55.5f);
        Assert.Equal(-5, lines[0][0].X, 2);
        // The bracket's blank half went, so five characters fit after it instead of four.
        Assert.Equal("「中文文本在", Text(lines[0]));
    }

    [Fact]
    public void The_property_is_inherited_and_known_to_supports()
    {
        var line = Assert.Single(Lines($"<div style='text-spacing-trim: space-all'>{P("（规则）。")}</div>"));
        Assert.Single(line);
        Assert.Contains("ok", HtmlToPdfText("<style>@supports (text-spacing-trim: trim-start) { p::after { content: 'ok' } }</style><p></p>"));
    }

    private static string HtmlToPdfText(string html) => PdfText.Extract(HtmlToPdf.Convert(html));
}
