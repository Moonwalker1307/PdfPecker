namespace PdfPecker.Tests;

/// <summary>-webkit-text-stroke and paint-order: outlined text drawn with the PDF stroke text rendering modes.</summary>
public class TextStrokeTests
{
    private const string Page = "<body style='margin:0'><p style='margin:0;font:bold 80px sans-serif;color:{0};{1}'>HHH</p>";

    private static RenderedImage Render(string color, string style)
    {
        var options = new PdfOptions { Margins = new PageMargins(0) };
        var pdf = HtmlToPdf.Convert(string.Format(System.Globalization.CultureInfo.InvariantCulture, Page, color, style), options);
        return PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
    }

    private static int Count(RenderedImage img, Func<int, int, int, bool> match)
    {
        int n = 0;
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 300; x++)
            {
                int i = (y * img.Width + x) * 4;
                if (match(img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2])) n++;
            }
        return n;
    }

    private static bool Blue(int r, int g, int b) => b > 200 && r < 60 && g < 60;
    private static bool Red(int r, int g, int b) => r > 200 && g < 60 && b < 60;

    [Theory]
    [InlineData("-webkit-text-stroke:2px blue;")]
    [InlineData("-webkit-text-stroke:blue 2px;")]
    [InlineData("-webkit-text-stroke-width:2px;-webkit-text-stroke-color:blue;")]
    public void White_text_gets_a_blue_outline(string style)
    {
        Assert.Equal(0, Count(Render("white", ""), Blue));
        Assert.True(Count(Render("white", style), Blue) > 500);
    }

    [Fact]
    public void The_stroke_is_currentcolor_by_default()
    {
        var plain = Count(Render("red", ""), Red);
        var stroked = Count(Render("red", "-webkit-text-stroke:3px;"), Red);
        Assert.True(stroked > plain, $"{stroked} <= {plain}");
    }

    [Fact]
    public void The_stroke_is_inherited()
    {
        var options = new PdfOptions { Margins = new PageMargins(0) };
        var pdf = HtmlToPdf.Convert("<body style='margin:0;-webkit-text-stroke:2px blue'><p style='margin:0;font:bold 80px sans-serif;color:white'><b>HHH</b></p>", options);
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
        Assert.True(Count(img, Blue) > 500);
    }

    [Fact]
    public void Paint_order_stroke_lets_the_fill_cover_the_inner_half()
    {
        const string stroke = "-webkit-text-stroke:12px blue;";
        var over = Count(Render("red", stroke), Red);
        var under = Count(Render("red", stroke + "paint-order:stroke;"), Red);
        Assert.True(under > over + 1_000, $"Fill pixels with paint-order stroke {under}, default {over}");
    }
}
