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
    public static byte[] Xmp(int part, string conformance, string? title, string? author, string? subject, string producer, DateTime created)
    {
        string date = created.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        var sb = new StringBuilder(1500);
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n")
          .Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n")
          .Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"")
          .Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n")
          .Append("<pdfaid:part>").Append(part).Append("</pdfaid:part>\n")
          .Append("<pdfaid:conformance>").Append(conformance).Append("</pdfaid:conformance>\n")
          .Append("<dc:format>application/pdf</dc:format>\n");
        if (!string.IsNullOrEmpty(title))
            sb.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(title!)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        if (!string.IsNullOrEmpty(author))
            sb.Append("<dc:creator><rdf:Seq><rdf:li>").Append(Escape(author!)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        if (!string.IsNullOrEmpty(subject))
            sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(Escape(subject!)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        sb.Append("<pdf:Producer>").Append(Escape(producer)).Append("</pdf:Producer>\n")
          .Append("<xmp:CreateDate>").Append(date).Append("</xmp:CreateDate>\n")
          .Append("<xmp:ModifyDate>").Append(date).Append("</xmp:ModifyDate>\n")
          .Append("<xmp:MetadataDate>").Append(date).Append("</xmp:MetadataDate>\n")
          .Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n");
        // Padding lets editors update the packet in place.
        for (int i = 0; i < 20; i++) sb.Append(' ', 99).Append('\n');
        sb.Append("<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(sb.ToString());
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
