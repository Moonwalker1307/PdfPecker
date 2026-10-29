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
    /// The time at which certificates must be valid. Default: the time of the signature's timestamp when it is valid and
    /// trusted (see <see cref="PdfSignatureVerification.TimestampTrusted"/>), else now. The signing time the signer claims
    /// is never used, because the signer chooses it.
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

    /// <summary>
    /// Whether the timestamp authority is trusted: its certificate chains to the same trusted roots as the signer's and
    /// is meant for timestamping. Only a trusted timestamp's time is used to check the signer's certificate, since anyone
    /// can make a token that states any time. Null without a timestamp.
    /// </summary>
    public bool? TimestampTrusted { get; internal set; }

    /// <summary>Whether the signer's certificate chains to a trusted root and is valid at the verification time.</summary>
    public bool ChainTrusted { get; internal set; }

    /// <summary>Why the chain is not trusted, one entry per problem from <see cref="X509Chain"/>; empty when it is.</summary>
    public IReadOnlyList<string> ChainErrors { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// The certification level when this is a certification signature: one with a DocMDP transform that the document
    /// catalog names in /Perms. <see cref="PdfCertificationLevel.None"/> for an approval signature or a document timestamp.
    /// </summary>
    public PdfCertificationLevel CertificationLevel { get; internal set; }

    /// <summary>
    /// For a certification signature, the changes appended after it that its level does not allow, in plain words; they
    /// are listed in <see cref="Problems"/> too and make the signature not <see cref="Valid"/>. Empty when every change
    /// is allowed, and always for an approval signature.
    /// </summary>
    /// <remarks>
    /// The check compares each object the later updates wrote with its certified version: validation data, document
    /// timestamps and document metadata may change at every level, filled-in fields and new or filled signature fields
    /// at <see cref="PdfCertificationLevel.FormFilling"/> and above, and annotations at
    /// <see cref="PdfCertificationLevel.FormFillingAndAnnotations"/>. Anything else, such as changed page content, is
    /// reported.
    /// </remarks>
    public IReadOnlyList<string> DisallowedChanges { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Everything found wrong, in plain words; empty for a valid signature, except that an untrusted timestamp is listed
    /// even when the signature is valid without it.
    /// </summary>
    public IReadOnlyList<string> Problems { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Whether the signed bytes are unchanged and the signature and any timestamp are cryptographically sound, regardless
    /// of whether the signer is trusted.
    /// </summary>
    public bool IsIntact => ByteRangeValid && DigestMatches && SignatureValid && TimestampValid != false;

    /// <summary>
    /// Intact, trusted, and for a certification signature without <see cref="DisallowedChanges"/>. Other changes
    /// appended after signing do not make a signature invalid (that is how a second signature or validation data is
    /// added), so check <see cref="CoversWholeDocument"/> too when the last signature must cover everything.
    /// </summary>
    /// <remarks>
    /// Revocation is not checked by default: with <see cref="PdfSignatureVerificationOptions.RevocationMode"/> at its
    /// default, <see cref="X509RevocationMode.NoCheck"/>, a signature made with a revoked certificate still reports
    /// valid. Set it to <see cref="X509RevocationMode.Online"/>, or to <see cref="X509RevocationMode.Offline"/> with the
    /// revocation data cached on the machine, when that matters.
    /// </remarks>
    public bool Valid => IsIntact && ChainTrusted && DisallowedChanges.Count == 0;
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
        => Verify(pdf, options, password, CancellationToken.None);

    /// <summary>
    /// Verifies every signed signature field in <paramref name="pdf"/>, oldest first, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled. Otherwise the same
    /// as <see cref="Verify(byte[], PdfSignatureVerificationOptions?, string?)"/>.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">Trusted roots and revocation settings. Default: the system's roots, no revocation checks.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Stops the verification before each signature.</param>
    public static IReadOnlyList<PdfSignatureVerification> Verify(byte[] pdf, PdfSignatureVerificationOptions? options, string? password,
        CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        cancellationToken.ThrowIfCancellationRequested();
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
        try
        {
            var hashes = new RangeHashes(file.Data);
            var results = new List<PdfSignatureVerification>(found.Count);
            foreach (var f in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(VerifyOne(file, f, options, dssCerts, hashes, password));
            }
            return results;
        }
        finally
        {
            // The validation data's certificates only help build chains; the results keep none of them.
            foreach (var cert in dssCerts) cert.Dispose();
        }
    }

    /// <summary>Verifies a PDF file. See <see cref="Verify(byte[], PdfSignatureVerificationOptions?, string?)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static IReadOnlyList<PdfSignatureVerification> VerifyFile(string path, PdfSignatureVerificationOptions? options = null, string? password = null)
        => Verify(File.ReadAllBytes(path), options, password);

    /// <summary>
    /// Verifies a PDF file, stopping with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled. See <see cref="Verify(byte[], PdfSignatureVerificationOptions?, string?, CancellationToken)"/>.
    /// </summary>
    /// <param name="path">The PDF file.</param>
    /// <param name="options">Trusted roots and revocation settings. Default: the system's roots, no revocation checks.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Stops the verification before each signature.</param>
    public static IReadOnlyList<PdfSignatureVerification> VerifyFile(string path, PdfSignatureVerificationOptions? options, string? password,
        CancellationToken cancellationToken)
        => Verify(File.ReadAllBytes(path), options, password, cancellationToken);

    /// <summary>
    /// Verifies a PDF read from <paramref name="pdf"/> (from its current position to its end). The stream is not disposed.
    /// See <see cref="Verify(byte[], PdfSignatureVerificationOptions?, string?, CancellationToken)"/>.
    /// </summary>
    /// <param name="pdf">The PDF; it is treated as untrusted input.</param>
    /// <param name="options">Trusted roots and revocation settings. Default: the system's roots, no revocation checks.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Stops the verification before each signature.</param>
    /// <exception cref="InvalidDataException">The data is not a readable PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    public static IReadOnlyList<PdfSignatureVerification> Verify(Stream pdf, PdfSignatureVerificationOptions? options = null, string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return Verify(PdfMerger.ReadAll(pdf), options, password, cancellationToken);
    }

    private static PdfSignatureVerification VerifyOne(PdfFile file, FoundSignature f, PdfSignatureVerificationOptions options, List<X509Certificate2> dssCerts,
        RangeHashes hashes, string? password)
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
        if (!f.IsDocumentTimestamp && DocMdp.Level(file, f.Dict) is var level and > 0
            && (ReferenceEquals(DocMdp.Certification(file), f.Dict)
                // A later update may have dropped or replaced /Perms; the signed revision still says what was certified.
                || f.ByteRangeValid && f.Contents != null && DocMdp.CertifiedRevision(file, f.ByteRange[2] + f.ByteRange[3], f.Contents, password)))
        {
            v.CertificationLevel = (PdfCertificationLevel)level;
            // Only a well-formed byte range marks where the certified revision ends.
            if (f.ByteRangeValid && !v.CoversWholeDocument)
            {
                v.DisallowedChanges = DocMdpChanges.Find(file, f.ByteRange[2] + f.ByteRange[3], level, password);
                problems.AddRange(v.DisallowedChanges);
            }
        }
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
            byte[] rangeDigest = hashes.Hash(f.ByteRange, hash);

            if (f.IsDocumentTimestamp)
            {
                // A document timestamp's token is the signature: its TSTInfo holds the hash of the byte ranges.
                if (cms.ContentType != CmsReader.IdTstInfo || cms.Content == null) throw new CryptographicException("The document timestamp holds no TSTInfo.");
                var (tsHash, imprint, time) = CmsReader.ReadTstInfo(cms.Content);
                v.HasTimestamp = true;
                v.TimestampTime = time;
                v.SigningTime ??= time;
                byte[] digest = tsHash == hash ? rangeDigest : hashes.Hash(f.ByteRange, tsHash);
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
                    var sha1 = hashes.Hash(f.ByteRange, HashAlgorithmName.SHA1);
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
                    v.TimestampValid = CheckTimestamp(tokenAttr, cms.Signer.Signature, out var time, out var tsa, out var tsCerts, out var tsProblem);
                    v.TimestampTime = time;
                    v.TimestampTrusted = false;
                    if (tsProblem != null) problems.Add(tsProblem);
                    else if (tsa != null && time != null)
                    {
                        // The token's own signature only proves it was made with the key in it, so the authority must
                        // be one the caller trusts before its time may stand in for the time of signing.
                        var tsaErrors = BuildChain(tsa, tsCerts, dssCerts, options, time.Value, out bool tsaTrusted);
                        if (!HasTimestampingUsage(tsa))
                        {
                            tsaTrusted = false;
                            tsaErrors.Add("The certificate is not meant for timestamping.");
                        }
                        v.TimestampTrusted = tsaTrusted;
                        if (!tsaTrusted) problems.Add("The timestamp is not trusted, so its time is not used: " + string.Join("; ", tsaErrors));
                    }
                }
            }
            if (!v.DigestMatches) problems.Add("The signed part of the document has been changed: its hash does not match the signature.");
            if (signer != null && !v.SignatureValid) problems.Add("The signature value does not verify with the signer's certificate.");
            if (v.TimestampValid == false && !f.IsDocumentTimestamp && !problems.Any(p => p.StartsWith("The timestamp", StringComparison.Ordinal)))
                problems.Add("The timestamp token is not valid for this signature.");

            if (signer != null)
            {
                // A document timestamp is checked at its own time, as the authority it chains to vouches for that time.
                // A signature is checked at the time of a trusted timestamp, or now: the signing time it claims is the
                // signer's own word and would let an expired or not yet valid certificate pass.
                bool trustedTime = f.IsDocumentTimestamp ? v.TimestampValid == true : v.TimestampTrusted == true;
                var time = options.VerificationTime ?? (trustedTime ? v.TimestampTime : null) ?? DateTimeOffset.UtcNow;
                var chainErrors = BuildChain(signer, certs, dssCerts, options, time, out bool trusted);
                if (f.IsDocumentTimestamp && !HasTimestampingUsage(signer))
                {
                    trusted = false;
                    chainErrors.Add("The certificate is not meant for timestamping.");
                }
                v.ChainErrors = chainErrors;
                if (f.IsDocumentTimestamp) v.TimestampTrusted = trusted;
                v.ChainTrusted = trusted;
                if (!trusted) problems.Add("The signer's certificate is not trusted: " + string.Join("; ", v.ChainErrors));
            }
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException or NotSupportedException or OverflowException)
        {
            problems.Add("The signature could not be checked: " + ex.Message);
        }
        v.Problems = problems;
        return v;
    }

    /// <summary>
    /// Checks a signature timestamp: its own CMS signature, and that it covers <paramref name="signatureValue"/>. Whether
    /// the authority in <paramref name="tsa"/> is trusted is for the caller to decide.
    /// </summary>
    private static bool CheckTimestamp(byte[] token, byte[] signatureValue, out DateTimeOffset? time, out X509Certificate2? tsa,
        out List<X509Certificate2> tsCerts, out string? problem)
    {
        time = null;
        tsa = null;
        tsCerts = new List<X509Certificate2>();
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
            tsCerts = cms.Certificates.Select(CmsReader.LoadCertificate).OfType<X509Certificate2>().ToList();
            tsa = CmsReader.FindSigner(cms.Signer, tsCerts);
            var digestHash = CmsReader.HashFromOid(cms.Signer.DigestOid) ?? throw new CryptographicException("Unsupported timestamp digest.");
            var (digestOk, sigOk) = CmsReader.Verify(cms.Signer, CmsReader.Hash(digestHash, cms.Content), tsa);
            if (!digestOk || !sigOk) problem = "The timestamp token's own signature does not verify.";
            return digestOk && sigOk;
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException or NotSupportedException or OverflowException)
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
        // X509Chain does not look at what the end certificate may be used for. A certificate whose key usage is limited
        // to, say, encryption was not issued for signing, so its signatures are not the holder's.
        if (signer.Extensions[KeyUsageOid] is X509KeyUsageExtension ku
            && (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            trusted = false;
            errors.Add("The certificate's key usage does not allow digital signatures.");
        }
        return errors;
    }

    private const string KeyUsageOid = "2.5.29.15";
    private const string ExtendedKeyUsageOid = "2.5.29.37";
    private const string IdKpTimeStamping = "1.3.6.1.5.5.7.3.8";

    /// <summary>Whether the certificate's extended key usage names timestamping, which RFC 3161 requires of an authority.</summary>
    private static bool HasTimestampingUsage(X509Certificate2 cert)
    {
        if (cert.Extensions[ExtendedKeyUsageOid] is not X509EnhancedKeyUsageExtension eku) return false;
        foreach (var oid in eku.EnhancedKeyUsages)
            if (oid.Value == IdKpTimeStamping) return true;
        return false;
    }

    // Certificates are a few kilobytes; this leaves room for hundreds of real ones while bounding what a store of
    // compression bombs can make the verifier inflate.
    private const long MaxStoreDecoded = 64L * 1024 * 1024;

    /// <summary>Certificates in the document security store, which help build chains for signatures made LTV-ready.</summary>
    /// <summary>
    /// The hashes of the signed byte ranges, each computed once. A file may hold a thousand signatures that each cover
    /// nearly all of it, so hashing stops once the signatures together cover more bytes than any real file would.
    /// </summary>
    private sealed class RangeHashes(byte[] data)
    {
        private const long MaxHashedBytes = 4L * 1024 * 1024 * 1024;
        private readonly Dictionary<(long, long, long, long, string), byte[]> _done = new();
        private long _hashed;

        public byte[] Hash(long[] range, HashAlgorithmName algorithm)
        {
            // A range that does not fit the file hashes to nothing, which matches no signature.
            if (range.Length != 4 || range[0] + range[1] > range[2] || range[2] + range[3] > data.Length) return Array.Empty<byte>();
            var key = (range[0], range[1], range[2], range[3], algorithm.Name ?? "");
            if (_done.TryGetValue(key, out var digest)) return digest;
            _hashed += range[1] + range[3];
            if (_hashed > MaxHashedBytes) throw new CryptographicException("The signatures together cover more than 4 GB, so the rest are not checked.");
            digest = SignatureScanner.HashRanges(data, range, algorithm);
            _done[key] = digest;
            return digest;
        }
    }

    private static List<X509Certificate2> DssCertificates(PdfFile file)
    {
        var list = new List<X509Certificate2>();
        try
        {
            var certs = file.GetArray(file.GetDict(file.Catalog, "DSS"), "Certs");
            if (certs == null) return list;
            foreach (var (data, _) in LtvWriter.DecodeStoreStreams(file, certs, 1000, MaxStoreDecoded))
                if (CmsReader.LoadCertificate(data) is { } c) list.Add(c);
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
