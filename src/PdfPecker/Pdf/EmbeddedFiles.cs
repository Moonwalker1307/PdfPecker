using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;
using PdfPecker.Signing;

namespace PdfPecker.Pdf;

/// <summary>
/// Embedded files (attachments): checking and ordering them for the writers, reading the embedded-files name tree of a
/// parsed PDF, and appending attachments to an existing PDF as an incremental update.
/// </summary>
internal static class EmbeddedFiles
{
    /// <summary>
    /// The attachments to write, the e-invoice's included, sorted as the name tree needs them: by the bytes of their
    /// keys. Throws when two have the same file name.
    /// </summary>
    public static List<PdfAttachment> Prepare(IEnumerable<PdfAttachment>? attachments, PdfEInvoice? invoice)
    {
        var list = new List<PdfAttachment>();
        if (attachments != null)
            foreach (var a in attachments)
                list.Add(a ?? throw new ArgumentException("The attachment list holds a null entry.", nameof(attachments)));
        if (invoice != null)
            list.Add(new PdfAttachment(invoice.FileName, invoice.Xml)
            {
                MimeType = "text/xml",
                Description = invoice.Profile == EInvoiceProfile.XRechnung ? "XRechnung invoice" : "Factur-X invoice",
                Relationship = invoice.RelationshipOrDefault switch
                {
                    EInvoiceRelationship.Data => PdfAttachmentRelationship.Data,
                    EInvoiceRelationship.Source => PdfAttachmentRelationship.Source,
                    _ => PdfAttachmentRelationship.Alternative,
                },
                OmitChecksum = true,
            });
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in list)
            if (!names.Add(a.FileName))
                throw new ArgumentException(invoice != null && a.FileName == invoice.FileName
                    ? $"An attachment is named \"{a.FileName}\", the e-invoice's file name: rename it."
                    : $"Two attachments are named \"{a.FileName}\": attachment file names must be unique.", nameof(attachments));
        list.Sort((x, y) => KeyBytes(x.FileName).AsSpan().SequenceCompareTo(KeyBytes(y.FileName)));
        return list;
    }

    /// <summary>A name-tree key as the writers encode it: ASCII bytes, else UTF-16BE with a byte order mark.</summary>
    public static byte[] KeyBytes(string s)
    {
        if (s.All(ch => ch <= 126)) return Encoding.Latin1.GetBytes(s);
        var raw = new byte[2 + s.Length * 2];
        raw[0] = 0xFE;
        raw[1] = 0xFF;
        for (int i = 0; i < s.Length; i++)
        {
            raw[2 + i * 2] = (byte)(s[i] >> 8);
            raw[3 + i * 2] = (byte)s[i];
        }
        return raw;
    }

    /// <summary>A PDF date in UTC, as the e-invoice attachment has always been written: D:YYYYMMDDHHmmSSZ.</summary>
    public static string PdfDate(DateTime utc) => "D:" + utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";

    public static string PdfDate(DateTimeOffset date) => PdfDate(date.UtcDateTime);

    /// <summary>Parses a PDF date (D:YYYYMMDDHHmmSSOHH'mm', every part after the year optional), or null.</summary>
    public static DateTimeOffset? ParseDate(string? s)
    {
        if (s == null) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        var m = Regex.Match(s, @"^(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:([Zz+\-])(?:(\d{2})'?(?:(\d{2})'?)?)?)?");
        if (!m.Success) return null;
        int Part(int g, int fallback) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : fallback;
        try
        {
            var offset = TimeSpan.Zero;
            if (m.Groups[7].Success && m.Groups[7].Value is "+" or "-")
            {
                offset = new TimeSpan(Part(8, 0), Part(9, 0), 0);
                if (m.Groups[7].Value == "-") offset = -offset;
            }
            return new DateTimeOffset(Part(1, 1), Part(2, 1), Part(3, 1), Part(4, 0), Part(5, 0), Part(6, 0), offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A MIME type for a file name's extension; application/octet-stream when it is not a common one.</summary>
    public static string GuessMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".xml" => "text/xml",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".htm" or ".html" => "text/html",
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        _ => "application/octet-stream",
    };

    // ------------------------------------------------------------------ reading

    /// <summary>An attached file found in a parsed PDF.</summary>
    public sealed record Found(string FileName, PdfDict Spec, PdfStream Stream)
    {
        public PdfAttachment ToAttachment(PdfFile file)
        {
            var att = new PdfAttachment(FileName, Decode(file, Stream))
            {
                MimeType = file.GetName(Stream, "Subtype"),
                Description = (file.Resolve(Spec["Desc"]) as PdfString)?.Text,
            };
            if (file.GetName(Spec, "AFRelationship") is { } rel && Enum.TryParse<PdfAttachmentRelationship>(rel, out var r)
                && Enum.IsDefined(r))
                att.Relationship = r;
            var parms = file.GetDict(Stream, "Params");
            att.CreationDate = ParseDate((file.Resolve(parms?["CreationDate"]) as PdfString)?.Text);
            att.ModDate = ParseDate((file.Resolve(parms?["ModDate"]) as PdfString)?.Text);
            return att;
        }
    }

    /// <summary>
    /// An embedded file's content, decoded within the same 512 MB budget a rendered page gets, so a small hostile file
    /// cannot inflate without bound. Inside a scope the caller opened, the budget is the whole call's: thousands of file
    /// specifications can point at one small stream that inflates to hundreds of megabytes.
    /// </summary>
    public static byte[] Decode(PdfFile file, PdfStream stream)
    {
        if (PdfFile.InRenderScope) return file.DecodeStream(stream);
        using (PdfFile.BeginRender(default)) return file.DecodeStream(stream);
    }

    /// <summary>The attached files: the embedded-files name tree in order, then associated files (/AF) not in it.</summary>
    public static List<Found> Read(PdfFile file)
    {
        var result = new List<Found>();
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        void Add(PdfObject? value, string? key)
        {
            if (file.Resolve(value) is not PdfDict spec || !seen.Add(spec)) return;
            var ef = file.GetDict(spec, "EF");
            var stream = file.GetStream(ef, "UF") ?? file.GetStream(ef, "F");
            if (stream == null) return;
            var name = (file.Resolve(spec["UF"]) as PdfString)?.Text ?? (file.Resolve(spec["F"]) as PdfString)?.Text ?? key;
            result.Add(new Found(CleanName(name), spec, stream));
        }
        var catalog = file.Catalog;
        foreach (var (key, value) in NameTree(file, catalog)) Add(value, key.Text);
        if (file.GetArray(catalog, "AF") is { } af)
            foreach (var item in af.Items) Add(item, null);
        return result;
    }

    /// <summary>A file name that <see cref="PdfAttachment"/> accepts: the last path part, without control characters.</summary>
    private static string CleanName(string? name)
    {
        if (name != null)
        {
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            name = new string(name[(slash + 1)..].Where(ch => ch >= ' ').ToArray());
        }
        return string.IsNullOrEmpty(name) ? "attachment" : name;
    }

    /// <summary>The key-value pairs of the catalog's embedded-files name tree, in tree order.</summary>
    public static List<(PdfString Key, PdfObject Value)> NameTree(PdfFile file, PdfDict? catalog)
    {
        var pairs = new List<(PdfString, PdfObject)>();
        var visited = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        void Walk(PdfDict? node, int depth)
        {
            if (node == null || depth > 32 || !visited.Add(node) || pairs.Count > 100_000) return;
            if (file.GetArray(node, "Names") is { } names)
                for (int i = 0; i + 1 < names.Count; i += 2)
                    if (file.Resolve(names[i]) is PdfString key) pairs.Add((key, names[i + 1]));
            if (file.GetArray(node, "Kids") is { } kids)
                foreach (var kid in kids.Items) Walk(file.Resolve(kid) as PdfDict, depth + 1);
        }
        Walk(file.GetDict(file.GetDict(catalog, "Names"), "EmbeddedFiles"), 0);
        return pairs;
    }

    /// <summary>
    /// The PDF/A part the document's XMP metadata claims (1, 2, 3 or 4), or 0. PDF/A-4f counts as 3, since both allow
    /// any attachment and list them in /AF.
    /// </summary>
    public static int PdfAPart(PdfFile file)
    {
        if (Xmp(file) is not { } xmp) return 0;
        var m = Regex.Match(xmp, @"pdfaid:part\s*(?:>|=\s*[""'])\s*(\d)", RegexOptions.NonBacktracking);
        if (!m.Success) return 0;
        int part = m.Groups[1].Value[0] - '0';
        // PDF/A-4f is the PDF/A-4 level that allows any attachment, as PDF/A-3 does; plain PDF/A-4 allows only PDF/A files.
        if (part == 4 && Regex.IsMatch(xmp, @"pdfaid:conformance\s*(?:>|=\s*[""'])\s*[Ff]", RegexOptions.NonBacktracking)) return 3;
        return part;
    }

    /// <summary>The document's XMP metadata, or null when it has none or it cannot be decoded.</summary>
    public static string? Xmp(PdfFile file)
    {
        if (file.GetStream(file.Catalog, "Metadata") is not { } meta) return null;
        try
        {
            return Encoding.UTF8.GetString(Decode(file, meta));
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value of an XMP property in any namespace prefix, written as an element or as an attribute, or null. The
    /// prefix varies: Factur-X writes fx, ZUGFeRD 2.0 often zf.
    /// </summary>
    public static string? XmpProperty(string xmp, string name)
    {
        // The XMP comes from the file, so the match must stay linear: a backtracking engine retries the lazy value at every
        // repeated property name that never closes, which is quadratic in the length of the metadata.
        var m = Regex.Match(xmp, @"\w+:" + Regex.Escape(name) + @"\s*(?:>|=\s*[""'])\s*([^<""']*?)\s*[<""']", RegexOptions.NonBacktracking);
        return m.Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------ appending

    /// <summary>Appends the (prepared) attachments to a PDF as an incremental update.</summary>
    public static byte[] Append(PdfFile file, byte[] pdf, List<PdfAttachment> attachments)
    {
        var trailer = file.Trailer;
        var rootRef = trailer["Root"] as PdfRef ?? throw new InvalidDataException("The PDF has no document catalog reference.");
        var catalog = file.Resolve(rootRef) as PdfDict ?? throw new InvalidDataException("The PDF has no document catalog.");
        int part = PdfAPart(file);
        if (part is 1 or 2 or 4)
            throw new ArgumentException($"The PDF is PDF/A-{part}, which allows attaching only PDF/A files; PdfPecker attaches files to PDF/A-3, PDF/A-4f and other PDFs.", nameof(pdf));
        var existing = NameTree(file, catalog);
        var taken = existing.Select(p => p.Key.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var a in attachments)
            if (taken.Contains(a.FileName))
                throw new ArgumentException($"The PDF already has an attachment named \"{a.FileName}\".", nameof(attachments));

        int next = Math.Max(file.GetInt(trailer, "Size", 0), file.MaxObjectNumber + 1);
        var update = new PdfUpdateWriter(file, pdf, attachments.Sum(a => a.Data.Length) + 4096);

        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var entries = existing.Select(p => (Key: p.Key.Bytes, p.Value)).ToList();
        var newSpecs = new List<PdfObject>();
        foreach (var a in attachments)
        {
            int specNum = next++, fileNum = next++;
            // The embedded file stream.
            byte[] content = a.Data;
            bool flate = false;
            if (content.Length > 64)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
                if (ms.Length < content.Length) { content = ms.ToArray(); flate = true; }
            }
            var parms = new PdfDict();
            parms.Entries["Size"] = PdfNumber.Int(a.Data.Length);
            if (a.CreationDate is { } created) parms.Entries["CreationDate"] = new PdfString(Encoding.ASCII.GetBytes(PdfDate(created)));
            parms.Entries["ModDate"] = new PdfString(Encoding.ASCII.GetBytes(a.ModDate is { } mod ? PdfDate(mod) : PdfDate(now)));
            if (!a.OmitChecksum) parms.Entries["CheckSum"] = new PdfString(MD5.HashData(a.Data), hex: true);
            var dict = new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["Type"] = PdfName.Get("EmbeddedFile"),
                ["Subtype"] = PdfName.Get(a.MimeTypeOrGuess),
                ["Params"] = parms,
                ["Length"] = PdfNull.Instance, // set to the encrypted length, in this place
            };
            if (flate) dict["Filter"] = PdfName.Get("FlateDecode");
            update.WriteStream(fileNum, 0, dict, content);

            // Its file specification.
            var spec = new PdfDict();
            spec.Entries["Type"] = PdfName.Get("Filespec");
            spec.Entries["F"] = SignatureWriter.Text(a.FileName);
            spec.Entries["UF"] = SignatureWriter.Text(a.FileName);
            spec.Entries["Desc"] = SignatureWriter.Text(string.IsNullOrEmpty(a.Description) ? a.FileName : a.Description);
            spec.Entries["AFRelationship"] = PdfName.Get(a.Relationship.ToString());
            var ef = new PdfDict();
            ef.Entries["F"] = new PdfRef(fileNum, 0);
            ef.Entries["UF"] = new PdfRef(fileNum, 0);
            spec.Entries["EF"] = ef;
            update.WriteObject(specNum, 0, spec);
            entries.Add((KeyBytes(a.FileName), new PdfRef(specNum, 0)));
            newSpecs.Add(new PdfRef(specNum, 0));
        }

        // A new, flat embedded-files tree with the old entries and the new ones, sorted.
        entries.Sort((x, y) => x.Key.AsSpan().SequenceCompareTo(y.Key));
        int treeNum = next++;
        var tree = new PdfDict();
        tree.Entries["Names"] = new PdfArray(entries.SelectMany(e => new PdfObject[] { new PdfString(e.Key), e.Value }).ToList());
        update.WriteObject(treeNum, 0, tree);

        var cat = Copy(catalog);
        var names = Copy(file.GetDict(catalog, "Names"));
        names.Entries["EmbeddedFiles"] = new PdfRef(treeNum, 0);
        cat.Entries["Names"] = names;
        if (part == 3)
        {
            var af = new List<PdfObject>();
            if (file.GetArray(catalog, "AF") is { } old) af.AddRange(old.Items);
            af.AddRange(newSpecs);
            cat.Entries["AF"] = new PdfArray(af);
        }
        update.WriteObject(rootRef.Num, rootRef.Gen, cat);
        update.Finish(rootRef, next);
        return update.Output.Span.ToArray();
    }

    private static PdfDict Copy(PdfDict? d)
        => d == null ? new PdfDict() : new PdfDict(new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal));
}
