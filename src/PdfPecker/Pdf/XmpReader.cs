using System.Globalization;
using System.Text;
using System.Xml;

namespace PdfPecker.Pdf;

/// <summary>Reads the simple properties of an XMP metadata packet, such as the title, the dates and the PDF/A claim.</summary>
internal static class XmpReader
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string DublinCore = "http://purl.org/dc/elements/1.1/";
    public const string Basic = "http://ns.adobe.com/xap/1.0/";
    public const string Pdf = "http://ns.adobe.com/pdf/1.3/";
    public const string PdfA = "http://www.aiim.org/pdfa/ns/id/";
    public const string PdfUA = "http://www.aiim.org/pdfua/ns/id/";
    public const string Pdfx = "http://ns.adobe.com/pdfx/1.3/";

    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>
    /// The most characters of metadata read. Real packets are a few kilobytes, with padding; a file that carries more is
    /// read up to this point and the rest ignored.
    /// </summary>
    private const int MaxCharacters = 16 << 20;

    /// <summary>
    /// The properties of the packet's top-level descriptions, keyed by namespace URI and local name, such as
    /// "http://purl.org/dc/elements/1.1/title". A language alternative gives its x-default item (else its first), and an
    /// ordered or unordered array its items joined with "; ". Structures are left out; the first value of a repeated
    /// property wins. Damaged XML gives the properties before the damage.
    /// </summary>
    public static Dictionary<string, string> Properties(string xmp)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaxCharacters,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xmp), settings);
            // A description nested in a property value is read with that value, so this loop meets only the top-level ones.
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && IsRdf(reader, "Description")) ReadDescription(reader, result);
        }
        catch (XmlException)
        {
            // Keep what was read before the damage.
        }
        return result;
    }

    /// <summary>A property's value, or null.</summary>
    public static string? Get(Dictionary<string, string> properties, string ns, string name)
        => properties.TryGetValue(ns + name, out var v) && v.Length > 0 ? v : null;

    /// <summary>An XMP date (ISO 8601, from a year alone to fractions of a second, with or without a time zone), or null.</summary>
    public static DateTimeOffset? Date(Dictionary<string, string> properties, string ns, string name)
    {
        if (Get(properties, ns, name) is not { } s) return null;
        return DateTimeOffset.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;
    }

    private static readonly string[] DateFormats =
    {
        "yyyy", "yyyy-MM", "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    };

    private static bool IsRdf(XmlReader reader, string localName) => reader.NamespaceURI == Rdf && reader.LocalName == localName;

    /// <summary>Whether an attribute or element in this namespace is a property rather than RDF or XML syntax.</summary>
    private static bool IsProperty(string ns) => ns.Length > 0 && ns is not (Rdf or XmlNamespace or XmlnsNamespace);

    private static void ReadDescription(XmlReader reader, Dictionary<string, string> result)
    {
        // Simple properties may be written as attributes of the description.
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (IsProperty(reader.NamespaceURI)) result.TryAdd(reader.NamespaceURI + reader.LocalName, reader.Value);
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        if (reader.IsEmptyElement) return;
        int depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
            string key = reader.NamespaceURI + reader.LocalName;
            bool property = IsProperty(reader.NamespaceURI);
            // The subtree reader leaves the outer reader on the property's end, whatever the value holds.
            using var value = reader.ReadSubtree();
            if (property && ReadValue(value) is { } v) result.TryAdd(key, v);
        }
    }

    private static string? ReadValue(XmlReader property)
    {
        property.Read();
        var resource = property.GetAttribute("resource", Rdf);
        if (property.IsEmptyElement) return resource;
        var text = new StringBuilder();
        var items = new List<string>();
        string? fallback = null;
        bool alternative = false;
        while (property.Read())
        {
            if (property.Depth == 1 && IsText(property.NodeType)) text.Append(property.Value);
            else if (property.NodeType == XmlNodeType.Element && property.Depth == 1 && IsRdf(property, "Alt")) alternative = true;
            else if (property.NodeType == XmlNodeType.Element && property.Depth == 2 && IsRdf(property, "li"))
            {
                string? language = property.GetAttribute("lang", XmlNamespace);
                string item = property.IsEmptyElement ? "" : DirectText(property);
                if (alternative && language == "x-default") fallback ??= item;
                items.Add(item);
            }
        }
        if (items.Count > 0) return alternative ? fallback ?? items[0] : string.Join("; ", items);
        return text.ToString().Trim() is { Length: > 0 } t ? t : resource;
    }

    /// <summary>The text directly inside the current element, read to its end.</summary>
    private static string DirectText(XmlReader reader)
    {
        int depth = reader.Depth;
        var text = new StringBuilder();
        while (reader.Read() && reader.Depth > depth)
            if (reader.Depth == depth + 1 && IsText(reader.NodeType)) text.Append(reader.Value);
        return text.ToString().Trim();
    }

    private static bool IsText(XmlNodeType type)
        => type is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace;
}
