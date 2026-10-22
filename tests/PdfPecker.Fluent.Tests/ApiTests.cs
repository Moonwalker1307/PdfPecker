using FluentExamples;

using System.Reflection;

namespace PdfPecker.Fluent.Tests;

/// <summary>Argument checks, colours, units, metadata and options.</summary>
public sealed class ApiTests
{
    [Theory]
    [InlineData("#abc", "#aabbcc")]
    [InlineData("#abcd", "#aabbccdd")]
    [InlineData("#A1B2C3", "#a1b2c3")]
    [InlineData("a1b2c380", "#a1b2c380")]
    [InlineData("#a1b2c3ff", "#a1b2c3")]
    public void Colors_parse_css_hex(string input, string expected)
        => Assert.Equal(expected, Color.FromHex(input).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#ggg")]
    [InlineData("red; background:url(x)")]
    [InlineData("#fff;}")]
    public void Anything_else_is_not_a_colour(string input)
        => Assert.Throws<ArgumentException>(() => Color.FromHex(input));

    [Fact]
    public void The_palette_uses_material_values()
    {
        Assert.Equal("#e0e0e0", Colors.Grey.Lighten2.ToString());
        Assert.Equal("#2196f3", Colors.Blue.Medium.ToString());
        Assert.Equal("#00000000", Colors.Transparent.ToString());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    [InlineData(2_000_000f)]
    public void Lengths_must_be_finite_and_in_range(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Padding(value)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Width(value)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Row(r => r.RelativeItem(value))));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextStyle.Default.FontSize(value));
    }

    [Fact]
    public void Units_convert_to_points()
        => Assert.Contains("{padding-top:72pt;padding-right:72pt;padding-bottom:72pt;padding-left:72pt}",
            Html.Css(Html.Page(c => c.Padding(25.4f, Unit.Millimetre).Text("x"))));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("relative/path")]
    [InlineData("data:text/html,x")]
    public void Links_only_go_to_web_and_mail_addresses(string url)
    {
        Assert.Throws<ArgumentException>(() => Html.Of(c => c.Hyperlink(url)));
        Assert.Throws<ArgumentException>(() => Html.Of(c => c.Text(t => t.Hyperlink("x", url))));
    }

    [Fact]
    public void Page_sides_must_be_reasonable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Document.Create(d => d.Page(p => p.Size(0, 100))).GenerateHtml());
        Assert.Throws<ArgumentOutOfRangeException>(() => Document.Create(d => d.Page(p => p.Size(100, 100, Unit.Meter))).GenerateHtml());
    }

    [Fact]
    public void Raw_css_cannot_end_the_style_sheet()
        => Assert.Throws<ArgumentException>(() => Document.Create(d => d.Css("p{}</style><p>x")).GenerateHtml());

    [Fact]
    public void Metadata_reaches_the_html()
    {
        string html = Document.Create(d => d.Page(p => p.Content().Text("x")))
            .WithMetadata(new DocumentMetadata { Title = "A & B", Language = "da" }).GenerateHtml();
        Assert.Contains("<html lang=\"da\">", html);
        Assert.Contains("<title>A &amp; B</title>", html);
    }

    [Fact]
    public void Pdf_ua_and_several_page_sets_do_not_mix()
        => Assert.Throws<InvalidOperationException>(() => Document.Create(d =>
        {
            d.Page(p => p.Content().Text("a"));
            d.Page(p => p.Content().Text("b"));
        }).WithSettings(new DocumentSettings { PdfUA = true }).GeneratePdf());

    [Fact]
    public void Pdf_ua_settings_make_a_tagged_pdf()
    {
        byte[] pdf = Html.Page(c => c.Heading(1).Text("Title"))
            .GeneratePdf(new PdfOptions { Title = "Title", Language = "en", Accessibility = PdfAccessibility.PdfUA1 });
        Assert.Contains("/StructTreeRoot", System.Text.Encoding.Latin1.GetString(pdf));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Linearize_writes_a_linearized_pdf_also_from_several_page_sets(int pageSets)
    {
        byte[] pdf = Document.Create(d =>
        {
            for (int i = 0; i < pageSets; i++) d.Page(p => p.Content().Text("Page set " + i));
        }).GeneratePdf(new PdfOptions { Linearize = true });
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Matches(@"^%PDF-1\.\d\n%[^\n]*\n\d+ 0 obj\n<< /Linearized 1 /L " + pdf.Length + " ", text);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(text, "/Linearized"));
    }

    [Fact]
    public void Generate_leaves_the_callers_options_unchanged()
    {
        Func<string, byte[]?> resolver = _ => null;
        var options = new PdfOptions { ResourceResolver = resolver };
        Html.Page(c => c.Text("x")).WithMetadata(new DocumentMetadata { Title = "T" }).GeneratePdf(options);
        Assert.Same(resolver, options.ResourceResolver);
        Assert.Null(options.Title);
        Assert.Equal(PageSize.A4.Width, options.PageSize.Width);
    }

    /// <summary>
    /// The options copy names every property by hand. When PdfPecker adds a setting, this test fails until the copy
    /// carries it too; otherwise the new setting would be silently dropped.
    /// </summary>
    [Fact]
    public void The_options_copy_covers_every_setting()
    {
        string[] copied =
        [
            "PageSize", "Margins", "Scale", "ShrinkToFit", "TruncateBandsToWholePoints", "IgnoreCssPageRules",
            "BaseDirectory", "ResourceResolver", "AllowFileAccess", "Fonts", "UseSystemFonts", "MissingFont", "MissingCharacter",
            "UserStyleSheet", "Bookmarks", "FormFields", "MaxPages", "Compress", "CompressionLevel", "Title", "Author",
            "Subject", "Keywords", "Creator", "Producer", "CustomProperties", "CreationDate", "FooterText", "HeaderText",
            "Watermark", "Security", "Conformance", "Accessibility", "Language", "Hyphenation", "EInvoice", "Attachments",
            "EmbedStandardFonts", "Signature", "Linearize",
        ];
        var settable = typeof(PdfOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite).Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(copied.Order(StringComparer.Ordinal), settable);
    }

    [Fact]
    public void Generation_is_repeatable()
    {
        var document = Examples.All["invoice"]();
        Assert.Equal(document.GeneratePdf(), document.GeneratePdf());
    }

    [Fact]
    public void Images_render_one_picture_per_page()
        => Assert.Single(Html.Page(c => c.Text("x")).GenerateImages(new ImageOptions { Dpi = 20 }));

    [Fact]
    public async Task Async_generation_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Html.Page(c => c.Text("x")).GeneratePdfAsync(cancellationToken: cts.Token));
    }
}
