using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Adding, changing and removing password protection on existing PDFs.</summary>
public class ProtectorTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string Html = "<html lang=\"en\"><head><title>Protected sample</title></head><body style=\"font-family:sans-serif\">"
        + "<h1>Chapter one</h1><p>First page text.</p><p>Name <input type=\"text\" name=\"name\" value=\"Jane Doe\"></p>"
        + "<p><input type=\"checkbox\" name=\"news\" checked> News</p>"
        + "<h1 style=\"page-break-before:always\">Chapter two</h1><p>Second page text with a <a href=\"https://example.com\">link</a>.</p></body></html>";

    private static readonly Lazy<byte[]> Source = new(() => HtmlToPdf.Convert(Html, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 }));

    private static PdfSecurity Security(PdfPermissions permissions = PdfPermissions.All)
        => new() { UserPassword = "open", OwnerPassword = "admin", Permissions = permissions };

    private static uint[][] Render(byte[] pdf, string? password)
    {
        var r = PdfRenderer.Open(pdf, password);
        return Enumerable.Range(0, r.PageCount).Select(i => r.RenderPage(i, new ImageOptions { Dpi = 36 }).Premultiplied).ToArray();
    }

    private static void AssertSameRendering(uint[][] expected, uint[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(expected[i].AsSpan().SequenceEqual(actual[i]), $"page {i + 1} renders differently");
    }

    private static PdfDict? EncryptDict(byte[] pdf) => PdfFile.Open(pdf, "admin").Resolve(PdfFile.Open(pdf, "admin").Trailer["Encrypt"]) as PdfDict;

    /// <summary>Checks the bookmarks, form fields, tags, document information and metadata of the sample document.</summary>
    private static void AssertDocumentIntact(byte[] pdf, string? password, bool xmp = true)
    {
        var file = PdfFile.Open(pdf, password);
        var catalog = file.Catalog!;
        Assert.Equal(2, file.GetInt(file.GetDict(catalog, "Pages"), "Count", 0));

        var titles = new List<string>();
        for (var item = file.GetDict(file.GetDict(catalog, "Outlines"), "First"); item != null && titles.Count < 10; item = file.GetDict(item, "Next"))
            titles.Add(((PdfString)file.Resolve(item["Title"])!).Text);
        Assert.Equal(new[] { "Chapter one", "Chapter two" }, titles);

        var fields = file.GetArray(file.GetDict(catalog, "AcroForm"), "Fields")!.Items.Select(f => (PdfDict)file.Resolve(f)!).ToList();
        var name = fields.Single(f => ((PdfString)file.Resolve(f["T"])!).Text == "name");
        Assert.Equal("Jane Doe", ((PdfString)file.Resolve(name["V"])!).Text);
        var news = fields.Single(f => ((PdfString)file.Resolve(f["T"])!).Text == "news");
        Assert.Equal("Yes", file.GetName(news, "V"));

        Assert.NotNull(file.GetDict(catalog, "StructTreeRoot"));
        Assert.Equal("en", ((PdfString)file.Resolve(catalog["Lang"])!).Text);
        Assert.Equal("Protected sample", ((PdfString)file.Resolve(file.GetDict(file.Trailer, "Info")!["Title"])!).Text);
        if (xmp) Assert.Contains("Protected sample", Encoding.UTF8.GetString(file.DecodeStream((PdfStream)file.Resolve(catalog["Metadata"])!)));
    }

    [Fact]
    public void Encrypts_and_decrypts_an_existing_pdf()
    {
        var source = Source.Value;
        var encrypted = PdfProtector.Encrypt(source, Security());
        PdfTestHelpers.AssertStructurallyValid(encrypted);
        Assert.True(PdfProtector.IsEncrypted(encrypted));
        Assert.False(PdfProtector.IsEncrypted(source));
        Assert.DoesNotContain("Protected sample", PdfTestHelpers.Latin1(encrypted));
        var enc = EncryptDict(encrypted)!;
        Assert.Equal(5, ((PdfNumber)enc["V"]!).IntValue);
        Assert.Equal(6, ((PdfNumber)enc["R"]!).IntValue);
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(encrypted));

        var expected = Render(source, null);
        AssertSameRendering(expected, Render(encrypted, "open"));
        AssertSameRendering(expected, Render(encrypted, "admin"));
        AssertDocumentIntact(encrypted, "open");

        foreach (var password in new[] { "open", "admin" })
        {
            var decrypted = PdfProtector.Decrypt(encrypted, password);
            PdfTestHelpers.AssertStructurallyValid(decrypted);
            Assert.False(PdfProtector.IsEncrypted(decrypted));
            Assert.DoesNotContain("/Encrypt", PdfTestHelpers.Latin1(decrypted));
            AssertSameRendering(expected, Render(decrypted, null));
            AssertDocumentIntact(decrypted, null);
        }
    }

    [Fact]
    public void Wrong_or_missing_passwords_are_refused()
    {
        var encrypted = PdfProtector.Encrypt(Source.Value, Security());
        Assert.Throws<PdfPasswordException>(() => PdfProtector.Decrypt(encrypted, "wrong"));
        Assert.Throws<PdfPasswordException>(() => PdfProtector.Decrypt(encrypted, null));
        Assert.Throws<PdfPasswordException>(() => PdfProtector.Encrypt(encrypted, new PdfSecurity { UserPassword = "x" }));
        Assert.Throws<PdfPasswordException>(() => PdfProtector.Encrypt(encrypted, new PdfSecurity { UserPassword = "x" }, "wrong"));
        Assert.Throws<PdfPasswordException>(() => PdfProtector.Decrypt(Fixture("encrypted-aes128.pdf"), "wrong"));
    }

    [Fact]
    public void Changes_the_password_of_an_encrypted_pdf()
    {
        var encrypted = PdfProtector.Encrypt(Source.Value, Security());
        var changed = PdfProtector.Encrypt(encrypted, new PdfSecurity { UserPassword = "new" }, "admin");
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(changed, "open"));
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(changed, "admin"));
        AssertSameRendering(Render(Source.Value, null), Render(changed, "new"));
        AssertDocumentIntact(changed, "new");
    }

    public static IEnumerable<object[]> LegacyFixtures() => new[]
    {
        new object[] { "encrypted-aes128.pdf", "user", "owner", 4 },
        new object[] { "encrypted-rc4-40.pdf", "user", "owner", 1 },
        new object[] { "password.pdf", "secret", "owner", 2 },
    };

    [Theory]
    [MemberData(nameof(LegacyFixtures))]
    public void Decrypts_and_reencrypts_rc4_and_aes128_inputs(string name, string user, string owner, int v)
    {
        var input = Fixture(name);
        var file = PdfFile.Open(input, user);
        Assert.Equal(v, file.GetInt((PdfDict)file.Resolve(file.Trailer["Encrypt"])!, "V", 0));
        var expected = Render(input, user);

        foreach (var password in new[] { user, owner })
        {
            var decrypted = PdfProtector.Decrypt(input, password);
            // The input's version is kept (these say 1.3); the helper only knows the versions this library writes.
            Assert.Equal(input[..9], decrypted[..9]);
            var check = decrypted.ToArray();
            "%PDF-1.4"u8.CopyTo(check);
            PdfTestHelpers.AssertStructurallyValid(check);
            Assert.False(PdfProtector.IsEncrypted(decrypted));
            AssertSameRendering(expected, Render(decrypted, null));
            if (name != "password.pdf") AssertDocumentIntact(decrypted, null, xmp: false); // made from a tagged, not PDF/UA, copy
        }

        var reencrypted = PdfProtector.Encrypt(input, Security(PdfPermissions.Print), owner);
        PdfTestHelpers.AssertStructurallyValid(reencrypted); // AES-256 needs PDF 1.7 (extension level 8)
        Assert.Equal(6, ((PdfNumber)EncryptDict(reencrypted)!["R"]!).IntValue);
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(reencrypted, user));
        AssertSameRendering(expected, Render(reencrypted, "open"));
        if (name != "password.pdf") AssertDocumentIntact(reencrypted, "open", xmp: false);
        Assert.NotEmpty(PdfToImage.Convert(reencrypted, new ImageOptions { Password = "admin", Dpi = 10 }));
    }

    [Fact]
    public void Permissions_are_written_and_can_be_changed()
    {
        var restricted = PdfProtector.Encrypt(Source.Value, new PdfSecurity { Permissions = PdfPermissions.Print | PdfPermissions.FillForms | PdfPermissions.ExtractForAccessibility });
        // No user password: anyone can open it, and the permissions are in the encryption dictionary.
        var file = PdfFile.Open(restricted, null);
        var p = (uint)file.GetInt((PdfDict)file.Resolve(file.Trailer["Encrypt"])!, "P", 0);
        Assert.Equal((uint)(PdfPermissions.Print | PdfPermissions.FillForms | PdfPermissions.ExtractForAccessibility), p & (uint)PdfPermissions.All);
        AssertSameRendering(Render(Source.Value, null), Render(restricted, null));

        // The random owner password cannot be guessed, but opening the file is enough to change or remove the protection.
        var relaxed = PdfProtector.Encrypt(restricted, new PdfSecurity { Permissions = PdfPermissions.All });
        file = PdfFile.Open(relaxed, null);
        p = (uint)file.GetInt((PdfDict)file.Resolve(file.Trailer["Encrypt"])!, "P", 0);
        Assert.Equal((uint)PdfPermissions.All, p & (uint)PdfPermissions.All);
        Assert.False(PdfProtector.IsEncrypted(PdfProtector.Decrypt(restricted, null)));
    }

    [Fact]
    public void Encrypted_output_renders_with_PdfToImage()
    {
        var encrypted = PdfProtector.Encrypt(Source.Value, Security());
        Assert.Equal(2, PdfToImage.Convert(encrypted, new ImageOptions { Password = "open", Dpi = 20 }).Length);
        Assert.Throws<PdfPasswordException>(() => PdfToImage.Convert(encrypted, new ImageOptions { Dpi = 20 }));
    }

    [Fact]
    public void Stream_overloads_round_trip()
    {
        using var input = new MemoryStream(Source.Value);
        using var encrypted = new MemoryStream();
        PdfProtector.Encrypt(input, encrypted, Security());
        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfProtector.Decrypt(encrypted, decrypted, "admin");
        AssertSameRendering(Render(Source.Value, null), Render(decrypted.ToArray(), null));
        AssertDocumentIntact(decrypted.ToArray(), null);
    }

    [Fact]
    public void Keeps_page_labels_and_attachments()
    {
        // A third-party-style file: page labels, an embedded file, an object only reachable through the attachment.
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /r /P (Intro-) >>] >> /Names << /EmbeddedFiles << /Names [(data.csv) 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            "<< /Length 32 >>\nstream\n1 0 0 rg 20 20 160 160 re f    \nendstream",
            "<< /Type /Filespec /F (data.csv) /UF (data.csv) /EF << /F 6 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 11 >>\nstream\na,b\n1,2\n3,4\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        var pdf = Encoding.Latin1.GetBytes(sb.ToString());

        var encrypted = PdfProtector.Encrypt(pdf, Security());
        Assert.DoesNotContain("a,b", PdfTestHelpers.Latin1(encrypted));
        foreach (var (bytes, password) in new[] { (encrypted, "open"), (PdfProtector.Decrypt(encrypted, "admin"), (string?)null) })
        {
            var file = PdfFile.Open(bytes, password);
            var label = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "PageLabels"), "Nums")![1])!;
            Assert.Equal("r", file.GetName(label, "S"));
            Assert.Equal("Intro-", ((PdfString)file.Resolve(label["P"])!).Text);
            var names = file.GetArray(file.GetDict(file.GetDict(file.Catalog, "Names"), "EmbeddedFiles"), "Names")!;
            Assert.Equal("data.csv", ((PdfString)file.Resolve(names[0])!).Text);
            var ef = file.GetStream(file.GetDict((PdfDict)file.Resolve(names[1])!, "EF"), "F")!;
            Assert.Equal("a,b\n1,2\n3,4", Encoding.ASCII.GetString(file.DecodeStream(ef)));
        }
    }

    [Fact]
    public void Decrypting_an_unencrypted_pdf_returns_it_unchanged()
    {
        var source = Source.Value;
        Assert.Equal(source, PdfProtector.Decrypt(source, null));
    }

    [Fact]
    public void Refuses_pdfa_and_signed_inputs()
    {
        var pdfa = HtmlToPdf.Convert("<p>Archive</p>", new PdfOptions { Conformance = PdfConformance.PdfA2b });
        var ex = Assert.Throws<ArgumentException>(() => PdfProtector.Encrypt(pdfa, Security()));
        Assert.Contains("PDF/A", ex.Message);

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var signed = PdfSigner.Sign(Source.Value, new PdfSignatureOptions(cert));
        ex = Assert.Throws<ArgumentException>(() => PdfProtector.Encrypt(signed, Security()));
        Assert.Contains("signed", ex.Message);
        var signedEncrypted = PdfSigner.Sign(PdfProtector.Encrypt(Source.Value, Security()), new PdfSignatureOptions(cert), "admin");
        Assert.Throws<ArgumentException>(() => PdfProtector.Decrypt(signedEncrypted, "admin"));

        Assert.Throws<ArgumentException>(() => PdfProtector.Encrypt(Source.Value, new PdfSecurity { Permissions = PdfPermissions.Print }));
    }
}
