namespace PdfPecker.Tests;

/// <summary>
/// Sliders, colour wells and block-level selects and buttons, drawn and sized as Chrome prints them: a slider is a
/// rounded track filled up to a round thumb at its value, a colour well a swatch of its colour, and a select or a
/// button with display: block keeps the width of its content instead of filling the line.
/// </summary>
public class FormControlLookTests
{
    private const uint White = 0xFFFFFF, Blue = 0x0075FF, Track = 0xEFEFEF;

    /// <summary>The page drawn at 96 dpi, so that a pixel is a CSS pixel.</summary>
    private static RenderedImage Render(string body)
    {
        var pdf = HtmlToPdf.Convert("<!doctype html><body style='margin:0'>" + body, new PdfOptions { Margins = new PageMargins(0) });
        return PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
    }

    private static uint Pixel(RenderedImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (uint)(img.Rgba[i] << 16 | img.Rgba[i + 1] << 8 | img.Rgba[i + 2]);
    }

    private static bool Near(uint p, uint color)
        => Math.Abs((int)(p >> 16) - (int)(color >> 16)) <= 8 && Math.Abs((int)(p >> 8 & 0xFF) - (int)(color >> 8 & 0xFF)) <= 8
            && Math.Abs((int)(p & 0xFF) - (int)(color & 0xFF)) <= 8;

    [Fact]
    public void A_slider_is_filled_up_to_its_thumb_at_the_middle_by_default()
    {
        // Chrome's slider is 129 by 16 pixels; the thumb's centre runs from 8 to 121, so the middle is at 64.5.
        var img = Render("<input type=range style='margin:0'>");
        Assert.True(Near(Pixel(img, 64, 8), Blue), "The thumb sits in the middle.");
        Assert.True(Near(Pixel(img, 30, 8), Blue), "The track is filled before the thumb.");
        Assert.True(Near(Pixel(img, 110, 8), Track), "The track is grey after the thumb.");
        Assert.True(Near(Pixel(img, 140, 8), White), "The slider is 129 pixels wide.");
    }

    [Theory]
    [InlineData("min=0 max=10 value=10", 116, true)]
    [InlineData("min=0 max=10 value=0", 30, false)]
    [InlineData("min=0 max=10 value=50", 116, true)]       // clamped to the maximum
    [InlineData("min=0 max=100 step=50 value=80", 80, true)] // snapped to 100
    [InlineData("min=0 max=100 step=50 value=20", 80, false)] // snapped to 0
    public void A_slider_takes_its_value_clamped_and_snapped(string attributes, int x, bool filled)
    {
        var img = Render($"<input type=range {attributes} style='margin:0'>");
        Assert.True(Near(Pixel(img, x, 8), filled ? Blue : Track));
    }

    [Theory]
    [InlineData("#ff0000", 0xFF0000)]
    [InlineData("#00AA00", 0x00AA00)]
    [InlineData("red", 0x000000)]
    [InlineData("#ff00001", 0x000000)]
    public void A_colour_well_shows_its_colour_or_black(string value, uint expected)
    {
        // Chrome's well is 50 by 27 pixels, with a 40 by 15 swatch 5 pixels from the left and 6 from the top.
        var img = Render($"<input type=color value='{value}' style='margin:0'>");
        Assert.True(Near(Pixel(img, 25, 13), expected));
        Assert.True(Near(Pixel(img, 60, 13), White), "The well is 50 pixels wide.");
    }

    [Theory]
    [InlineData("<select style='display:block;margin:0'><option>Choice one</select>")]
    [InlineData("<button style='display:block;margin:0'>Block button</button>")]
    public void A_block_select_or_button_keeps_the_width_of_its_content(string html)
    {
        var img = Render(html);
        for (int x = 200; x < 700; x++)
            Assert.True(Near(Pixel(img, x, 1), White), $"Nothing is drawn at x = {x}.");
        Assert.False(Near(Pixel(img, 1, 1), White), "The control is drawn at the left.");
    }

    [Fact]
    public void A_select_with_a_width_keeps_it()
    {
        var img = Render("<select style='display:block;margin:0;width:400px'><option>Choice one</select>");
        Assert.False(Near(Pixel(img, 398, 1), White));
    }
}
