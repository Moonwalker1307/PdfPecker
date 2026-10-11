using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

public class ConversionTests
{
    [Fact]
    public void Converts_simple_document_to_valid_pdf()
    {
        var pdf = HtmlToPdf.Convert("<h1>Hello</h1><p>World</p>");
        AssertStructurallyValid(pdf);
        Assert.Equal(1, PageCount(pdf));
        var content = AllContent(pdf);
        Assert.Contains("(Hello) Tj", content);
        Assert.Contains("(World) Tj", content);
    }

    [Fact]
    public void Empty_input_still_produces_one_page()
    {
        var pdf = HtmlToPdf.Convert("");
        AssertStructurallyValid(pdf);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void Uncompressed_output_has_no_filters_on_content()
    {
        var pdf = ConvertUncompressed("<p>Plain</p>");
        AssertStructurallyValid(pdf);
        Assert.Contains("(Plain) Tj", Latin1(pdf));
    }

    [Fact]
    public void Long_content_flows_onto_multiple_pages()
    {
        var html = string.Concat(Enumerable.Range(1, 200).Select(i => $"<p>Paragraph {i}</p>"));
        var pdf = HtmlToPdf.Convert(html);
        AssertStructurallyValid(pdf);
        Assert.True(PageCount(pdf) > 3);
        Assert.Contains("(Paragraph 200) Tj", AllContent(pdf));
    }

    [Fact]
    public void Page_break_before_starts_a_new_page()
    {
        var pdf = HtmlToPdf.Convert("<p>One</p><p style='page-break-before: always'>Two</p><div style='break-before: page'>Three</div>");
        Assert.Equal(3, PageCount(pdf));
    }

    [Fact]
    public void Css_page_rule_sets_size_and_orientation()
    {
        var pdf = ConvertUncompressed("<style>@page { size: A4 landscape; margin: 1cm }</style><p>x</p>");
        Assert.Contains("/MediaBox [0 0 841.89 595.28]", Latin1(pdf));
    }

    [Fact]
    public void Options_page_size_is_used()
    {
        var pdf = ConvertUncompressed("<p>x</p>", new PdfOptions { PageSize = PageSize.Letter });
        Assert.Contains("/MediaBox [0 0 612 792]", Latin1(pdf));
    }

    [Fact]
    public void Title_comes_from_html_title()
    {
        var pdf = ConvertUncompressed("<html><head><title>My  Report</title></head><body>x</body></html>");
        Assert.Contains("/Title (My Report)", Latin1(pdf));
    }

    [Fact]
    public void Only_used_standard_fonts_are_embedded_as_resources()
    {
        var pdf = ConvertUncompressed("<p style='font-family: Arial'>a <b>b</b></p>");
        var text = Latin1(pdf);
        Assert.Contains("/BaseFont /Helvetica ", text);
        Assert.Contains("/BaseFont /Helvetica-Bold ", text);
        Assert.DoesNotContain("/BaseFont /Courier", text);
    }

    [Fact]
    public void Font_families_map_to_standard_fonts()
    {
        var pdf = ConvertUncompressed(
            "<p style='font-family: \"Times New Roman\", serif'>t</p><p style='font-family: monospace'>m</p><p style='font: italic bold 12px Verdana'>v</p>");
        var text = Latin1(pdf);
        Assert.Contains("/BaseFont /Times-Roman", text);
        Assert.Contains("/BaseFont /Courier", text);
        Assert.Contains("/BaseFont /Helvetica-BoldOblique", text);
    }

    [Fact]
    public void External_and_internal_links_become_annotations()
    {
        var pdf = ConvertUncompressed("<p id='top'><a href='https://example.com/a(b)'>out</a> <a href='#top'>in</a></p>");
        var text = Latin1(pdf);
        Assert.Contains("/S /URI /URI (https://example.com/a\\(b\\))", text);
        Assert.Matches(new Regex(@"/Dest \[\d+ 0 R /XYZ 0 [\d.]+ null\]"), text);
    }

    [Fact]
    public void Png_data_uri_is_embedded_with_soft_mask()
    {
        var src = "data:image/png;base64," + Convert.ToBase64String(RgbaPng());
        var pdf = ConvertUncompressed($"<img src='{src}' width='40' height='40'>");
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/Subtype /Image", text);
        Assert.Contains("/SMask", text);
        Assert.Contains("/Im1 Do", AllContent(pdf));
    }

    [Fact]
    public void Opaque_png_is_passed_through_with_png_predictor()
    {
        var src = "data:image/png;base64," + Convert.ToBase64String(RgbPng());
        var pdf = ConvertUncompressed($"<img src='{src}'>");
        Assert.Contains("/Predictor 15 /Colors 3", Latin1(pdf));
    }

    [Theory]
    [InlineData("rgb.jpg", "/ColorSpace /DeviceRGB", null)]
    [InlineData("cmyk.jpg", "/ColorSpace /DeviceCMYK", "/Decode [1 0 1 0 1 0 1 0]")]
    public void Jpeg_is_embedded_unchanged_with_dct_filter(string file, string colorSpace, string? decode)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var jpeg = File.ReadAllBytes(Path.Combine(fixtures, file));
        var pdf = ConvertUncompressed($"<img src='{file}'>", new PdfOptions { BaseDirectory = fixtures });
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("/Filter /DCTDecode", text);
        Assert.Contains("/Width 320 /Height 160", text);
        Assert.Contains(colorSpace, text);
        if (decode != null) Assert.Contains(decode, text);
        Assert.Contains($"/Length {jpeg.Length} >>", text);
    }

    [Fact]
    public void Same_image_used_twice_is_embedded_once()
    {
        var src = "data:image/png;base64," + Convert.ToBase64String(RgbPng());
        var pdf = ConvertUncompressed($"<img src='{src}'><img src='{src}'>");
        Assert.Single(Regex.Matches(Latin1(pdf), "/Subtype /Image"));
    }

    [Fact]
    public void Missing_image_does_not_fail_conversion()
    {
        var pdf = HtmlToPdf.Convert("<p>before <img src='does-not-exist.png'> after</p>");
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Resource_resolver_is_used_for_images_and_stylesheets()
    {
        var requested = new List<string>();
        var options = new PdfOptions
        {
            Compress = false,
            ResourceResolver = url =>
            {
                requested.Add(url);
                return url switch
                {
                    "https://cdn/logo.png" => RgbPng(),
                    "https://cdn/site.css" => "p { color: #ff0000 }"u8.ToArray(),
                    _ => null,
                };
            },
        };
        var pdf = HtmlToPdf.Convert("<link rel='stylesheet' href='https://cdn/site.css'><img src='https://cdn/logo.png'><p>red</p>", options);
        Assert.Contains("https://cdn/logo.png", requested);
        Assert.Contains("https://cdn/site.css", requested);
        Assert.Contains("/Subtype /Image", Latin1(pdf));
        Assert.Contains("1 0 0 rg", AllContent(pdf));
    }

    [Fact]
    public void Footer_text_is_written_on_every_page()
    {
        var html = string.Concat(Enumerable.Range(1, 120).Select(i => $"<p>Line {i}</p>"));
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { FooterText = "Page {page} of {pages}" });
        int pages = PageCount(pdf);
        var content = AllContent(pdf);
        for (int p = 1; p <= pages; p++) Assert.Contains($"(Page {p} of {pages}) Tj", content);
    }

    [Fact]
    public void Non_ascii_characters_are_encoded_as_win_ansi()
    {
        var pdf = ConvertUncompressed("<p>caf&eacute; &euro; \u2014 \u201Cq\u201D</p>");
        var bytes = pdf;
        var content = AllContent(pdf);
        Assert.Contains("caf\u00E9 \u0080 \u0097 \u0093q\u0094", content);
        Assert.NotNull(bytes);
    }

    [Fact]
    public void Parentheses_and_backslashes_are_escaped_in_text()
    {
        var content = AllContent(ConvertUncompressed(@"<p>a(b)c\d</p>"));
        Assert.Contains(@"(a\(b\)c\\d) Tj", content);
    }

    [Fact]
    public void Background_colors_and_borders_are_painted()
    {
        var content = AllContent(ConvertUncompressed("<div style='background:#00ff00;border:2px solid blue;height:50px'>x</div>"));
        Assert.Contains("0 1 0 rg", content);
        Assert.Contains("0 0 1 rg", content);
        Assert.Contains(" re f", content);
    }

    [Fact]
    public void Display_none_content_is_not_rendered()
    {
        var content = AllContent(ConvertUncompressed("<p>shown</p><p style='display:none'>hidden</p><script>var x = '<p>no</p>';</script>"));
        Assert.Contains("(shown) Tj", content);
        Assert.DoesNotContain("hidden", content);
        Assert.DoesNotContain("var x", content);
    }

    [Fact]
    public void Table_rows_do_not_split_and_header_repeats()
    {
        var rows = string.Concat(Enumerable.Range(1, 150).Select(i => $"<tr><td>Row {i}</td><td>Value</td></tr>"));
        var pdf = HtmlToPdf.Convert($"<table border=1><thead><tr><th>Name</th><th>Val</th></tr></thead><tbody>{rows}</tbody></table>");
        AssertStructurallyValid(pdf);
        int pages = PageCount(pdf);
        Assert.True(pages >= 3);
        Assert.Equal(pages, Regex.Matches(AllContent(pdf), @"\(Name\) Tj").Count);
    }

    [Fact]
    public void Text_is_wrapped_within_the_page_width()
    {
        var words = string.Join(" ", Enumerable.Repeat("word", 400));
        var content = AllContent(ConvertUncompressed($"<p>{words}</p>"));
        // Every line starts with its own text matrix; the x of each must be inside the page.
        var xs = Regex.Matches(content, @"1 0 0 1 ([\d.]+) [\d.]+ Tm").Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(xs.Count > 10);
        Assert.All(xs, x => Assert.InRange(x, 0, 595));
    }

    [Fact]
    public void Conversions_are_thread_safe()
    {
        var html = "<h1>Parallel</h1>" + string.Concat(Enumerable.Range(1, 50).Select(i => $"<p>Para {i} <b>bold</b></p>"));
        var expected = AllContent(HtmlToPdf.Convert(html));
        var results = new byte[16][];
        Parallel.For(0, results.Length, i => results[i] = HtmlToPdf.Convert(html));
        foreach (var r in results) Assert.Equal(expected, AllContent(r));
    }

    [Fact]
    public async Task ConvertAsync_returns_pdf()
    {
        var pdf = await HtmlToPdf.ConvertAsync("<p>async</p>");
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void ConvertFile_resolves_relative_images()
    {
        var dir = Directory.CreateTempSubdirectory("pdfpecker");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "pic.png"), RgbPng());
            var htmlPath = Path.Combine(dir.FullName, "doc.html");
            File.WriteAllText(htmlPath, "<img src='pic.png'>");
            var pdfPath = Path.Combine(dir.FullName, "doc.pdf");
            HtmlToPdf.ConvertFile(htmlPath, pdfPath, new PdfOptions { Compress = false });
            Assert.Contains("/Subtype /Image", Latin1(File.ReadAllBytes(pdfPath)));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Files_outside_the_base_directory_need_AllowFileAccess()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var absolute = Path.Combine(fixtures, "rgb.jpg");
        int Images(string html, PdfOptions o) => System.Text.RegularExpressions.Regex.Matches(Latin1(ConvertUncompressed(html, o)), "/Subtype /Image").Count;

        // Relative paths inside the base directory work by default; absolute paths and file: URLs do not.
        Assert.Equal(1, Images("<img src='rgb.jpg'>", new PdfOptions { BaseDirectory = fixtures }));
        Assert.Equal(0, Images($"<img src='{absolute}'><img src='file://{absolute}'>", new PdfOptions { BaseDirectory = fixtures }));
        Assert.Equal(2, Images($"<img src='{absolute}'><img src='file://{absolute}'>", new PdfOptions { BaseDirectory = fixtures, AllowFileAccess = true }));

        // No base directory: no files at all.
        Assert.Equal(0, Images("<img src='rgb.jpg'>", new PdfOptions()));

        // Escaping the base directory with .. needs AllowFileAccess.
        var sub = Directory.CreateTempSubdirectory("pdfpecker-base");
        try
        {
            var escape = Path.GetRelativePath(sub.FullName, absolute);
            Assert.StartsWith("..", escape);
            Assert.Equal(0, Images($"<img src='{escape}'><link rel=stylesheet href='../x.css'>", new PdfOptions { BaseDirectory = sub.FullName }));
            Assert.Equal(1, Images($"<img src='{escape}'>", new PdfOptions { BaseDirectory = sub.FullName, AllowFileAccess = true }));

            // A symbolic link inside the base directory that points outside it is refused too.
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(sub.FullName, "link.jpg"), absolute);
                Directory.CreateSymbolicLink(Path.Combine(sub.FullName, "dir"), fixtures);
                File.Copy(absolute, Path.Combine(sub.FullName, "own.jpg"));
                Assert.Equal(1, Images("<img src='link.jpg'><img src='dir/rgb.jpg'><img src='own.jpg'>", new PdfOptions { BaseDirectory = sub.FullName }));
            }
        }
        finally
        {
            sub.Delete(recursive: true);
        }
    }

    [Fact]
    public void Stylesheets_outside_the_base_directory_need_AllowFileAccess()
    {
        var root = Directory.CreateTempSubdirectory("pdfpecker-css");
        try
        {
            var site = Directory.CreateDirectory(Path.Combine(root.FullName, "site"));
            File.WriteAllText(Path.Combine(root.FullName, "secret.css"), "p { color: #ff0000 }");
            File.WriteAllText(Path.Combine(site.FullName, "own.css"), "p { color: #00ff00 }");
            string Pdf(string href, bool any) => Latin1(ConvertUncompressed($"<link rel=stylesheet href='{href}'><p>x</p>",
                new PdfOptions { BaseDirectory = site.FullName, AllowFileAccess = any }));
            Assert.Contains("0 1 0 rg", Pdf("own.css", false));
            Assert.DoesNotContain("1 0 0 rg", Pdf("../secret.css", false));
            Assert.Contains("1 0 0 rg", Pdf("../secret.css", true));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Writes_to_stream()
    {
        using var ms = new MemoryStream();
        HtmlToPdf.Convert("<p>stream</p>", ms);
        AssertStructurallyValid(ms.ToArray());
    }
}

public class RobustnessTests
{
    [Theory]
    [InlineData("<div><p>unclosed <b>bold <i>both</div> text")]
    [InlineData("<table><td>cell without row<tr><td colspan=0 rowspan=99>x</table>")]
    [InlineData("</p></div></table>stray end tags")]
    [InlineData("<p style='color: ; font-size: -5px; margin: auto auto auto auto auto; width: 1e9px'>bad css</p>")]
    [InlineData("<style>{{{ p { color: red } @media { } @page :first { margin: 0 }</style><p>x</p>")]
    [InlineData("<img src='data:image/png;base64,AAAA'><img src='data:image/jpeg;base64,/9j/'>")]
    [InlineData("<ul><li><ol><li><table><tr><td><ul><li>deep</li></ul></td></tr></table></li></ol></li></ul>")]
    [InlineData("<p>&#0; &#xD800; &#99999999; \u0001\u0002 control chars</p>")]
    public void Malformed_input_does_not_throw(string html)
    {
        var pdf = HtmlToPdf.Convert(html);
        PdfTestHelpers.AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Very_deep_nesting_does_not_overflow_the_stack()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 20000)) + "deep" + string.Concat(Enumerable.Repeat("</div>", 20000));
        var pdf = HtmlToPdf.Convert(html);
        PdfTestHelpers.AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Huge_word_in_narrow_table_cell_is_broken()
    {
        var pdf = HtmlToPdf.Convert("<table style='width:100px'><tr><td>" + new string('W', 500) + "</td></tr></table>");
        PdfTestHelpers.AssertStructurallyValid(pdf);
    }
}
