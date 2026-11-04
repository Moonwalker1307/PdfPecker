using System.Text;
using PdfPecker.Rendering.Parsing;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// <see cref="PdfToPdfA"/>: PDFs as other tools write them, converted to PDF/A-2 and PDF/A-3, with what the conversion
/// changed and what it reports it could not fix. veraPDF checks the converted samples in CI (conformance.yml).
/// </summary>
public class PdfToPdfATests
{
    /// <summary>A PDF from numbered objects (the catalog first), with a classic cross-reference table.</summary>
    internal static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
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

    internal static string Stream(string content) => $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    /// <summary>
    /// A page as a simple tool writes it: Helvetica not embedded, a script that runs on opening, a square annotation
    /// with no appearance that does not print, and a colour in DeviceRGB without an output intent.
    /// </summary>
    private static byte[] Plain() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R /Annots [7 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /S /JavaScript /JS (app.alert\\('hi'\\);) >>",
        Stream("0.2 0.3 0.8 rg BT /F1 14 Tf 20 60 Td (Hello archive) Tj ET"),
        "<< /Type /Annot /Subtype /Square /Rect [10 10 120 40] /C [1 0 0] >>");

    private static string Xmp(byte[] pdf)
    {
        var text = Latin1(pdf);
        int start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        Assert.True(start >= 0, "The XMP metadata is not written uncompressed");
        return text[start..text.IndexOf("</x:xmpmeta>", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void A_plain_pdf_becomes_pdfa_2b_with_its_text_kept()
    {
        var result = PdfToPdfA.Convert(Plain());
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        var text = Latin1(result.Pdf);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.Contains("/OutputIntents", text);
        Assert.Contains("/GTS_PDFA1", text);
        Assert.Contains("/FontFile2", text);
        Assert.DoesNotContain("/JavaScript", text);
        Assert.DoesNotContain("/OpenAction", text);
        var xmp = Xmp(result.Pdf);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("Hello archive", PdfText.Extract(result.Pdf));
        Assert.Equal(1, PdfPages.Count(result.Pdf));

        // Each change is one sentence, and says what it did.
        Assert.Contains(result.Changes, c => c.Contains("Embedded 1 font", StringComparison.Ordinal) && c.Contains("Helvetica", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("output intent", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("script", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("to print", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("XMP", StringComparison.Ordinal));
    }

    [Fact]
    public void The_annotation_gets_an_appearance_and_prints()
    {
        var file = PdfFile.Open(PdfToPdfA.Convert(Plain()).Pdf, null);
        var page = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")![0])!;
        var annot = (PdfDict)file.Resolve(file.GetArray(page, "Annots")![0])!;
        Assert.Equal(4, file.GetInt(annot, "F", 0) & 4);
        Assert.NotNull(file.GetStream(file.GetDict(annot, "AP"), "N"));
    }

    [Fact]
    public void The_same_input_gives_the_same_bytes_and_a_converted_file_converts_again()
    {
        var first = PdfToPdfA.Convert(Plain());
        Assert.Equal(first.Pdf, PdfToPdfA.Convert(Plain()).Pdf);
        var again = PdfToPdfA.Convert(first.Pdf);
        Assert.True(again.IsConformant, string.Join("\n", again.Problems));
        Assert.Contains("Hello archive", PdfText.Extract(again.Pdf));
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b, 2, "B")]
    [InlineData(PdfConformance.PdfA2u, 2, "U")]
    [InlineData(PdfConformance.PdfA3b, 3, "B")]
    [InlineData(PdfConformance.PdfA3u, 3, "U")]
    public void PdfPecker_output_converts_to_every_level(PdfConformance conformance, int part, string level)
    {
        var pdf = HtmlToPdf.Convert("<h1>Report</h1><p style='color:#c03'>Total <b>42</b> <i>kr.</i></p><svg width='40' height='20'><rect width='40' height='20' fill='teal' opacity='.5'/></svg>");
        var result = PdfToPdfA.Convert(pdf, new PdfAConversionOptions { Conformance = conformance });
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        var xmp = Xmp(result.Pdf);
        Assert.Contains($"<pdfaid:part>{part}</pdfaid:part>", xmp);
        Assert.Contains($"<pdfaid:conformance>{level}</pdfaid:conformance>", xmp);
        AssertStructurallyValid(result.Pdf);
    }

    [Fact]
    public void Level_u_maps_every_character_to_unicode()
    {
        var result = PdfToPdfA.Convert(Plain(), new PdfAConversionOptions { Conformance = PdfConformance.PdfA2u });
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        Assert.Contains("/ToUnicode", Latin1(result.Pdf));
        Assert.Contains(result.Changes, c => c.Contains("level U", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_in_a_font_without_glyphs_to_embed_is_reported()
    {
        // A Japanese font that is not embedded, and whose characters PdfPecker's bundled fonts do not have.
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /MS-Gothic /Encoding /UniJIS-UCS2-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /MS-Gothic /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> /FontDescriptor 6 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /MS-Gothic /Flags 4 /FontBBox [0 -141 1000 859] /ItalicAngle 0 /Ascent 859 /Descent -141 /CapHeight 700 /StemV 80 >>",
            Stream("BT /F1 12 Tf 10 50 Td <65E5672C> Tj ET"));
        var result = PdfToPdfA.Convert(pdf);
        Assert.False(result.IsConformant);
        Assert.Contains(result.Problems, p => p.Contains("not embedded", StringComparison.Ordinal) && p.Contains("MS-Gothic", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("lack some of the characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdfa3_keeps_attachments_and_associates_them()
    {
        var options = new PdfOptions();
        options.Attachments.Add(new PdfAttachment("data.csv", Encoding.UTF8.GetBytes("a;b\n1;2\n")));
        var result = PdfToPdfA.Convert(HtmlToPdf.Convert("<p>With data</p>", options), new PdfAConversionOptions { Conformance = PdfConformance.PdfA3b });
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        var attachment = Assert.Single(PdfAttachments.List(result.Pdf));
        Assert.Equal("data.csv", attachment.FileName);
        var text = Latin1(result.Pdf);
        Assert.Contains("/AFRelationship", text);
        Assert.Contains("/AF [", text);
    }

    [Fact]
    public void Pdfa2_converts_pdf_attachments_and_removes_others()
    {
        var options = new PdfOptions();
        options.Attachments.Add(new PdfAttachment("data.csv", Encoding.UTF8.GetBytes("a;b\n1;2\n")));
        options.Attachments.Add(new PdfAttachment("inner.pdf", Plain()));
        var result = PdfToPdfA.Convert(HtmlToPdf.Convert("<p>With attachments</p>", options));
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        var inner = Assert.Single(PdfAttachments.List(result.Pdf));
        Assert.Equal("inner.pdf", inner.FileName);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", Xmp(inner.Data));
        Assert.Contains(result.Changes, c => c.Contains("Removed", StringComparison.Ordinal) && c.Contains("attachment", StringComparison.Ordinal));
    }

    [Fact]
    public void A_form_that_asks_viewers_to_draw_its_fields_gets_them_drawn()
    {
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Annots [5 0 R] /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Ann Archivist) /Rect [20 40 220 70] /F 4 /P 3 0 R /DA (/Helv 12 Tf 0 g) >>",
            Stream(""));
        var result = PdfToPdfA.Convert(pdf);
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        Assert.Contains(result.Changes, c => c.Contains("Drew the appearances", StringComparison.Ordinal));
        Assert.DoesNotContain("/NeedAppearances true", Latin1(result.Pdf));
        Assert.Contains("Ann Archivist", PdfText.Extract(result.Pdf));
    }

    [Fact]
    public void Encrypted_and_signed_pdfs_are_refused()
    {
        var encrypted = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encrypted-aes128.pdf"));
        Assert.Equal("pdf", Assert.Throws<ArgumentException>(() => PdfToPdfA.Convert(encrypted)).ParamName);
        using var cert = SignatureVerificationTests.SelfSigned("Archive Signer");
        var signed = PdfSigner.Sign(HtmlToPdf.Convert("<p>Signed</p>"), new PdfSignatureOptions(cert));
        Assert.Equal("pdf", Assert.Throws<ArgumentException>(() => PdfToPdfA.Convert(signed)).ParamName);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA4)]
    [InlineData(PdfConformance.None)]
    public void Other_levels_are_refused(PdfConformance conformance)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PdfToPdfA.Convert(Plain(), new PdfAConversionOptions { Conformance = conformance }));

    [Fact]
    public void Input_that_is_not_a_pdf_fails_with_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => PdfToPdfA.Convert(Encoding.ASCII.GetBytes("<html>not a pdf</html>")));
        Assert.Throws<InvalidDataException>(() => PdfToPdfA.Convert(Array.Empty<byte>()));
    }

    [Fact]
    public void The_stream_overload_writes_the_same_bytes()
    {
        using var input = new MemoryStream(Plain());
        using var output = new MemoryStream();
        var result = PdfToPdfA.Convert(input, output);
        Assert.Equal(result.Pdf, output.ToArray());
        Assert.Equal(PdfToPdfA.Convert(Plain()).Pdf, output.ToArray());
    }

    [Fact]
    public void Cancellation_stops_the_conversion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfToPdfA.Convert(Plain(), null, cts.Token));
    }
}
