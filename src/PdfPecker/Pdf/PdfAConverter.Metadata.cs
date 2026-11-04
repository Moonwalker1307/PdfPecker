using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>The document's XMP metadata (6.6): the PDF/A identification and the properties of the information dictionary.</summary>
internal sealed partial class PdfAConverter
{
    /// <summary>
    /// Writes new XMP metadata: the document's title, author, subject, keywords, creator, producer, dates and custom
    /// properties, from the information dictionary and the old metadata, with the PDF/A identification. The old packet is
    /// not kept, since PDF/A holds every property in it to schemas it may not follow.
    /// </summary>
    private void Metadata(PdfDict catalog)
    {
        var (part, letter, _) = PdfA.Identification(_conformance);
        var xmpText = EmbeddedFiles.Xmp(_file);
        var xmp = xmpText == null ? new Dictionary<string, string>() : XmpReader.Properties(xmpText);
        if (!_fix)
        {
            if (xmpText == null) Problem("The document has no XMP metadata, which PDF/A requires.");
            else if (XmpReader.Get(xmp, XmpReader.PdfA, "part")?.Trim() != part.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || XmpReader.Get(xmp, XmpReader.PdfA, "conformance")?.Trim() != letter)
                Problem("The document's XMP metadata does not identify it as this PDF/A level.");
            return;
        }
        var info = _file.GetDict(_file.Trailer, "Info");
        string? Pick(string key, string ns, string name) => InfoText(info, key) ?? XmpReader.Get(xmp, ns, name);
        DateTimeOffset? PickDate(string key, string name) => EmbeddedFiles.ParseDate(InfoText(info, key)) ?? XmpReader.Date(xmp, XmpReader.Basic, name);
        var custom = new List<KeyValuePair<string, string>>();
        if (info != null)
            foreach (var key in info.Entries.Keys.Order(StringComparer.Ordinal))
                if (key.Length > 0 && !DocumentProperties.StandardKeys.Contains(key) && InfoText(info, key) is { } value)
                    custom.Add(new KeyValuePair<string, string>(key, value));
        var props = new DocumentProperties
        {
            Title = Pick("Title", XmpReader.DublinCore, "title"),
            Author = Pick("Author", XmpReader.DublinCore, "creator"),
            Subject = Pick("Subject", XmpReader.DublinCore, "description"),
            Keywords = Pick("Keywords", XmpReader.Pdf, "Keywords"),
            Creator = Pick("Creator", XmpReader.Basic, "CreatorTool"),
            Producer = Pick("Producer", XmpReader.Pdf, "Producer"),
            Custom = custom,
        }.Cleaned();
        var data = PdfA.Xmp(part, letter, props, PickDate("CreationDate", "CreateDate"), PickDate("ModDate", "ModifyDate"));
        var stream = new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Type"] = PdfName.Get("Metadata"),
            ["Subtype"] = PdfName.Get("XML"),
        }, data);
        catalog.Entries["Metadata"] = _file.AddObject(stream);
        _report.Add(xmpText == null
            ? "Added XMP metadata with the document's properties and the PDF/A identification."
            : "Rewrote the XMP metadata with the document's properties and the PDF/A identification; other metadata it held is not kept.");
    }

    /// <summary>A text entry of the information dictionary: a string or, from careless writers, a name.</summary>
    private string? InfoText(PdfDict? info, string key)
    {
        var text = _file.Resolve(info?[key]) switch
        {
            PdfString s => s.Text.TrimEnd('\0'),
            PdfName n => n.Value,
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
