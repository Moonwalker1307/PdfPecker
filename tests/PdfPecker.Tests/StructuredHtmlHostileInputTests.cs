using System.Globalization;
using System.Text;

namespace PdfPecker.Tests;

/// <summary>Structured PDF to HTML on pages built to make finding tables, columns and paragraphs slow.</summary>
[Collection(HostileInputCollection.Name)]
public class StructuredHtmlHostileInputTests
{
    private static readonly PdfHtmlOptions Structured = new() { Mode = PdfHtmlMode.Structured };

    /// <summary>A one-page PDF of the given size showing <paramref name="content"/> in Helvetica.</summary>
    private static byte[] Page(string content, int width = 612, int height = 792)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Text(double x, double y, double size, string text) => $"BT /F1 {N(size)} Tf {N(x)} {N(y)} Td ({text}) Tj ET\n";

    private static string Convert(byte[] pdf)
    {
        var task = Task.Run(() =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return PdfToHtml.Convert(pdf, Structured, cts.Token);
        });
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    [Fact]
    public void A_dense_grid_of_rules_with_text_in_every_cell_stays_bounded()
    {
        // 300 by 300 lines that all cross, ninety thousand cells, a word in many of them.
        var sb = new StringBuilder("0.1 w\n");
        for (int i = 0; i < 300; i++)
        {
            double p = 6 + i * 2.6;
            sb.Append("6 ").Append(N(p)).Append(" m 786 ").Append(N(p)).Append(" l S\n");
            sb.Append(N(p)).Append(" 6 m ").Append(N(p)).Append(" 786 l S\n");
        }
        for (int r = 0; r < 299; r += 3)
            for (int c = 0; c < 299; c += 3)
                sb.Append(Text(6.3 + c * 2.6, 6.5 + r * 2.6, 1.5, "x"));
        Assert.Contains("<section", Convert(Page(sb.ToString(), 792, 792)));
    }

    [Fact]
    public void Tens_of_thousands_of_thin_shapes_stay_bounded()
    {
        // Thin filled shapes are read as rules, and wider ones give the edges of backgrounds.
        var sb = new StringBuilder();
        for (int i = 0; i < 30_000; i++)
        {
            double x = 10 + i % 150 * 4, y = 10 + i / 150 * 3.8;
            sb.Append(N(x)).Append(' ').Append(N(y)).Append(i % 2 == 0 ? " 3.5 0.3 re f\n" : " 3.5 3.5 re f\n");
        }
        for (int i = 0; i < 200; i++) sb.Append(Text(12 + i % 10 * 58, 20 + i / 10 * 38, 6, "cell " + i.ToString(CultureInfo.InvariantCulture)));
        Assert.Contains("cell 199", Convert(Page(sb.ToString())));
    }

    [Fact]
    public void Thousands_of_short_lines_in_shifting_columns_stay_bounded()
    {
        // Lines whose words sit at a different place on each line, so every run of lines has a different set of gaps.
        var sb = new StringBuilder();
        var random = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            double y = 20 + i * 3;
            int words = 1 + i % 5;
            for (int k = 0; k < words; k++) sb.Append(Text(10 + k * 110 + random.Next(0, 60), y, 2.5, "w" + (i % 97).ToString(CultureInfo.InvariantCulture)));
        }
        Assert.Contains("<section", Convert(Page(sb.ToString(), 612, 9100)));
    }

    [Fact]
    public void Alternating_prose_and_table_rows_stay_bounded()
    {
        // A table row, then a line of running text, again and again: each line of text splits the run it is in.
        var sb = new StringBuilder();
        for (int i = 0; i < 2500; i++)
        {
            double y = 20 + i * 3.4;
            if (i % 2 == 0)
            {
                sb.Append(Text(10, y, 3, "Item"));
                sb.Append(Text(200, y, 3, "12.00"));
                sb.Append(Text(400, y, 3, "kr"));
            }
            else sb.Append(Text(10, y, 3, "this is a line of running text that runs across most of the width of the page"));
        }
        Assert.Contains("<section", Convert(Page(sb.ToString(), 612, 8600)));
    }

    [Fact]
    public void Many_side_by_side_blocks_stay_bounded()
    {
        // Two parts side by side whose gap moves a little on every line, as several floated boxes would.
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            double y = 20 + i * 3;
            sb.Append(Text(10, y, 2.5, "left part of the line"));
            sb.Append(Text(120 + i % 40 * 2, y + i % 3 * 0.7, 2 + i % 2, "right part"));
        }
        Assert.Contains("<section", Convert(Page(sb.ToString(), 612, 9100)));
    }
}
