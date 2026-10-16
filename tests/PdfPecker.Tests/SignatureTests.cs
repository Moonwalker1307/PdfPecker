using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

public class SignatureTests
{
    private const string Html = "<html><head><title>Signed</title></head><body><h1>Lønseddel</h1><p>Signed content.</p></body></html>";

    private static X509Certificate2 RsaCert(string cn = "Test Signer", bool timestamping = false)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}, O=PdfPecker Tests, C=DK", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        if (timestamping) req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PFX, the way certificates usually arrive.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 EcdsaCert()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=EC Signer", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw");
    }

    private static readonly Lazy<X509Certificate2> Cert = new(() => RsaCert());

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed record Sig(int[] Range, byte[] Contents, SignedCms Cms, string Dict);

    /// <summary>Finds every signature, checks its byte range covers the whole file but the hole, and verifies it with SignedCms.</summary>
    private static List<Sig> Verify(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var result = new List<Sig>();
        foreach (Match m in Regex.Matches(text, @"/ByteRange \[\s*(\d+) (\d+) (\d+) (\d+)\s*\] /Contents <([0-9A-F]+)>"))
        {
            var r = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
            Assert.Equal(0, r[0]);
            Assert.Equal('<', (char)pdf[r[1]]);
            Assert.Equal('>', (char)pdf[r[2] - 1]);
            int end = r[2] + r[3];
            Assert.True(end <= pdf.Length);
            var signedData = pdf.AsSpan(0, r[1]).ToArray().Concat(pdf.AsSpan(r[2], r[3]).ToArray()).ToArray();
            var contents = Convert.FromHexString(m.Groups[5].Value);
            var cms = new SignedCms(new ContentInfo(signedData), detached: true);
            cms.Decode(contents); // trailing zero padding is ignored by the DER reader
            cms.CheckSignature(verifySignatureOnly: true);
            int dictStart = text.LastIndexOf("obj", m.Index, StringComparison.Ordinal);
            int dictEnd = text.IndexOf("endobj", m.Index, StringComparison.Ordinal);
            result.Add(new Sig(r, contents, cms, text.Substring(dictStart, m.Index - dictStart) + text.Substring(r[2], dictEnd - r[2])));
        }
        return result;
    }

    [Fact]
    public void Unsigned_output_has_no_signature()
    {
        var pdf = HtmlToPdf.Convert(Html);
        Assert.DoesNotContain("/ByteRange", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void Signs_an_invisible_PAdES_signature_that_covers_the_whole_file()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(Cert.Value) { Reason = "Approved", Location = "København" },
        });
        var sig = Assert.Single(Verify(pdf));
        Assert.Equal(pdf.Length, sig.Range[2] + sig.Range[3]);
        Assert.Contains("/SubFilter /ETSI.CAdES.detached", sig.Dict);
        Assert.Equal(Cert.Value.Thumbprint, sig.Cms.SignerInfos[0].Certificate!.Thumbprint);

        var attrs = sig.Cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        Assert.Contains("1.2.840.113549.1.9.16.2.47", attrs); // signing-certificate-v2, required by PAdES
        Assert.DoesNotContain("1.2.840.113549.1.9.5", attrs); // PAdES forbids signing-time; /M carries it
        Assert.Equal("2.16.840.1.101.3.4.2.1", sig.Cms.SignerInfos[0].DigestAlgorithm.Value);

        // The file still opens; the field is in the AcroForm and on page 1.
        var file = PdfFile.Open(pdf, null);
        var acro = file.GetDict(file.Catalog, "AcroForm")!;
        Assert.Equal(3, file.GetInt(acro, "SigFlags", 0));
        var field = (PdfDict)file.Resolve(file.GetArray(acro, "Fields")![0])!;
        Assert.Equal("Signature1", ((PdfString)field["T"]!).Text);
        Assert.Equal("Sig", file.GetName(field, "FT"));
        Assert.Equal(new double[] { 0, 0, 0, 0 }, file.GetNumbers(field, "Rect"));
        Assert.Equal(1, PdfRenderer.Open(pdf).PageCount);
    }

    [Fact]
    public void Draws_a_visible_signature_box()
    {
        var pdf = HtmlToPdf.Convert("<p>x</p>", new PdfOptions
        {
            Signature = new PdfSignatureOptions(Cert.Value)
            {
                Reason = "Godkendt",
                Appearance = new PdfSignatureAppearance { Page = -1, X = 300, Y = 700, Width = 240, Height = 70 },
            },
        });
        Assert.Single(Verify(pdf));
        var file = PdfFile.Open(pdf, null);
        var field = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")![0])!;
        var rect = file.GetNumbers(field, "Rect")!;
        Assert.Equal(300, rect[0], 2);
        Assert.Equal(841.89 - 770, rect[1], 2);

        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int dark = 0;
        for (int y = 700; y < 770; y++)
            for (int x = 300; x < 540; x++)
                if (img.Rgba[(y * img.Width + x) * 4] < 128) dark++;
        Assert.True(dark > 400, $"the signature text should be drawn ({dark} dark pixels)");
        // Nothing outside the box.
        for (int x = 0; x < 290; x++) Assert.True(img.Rgba[(735 * img.Width + x) * 4] > 200);
    }

    [Fact]
    public void Signs_encrypted_files()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Security = new PdfSecurity { UserPassword = "kodeord" },
            Signature = new PdfSignatureOptions(Cert.Value)
            {
                Reason = "Secret reason",
                Appearance = new PdfSignatureAppearance(),
            },
        });
        var sig = Assert.Single(Verify(pdf));
        Assert.DoesNotContain("Secret reason", Encoding.Latin1.GetString(pdf));
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(pdf));

        var file = PdfFile.Open(pdf, "kodeord");
        var field = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")![0])!;
        Assert.Equal("Signature1", ((PdfString)field["T"]!).Text);
        var sigDict = (PdfDict)file.Resolve(field["V"])!;
        Assert.Equal("Secret reason", ((PdfString)sigDict["Reason"]!).Text);
        // /Contents is not encrypted: Verify above read it as plain hex.
        Assert.Equal(0x30, sig.Contents[0]);
        Assert.True(PdfRenderer.Open(pdf, "kodeord").RenderPage(0, new ImageOptions { Dpi = 20 }).Width > 0);
    }

    [Fact]
    public void Signs_an_existing_RC4_encrypted_file()
    {
        var signed = PdfSigner.Sign(Fixture("password.pdf"), new PdfSignatureOptions(Cert.Value) { Appearance = new PdfSignatureAppearance() }, "secret");
        Assert.Single(Verify(signed));
        Assert.True(PdfRenderer.Open(signed, "secret").RenderPage(0, new ImageOptions { Dpi = 20 }).Width > 0);
        Assert.Throws<PdfPasswordException>(() => PdfSigner.Sign(Fixture("password.pdf"), new PdfSignatureOptions(Cert.Value)));
    }

    [Fact]
    public void Signs_PDF_A_files_without_breaking_them()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Conformance = PdfConformance.PdfA2b,
            Signature = new PdfSignatureOptions(Cert.Value) { Appearance = new PdfSignatureAppearance() },
        });
        var sig = Assert.Single(Verify(pdf));
        var file = PdfFile.Open(pdf, null);
        Assert.NotNull(file.Catalog!["Metadata"]);
        Assert.NotNull(file.Catalog!["OutputIntents"]);
        Assert.Equal(132, file.GetInt((PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")![0])!, "F", 0));
        // The appearance uses no fonts, so nothing unembedded is introduced.
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Resources << >>", text.Substring(text.LastIndexOf("/Subtype /Form", StringComparison.Ordinal)));
        Assert.True(sig.Contents.Length <= 16383, "PDF/A limits strings to 32767 bytes");
    }

    [Fact]
    public void A_second_signature_keeps_the_first_valid()
    {
        var once = HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(Cert.Value) });
        var twice = PdfSigner.Sign(once, new PdfSignatureOptions(RsaCert("Second Signer")) { Appearance = new PdfSignatureAppearance() });
        Assert.Equal(once, twice.AsSpan(0, once.Length).ToArray());
        var sigs = Verify(twice);
        Assert.Equal(2, sigs.Count);
        Assert.Equal(once.Length, sigs[0].Range[2] + sigs[0].Range[3]);
        Assert.Equal(twice.Length, sigs[1].Range[2] + sigs[1].Range[3]);

        var file = PdfFile.Open(twice, null);
        var fields = file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")!;
        Assert.Equal(new[] { "Signature1", "Signature2" }, fields.Items.Select(f => ((PdfString)((PdfDict)file.Resolve(f)!)["T"]!).Text));
    }

    [Fact]
    public void Signs_with_ECDSA_and_the_classic_PKCS7_format()
    {
        using var ec = EcdsaCert();
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(ec) { Format = PdfSignatureFormat.Pkcs7Detached, HashAlgorithm = HashAlgorithmName.SHA384 },
        });
        var sig = Assert.Single(Verify(pdf));
        Assert.Contains("/SubFilter /adbe.pkcs7.detached", sig.Dict);
        Assert.Equal("2.16.840.1.101.3.4.2.2", sig.Cms.SignerInfos[0].DigestAlgorithm.Value);
        Assert.NotNull(sig.Cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().FirstOrDefault(a => a.Oid.Value == "1.2.840.113549.1.9.5"));
    }

    [Fact]
    public void Signs_files_with_cross_reference_streams()
    {
        var signed = PdfSigner.Sign(Fixture("objstm-cairo.pdf"), new PdfSignatureOptions(Cert.Value) { Appearance = new PdfSignatureAppearance() });
        Assert.Single(Verify(signed));
        var tail = Encoding.Latin1.GetString(signed, signed.Length - 600, 600);
        Assert.Contains("/Type /XRef", tail);
        var file = PdfFile.Open(signed, null);
        Assert.NotNull(file.GetDict(file.Catalog, "AcroForm"));
        Assert.True(PdfRenderer.Open(signed).RenderPage(0, new ImageOptions { Dpi = 20 }).Width > 0);
    }

    [Fact]
    public void Rejects_certificates_without_a_private_key()
    {
        using var pub = X509CertificateLoader.LoadCertificate(Cert.Value.RawData);
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(pub) }));
    }

    [Fact]
    public void Adds_an_RFC_3161_timestamp()
    {
        using var tsaCert = RsaCert("Test TSA", timestamping: true);
        var handler = new FakeTsa(tsaCert);
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(Cert.Value)
            {
                TimestampUrl = "http://tsa.example/",
                TimestampHttpClient = new HttpClient(handler),
            },
        });
        var sig = Assert.Single(Verify(pdf));
        Assert.Equal("application/timestamp-query", handler.ContentType);
        var unsigned = sig.Cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>().Single();
        Assert.Equal("1.2.840.113549.1.9.16.2.14", unsigned.Oid.Value);
        // The token covers the signature value.
        Assert.True(Rfc3161TimestampToken.TryDecode(unsigned.Values[0].RawData, out var token, out _));
        Assert.True(token!.VerifySignatureForSignerInfo(sig.Cms.SignerInfos[0], out _, new X509Certificate2Collection(tsaCert)));
    }

    [Fact]
    public void Rejects_a_timestamp_without_the_request_nonce()
    {
        using var tsaCert = RsaCert("Test TSA", timestamping: true);
        var client = new HttpClient(new FakeTsa(tsaCert, omitNonce: true));
        var ex = Assert.Throws<PdfSignatureException>(() => HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(Cert.Value) { TimestampUrl = "http://tsa.example/", TimestampHttpClient = client },
        }));
        Assert.Contains("nonce", ex.Message);
    }

    [Fact]
    public void Timestamp_failures_are_reported()
    {
        var client = new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError));
        var ex = Assert.Throws<PdfSignatureException>(() => HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(Cert.Value) { TimestampUrl = "http://tsa.example/", TimestampHttpClient = client },
        }));
        Assert.Contains("500", ex.Message);
    }

    private sealed class StatusHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => new(code);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>
    /// A minimal timestamp authority: answers a TimeStampReq with a token signed by its certificate, stating the current
    /// time or <c>time</c> when given, which lets a test play a dishonest authority that backdates its tokens.
    /// </summary>
    internal sealed class FakeTsa(X509Certificate2 cert, bool omitNonce = false, DateTimeOffset? time = null) : HttpMessageHandler
    {
        public string? ContentType;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ContentType = request.Content!.Headers.ContentType!.MediaType;
            var body = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            Assert.True(Rfc3161TimestampRequest.TryDecode(body, out var req, out _));

            var tst = new AsnWriter(AsnEncodingRules.DER);
            using (tst.PushSequence())
            {
                tst.WriteInteger(1);
                tst.WriteObjectIdentifier("1.2.3.4.1");
                using (tst.PushSequence())
                {
                    using (tst.PushSequence()) tst.WriteObjectIdentifier(req!.HashAlgorithmId.Value!);
                    tst.WriteOctetString(req.GetMessageHash().Span);
                }
                tst.WriteInteger(42);
                tst.WriteGeneralizedTime(time ?? DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                if (!omitNonce) tst.WriteInteger(req.GetNonce()!.Value.Span);
            }
            var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), tst.Encode()));
            var signer = new CmsSigner(cert);
            var essV2 = new AsnWriter(AsnEncodingRules.DER);
            using (essV2.PushSequence())
            using (essV2.PushSequence())
            using (essV2.PushSequence())
                essV2.WriteOctetString(SHA256.HashData(cert.RawData));
            signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", essV2.Encode()));
            cms.ComputeSignature(signer);

            var resp = new AsnWriter(AsnEncodingRules.DER);
            using (resp.PushSequence())
            {
                using (resp.PushSequence()) resp.WriteInteger(0);
                resp.WriteEncodedValue(cms.Encode());
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(resp.Encode()) };
        }
    }
}
