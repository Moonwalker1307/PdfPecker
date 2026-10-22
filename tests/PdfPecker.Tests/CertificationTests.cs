using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Certification (DocMDP) signatures: making them, and checking what changed after them.</summary>
public class CertificationTests
{
    private const string Html = "<html><head><title>Payslip</title></head><body><h1>Payslip October</h1><p>Net pay: 31.250,00 kr.</p></body></html>";

    private const string Form = """
        <!DOCTYPE html><html><body style="font:12px Helvetica">
        <h1>Application</h1>
        <p>Name <input type="text" name="name" style="width:200px"></p>
        <p><input type="checkbox" name="terms"> I agree</p>
        </body></html>
        """;

    private static readonly Lazy<X509Certificate2> Cert = new(() => SignatureVerificationTests.SelfSigned("Certifying Office"));
    private static readonly Lazy<X509Certificate2> Second = new(() => SignatureVerificationTests.SelfSigned("Approving Clerk"));

    private static PdfSignatureVerificationOptions Trusting()
        => new() { TrustedRoots = new X509Certificate2Collection { Cert.Value, Second.Value } };

    private static byte[] Certified(PdfCertificationLevel level, string html = Html, PdfOptions? options = null)
    {
        options ??= new PdfOptions();
        options.Signature = new PdfSignatureOptions(Cert.Value) { Certification = level, Reason = "Issued" };
        return HtmlToPdf.Convert(html, options);
    }

    private static IReadOnlyList<PdfSignatureVerification> Verify(byte[] pdf, string? password = null)
        => PdfSignatureVerifier.Verify(pdf, Trusting(), password);

    /// <summary>Appends an incremental update that replaces object <paramref name="num"/> with <paramref name="value"/>.</summary>
    private static byte[] Replace(byte[] pdf, int num, PdfObject value, params (int Num, PdfObject Value)[] added)
    {
        var file = PdfFile.Open(pdf, null);
        var update = new PdfUpdateWriter(file, pdf, 4096);
        update.WriteObject(num, 0, value);
        int next = Math.Max(file.GetInt(file.Trailer, "Size", 0), file.MaxObjectNumber + 1);
        foreach (var (n, v) in added)
        {
            if (v is PdfStream stream) update.WriteStream(n, 0, new Dictionary<string, PdfObject>(stream.Entries, StringComparer.Ordinal), stream.RawData);
            else update.WriteObject(n, 0, v);
            next = Math.Max(next, n + 1);
        }
        update.Finish((PdfRef)file.Trailer["Root"]!, next);
        return update.Output.Span.ToArray();
    }

    private static (PdfFile File, PdfRef PageRef, PdfDict Page) FirstPage(byte[] pdf)
    {
        var file = PdfFile.Open(pdf, null);
        var kids = file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!;
        var pageRef = (PdfRef)kids[0];
        return (file, pageRef, (PdfDict)file.Resolve(pageRef)!);
    }

    private static PdfDict Copy(PdfDict d) => new(new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal));

    [Theory]
    [InlineData(PdfCertificationLevel.NoChanges, 1)]
    [InlineData(PdfCertificationLevel.FormFilling, 2)]
    [InlineData(PdfCertificationLevel.FormFillingAndAnnotations, 3)]
    public void Certifies_at_each_level(PdfCertificationLevel level, int p)
    {
        var pdf = Certified(level);
        var v = Assert.Single(Verify(pdf));
        Assert.True(v.Valid, string.Join("\n", v.Problems));
        Assert.Equal(level, v.CertificationLevel);
        Assert.Empty(v.DisallowedChanges);

        var file = PdfFile.Open(pdf, null);
        var sig = file.GetDict(file.GetDict(file.Catalog, "Perms"), "DocMDP");
        Assert.NotNull(sig);
        var reference = (PdfDict)file.Resolve(file.GetArray(sig, "Reference")![0])!;
        Assert.Equal("SigRef", file.GetName(reference, "Type"));
        Assert.Equal("DocMDP", file.GetName(reference, "TransformMethod"));
        var parameters = file.GetDict(reference, "TransformParams")!;
        Assert.Equal(p, file.GetInt(parameters, "P", 0));
        Assert.Equal("1.2", file.GetName(parameters, "V"));
        // PAdES and ISO 32000-2 leave the digest entries out of the reference.
        Assert.Null(reference["DigestMethod"]);
    }

    [Fact]
    public void An_approval_signature_is_not_a_certification()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(Cert.Value) });
        var v = Assert.Single(Verify(pdf));
        Assert.Equal(PdfCertificationLevel.None, v.CertificationLevel);
        Assert.DoesNotContain("/DocMDP", Encoding.Latin1.GetString(pdf));
        Assert.Null(PdfFile.Open(pdf, null).Catalog!["Perms"]);
    }

    [Fact]
    public void Only_the_first_signature_can_certify()
    {
        var signed = HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(Second.Value) });
        var ex = Assert.Throws<PdfSignatureException>(() =>
            PdfSigner.Sign(signed, new PdfSignatureOptions(Cert.Value) { Certification = PdfCertificationLevel.FormFilling }));
        Assert.Contains("first signature", ex.Message);
        Assert.Throws<PdfSignatureException>(() =>
            PdfSigner.Sign(Certified(PdfCertificationLevel.FormFilling), new PdfSignatureOptions(Cert.Value) { Certification = PdfCertificationLevel.FormFilling }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfSigner.Sign(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Cert.Value) { Certification = (PdfCertificationLevel)4 }));
    }

    [Fact]
    public void No_changes_refuses_another_signature_but_takes_validation_data()
    {
        var pdf = Certified(PdfCertificationLevel.NoChanges);
        var ex = Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(pdf, new PdfSignatureOptions(Second.Value)));
        Assert.Contains("no changes allowed", ex.Message);

        using var tsa = SignatureVerificationTests.SelfSigned("Test TSA", timestamping: true);
        var ltv = PdfSigner.AddValidationData(pdf, new PdfLtvOptions
        {
            Certificates = new[] { Cert.Value },
            TimestampUrl = "http://tsa.example/",
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(tsa)),
        });
        var all = PdfSignatureVerifier.Verify(ltv, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection { Cert.Value, tsa } });
        Assert.Equal(2, all.Count);
        Assert.Equal(PdfCertificationLevel.NoChanges, all[0].CertificationLevel);
        Assert.Empty(all[0].DisallowedChanges);
        Assert.True(all[0].Valid, string.Join("\n", all[0].Problems));
        Assert.True(all[1].IsDocumentTimestamp);
        Assert.True(all[1].Valid, string.Join("\n", all[1].Problems));
    }

    [Fact]
    public void Form_filling_allows_filling_and_signing()
    {
        var pdf = Certified(PdfCertificationLevel.FormFilling, Form);
        var filled = PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = "Jane Q. Public", ["terms"] = "Yes" });
        var signed = PdfSigner.Sign(filled, new PdfSignatureOptions(Second.Value));
        var all = Verify(signed);
        Assert.Equal(2, all.Count);
        Assert.Equal(PdfCertificationLevel.FormFilling, all[0].CertificationLevel);
        Assert.Empty(all[0].DisallowedChanges);
        Assert.True(all[0].Valid, string.Join("\n", all[0].Problems));
        Assert.False(all[0].CoversWholeDocument);
        Assert.True(all[1].Valid, string.Join("\n", all[1].Problems));
        Assert.Equal(PdfCertificationLevel.None, all[1].CertificationLevel);

        // A new visible signature box would change the page, which the certification does not allow.
        var ex = Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(filled, new PdfSignatureOptions(Second.Value) { Appearance = new PdfSignatureAppearance() }));
        Assert.Contains("visible", ex.Message);
    }

    [Fact]
    public void Signs_the_fields_a_certified_template_has()
    {
        var certified = PdfSigner.Sign(SignatureFieldTests.Template(),
            new PdfSignatureOptions(Cert.Value) { FieldName = "Approver", Certification = PdfCertificationLevel.FormFilling });
        var signed = PdfSigner.Sign(certified, new PdfSignatureOptions(Second.Value) { FieldName = "Parties.Buyer" });
        var all = Verify(signed);
        Assert.Equal(2, all.Count);
        Assert.Equal("Approver", all[0].FieldName);
        Assert.Equal(PdfCertificationLevel.FormFilling, all[0].CertificationLevel);
        Assert.All(all, v => Assert.True(v.Valid, string.Join("\n", v.Problems)));
    }

    [Fact]
    public void A_visible_signature_box_added_later_is_reported()
    {
        var pdf = PdfSigner.Sign(Certified(PdfCertificationLevel.FormFilling), new PdfSignatureOptions(Second.Value));
        // Give the invisible second signature's widget an area, as another tool could have drawn it.
        var file = PdfFile.Open(pdf, null);
        var fields = file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")!;
        var widgetRef = (PdfRef)fields[1];
        var widget = Copy((PdfDict)file.Resolve(widgetRef)!);
        widget.Entries["Rect"] = new PdfArray(new List<PdfObject> { PdfNumber.Int(36), PdfNumber.Int(36), PdfNumber.Int(236), PdfNumber.Int(96) });
        var all = Verify(Replace(pdf, widgetRef.Num, widget));
        Assert.Contains(all[0].DisallowedChanges, c => c.StartsWith("A visible signature field was added", StringComparison.Ordinal));
        Assert.False(all[0].Valid);
    }

    [Fact]
    public void No_changes_reports_a_filled_in_field()
    {
        var pdf = Certified(PdfCertificationLevel.NoChanges, Form);
        var filled = PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = "Jane Q. Public" });
        var v = Assert.Single(Verify(filled));
        Assert.True(v.IsIntact);
        Assert.False(v.Valid);
        Assert.Contains(v.DisallowedChanges, c => c.Contains("\"name\"") && c.Contains("allows no changes"));
        Assert.All(v.DisallowedChanges, c => Assert.Contains(c, v.Problems));
    }

    [Theory]
    [InlineData(PdfCertificationLevel.FormFilling, false)]
    [InlineData(PdfCertificationLevel.FormFillingAndAnnotations, true)]
    public void Annotations_need_level_three(PdfCertificationLevel level, bool allowed)
    {
        var v = Assert.Single(Verify(Annotated(level)));
        Assert.Equal(allowed, v.Valid);
        if (allowed) Assert.Empty(v.DisallowedChanges);
        else Assert.Contains(v.DisallowedChanges, c => c.StartsWith("An annotation was added to page 1", StringComparison.Ordinal));
    }

    /// <summary>A certified PDF with a comment added to its page afterwards.</summary>
    private static byte[] Annotated(PdfCertificationLevel level)
    {
        var pdf = Certified(level);
        var (file, pageRef, page) = FirstPage(pdf);
        int note = Math.Max(file.GetInt(file.Trailer, "Size", 0), file.MaxObjectNumber + 1);
        var annots = new List<PdfObject>();
        if (file.GetArray(page, "Annots") is { } old) annots.AddRange(old.Items);
        annots.Add(new PdfRef(note, 0));
        var edited = Copy(page);
        edited.Entries["Annots"] = new PdfArray(annots);
        var comment = new PdfDict();
        comment.Entries["Type"] = PdfName.Get("Annot");
        comment.Entries["Subtype"] = PdfName.Get("Text");
        comment.Entries["Rect"] = new PdfArray(new List<PdfObject> { PdfNumber.Int(50), PdfNumber.Int(50), PdfNumber.Int(70), PdfNumber.Int(70) });
        comment.Entries["Contents"] = new PdfString(Encoding.ASCII.GetBytes("Looks right"));
        return Replace(pdf, pageRef.Num, edited, (note, comment));
    }

    [Theory]
    [InlineData(PdfCertificationLevel.NoChanges)]
    [InlineData(PdfCertificationLevel.FormFilling)]
    [InlineData(PdfCertificationLevel.FormFillingAndAnnotations)]
    public void Changed_page_content_breaks_every_level(PdfCertificationLevel level)
    {
        var v = Assert.Single(Verify(ChangedContent(level)));
        Assert.True(v.IsIntact);
        Assert.False(v.Valid);
        Assert.Contains("Page 1 (/Contents) was changed after the certification, " + DocMdpAllows(level) + ".", v.DisallowedChanges);
    }

    /// <summary>A certified PDF whose page shows other text after an update.</summary>
    private static byte[] ChangedContent(PdfCertificationLevel level)
    {
        var pdf = Certified(level);
        var (file, pageRef, page) = FirstPage(pdf);
        int content = Math.Max(file.GetInt(file.Trailer, "Size", 0), file.MaxObjectNumber + 1);
        var edited = Copy(page);
        edited.Entries["Contents"] = new PdfRef(content, 0);
        var stream = new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal), Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 720 Td (Net pay: 99.999,00 kr.) Tj ET"));
        return Replace(pdf, pageRef.Num, edited, (content, stream));
    }

    private static string DocMdpAllows(PdfCertificationLevel level) => PdfPecker.Signing.DocMdp.Allows((int)level);

    [Fact]
    public void A_changed_catalog_or_object_is_reported()
    {
        var pdf = Certified(PdfCertificationLevel.FormFillingAndAnnotations);
        var file = PdfFile.Open(pdf, null);
        var rootRef = (PdfRef)file.Trailer["Root"]!;
        var catalog = Copy(file.Catalog!);
        catalog.Entries["OpenAction"] = new PdfArray(new List<PdfObject> { file.GetArray(file.GetDict(catalog, "Pages"), "Kids")![0], PdfName.Get("Fit") });
        var v = Assert.Single(Verify(Replace(pdf, rootRef.Num, catalog)));
        Assert.Contains(v.DisallowedChanges, c => c.StartsWith("The document catalog's /OpenAction", StringComparison.Ordinal));

        // Dropping the certification from /Perms is a change too.
        catalog = Copy(file.Catalog!);
        catalog.Entries.Remove("Perms");
        var dropped = Replace(pdf, rootRef.Num, catalog);
        v = Assert.Single(Verify(dropped));
        Assert.Equal(PdfCertificationLevel.None, v.CertificationLevel); // the newest catalog no longer names it
    }

    [Fact]
    public void Certifies_encrypted_and_PDF_A_documents()
    {
        var encrypted = Certified(PdfCertificationLevel.FormFilling, Form, new PdfOptions { Security = new PdfSecurity { UserPassword = "open" } });
        var filled = PdfForm.Fill(encrypted, new Dictionary<string, string> { ["name"] = "Secret" }, new PdfFormFillOptions { Password = "open" });
        var signed = PdfSigner.Sign(filled, new PdfSignatureOptions(Second.Value), "open");
        var all = Verify(signed, "open");
        Assert.Equal(2, all.Count);
        Assert.Equal(PdfCertificationLevel.FormFilling, all[0].CertificationLevel);
        Assert.All(all, v => Assert.True(v.Valid, string.Join("\n", v.Problems)));

        var archived = Certified(PdfCertificationLevel.NoChanges, Html, new PdfOptions { Conformance = PdfConformance.PdfA2b });
        var a = Assert.Single(Verify(archived));
        Assert.True(a.Valid, string.Join("\n", a.Problems));
        Assert.Equal(PdfCertificationLevel.NoChanges, a.CertificationLevel);
    }

    [Fact]
    public void Certifies_with_the_classic_PKCS7_format()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(Cert.Value) { Certification = PdfCertificationLevel.FormFilling, Format = PdfSignatureFormat.Pkcs7Detached },
        });
        var v = Assert.Single(Verify(pdf));
        Assert.Equal("adbe.pkcs7.detached", v.SubFilter);
        Assert.Equal(PdfCertificationLevel.FormFilling, v.CertificationLevel);
        Assert.True(v.Valid, string.Join("\n", v.Problems));
    }
}
