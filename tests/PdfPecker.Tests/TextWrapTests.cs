using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// text-wrap: balance and pretty, and the text-wrap, text-wrap-mode and text-wrap-style properties. The expected lines
/// are Chrome's, printed with Liberation Sans, whose widths are Helvetica's.
/// </summary>
public class TextWrapTests
{
    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return engine.Ops.ToList();
    }

    /// <summary>The text of each line, top to bottom.</summary>
    private static string[] Lines(string html) => Layout(html).OfType<TextOp>()
        .GroupBy(t => MathF.Round(t.Baseline, 1))
        .OrderBy(g => g.Key)
        .Select(g => string.Join(" ", g.OrderBy(t => t.X).Select(t => t.Text.Trim())))
        .ToArray();

    private static string[] Paragraph(string style, string text) => Lines($"<p style='font-family: Arial; margin: 0; {style}'>{text}</p>");

    private const string Library = "PdfPecker has its own HTML parser, CSS engine, layout engine and PDF writer, so the NuGet package depends only on the base class library.";

    [Fact]
    public void Balance_evens_out_the_lines()
    {
        Assert.Equal(new[] { "PdfPecker has its own HTML parser,", "CSS engine, layout engine and", "PDF writer, so the NuGet package", "depends only on the base class library." },
            Paragraph("width: 300px; font-size: 14px; text-wrap: balance", Library));
        Assert.Equal(new[] { "PdfPecker has its own HTML parser, CSS", "engine, layout engine and PDF writer, so the", "NuGet package depends only on the base class", "library." },
            Paragraph("width: 300px; font-size: 14px", Library));
    }

    [Fact]
    public void Balance_chooses_between_few_lines_and_short_words()
    {
        Assert.Equal(new[] { "Fast HTML", "to PDF for", "templates", "you control" },
            Paragraph("width: 148px; font-size: 24px; text-wrap: balance", "Fast HTML to PDF for templates you control"));
        Assert.Equal(new[] { "Balanced", "headings", "read", "better than", "ragged", "ones" },
            Paragraph("width: 118px; font-size: 24px; text-wrap: balance", "Balanced headings read better than ragged ones"));
    }

    [Fact]
    public void Balance_with_two_break_opportunities_narrows_the_lines()
    {
        // Too few breaks to choose from: the lines are broken at the narrowest width that keeps them to two.
        Assert.Equal(new[] { "Lightweight", "PDF output" }, Paragraph("width: 178px; font-size: 24px; text-wrap: balance", "Lightweight PDF output"));
        Assert.Equal(new[] { "Lightweight PDF", "output" }, Paragraph("width: 178px; font-size: 24px", "Lightweight PDF output"));
    }

    [Fact]
    public void Balance_works_with_overflow_wrap_until_a_word_is_too_wide()
    {
        Assert.Equal(new[] { "PdfPecker has its own HTML parser,", "CSS engine, layout engine and", "PDF writer, so the NuGet package", "depends only on the base class library." },
            Paragraph("width: 300px; font-size: 14px; overflow-wrap: break-word; text-wrap: balance", Library));
        const string url = "See https://pdfpecker.net/docs/html-and-css/text-wrapping for details";
        Assert.Equal(Paragraph("width: 120px; font-size: 14px; overflow-wrap: break-word", url),
            Paragraph("width: 120px; font-size: 14px; overflow-wrap: break-word; text-wrap: balance", url));
    }

    [Fact]
    public void Balanced_lines_are_aligned_in_the_full_width()
    {
        var ops = Layout("<p style='font-family: Arial; margin: 0; width: 400px; font-size: 24px; text-align: right; text-wrap: balance'>Lightweight PDF output for every page</p>");
        foreach (var t in ops.OfType<TextOp>())
            Assert.Equal(300, t.X + t.Font.Measure(t.Text.TrimEnd(), t.Size), 1f);
    }

    [Fact]
    public void Balance_stops_at_the_first_forced_break()
    {
        Assert.Equal(new[] { "A short paragraph of balanced", "text, with a forced break here", "and a second part after the break", "that is not balanced at all in", "Chrome." },
            Paragraph("width: 220px; font-size: 14px; text-wrap: balance",
                "A short paragraph of balanced text, with a forced break here<br>and a second part after the break that is not balanced at all in Chrome."));
    }

    [Fact]
    public void Balance_leaves_paragraphs_of_more_than_six_lines()
    {
        string text = string.Concat(Enumerable.Repeat("Long paragraphs keep their greedy lines. ", 8));
        Assert.Equal(Paragraph("width: 200px; font-size: 14px", text), Paragraph("width: 200px; font-size: 14px; text-wrap: balance", text));
    }

    [Fact]
    public void Pretty_keeps_a_word_from_ending_alone_on_the_last_line()
    {
        const string text = "Templates you control are converted to PDF without a browser, quickly and with the same output every time.";
        Assert.Equal(new[] { "Templates you", "control are", "converted to PDF", "without a browser,", "quickly and with the", "same output every", "time." },
            Paragraph("width: 118px; font-size: 13px", text));
        Assert.Equal(new[] { "Templates you", "control are", "converted to PDF", "without a browser,", "quickly and with", "the same output", "every time." },
            Paragraph("width: 118px; font-size: 13px; text-wrap: pretty", text));
    }

    [Fact]
    public void Pretty_leaves_a_long_enough_last_line()
    {
        const string text = "The quick brown fox jumps over the lazy dog and then runs away into the forest to hide.";
        Assert.Equal(Paragraph("width: 260px; font-size: 13px", text), Paragraph("width: 260px; font-size: 13px; text-wrap: pretty", text));
    }

    [Fact]
    public void Pretty_inherits_and_applies_to_each_paragraph()
    {
        var lines = Lines("<div style='text-wrap: pretty; font-family: Arial; font-size: 13px; width: 136px'>"
            + "<p style='margin: 0'>Each page of the invoice shows the total, the due date and the bank details at the bottom.</p></div>");
        Assert.Equal(new[] { "Each page of the", "invoice shows the", "total, the due date and", "the bank details at", "the bottom." }, lines);
    }

    [Fact]
    public void Text_wrap_shorthand_sets_the_mode_and_the_style()
    {
        const string text = "white-space nowrap then text-wrap balance wraps again.";
        Assert.Single(Paragraph("width: 200px; font-size: 14px; white-space: nowrap", text));
        Assert.Equal(new[] { "white-space nowrap then text-", "wrap balance wraps again." }, Paragraph("width: 200px; font-size: 14px; white-space: nowrap; text-wrap: balance", text));
        Assert.Single(Paragraph("width: 200px; font-size: 14px; text-wrap: nowrap", text));
        Assert.Single(Paragraph("width: 200px; font-size: 14px; text-wrap-mode: nowrap", text));
        Assert.Equal(2, Paragraph("width: 200px; font-size: 14px; text-wrap: nowrap; text-wrap-mode: wrap", text).Length);
    }

    [Fact]
    public void Text_wrap_mode_keeps_how_white_space_collapses()
    {
        var pre = Paragraph("width: 200px; font-size: 14px; white-space: pre; text-wrap-mode: wrap", "keeps    its spaces but wraps the long line again");
        Assert.True(pre.Length > 1);
        Assert.Contains("keeps    its", pre[0], StringComparison.Ordinal);
        var preLine = Paragraph("width: 100px; font-size: 14px; white-space: pre-line; text-wrap: nowrap", "spaces   collapse\nnewlines stay and long lines do not wrap");
        Assert.Equal(new[] { "spaces collapse", "newlines stay and long lines do not wrap" }, preLine);
    }

    [Fact]
    public void Invalid_text_wrap_values_are_ignored()
    {
        Assert.Single(Paragraph("width: 200px; font-size: 14px; text-wrap: nowrap; text-wrap: balance pretty", "an invalid value keeps the text on one long line"));
        Assert.Single(Paragraph("width: 200px; font-size: 14px; text-wrap: nowrap; text-wrap: wrap nowrap", "an invalid value keeps the text on one long line"));
    }

    [Fact]
    public void Text_wrap_is_supported_in_supports_queries()
    {
        Assert.True(CssSupports.Evaluate("(text-wrap: balance)"));
        Assert.True(CssSupports.Evaluate("(text-wrap-style: pretty)"));
        Assert.True(CssSupports.Evaluate("(text-wrap-mode: nowrap)"));
    }
}
