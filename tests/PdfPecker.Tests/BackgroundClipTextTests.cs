namespace PdfPecker.Tests;

/// <summary>background-clip: text, which paints a box's background only inside the glyphs of its text.</summary>
public class BackgroundClipTextTests
{
    private const string Box = "<body style='margin:0'><div style='background:rgb(255,0,0);color:transparent;font:bold 80px sans-serif;width:400px;height:100px;{0}'>HHH</div>";

    private static RenderedImage Render(string style, PdfOptions? options = null)
    {
        options ??= new PdfOptions();
        options.Margins = new PageMargins(0);
        var pdf = HtmlToPdf.Convert(string.Format(System.Globalization.CultureInfo.InvariantCulture, Box, style), options);
        return PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
    }

    /// <summary>The red and the white pixels inside the box.</summary>
    private static (int Red, int White) Count(RenderedImage img)
    {
        int red = 0, white = 0;
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 400; x++)
            {
                int i = (y * img.Width + x) * 4;
                if (img.Rgba[i] > 230 && img.Rgba[i + 1] < 30 && img.Rgba[i + 2] < 30) red++;
                else if (img.Rgba[i] > 230 && img.Rgba[i + 1] > 230 && img.Rgba[i + 2] > 230) white++;
            }
        return (red, white);
    }

    [Theory]
    [InlineData("background-clip:text;")]
    [InlineData("-webkit-background-clip:text;")]
    public void The_background_is_painted_only_inside_the_glyphs(string clip)
    {
        var plain = Count(Render(""));
        Assert.True(plain.Red > 39_000, $"Without the clip the whole box is red: {plain}");
        var clipped = Count(Render(clip));
        // Three bold capitals cover some of the box, not all of it and not none of it.
        Assert.True(clipped.Red > 2_000 && clipped.Red < 20_000, $"Red pixels: {clipped.Red}");
        Assert.True(clipped.White > 20_000, $"White pixels: {clipped.White}");
    }

    [Fact]
    public void The_text_is_extracted_once()
    {
        foreach (var options in new[] { new PdfOptions(), new PdfOptions { Accessibility = PdfAccessibility.PdfUA1, Title = "Clip" } })
        {
            var pdf = HtmlToPdf.Convert(string.Format(System.Globalization.CultureInfo.InvariantCulture, Box, "background-clip:text;"), options);
            Assert.Equal("HHH", PdfText.Extract(pdf).Trim());
        }
    }

    [Fact]
    public void A_box_without_text_paints_no_background()
    {
        var html = "<body style='margin:0'><div style='background-clip:text;background:linear-gradient(red,blue);background-clip:text;width:200px;height:100px'></div>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Margins = new PageMargins(0) });
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
        int i = (50 * img.Width + 100) * 4;
        Assert.Equal((255, 255, 255), (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]));
    }
}
