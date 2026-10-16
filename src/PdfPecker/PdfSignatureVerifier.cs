using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfPecker.Rendering.Parsing;
using PdfPecker.Signing;

namespace PdfPecker;

/// <summary>How <see cref="PdfSignatureVerifier"/> decides whether a signer's certificate is trusted.</summary>
public sealed class PdfSignatureVerificationOptions
{
    /// <summary>
    /// The root certificates to trust, e.g. your own company CA or a national eID root. When set, only these roots are
    /// trusted; when null (the default), the operating system's trusted roots are.
    /// </summary>
    public X509Certificate2Collection? TrustedRoots { get; set; }

    /// <summary>Intermediate certificates to use when building chains, in addition to those embedded in the signature and the PDF.</summary>
    public X509Certificate2Collection? ExtraCertificates { get; set; }

    /// <summary>
    /// Revocation checking. Default: <see cref="X509RevocationMode.NoCheck"/>, which keeps verification offline.
    /// <see cref="X509RevocationMode.Online"/> lets the platform fetch CRLs, OCSP answers and missing intermediates.
    /// </summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    /// The time at which certificates must be valid. Default: the time of a valid timestamp when the signature has one,
    /// else the signing time the signer claims, else now.
    /// </summary>
    public DateTimeOffset? VerificationTime { get; set; }
}

/// <summary>The outcome of checking one signature or document timestamp.</summary>
public sealed class PdfSignatureVerification
{
    internal PdfSignatureVerification() { }

    /// <summary>The fully qualified name of the signature field.</summary>
    public string FieldName { get; internal set; } = "";

    /// <summary>The signature's /SubFilter, e.g. ETSI.CAdES.detached, adbe.pkcs7.detached or ETSI.RFC3161.</summary>
    public string? SubFilter { get; internal set; }

    /// <summary>True for a document timestamp (PAdES B-LTA), which proves the time of the whole file rather than a signer.</summary>
    public bool IsDocumentTimestamp { get; internal set; }

    /// <summary>The signer's certificate (for a document timestamp, the timestamp authority's), or null when it is not in the signature.</summary>
    public X509Certificate2? SignerCertificate { get; internal set; }

    /// <summary>The signer's common name from the certificate, else the /Name in the signature dictionary.</summary>
    public string? SignerName { get; internal set; }

    /// <summary>Every certificate embedded in the signature.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; internal set; } = Array.Empty<X509Certificate2>();

    /// <summary>The signing time the signer claims: the signing-time attribute, else /M. Not proof; see <see cref="TimestampTime"/>.</summary>
    public DateTimeOffset? SigningTime { get; internal set; }

    /// <summary>The time from the signature's timestamp token (or of a document timestamp), when there is one.</summary>
    public DateTimeOffset? TimestampTime { get; internal set; }

    /// <summary>The /Reason in the signature dictionary.</summary>
    public string? Reason { get; internal set; }

    /// <summary>The /Location in the signature dictionary.</summary>
    public string? Location { get; internal set; }

    /// <summary>The digest algorithm, e.g. SHA256.</summary>
    public string? HashAlgorithm { get; internal set; }

    /// <summary>The /ByteRange: offset and length of the two signed parts of the file.</summary>
    public IReadOnlyList<long> ByteRange { get; internal set; } = Array.Empty<long>();

    /// <summary>
    /// Whether the byte range is well formed: it starts at the beginning of the file, leaves out exactly the signature's own
    /// /Contents string and nothing else, and stays inside the file.
    /// </summary>
    public bool ByteRangeValid { get; internal set; }

    /// <summary>
    /// Whether the signature covers the whole file. False when incremental updates were appended after signing: later
    /// signatures, validation data, or edits. See <see cref="UpdatesAfterSigning"/>.
    /// </summary>
    public bool CoversWholeDocument { get; internal set; }

    /// <summary>The number of incremental updates appended after this signature.</summary>
    public int UpdatesAfterSigning { get; internal set; }

    /// <summary>Whether the hash of the signed bytes matches the one the signature holds: the signed part is unchanged.</summary>
    public bool DigestMatches { get; internal set; }

    /// <summary>Whether the signature value verifies with the signer certificate's public key.</summary>
    public bool SignatureValid { get; internal set; }

    /// <summary>Whether the signature carries an RFC 3161 timestamp token (always true for a document timestamp).</summary>
    public bool HasTimestamp { get; internal set; }

    /// <summary>Whether the timestamp token is intact and covers this signature; null without a timestamp.</summary>
    public bool? TimestampValid { get; internal set; }

    /// <summary>Whether the signer's certificate chains to a trusted root and is valid at the verification time.</summary>
    public bool ChainTrusted { get; internal set; }

    /// <summary>Why the chain is not trusted, one entry per problem from <see cref="X509Chain"/>; empty when it is.</summary>
    public IReadOnlyList<string> ChainErrors { get; internal set; } = Array.Empty<string>();

    /// <summary>Everything found wrong, in plain words; empty for a valid signature.</summary>
    public IReadOnlyList<string> Problems { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Whether the signed bytes are unchanged and the signature and any timestamp are cryptographically sound, regardless
    /// of whether the signer is trusted.
    /// </summary>
    public bool IsIntact => ByteRangeValid && DigestMatches && SignatureValid && TimestampValid != false;

    /// <summary>
    /// Intact and trusted. Changes appended after signing do not make a signature invalid (that is how a second
    /// signature or validation data is added), so check <see cref="CoversWholeDocument"/> too when the last signature
    /// must cover everything.
    /// </summary>
    public bool Valid => IsIntact && ChainTrusted;
}

/// <summary>Checks the digital signatures and document timestamps in an existing PDF.</summary>
public static class PdfSignatureVerifier
{
    /// <summary>
    /// Verifies every signed signature field in <paramref name="pdf"/>, oldest first. A signature that is broken in any
    /// way is reported in its result, not thrown.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">Trusted roots and revocation settings. Default: the system's roots, no revocation checks.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <exception cref="InvalidDataException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    public static IReadOnlyList<PdfSignatureVerification> Verify(byte[] pdf, PdfSignatureVerificationOptions? options = null, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        options ??= new PdfSignatureVerificationOptions();
        PdfFile file;
        List<FoundSignature> found;
        try
        {
            file = PdfFile.Open(pdf, password);
            if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
            found = SignatureScanner.Find(file);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
        var dssCerts = DssCertificates(file);
        return found.Select(f => VerifyOne(file, f, options, dssCerts)).ToList();
    }

    /// <summary>Verifies a PDF file. See <see cref="Verify(byte[], PdfSignatureVerificationOptions?, string?)"/>.</summary>
    public static IReadOnlyList<PdfSignatureVerification> VerifyFile(string path, PdfSignatureVerificationOptions? options = null, string? password = null)
        => Verify(File.ReadAllBytes(path), options, password);

    private static PdfSignatureVerification VerifyOne(PdfFile file, FoundSignature f, PdfSignatureVerificationOptions options, List<X509Certificate2> dssCerts)
    {
        var data = file.Data;
        var problems = new List<string>();
        var v = new PdfSignatureVerification
        {
            FieldName = f.FieldName,
            SubFilter = f.SubFilter,
            IsDocumentTimestamp = f.IsDocumentTimestamp,
            ByteRange = f.ByteRange,
            ByteRangeValid = f.ByteRangeValid,
            Reason = (file.Resolve(f.Dict["Reason"]) as PdfString)?.Text,
            Location = (file.Resolve(f.Dict["Location"]) as PdfString)?.Text,
            SignerName = (file.Resolve(f.Dict["Name"]) as PdfString)?.Text,
            SigningTime = ParsePdfDate((file.Resolve(f.Dict["M"]) as PdfString)?.Text),
        };
        if (f.ByteRange.Length == 4)
        {
            long end = f.ByteRange[2] + f.ByteRange[3];
            v.UpdatesAfterSigning = end <= data.Length ? SignatureScanner.UpdatesAfter(data, end) : 0;
            v.CoversWholeDocument = end <= data.Length && OnlyWhiteSpaceAfter(data, end);
        }
        if (!f.ByteRangeValid) problems.Add("The byte range is malformed or does not leave out exactly the signature's /Contents.");
        if (f.Contents == null)
        {
            problems.Add("The signature value could not be read.");
            v.Problems = problems;
            return v;
        }

        try
        {
            var cms = CmsReader.Parse(f.Contents);
            var certs = cms.Certificates.Select(CmsReader.LoadCertificate).OfType<X509Certificate2>().ToList();
            v.Certificates = certs;
            var signer = CmsReader.FindSigner(cms.Signer, certs);
            v.SignerCertificate = signer;
            if (signer == null) problems.Add("The signer's certificate is not embedded in the signature.");
            else
            {
                var cn = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                v.SignerName = string.IsNullOrEmpty(cn) ? signer.Subject : cn;
            }
            var hash = CmsReader.HashFromOid(cms.Signer.DigestOid) ?? throw new CryptographicException("Unsupported digest algorithm " + cms.Signer.DigestOid + ".");
            v.HashAlgorithm = hash.Name;
            byte[] rangeDigest = f.ByteRange.Length == 4 && f.ByteRange[2] + f.ByteRange[3] <= data.Length && f.ByteRange[0] + f.ByteRange[1] <= f.ByteRange[2]
                ? SignatureScanner.HashRanges(data, f.ByteRange, hash)
                : Array.Empty<byte>();

            if (f.IsDocumentTimestamp)
            {
                // A document timestamp's token is the signature: its TSTInfo holds the hash of the byte ranges.
                if (cms.ContentType != CmsReader.IdTstInfo || cms.Content == null) throw new CryptographicException("The document timestamp holds no TSTInfo.");
                var (tsHash, imprint, time) = CmsReader.ReadTstInfo(cms.Content);
                v.HasTimestamp = true;
                v.TimestampTime = time;
                v.SigningTime ??= time;
                byte[] digest = tsHash == hash ? rangeDigest : SignatureScanner.HashRanges(data, f.ByteRange, tsHash);
                var (contentOk, sigOk) = CmsReader.Verify(cms.Signer, CmsReader.Hash(hash, cms.Content), signer);
                v.DigestMatches = imprint.AsSpan().SequenceEqual(digest) && contentOk;
                v.SignatureValid = sigOk;
                v.TimestampValid = v.DigestMatches && sigOk;
            }
            else
            {
                byte[] contentDigest = rangeDigest;
                bool sha1Ok = true;
                if (f.SubFilter == "adbe.pkcs7.sha1")
                {
                    // The old SHA-1 format signs an encapsulated SHA-1 of the byte ranges instead of the ranges themselves.
                    var sha1 = SignatureScanner.HashRanges(data, f.ByteRange, HashAlgorithmName.SHA1);
                    sha1Ok = cms.Content != null && cms.Content.AsSpan().SequenceEqual(sha1);
                    contentDigest = CmsReader.Hash(hash, cms.Content ?? Array.Empty<byte>());
                }
                var (digestOk, sigOk) = CmsReader.Verify(cms.Signer, contentDigest, signer);
                v.DigestMatches = digestOk && sha1Ok;
                v.SignatureValid = sigOk;
                if (CmsReader.ReadTime(cms.Signer.SignedAttribute(CmsReader.IdSigningTime)) is { } st) v.SigningTime = st;

                var tokenAttr = cms.Signer.UnsignedAttributes.FirstOrDefault(a => a.Oid == CmsReader.IdTimestampToken).Value;
                if (tokenAttr != null)
                {
                    v.HasTimestamp = true;
                    v.TimestampValid = CheckTimestamp(tokenAttr, cms.Signer.Signature, out var time, out var tsProblem);
                    v.TimestampTime = time;
                    if (tsProblem != null) problems.Add(tsProblem);
                }
            }
            if (!v.DigestMatches) problems.Add("The signed part of the document has been changed: its hash does not match the signature.");
            if (signer != null && !v.SignatureValid) problems.Add("The signature value does not verify with the signer's certificate.");
            if (v.TimestampValid == false && !f.IsDocumentTimestamp && !problems.Any(p => p.StartsWith("The timestamp", StringComparison.Ordinal)))
                problems.Add("The timestamp token is not valid for this signature.");

            if (signer != null)
            {
                var time = options.VerificationTime ?? (v.TimestampValid == true ? v.TimestampTime : null) ?? v.SigningTime ?? DateTimeOffset.UtcNow;
                v.ChainErrors = BuildChain(signer, certs, dssCerts, options, time, out bool trusted);
                v.ChainTrusted = trusted;
                if (!trusted) problems.Add("The signer's certificate is not trusted: " + string.Join("; ", v.ChainErrors));
            }
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException or NotSupportedException)
        {
            problems.Add("The signature could not be checked: " + ex.Message);
        }
        v.Problems = problems;
        return v;
    }

    /// <summary>Checks a signature timestamp: its own CMS signature, and that it covers <paramref name="signatureValue"/>.</summary>
    private static bool CheckTimestamp(byte[] token, byte[] signatureValue, out DateTimeOffset? time, out string? problem)
    {
        time = null;
        problem = null;
        try
        {
            var cms = CmsReader.Parse(token);
            if (cms.ContentType != CmsReader.IdTstInfo || cms.Content == null)
            {
                problem = "The timestamp token holds no TSTInfo.";
                return false;
            }
            var (hash, imprint, t) = CmsReader.ReadTstInfo(cms.Content);
            time = t;
            if (!imprint.AsSpan().SequenceEqual(CmsReader.Hash(hash, signatureValue)))
            {
                problem = "The timestamp token is for a different signature.";
                return false;
            }
            var certs = cms.Certificates.Select(CmsReader.LoadCertificate).OfType<X509Certificate2>().ToList();
            var digestHash = CmsReader.HashFromOid(cms.Signer.DigestOid) ?? throw new CryptographicException("Unsupported timestamp digest.");
            var (digestOk, sigOk) = CmsReader.Verify(cms.Signer, CmsReader.Hash(digestHash, cms.Content), CmsReader.FindSigner(cms.Signer, certs));
            if (!digestOk || !sigOk) problem = "The timestamp token's own signature does not verify.";
            return digestOk && sigOk;
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException)
        {
            problem = "The timestamp token could not be read: " + ex.Message;
            return false;
        }
    }

    private static List<string> BuildChain(X509Certificate2 signer, List<X509Certificate2> embedded, List<X509Certificate2> dss,
        PdfSignatureVerificationOptions options, DateTimeOffset time, out bool trusted)
    {
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.RevocationMode = options.RevocationMode;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.DisableCertificateDownloads = options.RevocationMode != X509RevocationMode.Online;
        policy.VerificationTime = time.UtcDateTime;
        policy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
        foreach (var c in embedded) policy.ExtraStore.Add(c);
        foreach (var c in dss) policy.ExtraStore.Add(c);
        if (options.ExtraCertificates != null) policy.ExtraStore.AddRange(options.ExtraCertificates);
        if (options.TrustedRoots != null)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(options.TrustedRoots);
        }
        trusted = chain.Build(signer);
        var errors = new List<string>();
        foreach (var s in chain.ChainStatus)
            if (s.Status != X509ChainStatusFlags.NoError)
                errors.Add(string.IsNullOrWhiteSpace(s.StatusInformation) ? s.Status.ToString() : s.StatusInformation.Trim());
        if (!trusted && errors.Count == 0) errors.Add("The certificate chain could not be built.");
        return errors;
    }

    /// <summary>Certificates in the document security store, which help build chains for signatures made LTV-ready.</summary>
    private static List<X509Certificate2> DssCertificates(PdfFile file)
    {
        var list = new List<X509Certificate2>();
        try
        {
            var certs = file.GetArray(file.GetDict(file.Catalog, "DSS"), "Certs");
            if (certs == null) return list;
            foreach (var item in certs.Items.Take(1000))
                if (file.Resolve(item) is PdfStream s && CmsReader.LoadCertificate(file.DecodeStream(s)) is { } c) list.Add(c);
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException)
        {
            // A broken store only means fewer certificates to build chains with.
        }
        return list;
    }

    private static bool OnlyWhiteSpaceAfter(byte[] data, long offset)
    {
        for (long i = offset; i < data.Length; i++)
            if (data[i] is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0)) return false;
        return true;
    }

    /// <summary>Parses a PDF date (D:YYYYMMDDHHmmSSOHH'mm'), which may stop after any field.</summary>
    internal static DateTimeOffset? ParsePdfDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        int Field(int start, int len, int fallback)
            => s.Length >= start + len && int.TryParse(s.AsSpan(start, len), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : fallback;
        int year = Field(0, 4, -1);
        if (year < 1) return null;
        try
        {
            var offset = TimeSpan.Zero;
            if (s.Length > 14 && (s[14] == '+' || s[14] == '-'))
            {
                offset = new TimeSpan(Field(15, 2, 0), s.Length >= 20 ? Field(18, 2, 0) : 0, 0);
                if (s[14] == '-') offset = -offset;
            }
            return new DateTimeOffset(year, Field(4, 2, 1), Field(6, 2, 1), Field(8, 2, 0), Field(10, 2, 0), Field(12, 2, 0), offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
