using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>
/// Signing with a key the library never sees, the way an HSM, a smart card or a cloud key vault signs: the test keeps the
/// key in memory and hands PdfPecker only the public certificate and a callback.
/// </summary>
public class ExternalSignerTests
{
    private const string Html = "<html><head><title>Payslip</title></head><body><h1>Lønseddel</h1><p>Signed externally.</p></body></html>";

    private sealed class RsaKey : IDisposable
    {
        public readonly RSA Key = RSA.Create(2048);
        public readonly X509Certificate2 WithKey;
        public readonly X509Certificate2 PublicOnly;

        public RsaKey(string cn = "HSM Signer")
        {
            var req = new CertificateRequest($"CN={cn}, O=PdfPecker Tests", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            WithKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            PublicOnly = X509CertificateLoader.LoadCertificate(WithKey.RawData);
        }

        public void Dispose()
        {
            Key.Dispose();
            WithKey.Dispose();
            PublicOnly.Dispose();
        }
    }

    private static readonly Lazy<RsaKey> Rsa = new(() => new RsaKey());

    private static (ECDsa Key, X509Certificate2 PublicOnly) EcKey(ECCurve curve)
    {
        var key = ECDsa.Create(curve);
        var req = new CertificateRequest("CN=Cloud EC Signer", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (key, X509CertificateLoader.LoadCertificate(cert.RawData));
    }

    /// <summary>Like a cloud key vault's "sign digest" operation: it sees only the hash.</summary>
    private static byte[] SignDigestPkcs1(PdfSignatureRequest r)
        => Rsa.Value.Key.SignHash(r.Digest.ToArray(), r.HashAlgorithm, RSASignaturePadding.Pkcs1);

    private static SignedCms VerifySingle(byte[] pdf)
    {
        var m = Regex.Matches(Encoding.Latin1.GetString(pdf), @"/ByteRange \[\s*(\d+) (\d+) (\d+) (\d+)\s*\] /Contents <([0-9A-F]+)>").Single();
        var r = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
        Assert.Equal(pdf.Length, r[2] + r[3]);
        var signed = pdf.AsSpan(0, r[1]).ToArray().Concat(pdf.AsSpan(r[2], r[3]).ToArray()).ToArray();
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(Convert.FromHexString(m.Groups[5].Value));
        cms.CheckSignature(verifySignatureOnly: true);
        return cms;
    }

    [Fact]
    public void An_RSA_digest_signer_gives_the_same_file_as_the_private_key()
    {
        var time = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(2));
        var unsigned = HtmlToPdf.Convert(Html);
        int calls = 0;
        var external = PdfSigner.Sign(unsigned, new PdfSignatureOptions(Rsa.Value.PublicOnly, new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, r =>
        {
            calls++;
            Assert.Equal(HashAlgorithmName.SHA256, r.HashAlgorithm);
            Assert.Equal(PdfSignatureAlgorithm.RsaPkcs1, r.Algorithm);
            Assert.Equal(SHA256.HashData(r.Data.Span), r.Digest.ToArray());
            Assert.Equal(0x31, r.Data.Span[0]); // the signed attributes, DER-encoded as a SET
            return SignDigestPkcs1(r);
        }))
        { SigningTime = time, Reason = "Approved" });
        var local = PdfSigner.Sign(unsigned, new PdfSignatureOptions(Rsa.Value.WithKey) { SigningTime = time, Reason = "Approved" });

        Assert.Equal(1, calls);
        var cms = VerifySingle(external);
        Assert.Equal(Rsa.Value.PublicOnly.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);
        Assert.Equal("1.2.840.113549.1.1.1", cms.SignerInfos[0].SignatureAlgorithm.Value);
        // PKCS#1 v1.5 is deterministic, so signing through the callback must not change a single byte.
        Assert.Equal(local, external);
    }

    [Fact]
    public async Task An_asynchronous_RSA_PSS_signer_with_a_visible_box()
    {
        var pdf = HtmlToPdf.Convert(Html);
        var options = new PdfSignatureOptions(Rsa.Value.PublicOnly, new PdfExternalSigner(PdfSignatureAlgorithm.RsaPss, async (r, ct) =>
        {
            await Task.Yield(); // a network round trip to the key vault
            return Rsa.Value.Key.SignHash(r.Digest.ToArray(), r.HashAlgorithm, RSASignaturePadding.Pss);
        }))
        {
            HashAlgorithm = HashAlgorithmName.SHA384,
            Appearance = new PdfSignatureAppearance { X = 300, Y = 700, Width = 240, Height = 70 },
        };
        var signed = await PdfSigner.SignAsync(pdf, options);
        var cms = VerifySingle(signed);
        Assert.Equal("1.2.840.113549.1.1.10", cms.SignerInfos[0].SignatureAlgorithm.Value);
        Assert.Equal("2.16.840.1.101.3.4.2.2", cms.SignerInfos[0].DigestAlgorithm.Value);

        var img = PdfRenderer.Open(signed).RenderPage(0, new ImageOptions { Dpi = 72 });
        int dark = 0;
        for (int y = 700; y < 770; y++)
            for (int x = 300; x < 540; x++)
                if (img.Rgba[(y * img.Width + x) * 4] < 128) dark++;
        Assert.True(dark > 400, $"the signature box should be drawn ({dark} dark pixels)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ECDSA_signatures_are_accepted_raw_or_DER_encoded(bool der)
    {
        var (key, cert) = EcKey(ECCurve.NamedCurves.nistP256);
        using (key)
        using (cert)
        {
            var signer = new PdfExternalSigner(PdfSignatureAlgorithm.Ecdsa, r => der
                ? key.SignHash(r.Digest.ToArray(), DSASignatureFormat.Rfc3279DerSequence) // AWS KMS style
                : key.SignHash(r.Digest.ToArray())); // Azure Key Vault style: r||s
            var pdf = HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(cert, signer) });
            var cms = VerifySingle(pdf);
            Assert.Equal("1.2.840.10045.4.3.2", cms.SignerInfos[0].SignatureAlgorithm.Value);
            // CMS always stores the DER form.
            Assert.Equal(0x30, cms.SignerInfos[0].GetSignature()[0]);
        }
    }

    [Fact]
    public void Signs_an_existing_field_with_a_timestamp_and_the_classic_format()
    {
        using var tsaCert = TsaCert();
        var signed = PdfSigner.Sign(SignatureFieldTests.Template(), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, SignDigestPkcs1))
        {
            FieldName = "Parties.Buyer",
            Format = PdfSignatureFormat.Pkcs7Detached,
            TimestampUrl = "http://tsa.example/",
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(tsaCert)),
        });
        var cms = VerifySingle(signed);
        Assert.Contains("/SubFilter /adbe.pkcs7.detached", Encoding.Latin1.GetString(signed));
        var unsignedAttr = cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>().Single();
        Assert.True(Rfc3161TimestampToken.TryDecode(unsignedAttr.Values[0].RawData, out var token, out _));
        Assert.True(token!.VerifySignatureForSignerInfo(cms.SignerInfos[0], out _, new X509Certificate2Collection(tsaCert)));

        var file = PdfFile.Open(signed, null);
        var buyer = (PdfDict)file.Resolve(new PdfRef(7, 0))!;
        Assert.IsType<PdfDict>(file.Resolve(buyer["V"]));
    }

    [Fact]
    public async Task PAdES_with_an_async_signer_and_an_async_timestamp()
    {
        using var tsaCert = TsaCert();
        var signed = await PdfSigner.SignAsync(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, (r, _) => ValueTask.FromResult(SignDigestPkcs1(r))))
        {
            TimestampUrl = "http://tsa.example/",
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(tsaCert)),
        });
        var cms = VerifySingle(signed);
        Assert.Contains("/SubFilter /ETSI.CAdES.detached", Encoding.Latin1.GetString(signed));
        var attrs = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        Assert.Contains("1.2.840.113549.1.9.16.2.47", attrs);
        Assert.Single(cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>());
    }

    [Fact]
    public void The_synchronous_API_waits_for_an_asynchronous_signer()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Signature = new PdfSignatureOptions(Rsa.Value.PublicOnly, new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, async (r, ct) =>
            {
                await Task.Delay(10, ct);
                return SignDigestPkcs1(r);
            })),
        });
        VerifySingle(pdf);
    }

    [Fact]
    public void A_signature_from_the_wrong_key_is_rejected()
    {
        using var other = RSA.Create(2048);
        var ex = Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, r => other.SignHash(r.Digest.ToArray(), r.HashAlgorithm, RSASignaturePadding.Pkcs1)))));
        Assert.Contains("does not verify", ex.Message);

        // The right key with the wrong padding is just as broken.
        Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, r => Rsa.Value.Key.SignHash(r.Digest.ToArray(), r.HashAlgorithm, RSASignaturePadding.Pss)))));
        Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, _ => Array.Empty<byte>()))));
    }

    [Fact]
    public void A_key_type_mismatch_fails_before_the_signer_is_called()
    {
        bool called = false;
        Assert.Throws<ArgumentException>(() => PdfSigner.Sign(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.Ecdsa, _ => { called = true; return new byte[64]; }))));
        Assert.False(called);
    }

    [Fact]
    public async Task Cancellation_reaches_the_signer()
    {
        using var cts = new CancellationTokenSource();
        var task = PdfSigner.SignAsync(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
            new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<byte>();
            })), cancellationToken: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void A_second_external_signature_keeps_the_first_valid()
    {
        var (key, ecCert) = EcKey(ECCurve.NamedCurves.nistP384);
        using (key)
        using (ecCert)
        {
            var once = PdfSigner.Sign(HtmlToPdf.Convert(Html), new PdfSignatureOptions(Rsa.Value.PublicOnly,
                new PdfExternalSigner(PdfSignatureAlgorithm.RsaPkcs1, SignDigestPkcs1)));
            var twice = PdfSigner.Sign(once, new PdfSignatureOptions(ecCert,
                new PdfExternalSigner(PdfSignatureAlgorithm.Ecdsa, r => key.SignData(r.Data.ToArray(), r.HashAlgorithm)))
            { HashAlgorithm = HashAlgorithmName.SHA384 });
            Assert.Equal(once, twice.AsSpan(0, once.Length).ToArray());
            VerifySingle(once);
            var m = Regex.Matches(Encoding.Latin1.GetString(twice), @"/ByteRange \[\s*(\d+) (\d+) (\d+) (\d+)\s*\] /Contents <([0-9A-F]+)>");
            Assert.Equal(2, m.Count);
            var r = Enumerable.Range(1, 4).Select(i => int.Parse(m[1].Groups[i].Value)).ToArray();
            var cms = new SignedCms(new ContentInfo(twice.AsSpan(0, r[1]).ToArray().Concat(twice.AsSpan(r[2], r[3]).ToArray()).ToArray()), detached: true);
            cms.Decode(Convert.FromHexString(m[1].Groups[5].Value));
            cms.CheckSignature(verifySignatureOnly: true);
            Assert.Equal("1.2.840.10045.4.3.3", cms.SignerInfos[0].SignatureAlgorithm.Value);
        }
    }

    private static X509Certificate2 TsaCert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw");
    }
}
