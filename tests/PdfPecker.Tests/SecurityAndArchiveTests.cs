using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

public class SecurityAndArchiveTests
{
    private const string RedPage = "<div style='background:#ff0000;width:300pt;height:300pt'></div><p>Lønseddel æøå</p>";

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static (int R, int G, int B) Pixel(RenderedImage img, int x, int y)
    {
        int o = (y * img.Width + x) * 4;
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2]);
    }

    // ------------------------------------------------------------------ watermarks

    [Fact]
    public void Watermark_is_a_form_drawn_on_every_page()
    {
        var html = "<div style='height:2000pt'>long</div>";
        var pdf = PdfTestHelpers.ConvertUncompressed(html, new PdfOptions { Watermark = new PdfWatermark { Text = "PRØVEBEREGNING" } });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        int pages = PdfTestHelpers.PageCount(pdf);
        Assert.True(pages > 1);
        Assert.Single(Regex.Matches(text, "/Subtype /Form"));
        Assert.Contains("(PRØVEBEREGNING) Tj", text);
        Assert.Contains("/ca 0.3 /CA 0.3", text);
        Assert.Equal(pages, Regex.Matches(text, @"q /Wm Do Q").Count);
        Assert.Contains("/BaseFont /Helvetica-Bold", text);
    }

    [Fact]
    public void Watermark_is_drawn_over_or_behind_the_content()
    {
        var html = "<div style='background:#0000ff;width:595pt;height:842pt;margin:0'></div>";
        var options = new PdfOptions
        {
            Margins = new PageMargins(0),
            Watermark = new PdfWatermark { Text = "X", FontSize = 600, Color = "#ff0000", Opacity = 1, Angle = 0 },
        };
        var over = PdfRenderer.Open(HtmlToPdf.Convert(html, options)).RenderPage(0, new ImageOptions { Dpi = 20 });
        var c = Pixel(over, over.Width / 2, over.Height / 2);
        Assert.True(c.R > 200 && c.B < 50, $"Expected red over blue, got {c}");

        options.Watermark.Behind = true;
        var behind = PdfRenderer.Open(HtmlToPdf.Convert(html, options)).RenderPage(0, new ImageOptions { Dpi = 20 });
        c = Pixel(behind, behind.Width / 2, behind.Height / 2);
        Assert.True(c.B > 200 && c.R < 50, $"Expected blue over the watermark, got {c}");
    }

    [Fact]
    public void Watermark_text_fits_the_page_diagonal_by_default()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<p>x</p>", new PdfOptions { Watermark = new PdfWatermark { Text = "DRAFT" } });
        var m = Regex.Match(PdfTestHelpers.Latin1(pdf), @"BT /Fw ([\d.]+) Tf\n([-\d.]+) ([-\d.]+) ");
        Assert.True(m.Success);
        float size = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(size, 100, 250);
        double angle = Math.Atan2(double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) * 180 / Math.PI;
        Assert.InRange(angle, 54, 55); // A4 diagonal
    }

    [Fact]
    public void Watermark_image_is_embedded_once()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<div style='height:2000pt'></div>",
            new PdfOptions { Watermark = new PdfWatermark { Image = PdfTestHelpers.RgbPng(8), Opacity = 0.2f } });
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Single(Regex.Matches(text, "/Subtype /Image"));
        Assert.Contains("/Iw Do", text);
        PdfTestHelpers.AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Watermark_without_text_or_image_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Watermark = new PdfWatermark() }));
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>",
            new PdfOptions { Watermark = new PdfWatermark { Image = new byte[] { 1, 2, 3 } } }));
    }

    // ------------------------------------------------------------------ encryption

    [Fact]
    public void Encrypted_pdf_opens_with_user_or_owner_password_only()
    {
        var pdf = HtmlToPdf.Convert(RedPage, new PdfOptions
        {
            Title = "Lønseddel oktober",
            Security = new PdfSecurity { UserPassword = "kodeord", OwnerPassword = "ejer" },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.Contains("/Filter /Standard /V 5 /R 6", text);
        Assert.Contains("/CFM /AESV3", text);
        Assert.Contains("/Encrypt ", text);
        Assert.DoesNotContain("Producer (PdfPecker)", text);

        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(pdf));
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(pdf, "wrong"));
        foreach (var password in new[] { "kodeord", "ejer" })
        {
            var page = PdfRenderer.Open(pdf, password).RenderPage(0, new ImageOptions { Dpi = 20 });
            var c = Pixel(page, 40 * 20 / 72 + 5, 40 * 20 / 72 + 5);
            Assert.True(c.R > 230 && c.G < 30 && c.B < 30, $"Expected the red box, got {c}");
        }
    }

    [Fact]
    public void Encryption_without_user_password_opens_freely_and_keeps_permissions()
    {
        var pdf = HtmlToPdf.Convert(RedPage, new PdfOptions { Security = new PdfSecurity { Permissions = PdfPermissions.Print } });
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
        var p = Regex.Match(PdfTestHelpers.Latin1(pdf), @"/P (-?\d+)");
        int value = int.Parse(p.Groups[1].Value);
        Assert.Equal(1 << 2, value & 0xF3C); // only print among the permission bits
        Assert.Equal(unchecked((int)0xFFFFF0C0), value & unchecked((int)0xFFFFF0C3));
    }

    [Fact]
    public void Encrypted_strings_and_images_round_trip()
    {
        var html = $"<img src='data:image/png;base64,{Convert.ToBase64String(PdfTestHelpers.RgbaPng(8))}' style='width:200pt;height:200pt'>"
                   + "<a href='https://example.com/løn'>link</a>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions
        {
            Margins = new PageMargins(0),
            Security = new PdfSecurity { UserPassword = "pw" },
            Watermark = new PdfWatermark { Text = "KOPI" },
        });
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.DoesNotContain("example.com", text);
        var page = PdfRenderer.Open(pdf, "pw").RenderPage(0, new ImageOptions { Dpi = 20 });
        var c = Pixel(page, 30, 30);
        Assert.True(c.R > 200 && c.G < 60, $"Expected the red image, got {c}");
    }

    [Fact]
    public void Indexed_palette_is_encrypted_with_the_image()
    {
        // Two-colour palette PNG: index 0 = green.
        var png = PalettePng();
        var html = $"<img src='data:image/png;base64,{Convert.ToBase64String(png)}' style='width:300pt;height:300pt'>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Margins = new PageMargins(0), Security = new PdfSecurity { UserPassword = "pw" } });
        Assert.DoesNotContain("<00FF00", PdfTestHelpers.Latin1(pdf));
        var page = PdfRenderer.Open(pdf, "pw").RenderPage(0, new ImageOptions { Dpi = 20 });
        var c = Pixel(page, 20, 20);
        Assert.True(c.G > 200 && c.R < 60, $"Expected the green palette colour, got {c}");
    }

    private static byte[] PalettePng()
    {
        // 4x4, 8-bit indexed, all pixels index 0 (green).
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            ms.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
            ms.Write(t);
            ms.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (var b in t.Concat(data))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            crc = ~crc;
            ms.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        }
        Chunk("IHDR", new byte[] { 0, 0, 0, 4, 0, 0, 0, 4, 8, 3, 0, 0, 0 });
        Chunk("PLTE", new byte[] { 0, 255, 0, 255, 0, 0 });
        var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(new byte[20]); // 4 rows: filter byte 0 + 4 zero indices
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ PDF/A

    [Fact]
    public void PdfA_and_encryption_cannot_be_combined()
    {
        var ex = Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>",
            new PdfOptions { Conformance = PdfConformance.PdfA2b, Security = new PdfSecurity { UserPassword = "x" } }));
        Assert.Contains("PDF/A", ex.Message);
    }

    [Fact]
    public void PdfA_has_metadata_output_intent_and_file_id()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<title>Løn &amp; ferie</title><p>x</p><a href='https://example.com'>a</a>",
            new PdfOptions { Conformance = PdfConformance.PdfA2b, Author = "Mr T" });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.Matches(@"/Type /Catalog /Pages \d+ 0 R /Metadata \d+ 0 R /OutputIntents \[\d+ 0 R\]", text);
        Assert.Contains("/S /GTS_PDFA1 /OutputConditionIdentifier (sRGB IEC61966-2.1)", text);
        Assert.Matches(@"/ID \[<[0-9A-F]{32}><[0-9A-F]{32}>\]", text);
        Assert.Contains("/Subtype /Link /Rect", text);
        Assert.Contains("/F 4", text);

        var xmp = Encoding.UTF8.GetString(pdf);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("<rdf:li xml:lang=\"x-default\">Løn &amp; ferie</rdf:li>", xmp);
        Assert.Contains("<rdf:li>Mr T</rdf:li>", xmp);
        // The XMP dates and the Info dictionary dates describe the same instant.
        var info = Regex.Match(text, @"/CreationDate \(D:(\d{4})(\d\d)(\d\d)(\d\d)(\d\d)(\d\d)Z\)");
        Assert.True(info.Success);
        var g = info.Groups;
        Assert.Contains($"<xmp:CreateDate>{g[1]}-{g[2]}-{g[3]}T{g[4]}:{g[5]}:{g[6]}Z</xmp:CreateDate>", xmp);
    }

    [Fact]
    public void PdfA3_sets_part_3()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Conformance = PdfConformance.PdfA3b });
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", Encoding.UTF8.GetString(pdf));
    }

    [Fact]
    public void PdfA_converts_cmyk_images_to_rgb()
    {
        var html = $"<img src='data:image/jpeg;base64,{Convert.ToBase64String(Fixture("cmyk.jpg"))}'>";
        var plain = PdfTestHelpers.Latin1(HtmlToPdf.Convert(html));
        Assert.Contains("/DeviceCMYK", plain);
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Conformance = PdfConformance.PdfA2b });
        Assert.DoesNotContain("/DeviceCMYK", PdfTestHelpers.Latin1(pdf));
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
    }

    [Fact]
    public void Srgb_profile_is_a_valid_icc_header()
    {
        var p = PdfPecker.Pdf.PdfA.SrgbProfile;
        Assert.Equal(p.Length, p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]);
        Assert.Equal("mntrRGB XYZ ", Encoding.ASCII.GetString(p, 12, 12));
        Assert.Equal("acsp", Encoding.ASCII.GetString(p, 36, 4));
        Assert.Equal(9, p[128] << 24 | p[129] << 16 | p[130] << 8 | p[131]);
    }
}
