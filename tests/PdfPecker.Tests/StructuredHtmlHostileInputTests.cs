using System.Globalization;
using System.Text;

namespace PdfPecker.Tests;

/// <summary>Structured PDF to HTML on pages built to make finding tables, columns and paragraphs slow.</summary>
[Collection(HostileInputCollection.Name)]
public class StructuredHtmlHostileInputTests
{
    private static readonly PdfHtmlOptions Structured = new() { Mode = PdfHtmlMode.Structured };

    private static readonly PdfHtmlOptions Styled = new() { Mode = PdfHtmlMode.Structured, IncludeStyles = true, IncludeImages = true };

    /// <summary>
    /// A one-page PDF of the given size showing <paramref name="content"/> in Helvetica; with a structure tree, its root
    /// is object 6 and the objects of <paramref name="structure"/> follow from there.
    /// </summary>
    private static byte[] Page(string content, int width = 612, int height = 792, string[]? structure = null)
    {
        var objects = new List<string>
        {
            structure == null ? "<< /Type /Catalog /Pages 2 0 R >>" : "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        if (structure != null) objects.AddRange(structure);
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Text(double x, double y, double size, string text) => $"BT /F1 {N(size)} Tf {N(x)} {N(y)} Td ({text}) Tj ET\n";

    /// <summary>The page in plain structured HTML, after checking that the output with styles and images is bounded too.</summary>
    private static string Convert(byte[] pdf)
    {
        Convert(pdf, Styled);
        return Convert(pdf, Structured);
    }

    private static string Convert(byte[] pdf, PdfHtmlOptions options)
    {
        var task = Task.Run(() =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return PdfToHtml.Convert(pdf, options, cts.Token);
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

    /// <summary>Marked content <paramref name="mcid"/> showing a word.</summary>
    private static string Tagged(int mcid, double x, double y, string text) => $"/P << /MCID {mcid} >> BDC " + Text(x, y, 8, text) + "EMC\n";

    [Fact]
    public void A_structure_tree_that_nests_deeply_and_loops_stays_bounded()
    {
        // Five thousand elements, each the only child of the one before, the last pointing back to the first.
        const int depth = 5000;
        var structure = new List<string> { "<< /Type /StructTreeRoot /K 7 0 R >>" };
        for (int i = 0; i < depth; i++)
        {
            int next = i + 1 < depth ? 8 + i : 7;
            structure.Add($"<< /Type /StructElem /S /Div /Pg 3 0 R /K [{i % 50} {next} 0 R] >>");
        }
        var content = new StringBuilder();
        for (int i = 0; i < 50; i++) content.Append(Tagged(i, 20, 20 + i * 12, "word" + i.ToString(CultureInfo.InvariantCulture)));
        var html = Convert(Page(content.ToString(), structure: structure.ToArray()));
        Assert.Contains("word49", html);
    }

    [Fact]
    public void A_tagged_table_with_huge_spans_stays_bounded()
    {
        // A thousand cells, each claiming to span a thousand rows and columns.
        var structure = new List<string> { "<< /Type /StructTreeRoot /K 7 0 R >>", "<< /Type /StructElem /S /Table /Pg 3 0 R /K 8 0 R >>" };
        var cells = string.Join(" ", Enumerable.Range(0, 1000).Select(i => $"<< /Type /StructElem /S /TD /A << /O /Table /RowSpan 1000 /ColSpan 1000 >> /K {i} >>"));
        structure.Add($"<< /Type /StructElem /S /TR /Pg 3 0 R /K [{cells}] >>");
        var content = new StringBuilder();
        for (int i = 0; i < 1000; i++) content.Append(Tagged(i, 10 + i % 20 * 29, 20 + i / 20 * 14, "c" + i.ToString(CultureInfo.InvariantCulture)));
        var html = Convert(Page(content.ToString(), structure: structure.ToArray()));
        Assert.Contains("c999", html);
    }

    [Fact]
    public void Wide_tagged_rows_with_cells_left_out_stay_bounded()
    {
        // A full header row of 2,000 cells, then rows that leave most cells out, which are lined up with the header.
        var structure = new List<string> { "<< /Type /StructTreeRoot /K 7 0 R >>" };
        var rows = new StringBuilder();
        var content = new StringBuilder();
        int mcid = 0;
        for (int r = 0; r < 20; r++)
        {
            rows.Append("<< /Type /StructElem /S /TR /Pg 3 0 R /K [");
            for (int c = 0; c < (r == 0 ? 2000 : 1000); c++)
            {
                rows.Append($"<< /S /TD /K {mcid} >> ");
                content.Append(Tagged(mcid++, 5 + c * 0.39, 20 + r * 30, "x"));
            }
            rows.Append("] >> ");
        }
        structure.Add($"<< /Type /StructElem /S /Table /Pg 3 0 R /K [{rows}] >>");
        Assert.Contains("<section", Convert(Page(content.ToString(), 792, 792, structure.ToArray())));
    }

    [Fact]
    public void One_path_of_thousands_of_nested_boxes_under_deep_clips_stays_bounded()
    {
        // Nested clips, then a single path of boxes inside one another, which the styles look at pairwise for frames.
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++) sb.Append("q ").Append(N(i)).Append(' ').Append(N(i)).Append(" 600 600 re 1 1 3 3 re W n\n");
        sb.Append("0.9 0.2 0.1 rg\n");
        for (int i = 0; i < 5000; i++) sb.Append(N(10 + i * 0.05)).Append(' ').Append(N(10 + i * 0.05)).Append(' ').Append(N(590 - i * 0.1)).Append(' ').Append(N(590 - i * 0.1)).Append(" re\n");
        sb.Append("f\n");
        for (int i = 0; i < 200; i++) sb.Append("Q\n");
        sb.Append(Text(50, 300, 10, "inside"));
        Assert.Contains("inside", Convert(Page(sb.ToString())));
    }

    [Fact]
    public void Words_that_change_colour_at_every_letter_stay_bounded()
    {
        var sb = new StringBuilder("BT /F1 8 Tf 10 780 Td\n");
        for (int i = 0; i < 20_000; i++)
        {
            sb.Append(N(i % 7 / 7.0)).Append(' ').Append(N(i % 5 / 5.0)).Append(" 0.3 rg (").Append((char)('a' + i % 26)).Append(") Tj\n");
            if (i % 120 == 119) sb.Append("0 -9 Td\n");
        }
        sb.Append("ET\n");
        Assert.Contains("<section", Convert(Page(sb.ToString())));
    }
}
