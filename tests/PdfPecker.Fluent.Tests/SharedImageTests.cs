using FluentExamples;

namespace PdfPecker.Fluent.Tests;

/// <summary>
/// QuestPDF's shared Image and SvgImage objects, and its image compression calls and document settings, which PdfPecker
/// applies with -pdfpecker-image-quality and -pdfpecker-image-max-resolution.
/// </summary>
public sealed class SharedImageTests
{
    private static readonly byte[] Logo = File.ReadAllBytes(Examples.AssetPath("logo.png"));
    private static readonly byte[] Photo = File.ReadAllBytes(Examples.AssetPath("photo.jpg"));

    private const string Square = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='5' height='5'/></svg>";

    [Fact]
    public void A_shared_image_is_stored_once()
    {
        var logo = Image.FromBinaryData(Logo);
        string html = Html.Of(c => c.Column(col => { col.Item().Image(logo); col.Item().Image(logo).FitArea(); }));
        Assert.Equal(2, html.Split("src=\"fluent:img/0\"").Length - 1);
    }

    [Fact]
    public void A_shared_image_is_stored_once_across_page_sets_and_documents()
    {
        using var logo = Image.FromBinaryData(Logo);
        var one = Document.Create(d => d.Page(p => p.Content().Image(logo)));
        var two = Document.Create(d => { d.Page(p => p.Header().Image(logo)); d.Page(p => p.Content().Image(logo)); });
        var pdf = Document.Merge(one, two).GeneratePdf();
        Assert.Single(PdfImages.Extract(pdf), i => !i.IsMask);
    }

    [Fact]
    public void Images_load_from_a_file_a_stream_and_bytes()
    {
        string path = Examples.AssetPath("logo.png");
        Assert.Equal(Logo, Image.FromFile(path).Data);
        using var stream = File.OpenRead(path);
        Assert.Equal(Logo, Image.FromStream(stream).Data);
        Assert.Same(Logo, Image.FromBinaryData(Logo).Data);
        Assert.Throws<ArgumentNullException>(() => Image.FromBinaryData(null!));
        Assert.Throws<ArgumentException>(() => Image.FromFile(""));
        Assert.Throws<ArgumentNullException>(() => Html.Of(c => c.Image((Image)null!)));
    }

    [Fact]
    public void An_svg_image_is_drawn_as_the_svg_markup_is()
    {
        var svg = SvgImage.FromText(Square);
        Assert.Equal(Html.Of(c => c.Svg(Square)), Html.Of(c => c.Svg(svg)));
        Assert.Throws<ArgumentException>(() => SvgImage.FromText(""));
    }

    [Fact]
    public void An_svg_file_path_is_read_as_in_QuestPDF()
    {
        string path = Path.Combine(Path.GetTempPath(), "fluent-shared-image-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(path, Square);
        try
        {
            Assert.Equal(Html.Of(c => c.Svg(Square)), Html.Of(c => c.Svg(path)));
            Assert.Equal(Html.Of(c => c.Svg(Square)), Html.Of(c => c.Svg(SvgImage.FromFile(path))));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_compression_calls_write_the_properties_on_the_image()
    {
        var document = Html.Page(c => c.Image(Logo).WithCompressionQuality(ImageCompressionQuality.Medium).WithRasterDpi(150));
        Assert.Contains("-pdfpecker-image-quality:50;-pdfpecker-image-max-resolution:150dpi", Html.Css(document), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ImageCompressionQuality.Best, 100)]
    [InlineData(ImageCompressionQuality.VeryHigh, 90)]
    [InlineData(ImageCompressionQuality.High, 75)]
    [InlineData(ImageCompressionQuality.Medium, 50)]
    [InlineData(ImageCompressionQuality.Low, 25)]
    [InlineData(ImageCompressionQuality.VeryLow, 10)]
    public void Each_quality_is_QuestPDFs_jpeg_quality(ImageCompressionQuality quality, int jpeg)
        => Assert.Contains($"-pdfpecker-image-quality:{jpeg}}}", Html.Css(Html.Page(c => c.Image(Logo).WithCompressionQuality(quality))), StringComparison.Ordinal);

    [Fact]
    public void Use_original_image_turns_the_document_settings_off()
    {
        var document = Html.Page(c => c.Image(Logo).WithCompressionQuality(ImageCompressionQuality.Low).UseOriginalImage());
        string css = Html.Css(document);
        Assert.Contains("-pdfpecker-image-quality:none;-pdfpecker-image-max-resolution:none", css, StringComparison.Ordinal);
        Assert.DoesNotContain("-pdfpecker-image-quality:25", css, StringComparison.Ordinal);
        Assert.DoesNotContain("-pdfpecker-image", Html.Css(Html.Page(c => c.Image(Logo).UseOriginalImage(false))), StringComparison.Ordinal);
    }

    [Fact]
    public void Document_settings_are_a_rule_on_the_root()
    {
        var document = Html.Page(c => c.Image(Logo)).WithSettings(new DocumentSettings { ImageCompressionQuality = ImageCompressionQuality.High, ImageRasterDpi = 288 });
        Assert.StartsWith("html{-pdfpecker-image-quality:75;-pdfpecker-image-max-resolution:288dpi;}", UserCss(document), StringComparison.Ordinal);
        Assert.DoesNotContain("-pdfpecker-image", Html.Css(Html.Page(c => c.Image(Logo))), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_values_throw_argument_exceptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Image(Logo).WithRasterDpi(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Image(Logo).WithCompressionQuality((ImageCompressionQuality)9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentSettings { ImageRasterDpi = -1 });
    }

    [Fact]
    public void A_photo_is_downsampled_and_recompressed_in_the_pdf()
    {
        var original = Single(Html.Page(c => c.Width(100).Image(Photo)).GeneratePdf());
        var smaller = Single(Html.Page(c => c.Width(100).Image(Photo).WithCompressionQuality(ImageCompressionQuality.Low).WithRasterDpi(72)).GeneratePdf());
        Assert.Equal(PdfImageFormat.Jpeg, smaller.Format);
        Assert.Equal(100, smaller.Width);
        Assert.True(smaller.Data.Length < original.Data.Length, $"{smaller.Data.Length} < {original.Data.Length}");
    }

    [Fact]
    public void Document_settings_reach_images_in_the_pdf_and_use_original_image_keeps_one()
    {
        var settings = new DocumentSettings { ImageRasterDpi = 72 };
        var pdf = Document.Create(d => d.Page(p => p.Content().Column(col =>
        {
            col.Item().Width(100).Image(Photo);
            col.Item().Width(100).Image(Image.FromBinaryData(Photo)).UseOriginalImage();
        }))).WithSettings(settings).GeneratePdf();
        var widths = PdfImages.Extract(pdf).Where(i => !i.IsMask).Select(i => i.Width).Order().ToArray();
        int full = Single(Html.Page(c => c.Image(Photo)).GeneratePdf()).Width;
        Assert.Equal([100, full], widths);
    }

    private static PdfExtractedImage Single(byte[] pdf) => Assert.Single(PdfImages.Extract(pdf), i => !i.IsMask);

    /// <summary>The style sheet after the fluent package's own base rules.</summary>
    private static string UserCss(IDocument document)
    {
        string css = Html.Css(document);
        return css[css.IndexOf("html{-pdfpecker", StringComparison.Ordinal)..];
    }
}
