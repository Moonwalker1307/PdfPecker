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

    private static byte[]? _cmyk;

    /// <summary>
    /// A compact ICC v2 output profile for CMYK, built once: the conversion to sRGB that PdfPecker's renderer uses, fitted
    /// to a typical press profile as Acrobat, Chrome and pdf.js approximate it too, so CMYK keeps the look it has in them
    /// once it is device independent. PDF/A needs such a profile for CMYK in a file whose output intent is RGB. The way
    /// back from Lab is the simple formula (red is (1 - C)(1 - K), and so on), which only colour-managed converters use.
    /// </summary>
    public static byte[] CmykProfile => _cmyk ??= BuildCmykProfile();

    // Grid points per channel of the lookup tables: 9 keeps the interpolation error well below what the eye notices.
    private const int CmykGridPoints = 9;

    private static byte[] BuildCmykProfile()
    {
        var toLab = Lut16(4, 3, CmykGridPoints, (input, output) =>
        {
            var (r, g, b) = Rendering.Content.DeviceCmyk.Convert((float)input[0], (float)input[1], (float)input[2], (float)input[3]);
            var lab = SrgbToLab(r / 255.0, g / 255.0, b / 255.0);
            output[0] = lab.L / 100 * (0xFF00 / 65535.0);
            output[1] = (lab.A + 128) * 256 / 65535;
            output[2] = (lab.B + 128) * 256 / 65535;
        });
        var fromLab = Lut16(3, 4, CmykGridPoints, (input, output) =>
        {
            var (r, g, b) = LabToSrgb(input[0] * 65535 / 0xFF00 * 100, input[1] * 65535 / 256 - 128, input[2] * 65535 / 256 - 128);
            double k = 1 - Math.Max(r, Math.Max(g, b));
            output[3] = k;
            output[0] = k >= 1 ? 0 : (1 - r - k) / (1 - k);
            output[1] = k >= 1 ? 0 : (1 - g - k) / (1 - k);
            output[2] = k >= 1 ? 0 : (1 - b - k) / (1 - k);
        });
        // Every colour is in gamut: the table maps all of Lab to zero.
        var gamut = Lut16(3, 1, 2, (_, output) => output[0] = 0);
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc("PdfPecker CMYK")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("A2B0", toLab), ("A2B1", toLab), ("A2B2", toLab),
            ("B2A0", fromLab), ("B2A1", fromLab), ("B2A2", fromLab),
            ("gamt", gamut),
        };
        return Profile("prtr", "CMYK", "Lab ", tags);
    }

    private static byte[]? _gray;

    /// <summary>A compact ICC v2 display profile for grey with the sRGB transfer curve, built once.</summary>
    public static byte[] GrayProfile => _gray ??= BuildGrayProfile();

    private static byte[] BuildGrayProfile()
    {
        const int curvePoints = 256;
        var curve = new byte[12 + curvePoints * 2];
        Encoding.ASCII.GetBytes("curv").CopyTo(curve, 0);
        BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(8), curvePoints);
        for (int i = 0; i < curvePoints; i++)
        {
            double v = i / (double)(curvePoints - 1);
            double lin = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + i * 2), (ushort)Math.Round(lin * 65535));
        }
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc("Grey with the sRGB curve")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("kTRC", curve),
        };
        return Profile("mntr", "GRAY", "XYZ ", tags);
    }

    /// <summary>A profile from its tags; tags that hold the same array share their data.</summary>
    private static byte[] Profile(string deviceClass, string colorSpace, string pcs, List<(string Sig, byte[] Data)> tags)
    {
        int offset = 128 + 4 + tags.Count * 12;
        var data = new MemoryStream();
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var table = new List<(string Sig, int Offset, int Size)>();
        foreach (var (sig, d) in tags)
        {
            if (!placed.TryGetValue(d, out int at))
            {
                at = offset + (int)data.Length;
                placed[d] = at;
                data.Write(d);
                while (data.Length % 4 != 0) data.WriteByte(0);
            }
            table.Add((sig, at, d.Length));
        }
        int size = offset + (int)data.Length;
        var p = new byte[size];
        BinaryPrimitives.WriteInt32BigEndian(p, size);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), 0x02100000); // version 2.1
        Encoding.ASCII.GetBytes(deviceClass + colorSpace + pcs).CopyTo(p, 12);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(28), 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(p, 36);
        WriteS15(p.AsSpan(68), 0.9642);
        WriteS15(p.AsSpan(72), 1.0);
        WriteS15(p.AsSpan(76), 0.8249);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(128), tags.Count);
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

    /// <summary>
    /// A lut16Type tag with identity input and output curves: <paramref name="fill"/> gets each grid point's inputs from
    /// 0 to 1 and sets the outputs from 0 to 1.
    /// </summary>
    private static byte[] Lut16(int inputs, int outputs, int grid, Action<double[], double[]> fill)
    {
        int points = 1;
        for (int i = 0; i < inputs; i++) points *= grid;
        const int curveEntries = 2;
        var d = new byte[52 + (inputs + outputs) * curveEntries * 2 + points * outputs * 2];
        Encoding.ASCII.GetBytes("mft2").CopyTo(d, 0);
        d[8] = (byte)inputs;
        d[9] = (byte)outputs;
        d[10] = (byte)grid;
        // The matrix applies only to XYZ input; identity.
        for (int i = 0; i < 3; i++) WriteS15(d.AsSpan(12 + i * 16), 1.0);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(48), curveEntries);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(50), curveEntries);
        int pos = 52;
        for (int i = 0; i < inputs; i++, pos += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pos + 2), 0xFFFF);
        var input = new double[inputs];
        var output = new double[outputs];
        for (int n = 0; n < points; n++)
        {
            // The first input channel varies slowest.
            for (int i = inputs - 1, rest = n; i >= 0; i--, rest /= grid) input[i] = rest % grid / (double)(grid - 1);
            fill(input, output);
            for (int o = 0; o < outputs; o++, pos += 2)
                BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pos), (ushort)Math.Round(Math.Clamp(output[o], 0, 1) * 65535));
        }
        for (int o = 0; o < outputs; o++, pos += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pos + 2), 0xFFFF);
        return d;
    }

    // The sRGB colorants adapted to D50, as in the sRGB profile, and the D50 white point.
    private static readonly double[,] SrgbToXyz =
    {
        { 0.4360747, 0.3850649, 0.1430804 },
        { 0.2225045, 0.7168786, 0.0606169 },
        { 0.0139322, 0.0971045, 0.7141733 },
    };

    private const double WhiteX = 0.9642, WhiteZ = 0.8249;

    private static (double L, double A, double B) SrgbToLab(double r, double g, double b)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double lr = Linear(r), lg = Linear(g), lb = Linear(b);
        double x = SrgbToXyz[0, 0] * lr + SrgbToXyz[0, 1] * lg + SrgbToXyz[0, 2] * lb;
        double y = SrgbToXyz[1, 0] * lr + SrgbToXyz[1, 1] * lg + SrgbToXyz[1, 2] * lb;
        double z = SrgbToXyz[2, 0] * lr + SrgbToXyz[2, 1] * lg + SrgbToXyz[2, 2] * lb;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x / WhiteX), fy = F(y), fz = F(z / WhiteZ);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static (double R, double G, double B) LabToSrgb(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double Finv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) * 27 / 24389;
        double x = Finv(fx) * WhiteX, y = Finv(fy), z = Finv(fz) * WhiteZ;
        // The inverse of the colorant matrix.
        double r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
        double g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
        double bl = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
        static double Encode(double v) => v <= 0 ? 0 : v >= 1 ? 1 : v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (Encode(r), Encode(g), Encode(bl));
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

    /// <summary>
    /// The PDF/A identification of a conformance level: the part of ISO 19005, the conformance letter (null for plain
    /// PDF/A-4, which has none) and the revision year PDF/A-4 states (0 before part 4).
    /// </summary>
    public static (int Part, string? Conformance, int Rev) Identification(PdfConformance conformance) => conformance switch
    {
        PdfConformance.PdfA2b => (2, "B", 0),
        PdfConformance.PdfA2u => (2, "U", 0),
        PdfConformance.PdfA3b => (3, "B", 0),
        PdfConformance.PdfA3u => (3, "U", 0),
        PdfConformance.PdfA2a => (2, "A", 0),
        PdfConformance.PdfA3a => (3, "A", 0),
        PdfConformance.PdfA4 => (4, null, 2020),
        PdfConformance.PdfA4f => (4, "F", 2020),
        _ => (0, null, 0),
    };

    /// <summary>Whether a PDF/A level is based on PDF 2.0 (PDF/A-4), so the file is written as PDF 2.0.</summary>
    public static bool IsPdf20(PdfConformance conformance) => conformance is PdfConformance.PdfA4 or PdfConformance.PdfA4f;

    /// <summary>Whether a PDF/A level is level A (accessible), which needs a tagged structure and a language.</summary>
    public static bool IsLevelA(PdfConformance conformance) => conformance is PdfConformance.PdfA2a or PdfConformance.PdfA3a;

    /// <summary>
    /// The XMP packet. Its values must match the document information dictionary. <paramref name="part"/> is the PDF/A
    /// part, or 0 for a file that is not PDF/A, with its <paramref name="conformance"/> letter (none for PDF/A-4) and
    /// <paramref name="rev"/>ision year (PDF/A-4); <paramref name="uaPart"/> claims PDF/UA-1 or PDF/UA-2 (0: neither).
    /// </summary>
    public static byte[] Xmp(int part, string? conformance, DocumentProperties props, DateTime created, PdfEInvoice? invoice = null,
        int uaPart = 0, int rev = 0)
    {
        string date = created.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        return Xmp(part, conformance, props, date, date, date, invoice, uaPart, rev, null, null);
    }

    /// <summary>
    /// The XMP packet of a PDF/X-4 file: the document's properties and dates, the PDF/X version, whether the file is
    /// trapped, and the version and rendition of xmpMM that ISO 15930-7 asks for. The document identifier is a hash of
    /// the properties and date when <paramref name="reproducible"/>, so the same input gives the same file, and random
    /// otherwise.
    /// </summary>
    public static byte[] XmpPdfX(DocumentProperties props, DateTime created, bool trapped, bool reproducible)
    {
        string date = created.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        Guid id;
        if (reproducible)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', props.Title, props.Author, props.Subject, props.Keywords, props.Creator, date)));
            id = new Guid(hash.AsSpan(0, 16));
        }
        else id = Guid.NewGuid();
        string uuid = "uuid:" + id.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        var pdfx = new StringBuilder(400)
            .Append("<rdf:Description rdf:about=\"\" xmlns:pdfxid=\"http://www.npes.org/pdfx/ns/id/\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\">\n")
            .Append("<pdfxid:GTS_PDFXVersion>PDF/X-4</pdfxid:GTS_PDFXVersion>\n")
            .Append("<xmpMM:DocumentID>").Append(uuid).Append("</xmpMM:DocumentID>\n")
            .Append("<xmpMM:InstanceID>").Append(uuid).Append("</xmpMM:InstanceID>\n")
            .Append("<xmpMM:VersionID>1</xmpMM:VersionID>\n")
            .Append("<xmpMM:RenditionClass>default</xmpMM:RenditionClass>\n")
            .Append("</rdf:Description>\n");
        return Xmp(0, null, props, date, date, date, null, 0, 0, pdfx.ToString(), trapped);
    }

    /// <summary>
    /// The XMP packet of a document converted to PDF/A, which keeps the dates it was created and changed (when it states
    /// them) so that converting the same file again gives the same result.
    /// </summary>
    public static byte[] Xmp(int part, string? conformance, DocumentProperties props, DateTimeOffset? created, DateTimeOffset? modified)
        => Xmp(part, conformance, props, XmpDate(created), XmpDate(modified), XmpDate(modified ?? created), null, 0, 0, null, null);

    /// <summary>An XMP date with its offset from UTC, or null.</summary>
    private static string? XmpDate(DateTimeOffset? date)
    {
        if (date is not { } d) return null;
        var text = d.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (d.Offset == TimeSpan.Zero) return text + "Z";
        var offset = d.Offset.Duration();
        return text + (d.Offset < TimeSpan.Zero ? "-" : "+") + offset.Hours.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":"
            + offset.Minutes.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Xmp(int part, string? conformance, DocumentProperties props, string? createDate, string? modifyDate, string? metadataDate,
        PdfEInvoice? invoice, int uaPart, int rev, string? pdfxDescription, bool? trapped)
    {
        bool pdfua = uaPart > 0;
        var custom = XmpCustomProperties(props.Custom);
        var sb = new StringBuilder(1500);
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n")
          .Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n")
          .Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"")
          .Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\"");
        if (custom.Count > 0) sb.Append(" xmlns:pdfx=\"").Append(PdfxNamespace).Append('"');
        sb.Append(">\n");
        if (part > 0)
        {
            sb.Append("<pdfaid:part>").Append(part).Append("</pdfaid:part>\n");
            if (rev > 0) sb.Append("<pdfaid:rev>").Append(rev).Append("</pdfaid:rev>\n");
            if (conformance != null) sb.Append("<pdfaid:conformance>").Append(conformance).Append("</pdfaid:conformance>\n");
        }
        sb.Append("<dc:format>application/pdf</dc:format>\n");
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
        sb.Append("<pdf:Producer>").Append(Escape(props.ProducerOrDefault)).Append("</pdf:Producer>\n");
        if (createDate != null) sb.Append("<xmp:CreateDate>").Append(createDate).Append("</xmp:CreateDate>\n");
        if (modifyDate != null) sb.Append("<xmp:ModifyDate>").Append(modifyDate).Append("</xmp:ModifyDate>\n");
        if (metadataDate != null) sb.Append("<xmp:MetadataDate>").Append(metadataDate).Append("</xmp:MetadataDate>\n");
        if (trapped is { } t) sb.Append("<pdf:Trapped>").Append(t ? "True" : "False").Append("</pdf:Trapped>\n");
        foreach (var (name, value) in custom)
            sb.Append("<pdfx:").Append(name).Append('>').Append(Escape(value)).Append("</pdfx:").Append(name).Append(">\n");
        sb.Append("</rdf:Description>\n");
        if (pdfxDescription != null) sb.Append(pdfxDescription);
        if (pdfua)
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"").Append(PdfUaNamespace).Append("\">\n")
              .Append("<pdfuaid:part>").Append(uaPart).Append("</pdfuaid:part>\n")
              // PDF/UA-2 also states the revision year of ISO 14289-2.
              .Append(uaPart >= 2 ? "<pdfuaid:rev>2024</pdfuaid:rev>\n" : "")
              .Append("</rdf:Description>\n");
        if (invoice != null)
        {
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:fx=\"").Append(FacturXNamespace).Append("\">\n")
              .Append("<fx:DocumentType>INVOICE</fx:DocumentType>\n")
              .Append("<fx:DocumentFileName>").Append(invoice.FileName).Append("</fx:DocumentFileName>\n")
              .Append("<fx:Version>1.0</fx:Version>\n")
              .Append("<fx:ConformanceLevel>").Append(invoice.ConformanceLevel).Append("</fx:ConformanceLevel>\n")
              .Append("</rdf:Description>\n");
        }
        // PDF/A-2 and PDF/A-3 allow properties outside their predefined schemas only when an extension schema describes
        // them; PDF/A-4 dropped that rule.
        bool extensions = part is 2 or 3;
        bool uaSchema = pdfua && extensions;
        if (extensions && (custom.Count > 0 || invoice != null || uaSchema))
        {
            // PDF/A allows properties outside the predefined schemas only when an extension schema describes them.
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"")
              .Append(" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n")
              .Append("<pdfaExtension:schemas><rdf:Bag>\n");
            if (custom.Count > 0)
                ExtensionSchema(sb, "PDF custom document properties", PdfxNamespace, "pdfx",
                    custom.Select(c => (c.Name, "Custom document property")));
            if (uaSchema)
                ExtensionSchema(sb, "PDF/UA Universal Accessibility Schema", PdfUaNamespace, "pdfuaid",
                    new[] { ("part", "Indicates, which part of ISO 14289 standard is followed") }, "Integer", "internal");
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

    private static void ExtensionSchema(StringBuilder sb, string schema, string ns, string prefix, IEnumerable<(string Name, string Description)> properties,
        string valueType = "Text", string category = "external")
    {
        sb.Append("<rdf:li rdf:parseType=\"Resource\">\n")
          .Append("<pdfaSchema:schema>").Append(schema).Append("</pdfaSchema:schema>\n")
          .Append("<pdfaSchema:namespaceURI>").Append(ns).Append("</pdfaSchema:namespaceURI>\n")
          .Append("<pdfaSchema:prefix>").Append(prefix).Append("</pdfaSchema:prefix>\n")
          .Append("<pdfaSchema:property><rdf:Seq>\n");
        foreach (var (name, description) in properties)
            sb.Append("<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>").Append(name)
              .Append("</pdfaProperty:name><pdfaProperty:valueType>").Append(valueType).Append("</pdfaProperty:valueType>")
              .Append("<pdfaProperty:category>").Append(category).Append("</pdfaProperty:category>")
              .Append("<pdfaProperty:description>").Append(description).Append("</pdfaProperty:description></rdf:li>\n");
        sb.Append("</rdf:Seq></pdfaSchema:property>\n</rdf:li>\n");
    }

    /// <summary>The PDF/UA identification schema (ISO 14289-1).</summary>
    private const string PdfUaNamespace = "http://www.aiim.org/pdfua/ns/id/";

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
            if (sb.Length == 0 || !char.IsAsciiLetter(sb[0]) && sb[0] != '_') sb.Insert(0, '_');
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
