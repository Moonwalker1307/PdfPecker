using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

public class AttachmentTests
{
    private const string Html = "<html><head><title>Report</title></head><body><h1>Report</h1><p>See the attachments.</p></body></html>";

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Invoice() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "einvoice", "en16931.xml"));

    private static PdfOptions WithFiles(params PdfAttachment[] files)
    {
        var options = new PdfOptions();
        foreach (var f in files) options.Attachments.Add(f);
        return options;
    }

    [Fact]
    public void Attachments_are_listed_sorted_and_extracted_with_exact_bytes()
    {
        var csv = "id;amount\n1;10.50\n"u8.ToArray();
        var binary = Random(20_000, 1);
        var created = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
        var modified = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var pdf = HtmlToPdf.Convert(Html, WithFiles(
            new PdfAttachment("data.csv", csv) { Description = "The figures", Relationship = PdfAttachmentRelationship.Data },
            new PdfAttachment("Ærø.bin", binary) { MimeType = "application/x-test", CreationDate = created, ModDate = modified },
            new PdfAttachment("appendix.txt", "Hello"u8.ToArray())));

        var list = PdfAttachments.List(pdf);
        // Sorted by key bytes: ASCII names first, then the UTF-16 one.
        Assert.Equal(new[] { "appendix.txt", "data.csv", "Ærø.bin" }, list.Select(a => a.FileName));
        Assert.Equal(csv, list[1].Data);
        Assert.Equal("The figures", list[1].Description);
        Assert.Equal("text/csv", list[1].MimeType);
        Assert.Equal(PdfAttachmentRelationship.Data, list[1].Relationship);
        Assert.Equal(binary, list[2].Data);
        Assert.Equal("application/x-test", list[2].MimeType);
        Assert.Equal(created, list[2].CreationDate);
        Assert.Equal(modified, list[2].ModDate);
        Assert.Equal("Ærø.bin", list[2].Description); // the file name stands in
        Assert.Equal("text/plain", list[0].MimeType);
        Assert.NotNull(list[0].ModDate);

        Assert.Equal(binary, PdfAttachments.Extract(pdf, "Ærø.bin"));
        Assert.Null(PdfAttachments.Extract(pdf, "missing.txt"));
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
    }

    [Fact]
    public void Name_tree_is_sorted_and_files_carry_size_dates_and_md5_checksum()
    {
        var data = "zeta"u8.ToArray();
        var pdf = PdfTestHelpers.ConvertUncompressed(Html, WithFiles(
            new PdfAttachment("z.txt", data), new PdfAttachment("a.txt", "alpha"u8.ToArray()), new PdfAttachment("M.txt", "m"u8.ToArray())));
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Matches(@"/Names << /EmbeddedFiles << /Names \[\(M\.txt\) \d+ 0 R \(a\.txt\) \d+ 0 R \(z\.txt\) \d+ 0 R\] >> >>", text);
        Assert.Matches(@"/AF \[\d+ 0 R \d+ 0 R \d+ 0 R\]", text);
        Assert.Contains("/Type /Filespec /F (z.txt) /UF (z.txt) /Desc (z.txt) /AFRelationship /Unspecified", text);
        var md5 = Convert.ToHexString(MD5.HashData(data));
        Assert.Matches(@"/Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size 4 /ModDate \(D:\d{14}Z\) /CheckSum <" + md5 + "> >>", text);
    }

    [Fact]
    public void Duplicate_file_names_are_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert(Html, WithFiles(
            new PdfAttachment("a.txt", new byte[1]), new PdfAttachment("a.txt", new byte[2]))));
        Assert.Contains("\"a.txt\"", ex.Message);
        Assert.Throws<ArgumentException>(() => new PdfAttachment("", new byte[1]));
        Assert.Throws<ArgumentException>(() => new PdfAttachment("dir/a.txt", new byte[1]));
    }

    [Fact]
    public void Pdfa3_attachments_are_associated_files_with_their_relationship()
    {
        var options = WithFiles(
            new PdfAttachment("source.docx", Random(500, 2)) { Relationship = PdfAttachmentRelationship.Source },
            new PdfAttachment("extra.pdf", Random(300, 3)) { Relationship = PdfAttachmentRelationship.Supplement });
        options.Conformance = PdfConformance.PdfA3b;
        var pdf = PdfTestHelpers.ConvertUncompressed(Html, options);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", text);
        Assert.Matches(@"/AF \[\d+ 0 R \d+ 0 R\]", text);
        Assert.Contains("/F (source.docx) /UF (source.docx) /Desc (source.docx) /AFRelationship /Source", text);
        Assert.Contains("/AFRelationship /Supplement", text);
        Assert.Contains("/Subtype /application#2Fvnd.openxmlformats-officedocument.wordprocessingml.document", text);
        Assert.Contains("/Subtype /application#2Fpdf", text);
        Assert.Equal(new[] { PdfAttachmentRelationship.Supplement, PdfAttachmentRelationship.Source },
            PdfAttachments.List(pdf).Select(a => a.Relationship));
    }

    [Fact]
    public void Pdfa2b_refuses_attachments_with_a_clear_message()
    {
        var options = WithFiles(new PdfAttachment("a.txt", new byte[1]));
        options.Conformance = PdfConformance.PdfA2b;
        var ex = Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert(Html, options));
        Assert.Contains("PDF/A-2b allows attaching only PDF/A files", ex.Message);
        Assert.Contains("PdfA3b", ex.Message);
        // Without PDF/A the file stays a plain PDF: attachments do not raise the conformance.
        var plain = PdfTestHelpers.Latin1(HtmlToPdf.Convert(Html, WithFiles(new PdfAttachment("a.txt", new byte[1]))));
        Assert.DoesNotContain("pdfaid", plain);
    }

    [Fact]
    public void Encrypted_output_encrypts_the_attachments_too()
    {
        var secret = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("TOP-SECRET-PAYLOAD ", 10)));
        var options = WithFiles(new PdfAttachment("secret.txt", secret) { Description = "Secret notes" });
        options.Security = new PdfSecurity { UserPassword = "pw", OwnerPassword = "owner" };
        options.Compress = false;
        var pdf = HtmlToPdf.Convert(Html, options);
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.DoesNotContain("TOP-SECRET", text);
        Assert.DoesNotContain("secret.txt", text);
        Assert.DoesNotContain("Secret notes", text);
        Assert.Throws<PdfPasswordException>(() => PdfAttachments.List(pdf));
        foreach (var password in new[] { "pw", "owner" })
        {
            var a = Assert.Single(PdfAttachments.List(pdf, password));
            Assert.Equal("secret.txt", a.FileName);
            Assert.Equal("Secret notes", a.Description);
            Assert.Equal(secret, a.Data);
        }
        Assert.Equal(1, PdfRenderer.Open(pdf, "pw").PageCount);
    }

    [Fact]
    public void Pdfua_file_specifications_have_a_description()
    {
        var options = WithFiles(new PdfAttachment("notes.txt", "n"u8.ToArray()));
        options.Accessibility = PdfAccessibility.PdfUA1;
        var text = PdfTestHelpers.Latin1(PdfTestHelpers.ConvertUncompressed(Html, options));
        Assert.Contains("/Type /Filespec /F (notes.txt) /UF (notes.txt) /Desc (notes.txt)", text);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", text);
    }

    [Fact]
    public void E_invoice_is_one_of_the_attachments()
    {
        var xml = Invoice();
        var options = WithFiles(new PdfAttachment("zz-timesheet.csv", "h;1"u8.ToArray()), new PdfAttachment("contract.pdf", Random(100, 4)));
        options.EInvoice = new PdfEInvoice(xml);
        var pdf = PdfTestHelpers.ConvertUncompressed("<p>x</p>", options);
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", text);
        Assert.Contains("<fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>", text);
        Assert.Matches(@"/Names \[\(contract\.pdf\) \d+ 0 R \(factur-x\.xml\) \d+ 0 R \(zz-timesheet\.csv\) \d+ 0 R\]", text);
        // The invoice keeps exactly its earlier dictionary (no checksum).
        Assert.Matches(@"/Type /EmbeddedFile /Subtype /text#2Fxml /Params << /Size " + xml.Length + @" /ModDate \(D:\d{14}Z\) >>", text);
        Assert.Contains("/Desc (Factur-X invoice) /AFRelationship /Alternative", text);
        var list = PdfAttachments.List(pdf);
        Assert.Equal(xml, list.Single(a => a.FileName == "factur-x.xml").Data);
        Assert.Equal(PdfAttachmentRelationship.Alternative, list[1].Relationship);

        var clash = WithFiles(new PdfAttachment("factur-x.xml", xml));
        clash.EInvoice = new PdfEInvoice(xml);
        Assert.Contains("e-invoice", Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", clash)).Message);
    }

    [Fact]
    public void Attachments_can_be_added_to_an_existing_pdf_as_an_incremental_update()
    {
        var original = HtmlToPdf.Convert(Html, WithFiles(new PdfAttachment("b.txt", "bee"u8.ToArray())));
        var data = Random(5000, 5);
        var updated = PdfAttachments.Add(original, new PdfAttachment("c.bin", data), new PdfAttachment("a.txt", "ay"u8.ToArray()));
        Assert.Equal(original, updated.AsSpan(0, original.Length).ToArray());
        var list = PdfAttachments.List(updated);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.bin" }, list.Select(a => a.FileName));
        Assert.Equal("bee"u8.ToArray(), list[1].Data);
        Assert.Equal(data, list[2].Data);
        Assert.Equal(1, PdfRenderer.Open(updated).PageCount);
        // A second update on top of the first.
        var again = PdfAttachments.Add(updated, new PdfAttachment("d.txt", "dee"u8.ToArray()));
        Assert.Equal(4, PdfAttachments.List(again).Count);
        Assert.Contains("\"b.txt\"", Assert.Throws<ArgumentException>(() => PdfAttachments.Add(again, new PdfAttachment("b.txt", new byte[1]))).Message);
        Assert.Same(original, PdfAttachments.Add(original));
    }

    [Fact]
    public void Adding_to_an_encrypted_pdf_keeps_it_encrypted()
    {
        var original = HtmlToPdf.Convert(Html, new PdfOptions { Security = new PdfSecurity { UserPassword = "pw" } });
        Assert.Throws<PdfPasswordException>(() => PdfAttachments.Add(original, new PdfAttachment("x.txt", new byte[1])));
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("CONFIDENTIAL ", 3)));
        var updated = PdfAttachments.Add(original, new[] { new PdfAttachment("x.txt", payload) { Description = "Hidden" } }, "pw");
        Assert.DoesNotContain("CONFIDENTIAL", PdfTestHelpers.Latin1(updated));
        Assert.DoesNotContain("Hidden", PdfTestHelpers.Latin1(updated));
        var a = Assert.Single(PdfAttachments.List(updated, "pw"));
        Assert.Equal(payload, a.Data);
        Assert.Equal("Hidden", a.Description);
        Assert.Equal(1, PdfRenderer.Open(updated, "pw").PageCount);
    }

    [Fact]
    public void Adding_respects_pdfa()
    {
        var a3 = HtmlToPdf.Convert(Html, new PdfOptions { Conformance = PdfConformance.PdfA3b });
        var updated = PdfAttachments.Add(a3, new PdfAttachment("data.xml", "<a/>"u8.ToArray()) { Relationship = PdfAttachmentRelationship.Data });
        var tail = PdfTestHelpers.Latin1(updated)[a3.Length..];
        Assert.Matches(@"/AF \[\d+ 0 R\]", tail);
        Assert.Contains("/AFRelationship /Data", tail);
        Assert.Equal(PdfAttachmentRelationship.Data, Assert.Single(PdfAttachments.List(updated)).Relationship);

        var a2 = HtmlToPdf.Convert(Html, new PdfOptions { Conformance = PdfConformance.PdfA2b });
        var ex = Assert.Throws<ArgumentException>(() => PdfAttachments.Add(a2, new PdfAttachment("data.xml", new byte[1])));
        Assert.Contains("PDF/A-2", ex.Message);
    }

    [Fact]
    public void Merging_keeps_attachments_and_renames_clashes()
    {
        var one = HtmlToPdf.Convert("<p>1</p>", WithFiles(new PdfAttachment("data.csv", "1"u8.ToArray())));
        var two = HtmlToPdf.Convert("<p>2</p>", WithFiles(new PdfAttachment("data.csv", "2"u8.ToArray()), new PdfAttachment("x.txt", "x"u8.ToArray())));
        var merged = PdfMerger.Merge(one, two);
        PdfTestHelpers.AssertStructurallyValid(merged);
        var raw = PdfAttachments.List(merged);
        Assert.Equal(3, raw.Count);
        Assert.Equal(new[] { "1", "2", "x" }, raw.Select(a => Encoding.ASCII.GetString(a.Data)).OrderBy(s => s));
        // The second "data.csv" is listed as "data (2).csv", which sorts first.
        var text = PdfTestHelpers.Latin1(merged);
        var m = Regex.Match(text, @"/EmbeddedFiles << /Names \[(.*?)\] >>");
        Assert.True(m.Success);
        Assert.Matches(@"^<" + Hex("data (2).csv") + @"> \d+ 0 R <" + Hex("data.csv") + @"> \d+ 0 R <" + Hex("x.txt") + @"> \d+ 0 R $", m.Groups[1].Value);
    }

    private static string Hex(string s) => Convert.ToHexString(Encoding.ASCII.GetBytes(s)).ToLowerInvariant();
}
