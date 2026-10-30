using System.Formats.Asn1;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Signing;

/// <summary>
/// Adds a Document Security Store (ISO 32000-2 12.8.4.3, ETSI EN 319 142-1) with the certificates, OCSP responses and CRLs
/// that the PDF's signatures need, and optionally a document timestamp. Both go in incremental updates, so the bytes the
/// existing signatures cover are left exactly as they are.
/// </summary>
internal static class LtvWriter
{
    private const int ByteRangeWidth = 48;

    /// <summary>The store's contents: each item once, in the order first added.</summary>
    private sealed class Pool
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        public readonly List<byte[]> Items = new();
        public readonly List<PdfObject?> ExistingRefs = new();

        /// <summary>Adds <paramref name="data"/> unless already present; returns its position.</summary>
        public int Add(byte[] data, PdfObject? existingRef = null)
        {
            string key = Convert.ToHexString(SHA256.HashData(data));
            if (_index.TryGetValue(key, out int i)) return i;
            _index[key] = Items.Count;
            Items.Add(data);
            ExistingRefs.Add(existingRef);
            return Items.Count - 1;
        }
    }

    private sealed class Vri
    {
        public readonly SortedSet<int> Certs = new(), Ocsps = new(), Crls = new();
    }

    /// <summary>
    /// Adds the validation data synchronously. The task is already complete (see <see cref="AddValidationDataAsync"/>),
    /// so reading its result does not block.
    /// </summary>
    public static byte[] AddValidationData(byte[] pdf, PdfLtvOptions o, string? password, CancellationToken cancellationToken)
        => AddValidationDataAsync(pdf, o, password, async: false, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Adds the validation data. With <paramref name="async"/> false every request is sent synchronously, so the
    /// synchronous API can run the same code and read the result without blocking on a pending task.
    /// </summary>
    public static async ValueTask<byte[]> AddValidationDataAsync(byte[] pdf, PdfLtvOptions o, string? password, bool async, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = Open(pdf, password);
        var signatures = SignatureScanner.Find(file);
        if (signatures.Count == 0) throw new PdfSignatureException("The PDF has no signatures to add validation data for.");

        var certs = new Pool();
        var ocsps = new Pool();
        var crls = new Pool();
        var existingDss = file.GetDict(file.Catalog, "DSS");
        ReadExisting(file, existingDss, "Certs", certs);
        ReadExisting(file, existingDss, "OCSPs", ocsps);
        ReadExisting(file, existingDss, "CRLs", crls);

        var extra = new List<X509Certificate2>();
        if (o.Certificates != null) extra.AddRange(o.Certificates);
        foreach (var der in certs.Items) if (CmsReader.LoadCertificate(der) is { } c) extra.Add(c);

        // Caller-supplied revocation data goes in whole; it is also matched to the certificates it covers for the VRI.
        var suppliedOcsp = new List<(OcspInfo Info, int Index)>();
        foreach (var bytes in o.OcspResponses ?? Array.Empty<byte[]>())
        {
            var info = Revocation.ReadOcsp(bytes) ?? throw new ArgumentException("An OCSP response in PdfLtvOptions.OcspResponses is not a successful DER OCSP response.", nameof(o));
            suppliedOcsp.Add((info, ocsps.Add(info.Encoded)));
            foreach (var c in info.Certificates) certs.Add(c);
        }
        var suppliedCrl = new List<(byte[] Issuer, int Index)>();
        foreach (var bytes in o.Crls ?? Array.Empty<byte[]>())
        {
            var issuer = Revocation.CrlIssuer(bytes) ?? throw new ArgumentException("A CRL in PdfLtvOptions.Crls is not a DER certificate revocation list.", nameof(o));
            suppliedCrl.Add((issuer, crls.Add(bytes)));
        }

        var errors = new List<string>();
        var vris = new SortedDictionary<string, Vri>(StringComparer.Ordinal);
        var fetched = new Dictionary<string, (int Ocsp, int Crl)>(StringComparer.Ordinal);
        using var fetcher = new RevocationFetcher(o.HttpClient, o.UrlFilter, async, cancellationToken);
        foreach (var sig in signatures)
        {
            if (sig.Contents == null) continue;
            CmsSignedData cms;
            try { cms = CmsReader.Parse(sig.Contents); }
            catch (Exception ex) when (ex is CryptographicException or AsnContentException) { continue; }

            var vri = new Vri();
            var embedded = cms.Certificates.Select(CmsReader.LoadCertificate).OfType<X509Certificate2>().ToList();
            var signers = new List<(X509Certificate2 Cert, List<X509Certificate2> Pool)>();
            if (CmsReader.FindSigner(cms.Signer, embedded) is { } signer) signers.Add((signer, embedded));
            // The timestamp authority's chain needs validation data as much as the signer's.
            var token = cms.Signer.UnsignedAttributes.FirstOrDefault(a => a.Oid == CmsReader.IdTimestampToken).Value;
            if (token != null)
            {
                try
                {
                    var ts = CmsReader.Parse(token);
                    var tsCerts = ts.Certificates.Select(CmsReader.LoadCertificate).OfType<X509Certificate2>().ToList();
                    if (CmsReader.FindSigner(ts.Signer, tsCerts) is { } tsa) signers.Add((tsa, tsCerts));
                }
                catch (Exception ex) when (ex is CryptographicException or AsnContentException) { }
            }

            foreach (var (leaf, pool) in signers)
            {
                var chain = Chain(leaf, pool.Concat(embedded).Concat(extra));
                for (int i = 0; i < chain.Count; i++)
                {
                    var cert = chain[i];
                    vri.Certs.Add(certs.Add(cert.RawData));
                    if (IsSelfSigned(cert)) continue; // a root vouches for itself
                    var issuer = i + 1 < chain.Count ? chain[i + 1] : null;
                    bool covered = false;
                    foreach (var (info, index) in suppliedOcsp)
                        if (Revocation.Covers(info, cert)) { vri.Ocsps.Add(index); covered = true; }
                    foreach (var (crlIssuer, index) in suppliedCrl)
                        if (crlIssuer.AsSpan().SequenceEqual(cert.IssuerName.RawData)) { vri.Crls.Add(index); covered = true; }
                    if (covered || !o.FetchRevocationData) continue;

                    if (!fetched.TryGetValue(cert.Thumbprint, out var got))
                    {
                        got = (-1, -1);
                        var certErrors = new List<string>();
                        var ocsp = issuer != null ? await Revocation.FetchOcspAsync(cert, issuer, fetcher, certErrors).ConfigureAwait(false) : null;
                        if (ocsp != null)
                        {
                            got.Ocsp = ocsps.Add(ocsp.Encoded);
                            foreach (var c in ocsp.Certificates) vri.Certs.Add(certs.Add(c));
                        }
                        else if (await Revocation.FetchCrlAsync(cert, fetcher, certErrors).ConfigureAwait(false) is { } crl) got.Crl = crls.Add(crl);
                        else errors.AddRange(certErrors);
                        fetched[cert.Thumbprint] = got;
                    }
                    if (got.Ocsp >= 0) vri.Ocsps.Add(got.Ocsp);
                    if (got.Crl >= 0) vri.Crls.Add(got.Crl);
                }
            }
            vris[VriKey(sig)] = vri;
        }
        if (errors.Count > 0)
            throw new PdfSignatureException("Revocation data could not be fetched: " + string.Join(" ", errors.Distinct()));

        byte[] result = WriteDss(file, pdf, existingDss, certs, ocsps, crls, vris);
        if (!string.IsNullOrEmpty(o.TimestampUrl))
            result = await AddDocumentTimestampAsync(result, o, password, async, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The VRI key: the upper-case hex SHA-1 of the signature's /Contents bytes (for a document timestamp, of the token
    /// without its zero padding), as Adobe and other validators compute it.
    /// </summary>
    private static string VriKey(FoundSignature sig)
    {
        byte[] bytes = sig.Contents!;
        if (sig.IsDocumentTimestamp)
        {
            AsnDecoder.ReadEncodedValue(bytes, AsnEncodingRules.BER, out _, out _, out int used);
            bytes = bytes[..used];
        }
#pragma warning disable CA5350 // The VRI key is defined as a SHA-1 hash; it identifies, it does not secure.
        return Convert.ToHexString(SHA1.HashData(bytes));
#pragma warning restore CA5350
    }

    private static List<X509Certificate2> Chain(X509Certificate2 leaf, IEnumerable<X509Certificate2> pool)
    {
        var result = new List<X509Certificate2> { leaf };
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        foreach (var c in pool) chain.ChainPolicy.ExtraStore.Add(c);
        try
        {
            chain.Build(leaf);
            foreach (var el in chain.ChainElements)
                if (!el.Certificate.RawData.AsSpan().SequenceEqual(leaf.RawData)) result.Add(el.Certificate);
        }
        catch (CryptographicException)
        {
            // No chain: the store gets the signer's certificate alone.
        }
        return result;
    }

    private static bool IsSelfSigned(X509Certificate2 c) => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData);

    /// <summary>Opens a PDF for an incremental update, turning a parse failure into the documented InvalidDataException.</summary>
    internal static PdfFile Open(byte[] pdf, string? password)
    {
        try
        {
            var file = PdfFile.Open(pdf, password);
            if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
            return file;
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
    }

    // An existing store may hold large CRLs that must be carried over, so its budget is wider than the verifier's.
    private const long MaxExistingStoreDecoded = 512L * 1024 * 1024;

    private static void ReadExisting(PdfFile file, PdfDict? dss, string key, Pool pool)
    {
        var array = file.GetArray(dss, key);
        if (array == null) return;
        foreach (var (data, item) in DecodeStoreStreams(file, array, 10_000, MaxExistingStoreDecoded)) pool.Add(data, item);
    }

    /// <summary>
    /// Decodes the streams of a store array (/Certs, /OCSPs or /CRLs) with their array items, each stream once however
    /// often it is listed, and stops when <paramref name="budget"/> decoded bytes are used up. Without both, a store
    /// listing one compression bomb thousands of times would make it inflate the bomb thousands of times.
    /// Streams that cannot be decoded are skipped.
    /// </summary>
    internal static List<(byte[] Data, PdfObject Item)> DecodeStoreStreams(PdfFile file, PdfArray array, int maxItems, long budget)
    {
        var result = new List<(byte[], PdfObject)>();
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        using (PdfFile.BeginRender(CancellationToken.None, budget))
        {
            foreach (var item in array.Items.Take(maxItems))
            {
                if (file.Resolve(item) is not PdfStream s || !seen.Add(s)) continue;
                try { result.Add((file.DecodeStream(s), item)); }
                catch (InvalidDataException) when (PdfFile.DecodeLimit == 0) { break; }
                catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException) { }
            }
        }
        return result;
    }

    private static byte[] WriteDss(PdfFile file, byte[] pdf, PdfDict? existingDss, Pool certs, Pool ocsps, Pool crls,
        SortedDictionary<string, Vri> vris)
    {
        var trailer = file.Trailer;
        var rootRef = trailer["Root"] as PdfRef ?? throw new PdfSignatureException("The PDF has no document catalog reference.");
        var catalog = file.Catalog!;
        var crypt = file.Decryptor;
        int next = Math.Max(file.GetInt(trailer, "Size", 0), file.MaxObjectNumber + 1);

        var update = new PdfUpdateWriter(file, pdf, 65536);

        List<PdfObject> WriteStreams(Pool pool)
        {
            var refs = new List<PdfObject>();
            for (int i = 0; i < pool.Items.Count; i++)
            {
                if (pool.ExistingRefs[i] is PdfRef existing) { refs.Add(existing); continue; }
                int num = next++;
                byte[] data = Deflate(pool.Items[i]);
                if (crypt != null) data = crypt.EncryptStream(data, num, 0);
                update.Begin(num, 0).Ascii("<< /Length ").Int(data.Length).Ascii(" /Filter /FlateDecode >>\nstream\n")
                    .Bytes(data).Ascii("\nendstream\nendobj\n");
                refs.Add(new PdfRef(num, 0));
            }
            return refs;
        }
        var certRefs = WriteStreams(certs);
        var ocspRefs = WriteStreams(ocsps);
        var crlRefs = WriteStreams(crls);

        var dss = PdfDict.CopyOf(existingDss);
        dss.Entries["Type"] = PdfName.Get("DSS");
        if (certRefs.Count > 0) dss.Entries["Certs"] = new PdfArray(certRefs);
        if (ocspRefs.Count > 0) dss.Entries["OCSPs"] = new PdfArray(ocspRefs);
        if (crlRefs.Count > 0) dss.Entries["CRLs"] = new PdfArray(crlRefs);

        var vriDict = PdfDict.CopyOf(file.GetDict(existingDss, "VRI"));
        string now = PdfDate(DateTimeOffset.Now);
        foreach (var (key, v) in vris)
        {
            var entry = new PdfDict();
            entry.Entries["Type"] = PdfName.Get("VRI");
            if (v.Certs.Count > 0) entry.Entries["Cert"] = new PdfArray(v.Certs.Select(i => certRefs[i]).ToList());
            if (v.Ocsps.Count > 0) entry.Entries["OCSP"] = new PdfArray(v.Ocsps.Select(i => ocspRefs[i]).ToList());
            if (v.Crls.Count > 0) entry.Entries["CRL"] = new PdfArray(v.Crls.Select(i => crlRefs[i]).ToList());
            entry.Entries["TU"] = new PdfString(System.Text.Encoding.Latin1.GetBytes(now));
            vriDict.Entries[key] = entry;
        }
        dss.Entries["VRI"] = vriDict;

        var dssRef = catalog["DSS"] as PdfRef ?? new PdfRef(next++, 0);
        update.WriteObject(dssRef.Num, dssRef.Gen, dss);

        // The catalog changes when the store is new, and to declare the ETSI extension that PDF 1.7 readers look for.
        var extensions = file.GetDict(catalog, "Extensions");
        bool needsExtension = file.GetDict(extensions, "ESIC") == null && !IsPdf2(file, catalog);
        if (catalog["DSS"] is not PdfRef || needsExtension)
        {
            var cat = PdfDict.CopyOf(catalog);
            cat.Entries["DSS"] = dssRef;
            if (needsExtension)
            {
                var ext = PdfDict.CopyOf(extensions);
                var esic = new PdfDict();
                esic.Entries["BaseVersion"] = PdfName.Get("1.7");
                esic.Entries["ExtensionLevel"] = PdfNumber.Int(5);
                ext.Entries["ESIC"] = esic;
                cat.Entries["Extensions"] = ext;
            }
            update.WriteObject(rootRef.Num, rootRef.Gen, cat);
        }
        update.Finish(rootRef, next);
        return update.Output.Span.ToArray();
    }

    private static bool IsPdf2(PdfFile file, PdfDict catalog)
    {
        if (file.GetName(catalog, "Version") is { } v && v.StartsWith('2')) return true;
        var d = file.Data;
        return d.Length > 8 && d[0] == '%' && d[5] == '2';
    }

    /// <summary>
    /// Appends an invisible signature field holding an RFC 3161 document timestamp (/SubFilter /ETSI.RFC3161) over the
    /// whole file as it is now.
    /// </summary>
    private static async ValueTask<byte[]> AddDocumentTimestampAsync(byte[] pdf, PdfLtvOptions o, string? password, bool async, CancellationToken cancellationToken)
    {
        Cms.HashOid(o.TimestampHashAlgorithm); // validate early
        var file = Open(pdf, password);
        int reserve = 16384;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = BuildDocumentTimestamp(file, pdf, o, reserve);
            byte[] token = await TimestampClient.GetTokenForImprintAsync(o.TimestampUrl!, o.TimestampHttpClient, prepared.Imprint,
                o.TimestampHashAlgorithm, async, cancellationToken).ConfigureAwait(false);
            if (token.Length <= reserve) return prepared.Fill(token);
            if (attempt > 0) throw new PdfSignatureException("The document timestamp does not fit its reserved space.");
            reserve = token.Length + 2048;
        }
    }

    /// <summary>The file with an empty /Contents hole, and the hash of everything around the hole.</summary>
    private sealed class PreparedTimestamp(ByteBuffer output, int contentsStart, byte[] imprint)
    {
        public byte[] Imprint { get; } = imprint;

        /// <summary>Writes <paramref name="token"/> as hex into the hole and returns the finished file.</summary>
        public byte[] Fill(byte[] token)
        {
            const string hex = "0123456789ABCDEF";
            byte[] buf = output.RawBuffer;
            for (int i = 0; i < token.Length; i++)
            {
                buf[contentsStart + 1 + 2 * i] = (byte)hex[token[i] >> 4];
                buf[contentsStart + 2 + 2 * i] = (byte)hex[token[i] & 15];
            }
            return output.Span.ToArray();
        }
    }

    private static PreparedTimestamp BuildDocumentTimestamp(PdfFile file, byte[] pdf, PdfLtvOptions o, int reserve)
    {
        var trailer = file.Trailer;
        var rootRef = trailer["Root"] as PdfRef ?? throw new PdfSignatureException("The PDF has no document catalog reference.");
        var catalog = file.Catalog!;
        var page = FirstPage(file, catalog["Pages"]) ?? throw new PdfSignatureException("The PDF has no pages.");

        var acroEntry = catalog["AcroForm"];
        var acro = PdfDict.CopyOf(file.Resolve(acroEntry) as PdfDict);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<PdfObject>();
        if (file.GetArray(acro, "Fields") is { } existing)
        {
            fields.AddRange(existing.Items);
            foreach (var f in existing.Items)
                if (file.Resolve(f) is PdfDict fd && file.Resolve(fd["T"]) is PdfString t) names.Add(t.Text);
        }
        string fieldName = "";
        for (int i = 1; ; i++)
            if (!names.Contains(fieldName = "Signature" + i.ToString(CultureInfo.InvariantCulture))) break;

        int next = Math.Max(file.GetInt(trailer, "Size", 0), file.MaxObjectNumber + 1);
        int widgetNum = next++, apNum = next++, sigNum = next++;
        fields.Add(new PdfRef(widgetNum, 0));
        acro.Entries["Fields"] = new PdfArray(fields);
        acro.Entries["SigFlags"] = PdfNumber.Int(3);

        var update = new PdfUpdateWriter(file, pdf, reserve * 2 + 8192);
        var output = update.Output;

        if (acroEntry is PdfRef acroRef) update.WriteObject(acroRef.Num, acroRef.Gen, acro);
        else
        {
            var cat = PdfDict.CopyOf(catalog);
            cat.Entries["AcroForm"] = acro;
            update.WriteObject(rootRef.Num, rootRef.Gen, cat);
        }

        var (pageRef, pageDict) = page;
        var annotsEntry = pageDict["Annots"];
        var annots = new List<PdfObject>();
        if (file.Resolve(annotsEntry) is PdfArray oldAnnots) annots.AddRange(oldAnnots.Items);
        annots.Add(new PdfRef(widgetNum, 0));
        if (annotsEntry is PdfRef annotsRef) update.WriteObject(annotsRef.Num, annotsRef.Gen, new PdfArray(annots));
        else
        {
            var p = PdfDict.CopyOf(pageDict);
            p.Entries["Annots"] = new PdfArray(annots);
            update.WriteObject(pageRef.Num, pageRef.Gen, p);
        }

        var widget = new PdfDict();
        widget.Entries["Type"] = PdfName.Get("Annot");
        widget.Entries["Subtype"] = PdfName.Get("Widget");
        widget.Entries["FT"] = PdfName.Get("Sig");
        widget.Entries["T"] = SignatureWriter.Text(fieldName);
        widget.Entries["V"] = new PdfRef(sigNum, 0);
        var zero = PdfNumber.Int(0);
        widget.Entries["Rect"] = new PdfArray(new List<PdfObject> { zero, zero, zero, zero });
        widget.Entries["P"] = pageRef;
        widget.Entries["F"] = PdfNumber.Int(132); // Print | Locked
        var apDict = new PdfDict();
        apDict.Entries["N"] = new PdfRef(apNum, 0);
        widget.Entries["AP"] = apDict;
        update.WriteObject(widgetNum, 0, widget);

        // An empty appearance, so PDF/A validators find the widget complete.
        update.Begin(apNum, 0).Ascii("<< /Type /XObject /Subtype /Form /BBox [0 0 0 0] /Resources << >> /Length 0 >>\nstream\n\nendstream\nendobj\n");

        update.Begin(sigNum, 0).Ascii("<< /Type /DocTimeStamp /Filter /Adobe.PPKLite /SubFilter /ETSI.RFC3161 /ByteRange [");
        int byteRangePos = output.Length;
        output.Ascii(new string(' ', ByteRangeWidth)).Ascii("] /Contents ");
        int contentsStart = output.Length;
        output.Byte((byte)'<');
        for (int i = 0; i < reserve * 2; i++) output.Byte((byte)'0');
        output.Byte((byte)'>');
        int contentsEnd = output.Length;
        output.Ascii(" >>\nendobj\n");

        update.Finish(rootRef, next);

        byte[] buf = output.RawBuffer;
        int total = output.Length;
        string range = string.Create(CultureInfo.InvariantCulture, $"0 {contentsStart} {contentsEnd} {total - contentsEnd}");
        for (int i = 0; i < range.Length; i++) buf[byteRangePos + i] = (byte)range[i];

        using var hash = IncrementalHash.CreateHash(o.TimestampHashAlgorithm);
        hash.AppendData(buf, 0, contentsStart);
        hash.AppendData(buf, contentsEnd, total - contentsEnd);
        return new PreparedTimestamp(output, contentsStart, hash.GetHashAndReset());
    }

    private static (PdfRef Ref, PdfDict Dict)? FirstPage(PdfFile file, PdfObject? root)
    {
        (PdfRef Ref, PdfDict Dict)? first = null;
        PageTree.Walk(file, root, (node, d) =>
        {
            first = ((PdfRef)node, d);
            return false;
        }, referencesOnly: true);
        return first;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static string PdfDate(DateTimeOffset t)
    {
        var off = t.Offset;
        string sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return "D:" + t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + sign
            + off.Hours.ToString("D2", CultureInfo.InvariantCulture) + "'" + off.Minutes.ToString("D2", CultureInfo.InvariantCulture) + "'";
    }
}
