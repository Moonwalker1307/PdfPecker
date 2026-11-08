using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// Inputs that could make redaction run without end or fail with an undocumented exception: forms that draw themselves,
/// damaged content and images, and text with sizes and spacing no viewer would show. Each must end quickly, and what is
/// in the area must still be gone.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class RedactionHostileInputTests
{
    private static byte[] Bounded(byte[] pdf, params PdfRedactionArea[] areas)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = Task.Run(() => PdfRedactor.Redact(pdf, areas, null, cts.Token));
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "Did not finish in time");
        return task.Result;
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string dict, string content) => "<< " + dict + " /Length " + Encoding.Latin1.GetByteCount(content) + " >>\nstream\n" + content + "\nendstream";

    [Fact]
    public void A_form_that_draws_itself_twice_at_every_level_ends()
    {
        // Each level draws the form twice, moved, so a walk without a budget would visit it 2^24 times.
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X 5 0 R >> /Font << /F 6 0 R >> >> /Contents 4 0 R >>",
            Stream("", "/X Do BT /F 12 Tf 20 20 Td (Kept) Tj ET"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 300] /Resources << /XObject << /X 5 0 R >> /Font << /F 6 0 R >> >>",
                "BT /F 12 Tf 20 200 Td (Loop) Tj ET q 1 0 0 1 0.5 0 cm /X Do Q /X Do"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var redacted = Bounded(pdf, new PdfRedactionArea(1, 0, 60, 400, 60));

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("Loop", text);
        Assert.Contains("Kept", text);
    }

    [Fact]
    public void Damaged_content_images_and_text_state_end_with_the_area_empty()
    {
        // An image whose data is far too short, an inline image without an end, a font size of 0 and huge spacing, a
        // TJ array of names, unbalanced Q and EMC, and operators with too few operands.
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /I 5 0 R >> /Font << /F 6 0 R >> >> /Contents 4 0 R >>",
            Stream("", "Q Q EMC cm Tm Td TJ Tf q 200 0 0 100 0 100 cm /I Do Q BT /F 0 Tf 1e30 Tc (Zero) Tj /F 12 Tf 0 Tc 20 150 Td"
                + " [/a /b (Secret) 1e300] TJ ET BT /F 12 Tf 1 0 0 1 20 50 Tm (Kept) Tj ET q 100 0 0 50 0 120 cm BI /W 4 /H 4 /BPC 8 /CS /RGB ID xyz"),
            Stream("/Type /XObject /Subtype /Image /Width 1000 /Height 1000 /BitsPerComponent 8 /ColorSpace /DeviceRGB", "short"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var redacted = Bounded(pdf, new PdfRedactionArea(1, 0, 100, 400, 100));

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("Secret", text);
        Assert.Contains("Kept", text);
        Assert.Empty(PdfImages.Extract(redacted));
    }
}
