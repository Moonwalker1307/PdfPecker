using System.Globalization;
using System.Text;
using PdfPecker.Pdf;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>The size and rotation of a page of an existing PDF, as <see cref="PdfDocumentProperties"/> reads it.</summary>
public sealed class PdfPageInfo
{
    internal PdfPageInfo(int pageNumber, double width, double height, int rotation)
    {
        PageNumber = pageNumber;
        Width = width;
        Height = height;
        Rotation = rotation;
    }

    /// <summary>The page number, from 1.</summary>
    public int PageNumber { get; }

    /// <summary>The page width in points (1/72 inch), as displayed: the crop box after the page's rotation and user unit.</summary>
    public double Width { get; }

    /// <summary>The page height in points (1/72 inch), as displayed.</summary>
    public double Height { get; }

    /// <summary>The page's rotation in degrees clockwise: 0, 90, 180 or 270.</summary>
    public int Rotation { get; }

    /// <summary>The page number and size, for debugging and logs.</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"Page {PageNumber}: {Width:0.##} x {Height:0.##} pt");
}

/// <summary>
/// The properties of an existing PDF, any PDF and not only those this library makes: its title, author and other
/// document information, its dates, PDF version, pages and their sizes, encryption and permissions, and the PDF/A and
/// PDF/UA conformance it claims. Read them with <see cref="Read(byte[], string?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The title, author, subject, keywords, creator, producer and dates come from the document information dictionary and,
/// where it lacks one, from the XMP metadata (a PDF/A-4 or PDF 2.0 file may have only XMP). When the XMP metadata was
/// changed after the information dictionary, as its metadata date shows, the XMP values come first, as PDF 2.0 says.
/// </para>
/// <para>
/// Conformance is what the file claims in its metadata, not the result of checking it: use a validator such as veraPDF
/// to check a claim.
/// </para>
/// </remarks>
public sealed class PdfDocumentProperties
{
    private PdfDocumentProperties() { }

    /// <summary>The document's title, or null.</summary>
    public string? Title { get; private init; }

    /// <summary>The document's author, or null. Several XMP authors are joined with "; ".</summary>
    public string? Author { get; private init; }

    /// <summary>The document's subject, or null.</summary>
    public string? Subject { get; private init; }

    /// <summary>The document's keywords as one text, or null.</summary>
    public string? Keywords { get; private init; }

    /// <summary>The application that created the original document, such as a word processor, or null.</summary>
    public string? Creator { get; private init; }

    /// <summary>The application that produced the PDF, or null.</summary>
    public string? Producer { get; private init; }

    /// <summary>When the document was created, or null when it does not say or the date cannot be read.</summary>
    public DateTimeOffset? CreationDate { get; private init; }

    /// <summary>When the document was last changed, or null when it does not say or the date cannot be read.</summary>
    public DateTimeOffset? ModificationDate { get; private init; }

    /// <summary>
    /// Custom document properties, such as an order number: the other text entries of the document information
    /// dictionary, or, when it has none, the custom properties of the XMP metadata (as PdfPecker writes them for PDF/A).
    /// Empty when there are none.
    /// </summary>
    public IReadOnlyDictionary<string, string> CustomProperties { get; private init; } = new Dictionary<string, string>();

    /// <summary>The document's natural language, such as "en-GB", or null.</summary>
    public string? Language { get; private init; }

    /// <summary>
    /// The PDF version, such as "1.7" or "2.0": the later of the file header's and the catalog's, or null when neither
    /// says.
    /// </summary>
    public string? Version { get; private init; }

    /// <summary>The number of pages.</summary>
    public int PageCount => Pages.Count;

    /// <summary>The pages, in order, with their sizes.</summary>
    public IReadOnlyList<PdfPageInfo> Pages { get; private init; } = Array.Empty<PdfPageInfo>();

    /// <summary>Whether the PDF is encrypted: password protected, or with only its permissions restricted.</summary>
    public bool IsEncrypted { get; private init; }

    /// <summary>
    /// What a reader may do when the PDF is opened with its user password, as the file states it;
    /// <see cref="PdfPermissions.All"/> for a PDF that is not encrypted. Opening with the owner password lifts these.
    /// </summary>
    public PdfPermissions Permissions { get; private init; } = PdfPermissions.All;

    /// <summary>The PDF/A conformance the metadata claims, such as "PDF/A-2b", "PDF/A-3u" or "PDF/A-4f", or null.</summary>
    public string? PdfAConformance { get; private init; }

    /// <summary>The PDF/UA conformance the metadata claims, "PDF/UA-1" or "PDF/UA-2", or null.</summary>
    public string? PdfUAConformance { get; private init; }

    /// <summary>Whether the PDF is tagged: it has a structure tree, which screen readers and reflow use.</summary>
    public bool IsTagged { get; private init; }

    /// <summary>
    /// Whether the PDF is linearized ("Fast Web View"), so a viewer can show its first page before the whole file has
    /// downloaded. A linearized file changed afterwards by an incremental update no longer counts.
    /// </summary>
    public bool IsLinearized { get; private init; }

    /// <summary>The document's XMP metadata packet as text, or null when it has none or it cannot be decoded.</summary>
    public string? XmpMetadata { get; private init; }

    /// <summary>Reads the properties of a PDF.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="NotSupportedException">The PDF is encrypted with a security handler other than the standard one.</exception>
    public static PdfDocumentProperties Read(byte[] pdf, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        RenderDocument doc;
        try
        {
            doc = new RenderDocument(pdf, password);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
        try
        {
            return Read(doc);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Reads the properties of a PDF read from <paramref name="pdf"/> (from its current position to its end). The stream
    /// is not disposed.
    /// </summary>
    /// <inheritdoc cref="Read(byte[], string?)"/>
    public static PdfDocumentProperties Read(Stream pdf, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Read(PdfMerger.ReadAll(pdf), password);
    }

    /// <summary>Reads the properties of the PDF file at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Read(byte[], string?)"/>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static PdfDocumentProperties ReadFile(string path, string? password = null)
        => Read(File.ReadAllBytes(path ?? throw new ArgumentNullException(nameof(path))), password);

    /// <summary>The title and page count, for debugging and logs.</summary>
    public override string ToString() => (Title ?? "(untitled)") + ", " + PageCount.ToString(CultureInfo.InvariantCulture) + " page(s)";

    private static PdfDocumentProperties Read(RenderDocument doc)
    {
        var file = doc.File;
        var catalog = file.Catalog;
        var info = file.GetDict(file.Trailer, "Info");
        var xmpText = Xmp(file);
        var xmp = xmpText == null ? new Dictionary<string, string>() : XmpReader.Properties(xmpText);

        // PDF 2.0 deprecates the information dictionary: when the XMP metadata is newer, its values win.
        var infoModified = EmbeddedFiles.ParseDate(Text(file, info, "ModDate"));
        bool xmpFirst = infoModified is { } m && XmpReader.Date(xmp, XmpReader.Basic, "MetadataDate") is { } metadataDate && metadataDate > m;
        string? Pick(string key, string ns, string name)
        {
            var fromInfo = Text(file, info, key);
            var fromXmp = XmpReader.Get(xmp, ns, name);
            return xmpFirst ? fromXmp ?? fromInfo : fromInfo ?? fromXmp;
        }
        DateTimeOffset? PickDate(string key, string name)
        {
            var fromInfo = EmbeddedFiles.ParseDate(Text(file, info, key));
            var fromXmp = XmpReader.Date(xmp, XmpReader.Basic, name);
            return xmpFirst ? fromXmp ?? fromInfo : fromInfo ?? fromXmp;
        }

        var pages = new PdfPageInfo[doc.Pages.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            var (w, h) = doc.PageSize(i);
            pages[i] = new PdfPageInfo(i + 1, w, h, doc.Pages[i].Rotate);
        }

        return new PdfDocumentProperties
        {
            Title = Pick("Title", XmpReader.DublinCore, "title"),
            Author = Pick("Author", XmpReader.DublinCore, "creator"),
            Subject = Pick("Subject", XmpReader.DublinCore, "description"),
            Keywords = Pick("Keywords", XmpReader.Pdf, "Keywords"),
            Creator = Pick("Creator", XmpReader.Basic, "CreatorTool"),
            Producer = Pick("Producer", XmpReader.Pdf, "Producer"),
            CreationDate = PickDate("CreationDate", "CreateDate"),
            ModificationDate = PickDate("ModDate", "ModifyDate"),
            CustomProperties = Custom(file, info, xmp),
            Language = Text(file, catalog, "Lang"),
            Version = LaterVersion(file.HeaderVersion, file.GetName(catalog, "Version")),
            Pages = pages,
            IsEncrypted = file.Decryptor != null,
            Permissions = file.Decryptor != null ? ReadPermissions(file) : PdfPermissions.All,
            PdfAConformance = PdfA(xmp),
            PdfUAConformance = XmpReader.Get(xmp, XmpReader.PdfUA, "part") is { } ua && IsPart(ua) ? "PDF/UA-" + ua.Trim() : null,
            IsTagged = file.GetDict(catalog, "StructTreeRoot") != null,
            IsLinearized = file.IsLinearized,
            XmpMetadata = xmpText,
        };
    }

    /// <summary>A text entry: a string (some writers end it with NUL characters) or, from careless writers, a name.</summary>
    private static string? Text(PdfFile file, PdfDict? dict, string key)
    {
        var text = file.Resolve(dict?[key]) switch
        {
            PdfString s => s.Text.TrimEnd('\0'),
            PdfName n => n.Value,
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// The XMP packet, or null when it is missing, cannot be decoded or decodes to more than
    /// <see cref="MaxXmpBytes"/>, which a decoder reaches by stopping at the budget.
    /// </summary>
    private static string? Xmp(PdfFile file)
    {
        using var scope = PdfFile.BeginRender(default, MaxXmpBytes);
        try
        {
            var xmp = EmbeddedFiles.Xmp(file);
            return PdfFile.DecodeLimit > 0 ? xmp : null;
        }
        catch (NotSupportedException)
        {
            return null; // a filter the reader does not decode
        }
    }

    /// <summary>The most bytes the XMP packet may decode to. Real packets are a few kilobytes, or more with a thumbnail.</summary>
    private const long MaxXmpBytes = 16L << 20;

    private static Dictionary<string, string> Custom(PdfFile file, PdfDict? info, Dictionary<string, string> xmp)
    {
        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        if (info != null)
            foreach (var key in info.Entries.Keys)
                if (!DocumentProperties.StandardKeys.Contains(key) && Text(file, info, key) is { } value) custom[NameText(key)] = value;
        if (custom.Count == 0)
            foreach (var (key, value) in xmp)
                if (key.StartsWith(XmpReader.Pdfx, StringComparison.Ordinal) && value.Length > 0) custom[key[XmpReader.Pdfx.Length..]] = value;
        return custom;
    }

    /// <summary>
    /// A name as text: a parsed name holds its bytes one per character, and writers (PdfPecker among them) put UTF-8 in
    /// names. Bytes that are not UTF-8 stay as they are.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string NameText(string name)
    {
        if (Ascii.IsValid(name)) return name;
        try
        {
            return StrictUtf8.GetString(Encoding.Latin1.GetBytes(name));
        }
        catch (DecoderFallbackException)
        {
            return name;
        }
    }

    /// <summary>The later of the header's version and the catalog's, which a file updated to a later version states.</summary>
    private static string? LaterVersion(string? header, string? catalog)
    {
        static (int, int)? Parse(string? v)
        {
            if (v == null || v.Length != 3 || v[1] != '.' || !char.IsAsciiDigit(v[0]) || !char.IsAsciiDigit(v[2])) return null;
            return (v[0] - '0', v[2] - '0');
        }
        var a = Parse(header);
        var b = Parse(catalog);
        if (a == null) return b == null ? null : catalog;
        return b != null && b.Value.CompareTo(a.Value) > 0 ? catalog : header;
    }

    private static PdfPermissions ReadPermissions(PdfFile file)
    {
        var encrypt = file.Resolve(file.Trailer["Encrypt"]) as PdfDict;
        // A 32-bit field: some writers give it unsigned (4294963396 for -3900).
        var p = (PdfPermissions)(int)(uint)(long)file.Num(encrypt?["P"]) & PdfPermissions.All;
        if (file.GetInt(encrypt, "R", 2) >= 3) return p;
        // Revision 2 has only the first four permissions; the later ones follow from them.
        if (p.HasFlag(PdfPermissions.Print)) p |= PdfPermissions.PrintHighQuality;
        if (p.HasFlag(PdfPermissions.Modify)) p |= PdfPermissions.Assemble;
        if (p.HasFlag(PdfPermissions.Copy)) p |= PdfPermissions.ExtractForAccessibility;
        if (p.HasFlag(PdfPermissions.Annotate)) p |= PdfPermissions.FillForms;
        return p & PdfPermissions.All;
    }

    private static bool IsPart(string s) => s.Trim() is { Length: 1 } t && t[0] is >= '1' and <= '9';

    private static string? PdfA(Dictionary<string, string> xmp)
    {
        if (XmpReader.Get(xmp, XmpReader.PdfA, "part") is not { } part || !IsPart(part)) return null;
        var level = XmpReader.Get(xmp, XmpReader.PdfA, "conformance")?.Trim();
        return "PDF/A-" + part.Trim() + (level is { Length: 1 } && char.IsAsciiLetter(level[0]) ? level.ToLowerInvariant() : "");
    }
}
