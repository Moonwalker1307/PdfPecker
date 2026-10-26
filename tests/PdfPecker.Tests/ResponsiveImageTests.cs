using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>srcset, sizes and the picture element choose an img's picture as Chrome does when it prints.</summary>
public class ResponsiveImageTests
{
    private const string Head = "<!doctype html><style>@page{margin:0} body{margin:0} img{display:block}</style>";

    // Squares of 40, 80 and 120 px: 30, 60 and 90 pt at 1x.
    private static readonly Dictionary<string, byte[]> Files = new()
    {
        ["red.png"] = SolidPng(40, 40, 255, 0, 0),
        ["green.png"] = SolidPng(80, 80, 0, 160, 0),
        ["blue.png"] = SolidPng(120, 120, 0, 0, 255),
    };

    private static byte[] Convert(string body)
        => HtmlToPdf.Convert(Head + body, new PdfOptions { ResourceResolver = url => Files.GetValueOrDefault(url) });

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    /// <summary>Asserts that the first image is a square of <paramref name="size"/> points in the given colour.</summary>
    private static void Shows(string body, (int R, int G, int B) color, int size)
    {
        var pdf = Convert(body);
        Assert.Equal(color, Pixel(pdf, size - 2, size - 2));
        Assert.Equal((255, 255, 255), Pixel(pdf, size + 2, 2));
        Assert.Equal((255, 255, 255), Pixel(pdf, 2, size + 2));
    }

    private static readonly (int, int, int) Red = (255, 0, 0), Green = (0, 160, 0), Blue = (0, 0, 255);

    [Fact]
    public void Density_descriptors_choose_the_1x_candidate()
        => Shows("<img srcset='green.png 2x, red.png 1x, blue.png 3x'>", Red, 30);

    [Fact]
    public void The_src_stands_for_1x_beside_a_srcset()
        => Shows("<img src='red.png' srcset='green.png 2x'>", Red, 30);

    [Fact]
    public void A_candidate_without_a_descriptor_is_1x()
        => Shows("<img src='blue.png' srcset='red.png, green.png 2x'>", Red, 30);

    [Fact]
    public void A_2x_candidate_shows_its_pixels_at_half_size()
        => Shows("<img srcset='green.png 2x'>", Green, 30);

    [Fact]
    public void Between_two_densities_the_geometric_mean_decides()
    {
        // 1 lies below the geometric mean of 0.8 and 1.5 (about 1.1), so the smaller candidate wins, as in Chrome; it
        // lies above that of 0.5 and 1.2 (about 0.77), so there the larger one does.
        Shows("<img srcset='red.png 0.8x, green.png 1.5x'>", Red, 38);
        Shows("<img srcset='red.png 0.5x, green.png 1.2x'>", Green, 50);
    }

    [Fact]
    public void Width_descriptors_choose_by_the_size_in_sizes()
        => Shows("<img srcset='red.png 40w, green.png 80w, blue.png 120w' sizes='80px'>", Green, 60);

    [Fact]
    public void Sizes_takes_the_first_entry_whose_media_condition_matches()
        => Shows("<img srcset='red.png 40w, green.png 80w, blue.png 120w' sizes='(max-width: 100px) 40px, (orientation: portrait) calc(100px + 20px), 10px'>", Blue, 90);

    [Fact]
    public void Without_sizes_the_image_is_as_wide_as_the_page_area()
    {
        var pdf = Convert("<img srcset='red.png 40w, blue.png 120w'>");
        // 120w over 100vw: the blue picture fills the A4 page area's width.
        Assert.Equal(Blue, Pixel(pdf, 590, 10));
    }

    [Fact]
    public void A_picture_takes_its_first_source_whose_media_and_type_match()
        => Shows("<picture><source media='screen' srcset='blue.png'><source type='image/avif' srcset='blue.png'>"
                 + "<source media='print' type='image/png' srcset='green.png'><img src='red.png'></picture>", Green, 60);

    [Fact]
    public void A_picture_without_a_matching_source_shows_its_img()
        => Shows("<picture><source media='(min-width: 5000px)' srcset='blue.png'><img src='red.png'></picture>", Red, 30);

    [Fact]
    public void A_source_with_sizes_chooses_by_width()
        => Shows("<picture><source srcset='red.png 40w, blue.png 120w' sizes='120px'><img src='green.png'></picture>", Blue, 90);

    [Fact]
    public void A_data_uri_with_a_comma_is_one_candidate()
        => Shows($"<img srcset='{DataUri(Files["green.png"])} 2x'>", Green, 30);

    [Theory]
    [InlineData("green.png 2q, red.png")]
    [InlineData("green.png 1x 2x, red.png")]
    [InlineData("green.png 10h, red.png")]
    [InlineData("green.png -1w, red.png")]
    public void A_candidate_with_invalid_descriptors_is_dropped(string srcset)
        => Shows($"<img srcset='{srcset}'>", Red, 30);

    [Fact]
    public void The_src_attribute_keeps_matching_selectors()
        => Shows("<style>img[src='red.png']{width:50px}</style><img src='red.png' srcset='green.png 1x'>", Green, 38);

    [Fact]
    public void A_chosen_picture_that_loads_hides_the_alt_text_and_a_missing_one_shows_it()
    {
        var shown = PdfText.ExtractPages(Convert("<p>A <img srcset='red.png' alt='Logo' style='display:inline'> B</p>"))[0].Lines;
        Assert.Equal("A B", string.Join("|", shown.Select(l => l.Text)));
        var missing = PdfText.ExtractPages(Convert("<p>A <img src='red.png' srcset='missing.png' alt='Logo' style='display:inline'> B</p>"))[0].Lines;
        Assert.Equal("A Logo B", string.Join("|", missing.Select(l => l.Text)));
    }
}

/// <summary>srcset and sizes values that are huge, tiny, many or malformed.</summary>
[Collection(HostileInputCollection.Name)]
public class ResponsiveImageHostileInputTests
{
    [Fact]
    public void Extreme_densities_widths_and_sizes_stay_bounded()
    {
        string png = DataUri(SolidPng(4, 4, 255, 0, 0));
        string many = string.Join(", ", Enumerable.Range(1, 5000).Select(i => $"{png} {i}w"));
        var html = $"<img srcset='{png} 1e-30x'><img srcset='{png} 1e30x'><img srcset='{png} 0.000001x'><img srcset='{png} 999999999w' sizes='1px'>"
            + $"<img srcset='{png} 1w' sizes='calc(1e9px * 1e9)'><img srcset='{many}' sizes='(((, (width > 0) -1px, auto'>"
            + $"<img srcset='{png} 1x 2x 3w 4h ((((, ,,,, {png} (1x), {png} NaNx, {png} Infinityx, {png} -0x'>"
            + $"<picture><source srcset='{png} 2x' media='(((' type=';;;'><source srcset=''><img srcset='{png} 1x'></picture>";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        PdfTestHelpers.AssertStructurallyValid(HtmlToPdf.Convert(html, null, cts.Token));
    }
}
