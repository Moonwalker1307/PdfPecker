using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Removing what pages draw in given areas, so that it is gone from the file and not only covered.</summary>
public class RedactionTests
{
    private const string Page = "<style>@page { size: 400pt 300pt; margin: 0 } body { margin: 0; font: 12pt Helvetica }"
        + " p { position: absolute; left: 20pt; margin: 0 }</style>";

    /// <summary>A PDF of the given objects, numbered from 1 with the catalog first, and a correct cross-reference table.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string content) => "<< /Length " + Encoding.Latin1.GetByteCount(content) + " >>\nstream\n" + content + "\nendstream";

    /// <summary>A one-page PDF (400 by 300 points) with Helvetica as /F1 and the given content.</summary>
    private static byte[] OnePage(string content, string pageExtra = "", params string[] more)
        => Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R " + pageExtra + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(content),
        }.Concat(more).ToArray());

    private static RenderedImage Render(byte[] pdf, int page = 0) => PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });

    /// <summary>The colour of a pixel as 0xRRGGBB, flattened on white.</summary>
    private static uint Pixel(RenderedImage image, int x, int y)
    {
        uint p = image.Premultiplied[y * image.Width + x], a = p >> 24;
        uint Channel(int shift) => ((p >> shift) & 255) + (255 - a);
        return Channel(0) << 16 | Channel(8) << 8 | Channel(16);
    }

    /// <summary>Every byte of the file, with each stream that inflates also inflated.</summary>
    private static string AllBytes(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder(text);
        foreach (Match m in Regex.Matches(text, "stream\r?\n"))
        {
            int start = m.Index + m.Length, end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            try
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, end - start), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                sb.Append(Encoding.Latin1.GetString(ms.ToArray()));
            }
            catch (InvalidDataException)
            {
            }
        }
        return sb.ToString();
    }

    /// <summary>How many streams of the given /Subtype the file holds, drawn or not.</summary>
    private static int Streams(byte[] pdf, string subtype)
    {
        var file = PdfFile.Open(pdf, null);
        int count = 0;
        for (int i = 1; i <= file.MaxObjectNumber; i++)
            if (file.GetObject(i) is PdfStream s && file.GetName(s, "Subtype") == subtype) count++;
        return count;
    }

    /// <summary>A 4 by 1 RGB image whose samples spell SECRETPIXELS, so that the original's data can be looked for.</summary>
    private const string SecretImage = "<< /Type /XObject /Subtype /Image /Width 4 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length 12 >>\nstream\nSECRETPIXELS\nendstream";

    private static PdfTextWord Word(byte[] pdf, string text)
        => PdfText.ExtractPages(pdf).SelectMany(p => p.Lines).SelectMany(l => l.Words).First(w => w.Text == text);

    [Fact]
    public void Redacted_text_no_longer_extracts_and_the_rest_keeps_its_place()
    {
        var pdf = HtmlToPdf.Convert(Page + "<p style='top:40pt'>Name Ada Lovelace</p><p style='top:80pt'>Account 12345678 closed</p>");
        var closed = Word(pdf, "closed");

        var redacted = PdfRedactor.RedactText(pdf, "12345678");

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("12345678", text);
        Assert.Contains("Ada Lovelace", text);
        Assert.Contains("Account", text);
        var after = Word(redacted, "closed");
        Assert.Equal(closed.X, after.X, 2);
        Assert.Equal(closed.Y, after.Y, 2);
        // The digits are not in the file in any form: not in a content stream, nor in an older revision.
        Assert.DoesNotContain("12345678", AllBytes(redacted));
        PdfTestHelpers.AssertStructurallyValid(redacted);
    }

    [Fact]
    public void A_black_box_covers_the_area_by_default_and_none_is_drawn_without_a_fill()
    {
        var pdf = HtmlToPdf.Convert(Page + "<p style='top:40pt'>Secret</p>");
        var word = Word(pdf, "Secret");
        var area = new[] { PdfRedactionArea.Of(1, word) };
        int x = (int)(word.X + word.Width / 2), y = (int)(word.Y + word.Height / 2);

        Assert.Equal(0x000000u, Pixel(Render(PdfRedactor.Redact(pdf, area)), x, y));
        var blank = PdfRedactor.Redact(pdf, area, new PdfRedactOptions { FillColor = null });
        Assert.Equal(0xFFFFFFu, Pixel(Render(blank), x, y));
        Assert.Equal(0xFF0000u, Pixel(Render(PdfRedactor.Redact(pdf, area, new PdfRedactOptions { FillColor = "red" })), x, y));
    }

    [Fact]
    public void Image_pixels_under_the_area_are_overwritten_and_the_rest_of_the_image_stays()
    {
        var red = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(40, 20, 255, 0, 0));
        var pdf = HtmlToPdf.Convert(Page + $"<img src='{red}' style='position:absolute;left:100pt;top:100pt;width:200pt;height:100pt'>");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 100, 100, 100, 100) }, new PdfRedactOptions { FillColor = null });

        var image = Render(redacted);
        Assert.Equal(0xFFFFFFu, Pixel(image, 150, 150));
        Assert.Equal(0xFF0000u, Pixel(image, 250, 150));
        // The image the page draws now is a new one; the original, with its red pixels in the area, is not in the file.
        Assert.Single(PdfImages.Extract(redacted));
        // Only the copy and the soft mask that makes its area transparent are left.
        Assert.Equal(2, Streams(redacted, "Image"));
    }

    [Fact]
    public void An_image_wholly_inside_an_area_is_removed()
    {
        var red = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(4, 4, 255, 0, 0));
        var pdf = HtmlToPdf.Convert(Page + $"<img src='{red}' style='position:absolute;left:100pt;top:100pt;width:50pt;height:50pt'>");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 90, 90, 80, 80) });

        Assert.Empty(PdfImages.Extract(redacted));
        Assert.Equal(0, Streams(redacted, "Image"));
    }

    [Fact]
    public void The_original_of_a_redacted_scan_is_not_left_in_the_file()
    {
        // A scanned page: the image fills it, and an invisible text layer over its second pixel says what is there.
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /Font << /F1 4 0 R >> /XObject << /Scan 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("q 400 0 0 300 0 0 cm /Scan Do Q BT 3 Tr /F1 12 Tf 110 150 Td (123-45-6789) Tj ET"),
            SecretImage);

        var redacted = PdfRedactor.RedactText(pdf, "123-45-6789");

        Assert.DoesNotContain("123-45-6789", PdfText.Extract(redacted));
        Assert.Equal(1, Streams(redacted, "Image"));
        Assert.DoesNotContain("SECRETPIXELS", AllBytes(redacted));
        Assert.Equal(0x000000u, Pixel(Render(redacted), 150, 145));
        PdfTestHelpers.AssertStructurallyValid(redacted);
    }

    [Fact]
    public void Originals_named_in_shared_and_inherited_resources_go_and_what_other_pages_draw_stays()
    {
        // Both pages inherit one resource dictionary from the page tree. Page 1 draws /A inside the area and /B twice,
        // once in it and once outside; page 2 draws /C. /A goes from the file; /B, still shown on page 1, and /C stay.
        var other = SecretImage.Replace("SECRETPIXELS", "OTHERPIXELS!", StringComparison.Ordinal);
        var third = SecretImage.Replace("SECRETPIXELS", "THIRDPIXELS!", StringComparison.Ordinal);
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] /Resources << /XObject << /A 7 0 R /B 8 0 R /C 9 0 R >> >> /MediaBox [0 0 400 300] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            Stream("q 40 0 0 40 20 220 cm /A Do Q q 40 0 0 40 100 220 cm /B Do Q q 40 0 0 40 300 20 cm /B Do Q"),
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            Stream("q 40 0 0 40 20 20 cm /C Do Q"),
            SecretImage,
            other,
            third);

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 0, 0, 200, 100) });

        var all = AllBytes(redacted);
        Assert.DoesNotContain("SECRETPIXELS", all);
        Assert.Contains("OTHERPIXELS!", all);
        Assert.Contains("THIRDPIXELS!", all);
        Assert.NotEqual(0xFFFFFFu, Pixel(Render(redacted), 320, 260));
        Assert.NotEqual(0xFFFFFFu, Pixel(Render(redacted, 1), 40, 260));
        PdfTestHelpers.AssertStructurallyValid(redacted);
    }

    [Fact]
    public void The_original_of_a_redacted_form_is_not_left_in_the_file_even_when_the_structure_tree_refers_to_it()
    {
        var form = "<< /Type /XObject /Subtype /Form /BBox [0 0 300 50] /Resources << /Font << /F1 6 0 R >> >> /Length 38 >>\nstream\nBT /F1 12 Tf 0 10 Td (FormSecret) Tj ET\nendstream";
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 1 0 0 1 20 200 cm /X1 Do Q"),
            form,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K << /S /P /P 7 0 R /Pg 3 0 R /K << /Type /MCR /Stm 5 0 R /MCID 0 >> >> >>");

        var redacted = PdfRedactor.RedactText(pdf, "FormSecret");

        Assert.DoesNotContain("FormSecret", PdfText.Extract(redacted));
        Assert.DoesNotContain("FormSecret", AllBytes(redacted));
        PdfTestHelpers.AssertStructurallyValid(redacted);
    }

    [Fact]
    public void Text_a_form_draws_outside_its_box_is_removed()
    {
        // Clipped away by the form's box, but still found by text extraction.
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/X1 Do"),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /Font << /F1 6 0 R >> >> /Length 44 >>\nstream\nBT /F1 12 Tf 100 200 Td (ClippedSecret) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Assert.Contains("ClippedSecret", PdfText.Extract(pdf));

        var redacted = PdfRedactor.RedactText(pdf, "ClippedSecret");

        Assert.DoesNotContain("ClippedSecret", PdfText.Extract(redacted));
        Assert.DoesNotContain("ClippedSecret", AllBytes(redacted));
    }

    [Fact]
    public void Text_whose_font_is_missing_is_removed_when_it_could_reach_an_area()
    {
        // /F9 is not in the resources: viewers draw the text with a substitute font, so it must go all the same.
        var pdf = OnePage("BT /F9 12 Tf 100 160 Td (MissingFontSecret) Tj ET BT /F9 12 Tf 100 40 Td (FarAway) Tj ET BT /F1 12 Tf 20 100 Td (Kept) Tj ET");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 150, 128, 20, 12) });

        var all = AllBytes(redacted);
        Assert.DoesNotContain("MissingFontSecret", all);
        Assert.Contains("FarAway", all);
        Assert.Contains("Kept", PdfText.Extract(redacted));
    }

    [Fact]
    public void The_box_is_drawn_in_page_space_when_the_content_leaves_a_changed_transform_saved()
    {
        var pdf = OnePage("1 0 0 1 0 -100 cm q BT /F1 12 Tf 20 200 Td (Shifted SECRET) Tj ET");
        var word = Word(pdf, "SECRET");

        var redacted = PdfRedactor.Redact(pdf, new[] { PdfRedactionArea.Of(1, word) });

        Assert.DoesNotContain("SECRET", PdfText.Extract(redacted));
        Assert.Equal(0x000000u, Pixel(Render(redacted), (int)(word.X + word.Width / 2), (int)(word.Y + word.Height / 2)));
    }

    [Fact]
    public void Shapes_wholly_inside_an_area_go_and_shapes_crossing_it_stay()
    {
        // A small square inside the area and a long bar through it, in one path and in two.
        var pdf = OnePage("0 0 1 rg 50 150 20 20 re 150 150 200 10 re f 1 0 0 rg 50 50 20 20 re f");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 40, 120, 140, 40), new PdfRedactionArea(1, 40, 220, 40, 40) },
            new PdfRedactOptions { FillColor = null });

        var image = Render(redacted);
        Assert.Equal(0xFFFFFFu, Pixel(image, 60, 140)); // the blue square
        Assert.Equal(0x0000FFu, Pixel(image, 300, 145)); // the bar, outside the area
        Assert.Equal(0xFFFFFFu, Pixel(image, 60, 240)); // the red square, a path of its own
    }

    [Fact]
    public void Text_in_forms_is_redacted_in_a_copy_and_other_pages_drawing_the_form_keep_it()
    {
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 1 0 0 1 20 200 cm /X1 Do Q"),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 50] /Resources << /Font << /F1 6 0 R >> >> /Length 38 >>\nstream\nBT /F1 12 Tf 0 10 Td (Classified) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>");

        var areas = PdfRedactor.FindText(pdf, "Classified").Where(a => a.PageNumber == 1).ToList();
        Assert.Single(areas);
        var redacted = PdfRedactor.Redact(pdf, areas);

        var pages = PdfText.ExtractPages(redacted);
        Assert.DoesNotContain("Classified", pages[0].Text);
        Assert.Contains("Classified", pages[1].Text);
    }

    [Fact]
    public void Replacement_text_of_marked_content_that_lost_glyphs_goes_too()
    {
        var pdf = OnePage("BT /F1 12 Tf 20 200 Td /Span << /ActualText (Top secret) >> BDC (XXXXXXXX) Tj EMC ET");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 10, 80, 200, 30) });

        Assert.DoesNotContain("secret", PdfText.Extract(redacted));
        Assert.DoesNotContain("secret", AllBytes(redacted));
    }

    [Fact]
    public void Replacement_text_in_named_marked_content_properties_goes_too()
    {
        // The properties are named in the page's resources rather than written in the content.
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /Font << /F1 4 0 R >> /Properties << /P1 << /ActualText (Name: John 555-12-3456) >> >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 20 200 Td /Span /P1 BDC (Name: John XXXXXXXXXX) Tj EMC ET"));
        Assert.Contains("555-12-3456", PdfText.Extract(pdf));

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 86, 85, 214, 25) });

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("555", text);
        Assert.Contains("John", text);
        var file = PdfFile.Open(redacted, null);
        for (int i = 1; i <= file.MaxObjectNumber; i++)
            Assert.DoesNotContain("ActualText", file.GetObject(i)?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Invisible_and_letter_spaced_text_are_removed_and_text_after_them_keeps_its_place()
    {
        var pdf = OnePage("BT /F1 12 Tf 3 Tr 20 200 Td (hidden) Tj 0 Tr 2 Tc 0 -40 Td [(spaced) -3000 (after)] TJ ET");
        var after = Word(pdf, "after");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 10, 85, 70, 60) });

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("spaced", text);
        Assert.Equal(after.X, Word(redacted, "after").X, 2);
    }

    [Fact]
    public void Text_on_a_rotated_page_is_found_and_removed()
    {
        var pdf = OnePage("BT /F1 12 Tf 20 200 Td (Rotated secret) Tj ET", "/Rotate 90");

        var redacted = PdfRedactor.RedactText(pdf, "secret");

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("secret", text);
        Assert.Contains("Rotated", text);
    }

    [Fact]
    public void Links_and_form_fields_that_touch_an_area_are_removed_with_their_values()
    {
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] >> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>",
            Stream("0 g 0 0 1 1 re f"),
            "<< /Type /Annot /Subtype /Link /Rect [20 250 120 270] /A << /S /URI /URI (https://example.com/private) >> >>",
            "<< /FT /Tx /T (ssn) /V (078-05-1120) /Type /Annot /Subtype /Widget /Rect [20 200 120 220] /P 3 0 R >>",
            "<< /FT /Tx /T (city) /V (Copenhagen) /Type /Annot /Subtype /Widget /Rect [20 100 120 120] /P 3 0 R >>");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 0, 20, 200, 90) });

        var content = AllBytes(redacted);
        Assert.DoesNotContain("example.com/private", content);
        Assert.DoesNotContain("078-05-1120", content);
        var file = PdfFile.Open(redacted, null);
        var fields = file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")!;
        var city = Assert.IsType<PdfDict>(file.Resolve(Assert.Single(fields.Items)));
        Assert.Equal("Copenhagen", (file.Resolve(city["V"]) as PdfString)?.Text);
    }

    [Fact]
    public void A_regular_expression_finds_what_to_redact_and_the_same_input_gives_the_same_bytes()
    {
        var pdf = HtmlToPdf.Convert(Page + "<p style='top:40pt'>Mail ada@example.com or call +45 12 34 56 78</p>",
            new PdfOptions { CreationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        var email = new Regex(@"\S+@\S+");

        var first = PdfRedactor.RedactText(pdf, email);

        Assert.Equal(first, PdfRedactor.RedactText(pdf, email));
        var text = PdfText.Extract(first);
        Assert.DoesNotContain("example.com", text);
        Assert.Contains("+45 12 34 56 78", text);
    }

    [Fact]
    public void Areas_on_pages_past_the_last_and_encrypted_pdfs_are_refused()
    {
        var pdf = OnePage("BT /F1 12 Tf 20 200 Td (x) Tj ET");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(2, 0, 0, 10, 10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRedactionArea(0, 0, 0, 10, 10));
        Assert.Throws<ArgumentException>(() => PdfRedactor.Redact(pdf, Array.Empty<PdfRedactionArea>(), new PdfRedactOptions { FillColor = "not a colour" }));

        var encrypted = PdfProtector.Encrypt(pdf, new PdfSecurity { UserPassword = "u", OwnerPassword = "o" });
        Assert.Throws<ArgumentException>(() => PdfRedactor.Redact(encrypted, new[] { new PdfRedactionArea(1, 0, 0, 10, 10) }));
        Assert.Throws<InvalidDataException>(() => PdfRedactor.Redact(Encoding.ASCII.GetBytes("not a pdf"), Array.Empty<PdfRedactionArea>()));
    }
}
