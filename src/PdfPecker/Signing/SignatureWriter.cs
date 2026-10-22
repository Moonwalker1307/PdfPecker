using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Signing;

/// <summary>
/// Appends a signature field, its widget and the signature dictionary to a PDF as an incremental update, then fills in
/// the CMS signature over everything but the /Contents hole.
/// </summary>
internal static class SignatureWriter
{
    private const int ByteRangeWidth = 48;

    /// <summary>
    /// Signs synchronously. The task is already complete (see <see cref="SignAsync"/>), so reading its result does not
    /// block; the token still reaches the external signer and the timestamp request.
    /// </summary>
    public static byte[] Sign(byte[] pdf, PdfSignatureOptions o, string? password, CancellationToken cancellationToken)
        => SignAsync(pdf, o, password, async: false, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Signs the PDF. With <paramref name="async"/> false nothing is awaited that has not already completed, so the
    /// synchronous API can run the same code and read the result without blocking on a pending task.
    /// </summary>
    public static async ValueTask<byte[]> SignAsync(byte[] pdf, PdfSignatureOptions o, string? password, bool async, CancellationToken cancellationToken)
    {
        var external = o.ExternalSigner;
        if (external != null) Cms.CheckKeyType(o.Certificate, external.Algorithm);
        else if (!o.Certificate.HasPrivateKey)
            throw new ArgumentException("The signing certificate has no private key. Load it from a .pfx file or a certificate store entry that includes the key, or sign with a PdfExternalSigner.", nameof(o));
        Cms.HashOid(o.HashAlgorithm); // validate early

        var file = LtvWriter.Open(pdf, password);
        DocMdp.CheckCanSign(file, o.Certification);
        var certs = Cms.CertificateChain(o.Certificate, o.ExtraCertificates);
        var time = o.SigningTime ?? DateTimeOffset.Now;
        time = time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond));
        bool pades = o.Format == PdfSignatureFormat.PAdES;

        int reserve = 2048 + certs.Sum(c => c.RawData.Length) + (string.IsNullOrEmpty(o.TimestampUrl) ? 0 : 12288);
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = Build(file, pdf, o, time, reserve);

            byte[] signedAttrs = Cms.SignedAttributes(prepared.Digest, o.Certificate, o.HashAlgorithm, pades, time);
            byte[] signature;
            PdfSignatureAlgorithm algorithm;
            if (external == null) (signature, algorithm) = Cms.SignWithPrivateKey(o.Certificate, signedAttrs, o.HashAlgorithm);
            else
            {
                algorithm = external.Algorithm;
                var request = new PdfSignatureRequest(o.Certificate, algorithm, o.HashAlgorithm, signedAttrs, Cms.HashData(o.HashAlgorithm, signedAttrs));
                byte[] raw = await external.SignAsync(request, async, cancellationToken).ConfigureAwait(false);
                signature = Cms.CheckExternalSignature(o.Certificate, algorithm, signedAttrs, o.HashAlgorithm, raw);
            }
            byte[]? token = string.IsNullOrEmpty(o.TimestampUrl)
                ? null
                : await TimestampClient.GetTokenAsync(o.TimestampUrl!, o.TimestampHttpClient, signature, o.HashAlgorithm, async, cancellationToken).ConfigureAwait(false);
            byte[] cms = Cms.Assemble(signedAttrs, signature, algorithm, token, o.Certificate, certs, o.HashAlgorithm);

            if (cms.Length <= reserve) return prepared.Fill(cms);
            if (attempt > 0) throw new PdfSignatureException("The signature does not fit its reserved space.");
            reserve = cms.Length + 2048;
        }
    }

    /// <summary>The signed file with an empty /Contents hole, and the digest of everything around the hole.</summary>
    private sealed class Prepared(ByteBuffer output, int contentsStart, byte[] digest)
    {
        public byte[] Digest { get; } = digest;

        /// <summary>Writes <paramref name="cms"/> as hex into the hole and returns the finished file.</summary>
        public byte[] Fill(byte[] cms)
        {
            const string hex = "0123456789ABCDEF";
            byte[] buf = output.RawBuffer;
            for (int i = 0; i < cms.Length; i++)
            {
                buf[contentsStart + 1 + 2 * i] = (byte)hex[cms[i] >> 4];
                buf[contentsStart + 2 + 2 * i] = (byte)hex[cms[i] & 15];
            }
            return output.Span.ToArray();
        }
    }

    private static Prepared Build(PdfFile file, byte[] pdf, PdfSignatureOptions o, DateTimeOffset time, int reserve)
    {
        var trailer = file.Trailer;
        var rootRef = trailer["Root"] as PdfRef ?? throw new PdfSignatureException("The PDF has no document catalog reference.");
        var catalog = file.Resolve(rootRef) as PdfDict ?? throw new PdfSignatureException("The PDF has no document catalog.");
        var crypt = file.Decryptor;

        // Pages, with their inherited media boxes.
        var pages = new List<(PdfRef Ref, PdfDict Dict, double[] Media)>();
        void Walk(PdfObject? node, double[] media, int depth)
        {
            if (depth > 64 || node is not PdfRef r || file.Resolve(r) is not PdfDict d) return;
            var box = file.GetNumbers(d, "MediaBox");
            if (box is { Length: 4 }) media = box;
            var kids = file.GetArray(d, "Kids");
            if (kids != null && file.GetName(d, "Type") != "Page")
                foreach (var k in kids.Items) Walk(k, media, depth + 1);
            else pages.Add((r, d, media));
        }
        Walk(catalog["Pages"], new double[] { 0, 0, 612, 792 }, 0);
        if (pages.Count == 0) throw new PdfSignatureException("The PDF has no pages.");

        var ap = o.Appearance;
        int pageIndex = 0;
        if (ap != null)
        {
            pageIndex = ap.Page < 0 ? pages.Count + ap.Page : ap.Page - 1;
            if (pageIndex < 0 || pageIndex >= pages.Count)
                throw new ArgumentOutOfRangeException(nameof(o), $"The signature box is on page {ap.Page}, but the PDF has {pages.Count} page(s).");
            if (ap.Width <= 0 || ap.Height <= 0) throw new ArgumentOutOfRangeException(nameof(o), "The signature box needs a positive width and height.");
        }
        var (pageRef, pageDict, media) = pages[pageIndex];

        // AcroForm: the signature goes into the named empty signature field when there is one, else into a new field.
        var acroEntry = catalog["AcroForm"];
        var acro = Copy(file.Resolve(acroEntry) as PdfDict);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var target = FindField(file, file.GetArray(acro, "Fields"), o.FieldName, names);
        if (target != null)
        {
            if (FieldType(file, target.Field) != "Sig")
                throw new PdfSignatureException($"The field \"{o.FieldName}\" is not a signature field.");
            if (file.Resolve(target.Field["V"]) is { IsNull: false })
                throw new PdfSignatureException($"The signature field \"{o.FieldName}\" is already signed.");
        }

        // A certified document takes new signatures only invisibly or in the fields it had when it was certified: a new
        // visible field changes how a page looks, which validators such as pyHanko count against the certification.
        if (target == null && ap != null && DocMdp.Certification(file) != null)
            throw new PdfSignatureException("The PDF is certified, so a new visible signature box would break the certification. Sign invisibly, or into an empty signature field the document had when it was certified.");

        int next = Math.Max(file.GetInt(trailer, "Size", 0), file.MaxObjectNumber + 1);
        var widgetRef = target?.WidgetRef ?? new PdfRef(next++, 0);
        int apNum = next++, sigNum = next++;

        if (target == null)
        {
            var fields = new List<PdfObject>();
            if (file.GetArray(acro, "Fields") is { } existing) fields.AddRange(existing.Items);
            fields.Add(widgetRef);
            acro.Entries["Fields"] = new PdfArray(fields);
        }
        acro.Entries["SigFlags"] = PdfNumber.Int(3);
        string fieldName = o.FieldName ?? "";
        if (fieldName.Length == 0)
            for (int i = 1; ; i++)
                if (!names.Contains(fieldName = "Signature" + i.ToString(CultureInfo.InvariantCulture))) break;

        var update = new PdfUpdateWriter(file, pdf, reserve * 2 + 8192);
        var output = update.Output;

        // Catalog (or the AcroForm object it points to). A certification is named in the catalog's /Perms.
        bool certify = o.Certification != PdfCertificationLevel.None;
        if (acroEntry is PdfRef acroRef) update.WriteObject(acroRef.Num, acroRef.Gen, acro);
        if (acroEntry is not PdfRef || certify)
        {
            var cat = Copy(catalog);
            if (acroEntry is not PdfRef) cat.Entries["AcroForm"] = acro;
            if (certify)
            {
                var perms = Copy(file.GetDict(catalog, "Perms"));
                perms.Entries["DocMDP"] = new PdfRef(sigNum, 0);
                cat.Entries["Perms"] = perms;
            }
            update.WriteObject(rootRef.Num, rootRef.Gen, cat);
        }

        float x1 = 0, y1 = 0, w = 0, h = 0;
        PdfDict widget;
        if (target == null)
        {
            // The page's annotations.
            var annotsEntry = pageDict["Annots"];
            var annots = new List<PdfObject>();
            if (file.Resolve(annotsEntry) is PdfArray oldAnnots) annots.AddRange(oldAnnots.Items);
            annots.Add(widgetRef);
            if (annotsEntry is PdfRef annotsRef) update.WriteObject(annotsRef.Num, annotsRef.Gen, new PdfArray(annots));
            else
            {
                var page = Copy(pageDict);
                page.Entries["Annots"] = new PdfArray(annots);
                update.WriteObject(pageRef.Num, pageRef.Gen, page);
            }

            // Widget annotation, merged with the signature field.
            if (ap != null)
            {
                w = ap.Width;
                h = ap.Height;
                x1 = (float)media[0] + ap.X;
                y1 = (float)media[3] - ap.Y - h;
            }
            widget = new PdfDict();
            widget.Entries["Type"] = PdfName.Get("Annot");
            widget.Entries["Subtype"] = PdfName.Get("Widget");
            widget.Entries["FT"] = PdfName.Get("Sig");
            widget.Entries["T"] = Text(fieldName);
            widget.Entries["V"] = new PdfRef(sigNum, 0);
            widget.Entries["Rect"] = new PdfArray(new List<PdfObject> { Real(x1), Real(y1), Real(x1 + w), Real(y1 + h) });
            widget.Entries["P"] = pageRef;
            widget.Entries["F"] = PdfNumber.Int(132); // Print | Locked
        }
        else
        {
            // The existing field keeps its place on the page; the signature box fills its rectangle.
            widget = Copy(target.Widget);
            if (file.GetNumbers(widget, "Rect") is { Length: 4 } rect)
            {
                w = (float)Math.Abs(rect[2] - rect[0]);
                h = (float)Math.Abs(rect[3] - rect[1]);
            }
            widget.Entries["F"] = PdfNumber.Int(file.GetInt(widget, "F", 0) | 132);
            if (ReferenceEquals(target.Field, target.Widget)) widget.Entries["V"] = new PdfRef(sigNum, 0);
            else
            {
                var field = Copy(target.Field);
                field.Entries["V"] = new PdfRef(sigNum, 0);
                update.WriteObject(target.FieldRef.Num, target.FieldRef.Gen, field);
            }
            // A visible field shows the signer even when no appearance was asked for.
            if (ap == null && w > 0 && h > 0) ap = new PdfSignatureAppearance();
        }
        var apDict = new PdfDict();
        apDict.Entries["N"] = new PdfRef(apNum, 0);
        widget.Entries["AP"] = apDict;
        update.WriteObject(widgetRef.Num, widgetRef.Gen, widget);

        // Appearance stream: the visible box, or an empty form for an invisible signature.
        string signer = o.SignerName ?? SignerName(o.Certificate);
        byte[] content = ap == null ? Array.Empty<byte>() : SignatureAppearanceBuilder.Content(w, h, Lines(ap, o, signer, time), ap.Border);
        bool flate = content.Length > 64;
        if (flate)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
            content = ms.ToArray();
        }
        if (crypt != null) content = crypt.EncryptStream(content, apNum, 0);
        update.Begin(apNum, 0).Ascii("<< /Type /XObject /Subtype /Form /BBox [0 0 ").Num(w).Sp().Num(h)
            .Ascii("] /Resources << >> /Length ").Int(content.Length);
        if (flate) output.Ascii(" /Filter /FlateDecode");
        output.Ascii(" >>\nstream\n").Bytes(content).Ascii("\nendstream\nendobj\n");

        // Signature dictionary with the /ByteRange and /Contents placeholders.
        update.Begin(sigNum, 0).Ascii("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /")
            .Ascii(o.Format == PdfSignatureFormat.PAdES ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached")
            .Ascii(" /ByteRange [");
        int byteRangePos = output.Length;
        output.Ascii(new string(' ', ByteRangeWidth)).Ascii("] /Contents ");
        int contentsStart = output.Length;
        output.Byte((byte)'<');
        for (int i = 0; i < reserve * 2; i++) output.Byte((byte)'0');
        output.Byte((byte)'>');
        int contentsEnd = output.Length;
        var strings = update.Objects; // encrypts for the signature dictionary, begun above
        output.Ascii(" /M ");
        strings.WriteString(output, Latin(PdfDate(time)));
        output.Ascii(" /Name ");
        strings.WriteString(output, Text(signer).Bytes);
        if (!string.IsNullOrEmpty(o.Reason)) { output.Ascii(" /Reason "); strings.WriteString(output, Text(o.Reason!).Bytes); }
        if (!string.IsNullOrEmpty(o.Location)) { output.Ascii(" /Location "); strings.WriteString(output, Text(o.Location!).Bytes); }
        if (!string.IsNullOrEmpty(o.ContactInfo)) { output.Ascii(" /ContactInfo "); strings.WriteString(output, Text(o.ContactInfo!).Bytes); }
        if (certify)
        {
            // The DocMDP transform without the digest entries, which ISO 32000-2 and PAdES leave out.
            output.Ascii(" /Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P ")
                .Int((int)o.Certification).Ascii(" /V /1.2 >> >>]");
        }
        output.Ascii(" /Prop_Build << /App << /Name /PdfPecker >> >> >>\nendobj\n");

        update.Finish(rootRef, next);

        // Fill in the byte range, hash it and sign.
        byte[] buf = output.RawBuffer;
        int total = output.Length;
        string range = string.Create(CultureInfo.InvariantCulture, $"0 {contentsStart} {contentsEnd} {total - contentsEnd}");
        for (int i = 0; i < range.Length; i++) buf[byteRangePos + i] = (byte)range[i];

        using var hash = IncrementalHash.CreateHash(o.HashAlgorithm);
        hash.AppendData(buf, 0, contentsStart);
        hash.AppendData(buf, contentsEnd, total - contentsEnd);
        return new Prepared(output, contentsStart, hash.GetHashAndReset());
    }

    private sealed record ExistingField(PdfRef FieldRef, PdfDict Field, PdfRef WidgetRef, PdfDict Widget);

    /// <summary>
    /// The form field with the fully qualified <paramref name="name"/> ("parent.child") and its widget, or null; collects the
    /// top-level field names into <paramref name="topNames"/> on the way.
    /// </summary>
    private static ExistingField? FindField(PdfFile file, PdfArray? fields, string? name, HashSet<string> topNames)
    {
        if (fields == null) return null;
        ExistingField? found = null;
        var seen = new HashSet<int>();
        void Walk(PdfArray kids, string prefix, int depth)
        {
            if (depth > 32) return;
            foreach (var k in kids.Items)
            {
                if (k is not PdfRef r || seen.Count >= 100_000 || !seen.Add(r.Num) || file.Resolve(r) is not PdfDict d) continue;
                if (file.Resolve(d["T"]) is not PdfString t) continue; // a widget of its parent
                if (depth == 0) topNames.Add(t.Text);
                string full = prefix.Length == 0 ? t.Text : prefix + "." + t.Text;
                if (found == null && full == name) found = WithWidget(file, r, d, full);
                else if (file.GetArray(d, "Kids") is { } sub) Walk(sub, full, depth + 1);
            }
        }
        Walk(fields, "", 0);
        return found;
    }

    private static ExistingField WithWidget(PdfFile file, PdfRef fieldRef, PdfDict field, string name)
    {
        if (file.GetName(field, "Subtype") == "Widget") return new ExistingField(fieldRef, field, fieldRef, field);
        if (file.GetArray(field, "Kids") is { } kids)
            foreach (var k in kids.Items)
                if (k is PdfRef kr && file.Resolve(kr) is PdfDict kd && kd["T"] == null && file.GetName(kd, "Subtype") == "Widget")
                    return new ExistingField(fieldRef, field, kr, kd);
        throw new PdfSignatureException($"The signature field \"{name}\" has no widget annotation to sign.");
    }

    /// <summary>A field's type, inherited from its ancestors when it has none of its own.</summary>
    private static string? FieldType(PdfFile file, PdfDict field)
    {
        PdfDict? f = field;
        for (int i = 0; f != null && i < 32; i++)
        {
            if (file.GetName(f, "FT") is { } ft) return ft;
            f = file.GetDict(f, "Parent");
        }
        return null;
    }

    private static string[] Lines(PdfSignatureAppearance ap, PdfSignatureOptions o, string signer, DateTimeOffset time)
    {
        string date = time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        string text = ap.Text ?? "Digitally signed by {name}\nDate: {date}"
            + (string.IsNullOrEmpty(o.Reason) ? "" : "\nReason: {reason}")
            + (string.IsNullOrEmpty(o.Location) ? "" : "\nLocation: {location}");
        text = text.Replace("{name}", signer).Replace("{date}", date)
            .Replace("{reason}", o.Reason ?? "").Replace("{location}", o.Location ?? "");
        return text.Replace("\r\n", "\n").Split('\n');
    }

    private static string SignerName(X509Certificate2 cert)
    {
        var name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(name) ? cert.Subject : name;
    }

    private static string PdfDate(DateTimeOffset t)
    {
        var off = t.Offset;
        string sign = off < TimeSpan.Zero ? "-" : "+";
        off = off.Duration();
        return "D:" + t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + sign
            + off.Hours.ToString("D2", CultureInfo.InvariantCulture) + "'" + off.Minutes.ToString("D2", CultureInfo.InvariantCulture) + "'";
    }

    private static byte[] Latin(string s) => System.Text.Encoding.Latin1.GetBytes(s);

    /// <summary>A PDF text string: ASCII when possible, otherwise UTF-16BE with a byte order mark.</summary>
    internal static PdfString Text(string s)
    {
        if (s.All(ch => ch >= 32 && ch < 127)) return new PdfString(Latin(s));
        var bytes = new byte[2 + 2 * s.Length];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        for (int i = 0; i < s.Length; i++)
        {
            bytes[2 + 2 * i] = (byte)(s[i] >> 8);
            bytes[3 + 2 * i] = (byte)s[i];
        }
        return new PdfString(bytes);
    }

    private static PdfNumber Real(float v) => PdfNumber.Real(Math.Round(v, 3));

    private static PdfDict Copy(PdfDict? d)
        => d == null ? new PdfDict() : new PdfDict(new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal));
}
