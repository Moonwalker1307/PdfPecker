namespace PdfPecker.Tests;

/// <summary>
/// accent-color and appearance: none on check boxes and radio buttons, with Chrome's colours: a checked control is
/// painted in its accent colour with a white mark, or a dark grey one on a light accent; a disabled control stays grey;
/// and appearance: none leaves a plain box that the page's CSS draws, :checked and ::before included.
/// </summary>
public class FormAppearanceTests
{
    private const uint White = 0xFFFFFF, Dark = 0x3B3B3B, Blue = 0x0075FF, Grey = 0xBDBDBD;

    /// <summary>The page drawn at 96 dpi, so that a pixel is a CSS pixel.</summary>
    private static RenderedImage Render(string body, bool fields = true)
    {
        var pdf = HtmlToPdf.Convert("<!doctype html><body style='margin:0'>" + body,
            new PdfOptions { Margins = new PageMargins(0), FormFields = fields });
        return PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
    }

    private static uint Pixel(RenderedImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (uint)(img.Rgba[i] << 16 | img.Rgba[i + 1] << 8 | img.Rgba[i + 2]);
    }

    /// <summary>The pixels of the colour in the square (0, 0, size, size), allowing for a little rounding.</summary>
    private static int Count(RenderedImage img, uint color, int size = 52)
    {
        int n = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint p = Pixel(img, x, y);
                if (Math.Abs((int)(p >> 16) - (int)(color >> 16)) <= 3 && Math.Abs((int)(p >> 8 & 0xFF) - (int)(color >> 8 & 0xFF)) <= 3
                    && Math.Abs((int)(p & 0xFF) - (int)(color & 0xFF)) <= 3) n++;
            }
        return n;
    }

    // A 52px control at the top left of the page.
    private const string Big = "margin:0;zoom:4;";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_checked_check_box_is_painted_in_its_accent_colour(bool fields)
    {
        var img = Render($"<input type=checkbox checked style='{Big}accent-color:#d6336c'>", fields);
        Assert.Equal(0xD6336Cu, Pixel(img, 8, 8));
        Assert.True(Count(img, White) > 100, "The tick is white on a dark accent.");
        Assert.Equal(0, Count(img, Blue));
    }

    [Fact]
    public void A_light_accent_gets_a_dark_mark()
    {
        var img = Render($"<input type=checkbox checked style='{Big}accent-color:gold'>");
        Assert.Equal(0xFFD700u, Pixel(img, 8, 8));
        Assert.True(Count(img, Dark) > 100);
        Assert.Equal(0, Count(img, White));
    }

    [Fact]
    public void A_radio_button_takes_the_accent_and_inherits_it()
    {
        var img = Render($"<div style='accent-color:gold'><input type=radio checked style='{Big}'></div>");
        Assert.Equal(0xFFD700u, Pixel(img, 26, 26));
        Assert.True(Count(img, Dark) > 100, "The ring between the edge and the dot is dark on a light accent.");
    }

    [Fact]
    public void A_disabled_control_stays_grey()
    {
        var img = Render($"<input type=checkbox checked disabled style='{Big}accent-color:#d6336c'>");
        Assert.Equal(Grey, Pixel(img, 8, 8));
        Assert.True(Count(img, White) > 100);
    }

    [Fact]
    public void Auto_and_no_accent_keep_the_blue()
    {
        Assert.Equal(Blue, Pixel(Render($"<input type=checkbox checked style='{Big}'>"), 8, 8));
        Assert.Equal(Blue, Pixel(Render($"<input type=checkbox checked style='{Big}accent-color:red;accent-color:auto'>"), 8, 8));
    }

    [Fact]
    public void A_transparent_accent_shows_the_white_through_it()
    {
        // Red at 30% over white, which is light, so the tick is dark as in Chrome.
        var img = Render($"<input type=checkbox checked style='{Big}accent-color:rgb(255 0 0 / .3)'>");
        Assert.Equal(0xFFB3B3u, Pixel(img, 8, 8));
        Assert.True(Count(img, Dark) > 100);
    }

    [Fact]
    public void An_unchecked_box_is_not_painted_in_the_accent()
    {
        var img = Render($"<input type=checkbox style='{Big}accent-color:#d6336c'>");
        Assert.Equal(0, Count(img, 0xD6336C));
    }

    [Theory]
    [InlineData("appearance:none")]
    [InlineData("-webkit-appearance:none")]
    public void Appearance_none_draws_only_the_pages_css(string appearance)
    {
        var css = $"<style>input {{ {appearance}; margin:0; width:40px; height:40px; background:#ccc }} input:checked {{ background:#2e9d5b }}</style>";
        var img = Render(css + "<input type=checkbox checked>");
        Assert.Equal(0x2E9D5Bu, Pixel(img, 20, 20));
        Assert.Equal(0, Count(img, Blue, 40));
        Assert.Equal(0, Count(img, White, 40));
        Assert.Equal(0xCCCCCCu, Pixel(Render(css + "<input type=checkbox>"), 20, 20));
    }

    [Fact]
    public void Without_a_size_a_control_without_its_look_has_none()
    {
        // As in Chrome, nothing of the box is drawn: no size, border or background.
        var img = Render($"<input type=checkbox checked style='appearance:none;margin:0;zoom:4'>");
        Assert.Equal(0, Count(img, Blue));
        Assert.Equal(52 * 52, Count(img, White));
    }

    [Fact]
    public void Appearance_none_controls_are_not_form_fields()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<input type=checkbox checked style='appearance:none'><input type=checkbox name=b>",
            new PdfOptions { FormFields = true });
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "/Subtype /Widget"));
    }

    [Fact]
    public void A_control_without_its_look_has_before_and_after_boxes()
    {
        var css = "<style>input { appearance:none; margin:0; width:40px; height:20px; position:relative } "
                  + "input::before { content:''; position:absolute; left:0; top:0; width:20px; height:20px; background:#2e9d5b }</style>";
        var img = Render(css + "<input type=checkbox>");
        Assert.Equal(0x2E9D5Bu, Pixel(img, 10, 10));
        Assert.Equal(White, Pixel(img, 30, 10));
        // A check box with its native look has none, as in Chrome.
        var native = Render(css.Replace("appearance:none;", "", StringComparison.Ordinal) + "<input type=checkbox style='zoom:1'>");
        Assert.Equal(0, Count(native, 0x2E9D5B, 40));
    }

    [Fact]
    public void A_text_field_without_its_look_gets_chromes_plain_border()
    {
        // Chrome's fallback for a field without its look: a 2px inset border, as for a field the page gives a background.
        var plain = Render("<input type=text style='margin:0;width:100px;appearance:none'>");
        var native = Render("<input type=text style='margin:0;width:100px'>");
        Assert.NotEqual(Pixel(native, 1, 10), Pixel(plain, 1, 10));
    }

    [Fact]
    public void Supports_knows_both_properties()
    {
        Assert.True(Css.CssSupports.Evaluate("(accent-color: red)"));
        Assert.True(Css.CssSupports.Evaluate("(accent-color: auto)"));
        Assert.True(Css.CssSupports.Evaluate("(appearance: none)"));
        Assert.True(Css.CssSupports.Evaluate("(-webkit-appearance: none)"));
        Assert.False(Css.CssSupports.Evaluate("(appearance: fancy)"));
        Assert.False(Css.CssSupports.Evaluate("(accent-color: 12px)"));
    }
}
