using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>
/// Objects that an incremental update frees. A reference to a free object is null (ISO 32000-2, 7.3.10); looking one
/// up used to rebuild the cross-reference table from the object headers instead, which brought back the deleted object
/// and the first revision of every object a later update had replaced, and which page won that race varied between runs.
/// </summary>
public class FreedObjectTests
{
    private static string Font(char glyph) => $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Differences [65 /{glyph}] >> >>";

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static void Row(MemoryStream rows, int type, long field2, int field3)
    {
        Span<byte> row = stackalloc byte[7];
        row[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(row[1..], (uint)field2);
        BinaryPrimitives.WriteUInt16BigEndian(row[5..], (ushort)field3);
        rows.Write(row);
    }

    /// <summary>
    /// Two pages that share font 8, whose glyph A draws a B. Page 1 has the comment annotation 9, drawn by its
    /// appearance 10. An incremental update with an xref stream then frees the annotation (generation 1) and replaces
    /// the font with one that draws a C, stored in an object stream, while page 1's /Annots still names the annotation.
    /// </summary>
    private static byte[] DeletedAnnotation()
    {
        const string page1 = "BT /F1 24 Tf 10 100 Td (Page1) Tj ET", page2 = "BT /F1 24 Tf 10 100 Td (AAA) Tj ET";
        const string comment = "BT /Helv 12 Tf 2 10 Td (DeletedComment) Tj ET";
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 11 0 R] /Count 2 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 8 0 R >> >> /Contents 4 0 R /Annots [9 0 R] >>",
            [4] = $"<< /Length {page1.Length} >>\nstream\n{page1}\nendstream",
            [8] = Font('B'),
            [9] = "<< /Type /Annot /Subtype /FreeText /Rect [0 0 150 30] /Contents (DeletedComment) /AP << /N 10 0 R >> >>",
            [10] = "<< /Type /XObject /Subtype /Form /BBox [0 0 150 30] /Resources << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"
                + $" /Length {comment.Length} >>\nstream\n{comment}\nendstream",
            [11] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 8 0 R >> >> /Contents 12 0 R >>",
            [12] = $"<< /Length {page2.Length} >>\nstream\n{page2}\nendstream",
        };
        var ms = new MemoryStream();
        ms.Write(Latin1("%PDF-1.7\n"));
        var offsets = new Dictionary<int, long>();
        foreach (var (num, body) in objects)
        {
            offsets[num] = ms.Position;
            ms.Write(Latin1($"{num} 0 obj\n{body}\nendobj\n"));
        }
        long xref1 = ms.Position;
        var table = new StringBuilder("xref\n0 13\n0000000000 65535 f \n");
        for (int num = 1; num < 13; num++)
            table.Append(offsets.TryGetValue(num, out long at) ? at.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n" : "0000000000 65535 f \n");
        table.Append($"trailer\n<< /Size 13 /Root 1 0 R >>\nstartxref\n{xref1}\n%%EOF\n");
        ms.Write(Latin1(table.ToString()));

        // The update: object stream 13 holds the new font 8; annotation 9 is free with generation 1.
        string header = "8 0 ";
        var packed = Deflate(Latin1(header + Font('C')));
        long objStm = ms.Position;
        ms.Write(Latin1($"13 0 obj\n<< /Type /ObjStm /N 1 /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n"));
        ms.Write(packed);
        ms.Write(Latin1("\nendstream\nendobj\n"));
        long xref2 = ms.Position;
        var rows = new MemoryStream();
        Row(rows, 2, 13, 0);
        Row(rows, 0, 0, 1);
        Row(rows, 1, objStm, 0);
        Row(rows, 1, xref2, 0);
        ms.Write(Latin1($"14 0 obj\n<< /Type /XRef /Size 15 /W [1 4 2] /Index [8 2 13 2] /Root 1 0 R /Prev {xref1} /Length {rows.Length} >>\nstream\n"));
        ms.Write(rows.ToArray());
        ms.Write(Latin1($"\nendstream\nendobj\nstartxref\n{xref2}\n%%EOF\n"));
        return ms.ToArray();
    }

    [Fact]
    public void Reference_to_a_freed_object_is_null_and_the_newest_revision_stays()
    {
        var pdf = DeletedAnnotation();
        var file = PdfFile.Open(pdf, null);
        Assert.True(file.IsFree(9));
        Assert.True(file.GetObject(9)!.IsNull);
        Assert.False(file.Reconstructed);

        string text = PdfText.Extract(pdf, new PdfTextOptions { MaxDegreeOfParallelism = 1 });
        Assert.Contains("Page1", text, StringComparison.Ordinal);
        Assert.Contains("CCC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BBB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeletedComment", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_that_reference_a_freed_object_read_the_same_on_every_run()
    {
        // With pages read in parallel, the page that looked up the freed annotation rebuilt the table while the other
        // was reading the font, so whether page 2 got the old font or the new one depended on timing.
        var pdf = DeletedAnnotation();
        string sequential = PdfText.Extract(pdf, new PdfTextOptions { MaxDegreeOfParallelism = 1 });
        for (int run = 0; run < 40; run++) Assert.Equal(sequential, PdfText.Extract(pdf));
        Assert.Equal("CCC", PdfText.Extract(pdf, new PdfTextOptions { Pages = PdfPageRange.Parse("2") }).Trim());
    }

    [Fact]
    public void Freed_annotation_is_not_drawn()
    {
        var pdf = DeletedAnnotation();
        var withAnnotations = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        var withoutAnnotations = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72, RenderAnnotations = false });
        Assert.Equal(withoutAnnotations.Rgba, withAnnotations.Rgba);
    }

    /// <summary>
    /// A hybrid-reference file: its classic table lists the page tree and the font, which are in object stream 6, as
    /// free, and the cross-reference stream that /XRefStm points at says where they are.
    /// </summary>
    private static byte[] Hybrid()
    {
        const string content = "BT /F1 24 Tf 10 100 Td (Hybrid) Tj ET";
        var ms = new MemoryStream();
        ms.Write(Latin1("%PDF-1.5\n"));
        var offsets = new Dictionary<int, long>();
        void Obj(int num, string body)
        {
            offsets[num] = ms.Position;
            ms.Write(Latin1($"{num} 0 obj\n{body}\nendobj\n"));
        }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Obj(5, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        const string pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n";
        string header = $"2 0 4 {pages.Length} ";
        var packed = Deflate(Latin1(header + pages + "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        offsets[6] = ms.Position;
        ms.Write(Latin1($"6 0 obj\n<< /Type /ObjStm /N 2 /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n"));
        ms.Write(packed);
        ms.Write(Latin1("\nendstream\nendobj\n"));
        var rows = new MemoryStream();
        Row(rows, 2, 6, 0);
        Row(rows, 2, 6, 1);
        offsets[7] = ms.Position;
        ms.Write(Latin1($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 4 2] /Index [2 1 4 1] /Length {rows.Length} >>\nstream\n"));
        ms.Write(rows.ToArray());
        ms.Write(Latin1("\nendstream\nendobj\n"));
        long xref = ms.Position;
        var table = new StringBuilder("xref\n0 8\n0000000000 65535 f \n");
        for (int num = 1; num < 8; num++)
            table.Append(offsets.TryGetValue(num, out long at) ? at.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n" : "0000000000 00000 f \n");
        table.Append($"trailer\n<< /Size 8 /Root 1 0 R /XRefStm {offsets[7]} >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Latin1(table.ToString()));
        return ms.ToArray();
    }

    [Fact]
    public void Hybrid_file_finds_the_objects_its_table_lists_as_free_in_its_xref_stream()
    {
        var pdf = Hybrid();
        var file = PdfFile.Open(pdf, null);
        Assert.False(file.IsFree(2));
        Assert.False(file.IsFree(4));
        Assert.Equal(1, PdfPages.Count(pdf));
        Assert.Contains("Hybrid", PdfText.Extract(pdf), StringComparison.Ordinal);
        Assert.False(file.Reconstructed);
    }

    [Fact]
    public void Entry_in_use_without_an_offset_still_rebuilds_the_table()
    {
        // A table entry marked in use with offset 0 is damage, not a deletion: the object is found by its header.
        const string content = "BT /F1 24 Tf 10 100 Td (Found) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 0; i < offsets.Count; i++) sb.Append(i == 3 ? "0000000000" : offsets[i].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var pdf = Latin1(sb.ToString());
        Assert.Contains("Found", PdfText.Extract(pdf), StringComparison.Ordinal);
        Assert.False(PdfFile.Open(pdf, null).IsFree(4));
    }
}
