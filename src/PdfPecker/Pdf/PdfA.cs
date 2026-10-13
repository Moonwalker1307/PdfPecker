using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Pdf;

/// <summary>Pieces of a PDF/A file: the sRGB output intent profile and the XMP metadata packet.</summary>
internal static class PdfA
{
    public const string OutputCondition = "sRGB IEC61966-2.1";

    private static byte[]? _srgb;

    /// <summary>A compact ICC v2 display profile for sRGB (D50 colorants, sampled sRGB transfer curve), built once.</summary>
    public static byte[] SrgbProfile => _srgb ??= BuildSrgbProfile();

    private static byte[] BuildSrgbProfile()
    {
        const int curvePoints = 1024;
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc("sRGB IEC61966-2.1")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
        };
        var curve = new byte[12 + curvePoints * 2];
        Encoding.ASCII.GetBytes("curv").CopyTo(curve, 0);
        BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(8), curvePoints);
        for (int i = 0; i < curvePoints; i++)
        {
            double v = i / (double)(curvePoints - 1);
            double lin = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + i * 2), (ushort)Math.Round(lin * 65535));
        }

        // The three TRC tags share one curve.
        int tagCount = tags.Count + 3;
        int offset = 128 + 4 + tagCount * 12;
        var table = new List<(string Sig, int Offset, int Size)>();
        var data = new MemoryStream();
        foreach (var (sig, d) in tags)
        {
            table.Add((sig, offset + (int)data.Length, d.Length));
            data.Write(d);
            while (data.Length % 4 != 0) data.WriteByte(0);
        }
        int curveOffset = offset + (int)data.Length;
        data.Write(curve);
        foreach (var sig in new[] { "rTRC", "gTRC", "bTRC" }) table.Add((sig, curveOffset, curve.Length));

        int size = offset + (int)data.Length;
        var p = new byte[size];
        BinaryPrimitives.WriteInt32BigEndian(p, size);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), 0x02100000); // version 2.1
        Encoding.ASCII.GetBytes("mntrRGB XYZ ").CopyTo(p, 12);
        // Creation date: 2026-01-01 00:00:00.
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(28), 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(p, 36);
        // Rendering intent 0 (perceptual); PCS illuminant D50.
        WriteS15(p.AsSpan(68), 0.9642);
        WriteS15(p.AsSpan(72), 1.0);
        WriteS15(p.AsSpan(76), 0.8249);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(128), tagCount);
        for (int i = 0; i < table.Count; i++)
        {
            int e = 132 + i * 12;
            Encoding.ASCII.GetBytes(table[i].Sig).CopyTo(p, e);
            BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(e + 4), table[i].Offset);
            BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(e + 8), table[i].Size);
        }
        data.ToArray().CopyTo(p, offset);
        return p;
    }

    private static void WriteS15(Span<byte> s, double v) => BinaryPrimitives.WriteInt32BigEndian(s, (int)Math.Round(v * 65536));

    private static byte[] Xyz(double x, double y, double z)
    {
        var d = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(d, 0);
        WriteS15(d.AsSpan(8), x);
        WriteS15(d.AsSpan(12), y);
        WriteS15(d.AsSpan(16), z);
        return d;
    }

    private static byte[] Text(string s)
    {
        var d = new byte[8 + s.Length + 1];
        Encoding.ASCII.GetBytes("text").CopyTo(d, 0);
        Encoding.ASCII.GetBytes(s).CopyTo(d, 8);
        return d;
    }

    /// <summary>textDescriptionType: ASCII description, empty Unicode and ScriptCode parts.</summary>
    private static byte[] Desc(string s)
    {
        var d = new byte[8 + 4 + s.Length + 1 + 8 + 3 + 67];
        Encoding.ASCII.GetBytes("desc").CopyTo(d, 0);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), s.Length + 1);
        Encoding.ASCII.GetBytes(s).CopyTo(d, 12);
        return d;
    }

    /// <summary>The XMP packet. Its values must match the document information dictionary.</summary>
    public static byte[] Xmp(int part, string conformance, DocumentProperties props, DateTime created, PdfEInvoice? invoice = null)
    {
        string date = created.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        var custom = XmpCustomProperties(props.Custom);
        var sb = new StringBuilder(1500);
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n")
          .Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n")
          .Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"")
          .Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\"");
        if (custom.Count > 0) sb.Append(" xmlns:pdfx=\"").Append(PdfxNamespace).Append('"');
        sb.Append(">\n")
          .Append("<pdfaid:part>").Append(part).Append("</pdfaid:part>\n")
          .Append("<pdfaid:conformance>").Append(conformance).Append("</pdfaid:conformance>\n")
          .Append("<dc:format>application/pdf</dc:format>\n");
        if (!string.IsNullOrEmpty(props.Title))
            sb.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(props.Title!)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        if (!string.IsNullOrEmpty(props.Author))
            sb.Append("<dc:creator><rdf:Seq><rdf:li>").Append(Escape(props.Author!)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        if (!string.IsNullOrEmpty(props.Subject))
            sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(props.Subject!)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        if (!string.IsNullOrEmpty(props.Keywords))
            sb.Append("<pdf:Keywords>").Append(Escape(props.Keywords!)).Append("</pdf:Keywords>\n");
        if (!string.IsNullOrEmpty(props.Creator))
            sb.Append("<xmp:CreatorTool>").Append(Escape(props.Creator!)).Append("</xmp:CreatorTool>\n");
        sb.Append("<pdf:Producer>").Append(Escape(props.ProducerOrDefault)).Append("</pdf:Producer>\n")
          .Append("<xmp:CreateDate>").Append(date).Append("</xmp:CreateDate>\n")
          .Append("<xmp:ModifyDate>").Append(date).Append("</xmp:ModifyDate>\n")
          .Append("<xmp:MetadataDate>").Append(date).Append("</xmp:MetadataDate>\n");
        foreach (var (name, value) in custom)
            sb.Append("<pdfx:").Append(name).Append('>').Append(Escape(value)).Append("</pdfx:").Append(name).Append(">\n");
        sb.Append("</rdf:Description>\n");
        if (invoice != null)
        {
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:fx=\"").Append(FacturXNamespace).Append("\">\n")
              .Append("<fx:DocumentType>INVOICE</fx:DocumentType>\n")
              .Append("<fx:DocumentFileName>").Append(invoice.FileName).Append("</fx:DocumentFileName>\n")
              .Append("<fx:Version>1.0</fx:Version>\n")
              .Append("<fx:ConformanceLevel>").Append(invoice.ConformanceLevel).Append("</fx:ConformanceLevel>\n")
              .Append("</rdf:Description>\n");
        }
        if (custom.Count > 0 || invoice != null)
        {
            // PDF/A allows properties outside the predefined schemas only when an extension schema describes them.
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"")
              .Append(" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n")
              .Append("<pdfaExtension:schemas><rdf:Bag>\n");
            if (custom.Count > 0)
                ExtensionSchema(sb, "PDF custom document properties", PdfxNamespace, "pdfx",
                    custom.Select(c => (c.Name, "Custom document property")));
            if (invoice != null)
                ExtensionSchema(sb, "Factur-X PDFA Extension Schema", FacturXNamespace, "fx", new[]
                {
                    ("DocumentFileName", "The name of the embedded XML document"),
                    ("DocumentType", "The type of the hybrid document in capital letters, e.g. INVOICE or ORDER"),
                    ("Version", "The actual version of the standard applying to the embedded XML document"),
                    ("ConformanceLevel", "The conformance level of the embedded XML document"),
                });
            sb.Append("</rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n");
        }
        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n");
        // Padding lets editors update the packet in place.
        for (int i = 0; i < 20; i++) sb.Append(' ', 99).Append('\n');
        sb.Append("<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void ExtensionSchema(StringBuilder sb, string schema, string ns, string prefix, IEnumerable<(string Name, string Description)> properties)
    {
        sb.Append("<rdf:li rdf:parseType=\"Resource\">\n")
          .Append("<pdfaSchema:schema>").Append(schema).Append("</pdfaSchema:schema>\n")
          .Append("<pdfaSchema:namespaceURI>").Append(ns).Append("</pdfaSchema:namespaceURI>\n")
          .Append("<pdfaSchema:prefix>").Append(prefix).Append("</pdfaSchema:prefix>\n")
          .Append("<pdfaSchema:property><rdf:Seq>\n");
        foreach (var (name, description) in properties)
            sb.Append("<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>").Append(name)
              .Append("</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>")
              .Append("<pdfaProperty:category>external</pdfaProperty:category>")
              .Append("<pdfaProperty:description>").Append(description).Append("</pdfaProperty:description></rdf:li>\n");
        sb.Append("</rdf:Seq></pdfaSchema:property>\n</rdf:li>\n");
    }

    /// <summary>The Factur-X (and ZUGFeRD 2.1 and later) XMP namespace for invoices.</summary>
    private const string FacturXNamespace = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";

    /// <summary>The namespace Acrobat uses for custom document properties in XMP.</summary>
    private const string PdfxNamespace = "http://ns.adobe.com/pdfx/1.3/";

    /// <summary>
    /// Custom properties as XMP property names: an XML name can't hold every character a PDF name can, so others become
    /// '_'. A name that collides with an earlier one is left out of the XMP (the information dictionary still has it).
    /// </summary>
    private static List<(string Name, string Value)> XmpCustomProperties(IReadOnlyList<KeyValuePair<string, string>> custom)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in custom)
        {
            var sb = new StringBuilder(kv.Key.Length + 1);
            foreach (char c in kv.Key)
                sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
            if (!char.IsAsciiLetter(sb[0]) && sb[0] != '_') sb.Insert(0, '_');
            var name = sb.ToString();
            if (seen.Add(name)) list.Add((name, kv.Value));
        }
        return list;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    // Control characters are not allowed in XML 1.0.
                    if (c >= 0x20 || c is '\t' or '\n' or '\r') sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
