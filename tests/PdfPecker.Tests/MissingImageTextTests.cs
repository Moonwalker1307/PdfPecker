using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>An img whose picture is missing shows its alt text, as Chrome prints it.</summary>
public class MissingImageTextTests
{
    private const string Head = "<!doctype html><style>@page{margin:0} body{margin:0;font:12px Arial}</style>";

    private static IReadOnlyList<PdfTextLine> Lines(string body, PdfOptions? options = null)
        => PdfText.ExtractPages(HtmlToPdf.Convert(Head + body, options))[0].Lines;

    private static string Text(string body, PdfOptions? options = null)
        => string.Join("|", Lines(body, options).Select(l => l.Text));

    [Fact]
    public void A_missing_image_prints_its_alt_text_in_the_line()
        => Assert.Equal("A Missing picture B", Text("<p>A <img src='missing.png' alt='Missing picture'> B</p>"));

    [Fact]
    public void An_image_without_a_src_prints_its_alt_text()
        => Assert.Equal("A No source B", Text("<p>A <img alt='No source'> B</p>"));

    [Fact]
    public void An_image_that_cannot_be_decoded_prints_its_alt_text()
        => Assert.Equal("A Broken B", Text("<p>A <img src='data:image/png;base64,AAAA' alt='Broken'> B</p>"));

    [Fact]
    public void A_resolver_that_finds_nothing_leaves_the_alt_text()
    {
        var options = new PdfOptions { ResourceResolver = _ => null };
        Assert.Equal("A Not found B", Text("<p>A <img src='https://example.com/x.png' alt='Not found'> B</p>", options));
    }

    [Theory]
    [InlineData("<img src='missing.png'>")]
    [InlineData("<img src='missing.png' alt=''>")]
    [InlineData("<img src='missing.png' alt='   '>")]
    [InlineData("<img src='missing.png' width='100' height='40'>")]
    [InlineData("<img src='missing.png' alt='' width='100' height='40'>")]
    public void A_missing_image_without_alt_text_prints_nothing(string img)
        => Assert.Equal("A B", Text("<p>A " + img + " B</p>"));

    [Fact]
    public void An_image_that_loads_does_not_print_its_alt_text()
    {
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.RgbaPng());
        Assert.Equal("A B", Text($"<p>A <img src='{png}' alt='Logo'> B</p>"));
    }

    [Theory]
    [InlineData("width='100' height='40'")]
    [InlineData("style='width:100px;height:40px'")]
    [InlineData("width='10' height='10'")]
    [InlineData("width='100'")]
    public void An_inline_alt_text_takes_the_room_its_text_needs_whatever_size_the_image_has(string size)
    {
        var line = Assert.Single(Lines($"<p>A <img src='missing.png' alt='Missing picture' {size}> B</p>"));
        Assert.Equal("A Missing picture B", line.Text);
        Assert.True(line.Height < 20, $"line is {line.Height} pt high");
    }

    [Fact]
    public void A_long_alt_text_wraps_inside_its_box()
    {
        var alt = string.Join(" ", Enumerable.Repeat("alternative", 40));
        var lines = Lines($"<p>A <img src='missing.png' alt='{alt}'> B</p>");
        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        Assert.Equal(alt, string.Join(" ", lines.Skip(1).Take(lines.Count - 2).Select(l => l.Text)).Trim());
    }

    [Fact]
    public void A_block_image_with_a_size_keeps_it_around_its_alt_text()
    {
        var lines = Lines("<img src='missing.png' alt='Boxed' width='100' height='40' style='display:block'><p>After</p>");
        Assert.Equal(new[] { "Boxed", "After" }, lines.Select(l => l.Text));
        // The 40 px tall box (30 pt) is below the alt text's own line.
        Assert.True(lines[1].Y - lines[0].Y > 25, $"{lines[1].Y - lines[0].Y} pt between the lines");
    }

    [Fact]
    public void Alt_text_takes_the_text_style_around_it_and_the_images_own_rules()
    {
        var pages = PdfText.ExtractPages(HtmlToPdf.Convert(Head + "<p style='font-size:20px'>A <img src='missing.png' alt='Big' style='font-size:30px'></p>"));
        var words = pages[0].Lines.SelectMany(l => l.Words).ToList();
        Assert.Equal(22.5, words.Single(w => w.Text == "Big").FontSize, 1);
        Assert.Equal(15, words.Single(w => w.Text == "A").FontSize, 1);
    }

    [Fact]
    public void An_image_with_alt_text_in_Markdown_prints_it()
    {
        var pdf = MarkdownToPdf.Convert("Before ![The logo](missing.png) after");
        Assert.Equal("Before The logo after", PdfText.Extract(pdf).Trim());
    }

    [Fact]
    public void Alt_text_of_a_missing_image_is_marked_content_in_a_tagged_document()
    {
        var options = new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 };
        var pdf = PdfTestHelpers.ConvertUncompressed(Head + "<title>T</title><p>A <img src='missing.png' alt='Missing picture'> B</p>", options);
        var content = PdfTestHelpers.AllContent(pdf);
        Assert.Equal("A Missing picture B", PdfText.Extract(pdf).Trim());
        // All the page's text is inside marked content, as the PDF/UA profile asks.
        Assert.Equal(Regex.Matches(content, @"\bBT\b").Count, Regex.Matches(content, @"\bBDC\b").Count);
    }
}
