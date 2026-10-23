using System.Text;

namespace PdfPecker.Tests;

public class DocumentPropertiesTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly DateTimeOffset Created = new(2026, 10, 22, 8, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// A small PDF written by hand, without a cross-reference table (the reader rebuilds it), with the given catalog
    /// entries, information dictionary and XMP packet.
    /// </summary>
    private static byte[] HandMade(string header = "1.4", string catalog = "", string? info = null, string? xmp = null)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-").Append(header).Append('\n');
        sb.Append("1 0 obj << /Type /Catalog /Pages 2 0 R").Append(xmp != null ? " /Metadata 4 0 R " : " ").Append(catalog).Append(" >> endobj\n");
        sb.Append("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        sb.Append("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >> endobj\n");
        if (xmp != null)
            sb.Append("4 0 obj << /Type /Metadata /Subtype /XML /Length ").Append(Encoding.UTF8.GetByteCount(xmp))
              .Append(" >> stream\n").Append(xmp).Append("\nendstream endobj\n");
        if (info != null) sb.Append("5 0 obj << ").Append(info).Append(" >> endobj\n");
        sb.Append("trailer << /Root 1 0 R").Append(info != null ? " /Info 5 0 R" : "").Append(" >>\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Packet(string body) =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
        + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" + body + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    [Fact]
    public void Reads_back_the_properties_a_conversion_writes()
    {
        var pdf = HtmlToPdf.Convert("<p>One</p><p style='break-before: page'>Two</p>", new PdfOptions
        {
            Title = "Lønseddel oktober",
            Author = "MyApp",
            Subject = "Payslip",
            Keywords = "payslip, 2026",
            Creator = "MyApp 3.1",
            Producer = "MyApp PDF",
            CreationDate = Created,
            CustomProperties = { ["EmployeeId"] = "42", ["Company"] = "Ærø A/S", ["Afdeling ø"] = "Løn" },
        });

        var p = PdfDocumentProperties.Read(pdf);

        Assert.Equal("Lønseddel oktober", p.Title);
        Assert.Equal("MyApp", p.Author);
        Assert.Equal("Payslip", p.Subject);
        Assert.Equal("payslip, 2026", p.Keywords);
        Assert.Equal("MyApp 3.1", p.Creator);
        Assert.Equal("MyApp PDF", p.Producer);
        Assert.Null(p.Language); // written only for a tagged PDF
        Assert.Equal(Created, p.CreationDate);
        Assert.Equal(3, p.CustomProperties.Count);
        Assert.Equal("Løn", p.CustomProperties["Afdeling ø"]);
        Assert.Equal("42", p.CustomProperties["EmployeeId"]);
        Assert.Equal("Ærø A/S", p.CustomProperties["Company"]);
        Assert.Equal(Encoding.ASCII.GetString(pdf, 5, 3), p.Version);
        Assert.Equal(2, p.PageCount);
        Assert.All(p.Pages, page =>
        {
            Assert.Equal(595.28, page.Width, 1);
            Assert.Equal(841.89, page.Height, 1);
            Assert.Equal(0, page.Rotation);
        });
        Assert.Equal(new[] { 1, 2 }, p.Pages.Select(x => x.PageNumber));
        Assert.False(p.IsEncrypted);
        Assert.Equal(PdfPermissions.All, p.Permissions);
        Assert.Null(p.PdfAConformance);
        Assert.Null(p.PdfUAConformance);
        Assert.False(p.IsTagged);
        Assert.False(p.IsLinearized);
        Assert.Null(p.XmpMetadata);
    }

    [Fact]
    public void A_plain_conversion_names_PdfPecker_as_producer_and_has_no_title()
    {
        var p = PdfDocumentProperties.Read(HtmlToPdf.Convert("<p>x</p>"));
        Assert.Null(p.Title);
        Assert.Equal("PdfPecker", p.Producer);
        Assert.Empty(p.CustomProperties);
        Assert.NotNull(p.CreationDate);
    }

    [Fact]
    public void Reads_PdfA_and_PdfUA_claims_and_tagging()
    {
        var pdf = HtmlToPdf.Convert("<h1>Report</h1><p>Text</p>", new PdfOptions
        {
            Title = "Report", Language = "en-GB", Conformance = PdfConformance.PdfA3u, Accessibility = PdfAccessibility.PdfUA1,
        });
        var p = PdfDocumentProperties.Read(pdf);
        Assert.Equal("PDF/A-3u", p.PdfAConformance);
        Assert.Equal("PDF/UA-1", p.PdfUAConformance);
        Assert.True(p.IsTagged);
        Assert.Equal("en-GB", p.Language);
        Assert.Contains("pdfaid:part", p.XmpMetadata);
    }

    [Fact]
    public void A_PdfA4_file_without_an_information_dictionary_is_read_from_its_XMP_metadata()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions
        {
            Title = "Archive copy", Author = "MyApp", Subject = "Invoice 17", Keywords = "invoice", Creator = "MyApp 3.1",
            CreationDate = Created, Conformance = PdfConformance.PdfA4,
            CustomProperties = { ["InvoiceNo"] = "17" },
        });
        Assert.DoesNotMatch(@"/Info \d", PdfTestHelpers.Latin1(pdf));

        var p = PdfDocumentProperties.Read(pdf);

        Assert.Equal("2.0", p.Version);
        Assert.Equal("PDF/A-4", p.PdfAConformance);
        Assert.Equal("Archive copy", p.Title);
        Assert.Equal("MyApp", p.Author);
        Assert.Equal("Invoice 17", p.Subject);
        Assert.Equal("invoice", p.Keywords);
        Assert.Equal("MyApp 3.1", p.Creator);
        Assert.Equal("PdfPecker", p.Producer);
        Assert.Equal(Created, p.CreationDate);
        Assert.Equal(Created, p.ModificationDate);
        Assert.Equal("17", Assert.Single(p.CustomProperties).Value);
    }

    [Fact]
    public void Encrypted_files_need_their_password_and_report_their_permissions()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions
        {
            Title = "Secret",
            Security = new PdfSecurity { UserPassword = "user", OwnerPassword = "owner", Permissions = PdfPermissions.Print | PdfPermissions.Copy },
        });
        Assert.Throws<PdfPasswordException>(() => PdfDocumentProperties.Read(pdf));
        Assert.Throws<PdfPasswordException>(() => PdfDocumentProperties.Read(pdf, "wrong"));

        foreach (var password in new[] { "user", "owner" })
        {
            var p = PdfDocumentProperties.Read(pdf, password);
            Assert.Equal("Secret", p.Title);
            Assert.True(p.IsEncrypted);
            Assert.Equal(PdfPermissions.Print | PdfPermissions.Copy, p.Permissions);
        }

        // A file that only restricts permissions opens without a password.
        var restricted = PdfDocumentProperties.Read(HtmlToPdf.Convert("<p>x</p>", new PdfOptions
        {
            Title = "Open", Security = new PdfSecurity { Permissions = PdfPermissions.Print },
        }));
        Assert.Equal("Open", restricted.Title);
        Assert.True(restricted.IsEncrypted);
        Assert.Equal(PdfPermissions.Print, restricted.Permissions);
    }

    [Theory]
    [InlineData("third-party/libreoffice.pdf", null, "1.7", false)]
    [InlineData("third-party/libreoffice-aes256.pdf", "user", "1.7", true)]
    [InlineData("third-party/libreoffice-rc4-128.pdf", "owner", "1.7", true)]
    [InlineData("third-party/chromium.pdf", null, "1.4", false)]
    [InlineData("third-party/ghostscript.pdf", null, "1.7", false)]
    [InlineData("third-party/reportlab.pdf", null, "1.4", false)]
    [InlineData("encrypted-rc4-40.pdf", "user", "1.3", true)]
    [InlineData("encrypted-aes128.pdf", "owner", "1.3", true)]
    public void Reads_files_from_other_writers(string name, string? password, string version, bool encrypted)
    {
        var p = PdfDocumentProperties.Read(Fixture(name), password);
        Assert.Equal(version, p.Version);
        Assert.Equal(encrypted, p.IsEncrypted);
        Assert.True(p.PageCount > 0);
        Assert.Equal(PdfPages.Count(Fixture(name), password), p.PageCount);
        Assert.NotNull(p.Producer);
        Assert.All(p.Pages, page => Assert.True(page.Width > 0 && page.Height > 0));
    }

    [Fact]
    public void Linearized_files_are_recognised_until_an_update_changes_them()
    {
        var linearized = Fixture("third-party/chromium-qpdf.pdf");
        Assert.True(PdfDocumentProperties.Read(linearized).IsLinearized);
        Assert.False(PdfDocumentProperties.Read(Fixture("third-party/chromium.pdf")).IsLinearized);

        // An incremental update makes the file longer than its linearization dictionary says.
        var updated = PdfAttachments.Add(linearized, new PdfAttachment("note.txt", Encoding.ASCII.GetBytes("note")));
        Assert.False(PdfDocumentProperties.Read(updated).IsLinearized);
    }

    [Fact]
    public void Rotated_pages_report_their_displayed_size()
    {
        var pdf = PdfPages.Rotate(HtmlToPdf.Convert("<p>a</p><p style='break-before: page'>b</p>"), 90, "2");
        var p = PdfDocumentProperties.Read(pdf);
        Assert.Equal(0, p.Pages[0].Rotation);
        Assert.Equal(90, p.Pages[1].Rotation);
        Assert.Equal(p.Pages[0].Width, p.Pages[1].Height, 2);
        Assert.Equal(p.Pages[0].Height, p.Pages[1].Width, 2);
        Assert.Equal("Page 2: 841.89 x 595.28 pt", p.Pages[1].ToString());
    }

    [Fact]
    public void Stream_and_file_overloads_read_the_same()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Title = "Overloads" });
        Assert.Equal("Overloads", PdfDocumentProperties.Read(new MemoryStream(pdf)).Title);
        var path = Path.Combine(Path.GetTempPath(), "pdfpecker-properties-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            Assert.Equal("Overloads", PdfDocumentProperties.ReadFile(path).Title);
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Throws<ArgumentNullException>(() => PdfDocumentProperties.Read((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => PdfDocumentProperties.Read((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => PdfDocumentProperties.ReadFile(null!));
    }

    [Fact]
    public void Newer_XMP_metadata_wins_over_the_information_dictionary()
    {
        var xmp = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">"
            + "<dc:title><rdf:Alt><rdf:li xml:lang=\"de\">Neu</rdf:li><rdf:li xml:lang=\"x-default\">New</rdf:li></rdf:Alt></dc:title>"
            + "<xmp:MetadataDate>2026-10-22T10:00:00+02:00</xmp:MetadataDate></rdf:Description>");
        var info = "/Title (Old) /Author (Info author) /ModDate (D:20250101000000Z)";
        var p = PdfDocumentProperties.Read(HandMade(info: info, xmp: xmp));
        Assert.Equal("New", p.Title);
        Assert.Equal("Info author", p.Author); // the XMP metadata does not have one
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), p.ModificationDate);

        // When the information dictionary is the newer, it wins.
        var older = PdfDocumentProperties.Read(HandMade(info: "/Title (Old) /ModDate (D:20270101000000Z)", xmp: xmp));
        Assert.Equal("Old", older.Title);
    }

    [Fact]
    public void XMP_properties_may_be_attributes_arrays_or_text()
    {
        var xmp = Packet(
            "<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\""
            + " xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdf:Producer=\"Attribute producer\" pdfaid:part=\"2\" pdfaid:conformance=\"B\""
            + " xmp:CreateDate=\"2024-05\"/>"
            + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdfx=\"http://ns.adobe.com/pdfx/1.3/\">"
            + "<dc:creator><rdf:Seq><rdf:li>Ann</rdf:li><rdf:li>Bo</rdf:li></rdf:Seq></dc:creator>"
            + "<dc:description><rdf:Alt><rdf:li xml:lang=\"en\">Only English</rdf:li></rdf:Alt></dc:description>"
            + "<pdfx:Order><![CDATA[A&B]]></pdfx:Order></rdf:Description>");
        var p = PdfDocumentProperties.Read(HandMade(header: "1.4", catalog: "/Version /2.0 /Lang (fr)", xmp: xmp));
        Assert.Equal("Attribute producer", p.Producer);
        Assert.Equal("PDF/A-2b", p.PdfAConformance);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), p.CreationDate);
        Assert.Equal("Ann; Bo", p.Author);
        Assert.Equal("Only English", p.Subject);
        Assert.Equal("A&B", p.CustomProperties["Order"]);
        Assert.Equal("2.0", p.Version);
        Assert.Equal("fr", p.Language);
        Assert.Equal(200, Assert.Single(p.Pages).Width);
    }

    [Theory]
    [InlineData("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Cut off")]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><x>&b;</x>")]
    [InlineData("not xml at all <<<")]
    [InlineData("")]
    public void Damaged_XMP_metadata_leaves_the_information_dictionary(string xmp)
    {
        var p = PdfDocumentProperties.Read(HandMade(info: "/Title (From info) /CreationDate (D:2026)", xmp: xmp));
        Assert.Equal("From info", p.Title);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), p.CreationDate);
        Assert.Null(p.PdfAConformance);
    }

    [Theory]
    [InlineData("/Title 42 /Author /Named /Subject (Ends with nul\0\0) /CreationDate (yesterday) /Custom [1 2]")]
    [InlineData("/Title () /Keywords <> /ModDate (D:20261399999999)")]
    public void Odd_information_entries_are_skipped_or_cleaned(string info)
    {
        var p = PdfDocumentProperties.Read(HandMade(info: info));
        Assert.Null(p.Title);
        Assert.Null(p.Keywords);
        Assert.Null(p.CreationDate);
        Assert.Null(p.ModificationDate);
        Assert.Empty(p.CustomProperties);
        if (info.Contains("Named", StringComparison.Ordinal))
        {
            Assert.Equal("Named", p.Author);
            Assert.Equal("Ends with nul", p.Subject);
        }
    }

    [Theory]
    [InlineData("%PDF-x.y")]
    [InlineData("%PDF-")]
    public void A_damaged_header_has_no_version(string header)
    {
        var pdf = HandMade();
        var bytes = Encoding.ASCII.GetBytes(header + "\n" + Encoding.ASCII.GetString(pdf).Split('\n', 2)[1]);
        Assert.Null(PdfDocumentProperties.Read(bytes).Version);
    }

    [Fact]
    public void Not_a_PDF_is_invalid_data()
        => Assert.Throws<InvalidDataException>(() => PdfDocumentProperties.Read(Encoding.ASCII.GetBytes("hello")));
}
