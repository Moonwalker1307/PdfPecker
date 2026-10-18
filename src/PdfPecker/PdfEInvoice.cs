using System.Xml;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>A Factur-X / ZUGFeRD profile: how much of the invoice the XML carries.</summary>
public enum EInvoiceProfile
{
    /// <summary>MINIMUM: the header totals only; not a full invoice in Germany.</summary>
    Minimum,
    /// <summary>BASIC WL: header and totals without invoice lines; not a full invoice in Germany.</summary>
    BasicWL,
    /// <summary>BASIC: a subset of EN 16931 with invoice lines.</summary>
    Basic,
    /// <summary>EN 16931 (COMFORT): the European standard's core invoice. The usual choice.</summary>
    En16931,
    /// <summary>EXTENDED: EN 16931 plus extra fields.</summary>
    Extended,
    /// <summary>XRECHNUNG: a German CIUS of EN 16931, embedded as xrechnung.xml.</summary>
    XRechnung,
}

/// <summary>How the embedded XML relates to the visible pages (the file specification's /AFRelationship).</summary>
public enum EInvoiceRelationship
{
    /// <summary>The XML is an equivalent, machine-readable form of the invoice. Default for BASIC and above.</summary>
    Alternative,
    /// <summary>The XML carries data the visible invoice is built from. Default for MINIMUM and BASIC WL.</summary>
    Data,
    /// <summary>The XML is the source the PDF was made from.</summary>
    Source,
}

/// <summary>
/// A Factur-X / ZUGFeRD e-invoice: the invoice as UN/CEFACT Cross Industry Invoice (CII) XML, embedded in a PDF/A-3
/// file next to the human-readable pages. PdfPecker does not build the XML; pass the one your invoicing system makes.
/// </summary>
public sealed class PdfEInvoice
{
    /// <summary>The root namespace of a Cross Industry Invoice.</summary>
    internal const string CiiNamespace = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private const string RamNamespace = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";

    /// <summary>
    /// An e-invoice from CII XML. The profile is read from the XML's guideline (BT-24,
    /// <c>GuidelineSpecifiedDocumentContextParameter/ID</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The XML is not a Cross Industry Invoice, or its guideline names no known profile.</exception>
    public PdfEInvoice(byte[] xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var guideline = ReadGuideline(xml);
        Xml = xml;
        Profile = ProfileFromGuideline(guideline)
            ?? throw new ArgumentException($"The invoice's guideline \"{guideline}\" names no Factur-X / ZUGFeRD profile; pass the profile explicitly.", nameof(xml));
    }

    /// <summary>An e-invoice from CII XML with the given profile.</summary>
    /// <exception cref="ArgumentException">The XML is not a Cross Industry Invoice.</exception>
    public PdfEInvoice(byte[] xml, EInvoiceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ReadGuideline(xml);
        Xml = xml;
        Profile = profile;
    }

    private PdfEInvoice(byte[] xml, EInvoiceProfile profile, EInvoiceRelationship? relationship)
    {
        Xml = xml;
        Profile = profile;
        Relationship = relationship;
    }

    /// <summary>
    /// The e-invoice embedded in a Factur-X, ZUGFeRD 2.x or XRechnung PDF, or null when the PDF carries none. The XML is
    /// returned unchanged; PdfPecker does not interpret or validate it.
    /// </summary>
    /// <remarks>
    /// The invoice is the attachment the XMP metadata names (DocumentFileName), else one named factur-x.xml,
    /// zugferd-invoice.xml or xrechnung.xml, else any other attached Cross Industry Invoice. The profile comes from the
    /// XML's guideline, else from the XMP conformance level, else EN 16931. ZUGFeRD 1.0 invoices use an older schema
    /// and are not found; <see cref="PdfAttachments.List"/> still returns them.
    /// </remarks>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static PdfEInvoice? Read(byte[] pdf, string? password = null)
    {
        var file = PdfAttachments.Open(pdf, password);
        // One decode budget for the metadata and every candidate, so many candidates that share a stream which inflates
        // to hundreds of megabytes are not each inflated again.
        using var scope = PdfFile.BeginRender(default);
        try
        {
            var xmp = EmbeddedFiles.Xmp(file);
            var named = xmp == null ? null : EmbeddedFiles.XmpProperty(xmp, "DocumentFileName");
            // The XMP name wins over the usual names, which win over any other XML file; OrderBy keeps the list's order otherwise.
            var candidates = EmbeddedFiles.Read(file)
                .Select(f => (File: f, Rank: Rank(f.FileName, named)))
                .Where(c => c.Rank >= 0)
                .OrderBy(c => c.Rank);
            foreach (var (found, _) in candidates)
            {
                byte[] xml;
                string? guideline;
                try
                {
                    xml = EmbeddedFiles.Decode(file, found.Stream);
                    guideline = ReadGuideline(xml);
                }
                catch (Exception ex) when (ex is ArgumentException or PdfFormatException or InvalidDataException)
                {
                    // Not a Cross Industry Invoice, or a stream that does not decode: look at the next candidate.
                    continue;
                }
                var profile = ProfileFromGuideline(guideline)
                    ?? ProfileFromConformanceLevel(xmp == null ? null : EmbeddedFiles.XmpProperty(xmp, "ConformanceLevel"))
                    ?? EInvoiceProfile.En16931;
                var relationship = file.GetName(found.Spec, "AFRelationship") switch
                {
                    "Alternative" => EInvoiceRelationship.Alternative,
                    "Data" => EInvoiceRelationship.Data,
                    "Source" => EInvoiceRelationship.Source,
                    _ => (EInvoiceRelationship?)null,
                };
                return new PdfEInvoice(xml, profile, relationship);
            }
            return null;
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }

        static int Rank(string fileName, string? named)
        {
            if (named != null && string.Equals(fileName, named, StringComparison.OrdinalIgnoreCase)) return 0;
            if (fileName.ToLowerInvariant() is "factur-x.xml" or "zugferd-invoice.xml" or "xrechnung.xml") return 1;
            return fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? 2 : -1;
        }
    }

    /// <summary>The CII XML, embedded unchanged.</summary>
    public byte[] Xml { get; }

    /// <summary>The profile written to the XMP metadata. It must match the XML's guideline.</summary>
    public EInvoiceProfile Profile { get; set; }

    /// <summary>How the XML relates to the pages. Default: null, Data for MINIMUM and BASIC WL, Alternative otherwise.</summary>
    public EInvoiceRelationship? Relationship { get; set; }

    /// <summary>The attachment's file name: factur-x.xml, or xrechnung.xml for the XRECHNUNG profile.</summary>
    public string FileName => Profile == EInvoiceProfile.XRechnung ? "xrechnung.xml" : "factur-x.xml";

    internal EInvoiceRelationship RelationshipOrDefault
        => Relationship ?? (Profile is EInvoiceProfile.Minimum or EInvoiceProfile.BasicWL ? EInvoiceRelationship.Data : EInvoiceRelationship.Alternative);

    /// <summary>The fx:ConformanceLevel value.</summary>
    internal string ConformanceLevel => Profile switch
    {
        EInvoiceProfile.Minimum => "MINIMUM",
        EInvoiceProfile.BasicWL => "BASIC WL",
        EInvoiceProfile.Basic => "BASIC",
        EInvoiceProfile.En16931 => "EN 16931",
        EInvoiceProfile.Extended => "EXTENDED",
        EInvoiceProfile.XRechnung => "XRECHNUNG",
        _ => throw new ArgumentOutOfRangeException(nameof(Profile), Profile, "Unknown e-invoice profile."),
    };

    /// <summary>The profile a guideline ID (BT-24) names, or null.</summary>
    internal static EInvoiceProfile? ProfileFromGuideline(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim().ToLowerInvariant();
        if (id.Contains("xrechnung")) return EInvoiceProfile.XRechnung;
        if (id.EndsWith(":extended") || id.Contains(":extended-")) return EInvoiceProfile.Extended;
        if (id.EndsWith(":basicwl")) return EInvoiceProfile.BasicWL;
        if (id.EndsWith(":basic")) return EInvoiceProfile.Basic;
        if (id.EndsWith(":minimum")) return EInvoiceProfile.Minimum;
        if (id.StartsWith("urn:cen.eu:en16931:2017")) return EInvoiceProfile.En16931;
        return null;
    }

    /// <summary>The profile an XMP ConformanceLevel names, or null. ZUGFeRD 2.0 called EN 16931 COMFORT.</summary>
    internal static EInvoiceProfile? ProfileFromConformanceLevel(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "MINIMUM" => EInvoiceProfile.Minimum,
        "BASIC WL" => EInvoiceProfile.BasicWL,
        "BASIC" => EInvoiceProfile.Basic,
        "EN 16931" or "COMFORT" => EInvoiceProfile.En16931,
        "EXTENDED" => EInvoiceProfile.Extended,
        "XRECHNUNG" => EInvoiceProfile.XRechnung,
        _ => null,
    };

    /// <summary>Checks that the XML is a Cross Industry Invoice and returns its guideline ID, or null.</summary>
    private static string? ReadGuideline(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings);
            reader.MoveToContent();
            if (reader.LocalName != "CrossIndustryInvoice" || reader.NamespaceURI != CiiNamespace)
                throw new ArgumentException("The e-invoice XML is not a UN/CEFACT Cross Industry Invoice (Factur-X / ZUGFeRD need CII, not UBL).", nameof(xml));
            // The guideline is the ID in ExchangedDocumentContext/GuidelineSpecifiedDocumentContextParameter, near the top.
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "GuidelineSpecifiedDocumentContextParameter") continue;
                using var sub = reader.ReadSubtree();
                while (sub.Read())
                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "ID" && sub.NamespaceURI == RamNamespace)
                        return sub.ReadElementContentAsString();
                return null;
            }
            return null;
        }
        catch (XmlException ex)
        {
            throw new ArgumentException("The e-invoice XML is not well-formed: " + ex.Message, nameof(xml), ex);
        }
    }
}
