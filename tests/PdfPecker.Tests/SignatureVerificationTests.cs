using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfPecker.Tests;

/// <summary>Checking signatures in existing PDFs with <see cref="PdfSignatureVerifier"/>.</summary>
public class SignatureVerificationTests
{
    private const string Html = "<html><head><title>Invoice</title></head><body><h1>Faktura 1001</h1><p>Total: 1.250,00 kr.</p></body></html>";

    internal static X509Certificate2 SelfSigned(string cn, bool ec = false)
    {
        if (ec)
        {
            using var ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var ecCert = new CertificateRequest($"CN={cn}", ecKey, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            return X509CertificateLoader.LoadPkcs12(ecCert.Export(X509ContentType.Pfx, "pw"), "pw");
        }
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}, O=PdfPecker Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw");
    }

    private static readonly Lazy<X509Certificate2> Cert = new(() => SelfSigned("Verified Signer"));

    private static PdfSignatureVerificationOptions Trusting(params X509Certificate2[] roots)
        => new() { TrustedRoots = new X509Certificate2Collection(roots) };

    private static byte[] Signed(PdfSignatureOptions? o = null)
        => HtmlToPdf.Convert(Html, new PdfOptions { Signature = o ?? new PdfSignatureOptions(Cert.Value) { Reason = "Approved", Location = "Aarhus" } });

    [Fact]
    public void A_fresh_signature_is_valid()
    {
        var pdf = Signed();
        var v = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value)));
        Assert.True(v.Valid, string.Join("\n", v.Problems));
        Assert.Empty(v.Problems);
        Assert.Equal("Signature1", v.FieldName);
        Assert.Equal("ETSI.CAdES.detached", v.SubFilter);
        Assert.Equal("Verified Signer", v.SignerName);
        Assert.Equal(Cert.Value.Thumbprint, v.SignerCertificate!.Thumbprint);
        Assert.Equal("Approved", v.Reason);
        Assert.Equal("Aarhus", v.Location);
        Assert.Equal("SHA256", v.HashAlgorithm);
        Assert.True(v.CoversWholeDocument);
        Assert.Equal(0, v.UpdatesAfterSigning);
        Assert.True(v.ByteRangeValid && v.DigestMatches && v.SignatureValid && v.ChainTrusted);
        Assert.False(v.HasTimestamp);
        Assert.Null(v.TimestampValid);
        Assert.NotNull(v.SigningTime);
        Assert.True(Math.Abs((v.SigningTime!.Value - DateTimeOffset.Now).TotalMinutes) < 5);
        Assert.Equal(pdf.Length, v.ByteRange[2] + v.ByteRange[3]);
    }

    [Fact]
    public void A_changed_byte_breaks_the_digest()
    {
        var pdf = Signed();
        // Change a byte of the page content stream area, well inside the first signed range.
        int pos = Encoding.Latin1.GetString(pdf).IndexOf("/MediaBox", StringComparison.Ordinal);
        pdf[pos + 1] = (byte)'N';
        var v = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value)));
        Assert.False(v.DigestMatches);
        Assert.True(v.SignatureValid); // the CMS itself is untouched
        Assert.False(v.IsIntact);
        Assert.False(v.Valid);
        Assert.Contains(v.Problems, p => p.Contains("changed"));
    }

    [Fact]
    public void A_changed_signature_value_fails_the_signature_check()
    {
        var pdf = Signed();
        var v0 = PdfSignatureVerifier.Verify(pdf)[0];
        // The RSA signature value is the last 256 bytes of the CMS; flip a hex digit inside it.
        int contentsStart = (int)v0.ByteRange[1] + 1;
        int sigLength = SignedCmsLength(pdf, v0);
        int pos = contentsStart + 2 * (sigLength - 20);
        pdf[pos] = pdf[pos] == (byte)'0' ? (byte)'1' : (byte)'0';
        var v = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value)));
        Assert.True(v.DigestMatches);
        Assert.False(v.SignatureValid);
        Assert.False(v.Valid);
    }

    private static int SignedCmsLength(byte[] pdf, PdfSignatureVerification v)
    {
        var hex = Encoding.Latin1.GetString(pdf, (int)v.ByteRange[1] + 1, (int)(v.ByteRange[2] - v.ByteRange[1] - 2));
        var bytes = Convert.FromHexString(hex);
        System.Formats.Asn1.AsnDecoder.ReadEncodedValue(bytes, System.Formats.Asn1.AsnEncodingRules.BER, out _, out _, out int consumed);
        return consumed;
    }

    [Fact]
    public void An_appended_update_is_flagged()
    {
        var pdf = Signed();
        var text = Encoding.Latin1.GetString(pdf);
        int startxref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0]);
        string root = System.Text.RegularExpressions.Regex.Matches(text, @"/Root (\d+ \d+ R)")[^1].Groups[1].Value;
        // An incremental update that adds an object, the way an editor appends changes.
        const string obj = "99 0 obj\n(appended)\nendobj\n";
        var update = obj + $"xref\n99 1\n{pdf.Length:D10} 00000 n \ntrailer\n<< /Size 100 /Root {root} /Prev {startxref} >>\nstartxref\n{pdf.Length + obj.Length}\n%%EOF\n";
        var edited = pdf.Concat(Encoding.Latin1.GetBytes(update)).ToArray();
        var v = Assert.Single(PdfSignatureVerifier.Verify(edited, Trusting(Cert.Value)));
        Assert.True(v.IsIntact);
        Assert.False(v.CoversWholeDocument);
        Assert.Equal(1, v.UpdatesAfterSigning);
    }

    [Fact]
    public void Trust_depends_on_the_trusted_roots()
    {
        var pdf = Signed();
        using var other = SelfSigned("Someone Else");
        var trusted = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value)));
        var untrusted = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(other)));
        Assert.True(trusted.ChainTrusted);
        Assert.False(untrusted.ChainTrusted);
        Assert.True(untrusted.IsIntact);
        Assert.False(untrusted.Valid);
        Assert.NotEmpty(untrusted.ChainErrors);
        // A self-signed test certificate is not in the system store either.
        Assert.False(PdfSignatureVerifier.Verify(pdf)[0].ChainTrusted);
    }

    [Fact]
    public void An_expired_certificate_at_the_verification_time_is_not_trusted()
    {
        var pdf = Signed();
        var v = PdfSignatureVerifier.Verify(pdf, new PdfSignatureVerificationOptions
        {
            TrustedRoots = new X509Certificate2Collection(Cert.Value),
            VerificationTime = DateTimeOffset.UtcNow.AddYears(3),
        })[0];
        Assert.False(v.ChainTrusted);
    }

    [Fact]
    public void Two_signatures_are_reported_in_order()
    {
        using var second = SelfSigned("Second Signer", ec: true);
        var once = Signed();
        var twice = PdfSigner.Sign(once, new PdfSignatureOptions(second) { Format = PdfSignatureFormat.Pkcs7Detached, HashAlgorithm = HashAlgorithmName.SHA384 });
        var all = PdfSignatureVerifier.Verify(twice, Trusting(Cert.Value, second));
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "Signature1", "Signature2" }, all.Select(v => v.FieldName));
        Assert.All(all, v => Assert.True(v.Valid, string.Join("\n", v.Problems)));
        Assert.False(all[0].CoversWholeDocument);
        Assert.Equal(1, all[0].UpdatesAfterSigning);
        Assert.True(all[1].CoversWholeDocument);
        Assert.Equal("adbe.pkcs7.detached", all[1].SubFilter);
        Assert.Equal("SHA384", all[1].HashAlgorithm);
        Assert.Equal("Second Signer", all[1].SignerName);
    }

    [Fact]
    public void A_timestamped_signature_reports_the_timestamp()
    {
        using var tsa = SelfSigned("Test TSA");
        var pdf = Signed(new PdfSignatureOptions(Cert.Value)
        {
            TimestampUrl = "http://tsa.example/",
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(tsa)),
        });
        var v = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value)));
        Assert.True(v.HasTimestamp);
        Assert.True(v.TimestampValid);
        Assert.True(Math.Abs((v.TimestampTime!.Value - DateTimeOffset.UtcNow).TotalMinutes) < 5);
        Assert.True(v.Valid, string.Join("\n", v.Problems));
    }

    [Fact]
    public void Encrypted_signed_files_verify_with_the_password()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Security = new PdfSecurity { UserPassword = "kodeord" },
            Signature = new PdfSignatureOptions(Cert.Value) { Reason = "Hemmelig" },
        });
        Assert.Throws<PdfPasswordException>(() => PdfSignatureVerifier.Verify(pdf));
        var v = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value), "kodeord"));
        Assert.True(v.Valid, string.Join("\n", v.Problems));
        Assert.Equal("Hemmelig", v.Reason);
    }

    [Fact]
    public void A_byte_range_that_points_at_another_string_is_rejected()
    {
        var pdf = Signed();
        var text = Encoding.Latin1.GetString(pdf);
        var v0 = PdfSignatureVerifier.Verify(pdf)[0];
        // Rewrite the byte range so its gap starts one byte later: the gap is no longer exactly the /Contents string.
        int at = text.IndexOf("/ByteRange [", StringComparison.Ordinal) + 12;
        string range = $"0 {v0.ByteRange[1] + 1} {v0.ByteRange[2]} {v0.ByteRange[3]}";
        var bytes = (byte[])pdf.Clone();
        Encoding.Latin1.GetBytes(range.PadRight(48)).CopyTo(bytes, at);
        var v = Assert.Single(PdfSignatureVerifier.Verify(bytes, Trusting(Cert.Value)));
        Assert.False(v.ByteRangeValid);
        Assert.False(v.Valid);
    }

    [Fact]
    public void Unsigned_and_unreadable_input()
    {
        Assert.Empty(PdfSignatureVerifier.Verify(HtmlToPdf.Convert(Html)));
        Assert.Throws<InvalidDataException>(() => PdfSignatureVerifier.Verify(Encoding.ASCII.GetBytes("not a pdf at all")));
    }

    [Fact]
    public void Garbage_signature_contents_are_reported_not_thrown()
    {
        var pdf = Signed();
        var v0 = PdfSignatureVerifier.Verify(pdf)[0];
        var bytes = (byte[])pdf.Clone();
        for (long i = v0.ByteRange[1] + 1; i < v0.ByteRange[1] + 41; i++) bytes[i] = (byte)'7';
        var v = Assert.Single(PdfSignatureVerifier.Verify(bytes, Trusting(Cert.Value)));
        Assert.False(v.Valid);
        Assert.NotEmpty(v.Problems);

        // Corrupt hex digits inside the signature itself, so the ASN.1 and certificate parsers see hostile input.
        var rng = new Random(7);
        int cmsLength = SignedCmsLength(pdf, v0);
        for (int round = 0; round < 60; round++)
        {
            var copy = (byte[])pdf.Clone();
            for (int k = 0; k < 1 + round % 6; k++)
                copy[v0.ByteRange[1] + 1 + rng.Next(cmsLength * 2)] = (byte)"0123456789ABCDEF"[rng.Next(16)];
            var r = Assert.Single(PdfSignatureVerifier.Verify(copy, Trusting(Cert.Value)));
            Assert.Equal(r.Valid, r.Problems.Count == 0);
        }

        // Random corruption anywhere must never throw anything but the documented exceptions.
        for (int round = 0; round < 40; round++)
        {
            var copy = (byte[])pdf.Clone();
            for (int k = 0; k < 8; k++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            try { PdfSignatureVerifier.Verify(copy, Trusting(Cert.Value)); }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void Reads_the_old_adbe_pkcs7_sha1_format()
    {
        // Turn a classic signature into an adbe.pkcs7.sha1 one made by SignedCms: same byte range, new /SubFilter, and a
        // CMS that encapsulates the SHA-1 of the signed bytes.
        var pdf = Signed(new PdfSignatureOptions(Cert.Value) { Format = PdfSignatureFormat.Pkcs7Detached });
        var text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf("/adbe.pkcs7.detached", StringComparison.Ordinal);
        Encoding.Latin1.GetBytes("/adbe.pkcs7.sha1    ").CopyTo(pdf, at);
        var r = PdfSignatureVerifier.Verify(pdf)[0].ByteRange;
        var ranges = pdf.AsSpan(0, (int)r[1]).ToArray().Concat(pdf.AsSpan((int)r[2], (int)r[3]).ToArray()).ToArray();
        var cms = new SignedCms(new ContentInfo(SHA1.HashData(ranges)));
        cms.ComputeSignature(new CmsSigner(Cert.Value) { DigestAlgorithm = new Oid("1.3.14.3.2.26") });
        var hex = Convert.ToHexString(cms.Encode());
        Encoding.ASCII.GetBytes(hex).CopyTo(pdf, (int)r[1] + 1);

        var v = Assert.Single(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value)));
        Assert.Equal("adbe.pkcs7.sha1", v.SubFilter);
        Assert.Equal("SHA1", v.HashAlgorithm);
        Assert.True(v.Valid, string.Join("\n", v.Problems));

        pdf[10] ^= 1; // inside the header comment, which the SHA-1 covers
        Assert.False(PdfSignatureVerifier.Verify(pdf, Trusting(Cert.Value))[0].DigestMatches);
    }

    [Fact]
    public void Agrees_with_SignedCms_on_the_signer()
    {
        var pdf = Signed();
        var v = PdfSignatureVerifier.Verify(pdf)[0];
        var signedData = pdf.AsSpan(0, (int)v.ByteRange[1]).ToArray().Concat(pdf.AsSpan((int)v.ByteRange[2], (int)v.ByteRange[3]).ToArray()).ToArray();
        var hex = Encoding.Latin1.GetString(pdf, (int)v.ByteRange[1] + 1, (int)(v.ByteRange[2] - v.ByteRange[1] - 2));
        var cms = new SignedCms(new ContentInfo(signedData), detached: true);
        cms.Decode(Convert.FromHexString(hex));
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal(cms.SignerInfos[0].Certificate!.Thumbprint, v.SignerCertificate!.Thumbprint);
    }
}
