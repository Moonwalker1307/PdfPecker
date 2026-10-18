using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Line spacing, line breaking, table heights and flex shrinking, checked against what Chrome produces for the same
/// HTML (the expected values were measured in Chromium's PDF output).
/// </summary>
public class BrowserMatchTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).ToList();
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static List<string> Lines(List<DrawOp> ops) => ops.OfType<TextOp>().Select(t => t.Text).ToList();

    private static RectOp Box(List<DrawOp> ops, string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return ops.OfType<RectOp>().Single(r => r.Color == c);
    }

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    [Fact]
    public void Line_height_normal_uses_the_font_metrics_rounded_to_pixels()
    {
        // Arial 12px: ascent 11px, descent 3px, no gap: lines 14px (10.5pt) apart, the first baseline 11px down.
        var ops = Layout("<p style='font: 12px Arial; margin: 0'>one<br>two<br>three</p>");
        var baselines = ops.OfType<TextOp>().Select(t => t.Baseline).ToList();
        Assert.Equal(3, baselines.Count);
        Near(8.25f, baselines[0]);
        Near(18.75f, baselines[1]);
        Near(29.25f, baselines[2]);
    }

    [Fact]
    public void Line_height_normal_differs_per_font()
    {
        // Times 16px: 14 + 3 + 1px gap = 18px. Courier 11px: 9 + 3 = 12px.
        var times = Layout("<p style='font: 16px Times New Roman; margin: 0'>a<br>b</p>").OfType<TextOp>().ToList();
        Near(13.5f, times[1].Baseline - times[0].Baseline);
        var courier = Layout("<p style='font: 11px Courier New; margin: 0'>a<br>b</p>").OfType<TextOp>().ToList();
        Near(9f, courier[1].Baseline - courier[0].Baseline);
    }

    [Fact]
    public void Explicit_line_height_puts_whole_pixels_of_leading_above_the_text()
    {
        // 20px line, 14px of text: 3px of leading above, so the baseline is 14px down.
        var ops = Layout("<p style='font: 12px Arial; line-height: 20px; margin: 0'>one<br>two</p>").OfType<TextOp>().ToList();
        Near(10.5f, ops[0].Baseline);
        Near(15f, ops[1].Baseline - ops[0].Baseline);
    }

    [Fact]
    public void Words_wider_than_the_line_overflow_instead_of_breaking()
    {
        Assert.Equal(new[] { "0100" }, Lines(Layout("<div style='width: 24px; font: 12px Arial'>0100</div>")));
        var grid = Layout("<div style='display: grid; grid-template-columns: 24px 1fr; font: 12px Arial'><span>0100</span><span>Månedsløn</span></div>");
        Assert.Equal(new[] { "0100", "Månedsløn" }, Lines(grid));
    }

    [Fact]
    public void Lines_break_after_hyphens()
    {
        var ops = Layout("<div style='width: 30mm; font: 12px Arial'>Arbejdsmarkeds-bidrag og arbejdsskade-afgift</div>");
        Assert.Equal(new[] { "Arbejdsmarkeds-", "bidrag og", "arbejdsskade-afgift" }, Lines(ops));
        var url = Layout("<div style='width: 70mm; font: 12px Arial'>Se mere på www.virk.dk/vejledning/samlet-betaling/selvbetjening/ i dag</div>");
        Assert.Equal(new[] { "Se mere på www.virk.dk/vejledning/samlet-", "betaling/selvbetjening/ i dag" }, Lines(url));
    }

    [Fact]
    public void Soft_hyphens_show_a_hyphen_only_where_the_line_breaks()
    {
        var ops = Layout("<div style='width: 22mm; font: 12px Arial'>Arbejds&shy;markeds&shy;bidrag ok</div>");
        Assert.Equal(new[] { "Arbejds-", "markedsbidrag", "ok" }, Lines(ops));
        Assert.Equal(new[] { "Arbejdsmarkedsbidrag" }, Lines(Layout("<div style='font: 12px Arial'>Arbejds&shy;markeds&shy;bidrag</div>")));
    }

    [Fact]
    public void Overflow_wrap_and_word_break_allow_breaking_inside_words()
    {
        Assert.Equal(new[] { "010", "001", "00" }, Lines(Layout("<div style='width: 24px; font: 12px Arial; overflow-wrap: anywhere'>01000100</div>")));
        Assert.Equal(new[] { "010", "001", "00 2" }, Lines(Layout("<div style='width: 24px; font: 12px Arial; word-wrap: break-word'>01000100 2</div>")));
        // break-all fills the end of a line instead of moving the word to the next one.
        Assert.Equal(new[] { "ab c", "def" }, Lines(Layout("<div style='width: 30px; font: 12px Courier New; word-break: break-all'>ab cdef</div>")));
    }

    [Fact]
    public void A_table_height_is_shared_out_over_its_rows()
    {
        // Rows of 3 lines and 1 line (13.5pt each) fill 100pt in proportion: 40.5 + 34.5 and 13.5 + 11.5.
        var ops = Layout("""
            <table style="height: 100pt; border-collapse: collapse">
              <tr style="background: #ff0000"><td style="padding: 0">a<br>a<br>a</td></tr>
              <tr style="background: #00ff00"><td style="padding: 0">b</td></tr>
            </table>
            """);
        Near(75f, Box(ops, "#ff0000").H);
        Near(25f, Box(ops, "#00ff00").H);
    }

    [Fact]
    public void A_collapsed_border_set_only_on_the_row_above_takes_room_in_the_row_below()
    {
        // Lines under each row only (2px): every row after the first owns half the line above it and half its own,
        // so rows are 10.5 + 1.5 = 12pt apart in Chrome, not 10.5 + 0.75.
        var ops = Layout("""
            <table style="border-collapse: collapse; font: 12px Arial">
              <tr style="background: #ff0000"><td style="padding: 0; border-bottom: 2px solid #000">a</td></tr>
              <tr style="background: #00ff00"><td style="padding: 0; border-bottom: 2px solid #000">b</td></tr>
              <tr style="background: #0000ff"><td style="padding: 0; border-bottom: 2px solid #000">c</td></tr>
            </table>
            """);
        Near(11.25f, Box(ops, "#ff0000").H);
        Near(12f, Box(ops, "#00ff00").H);
        Near(12f, Box(ops, "#0000ff").H);
        Near(12f, Box(ops, "#0000ff").Y - Box(ops, "#00ff00").Y);
    }

    [Fact]
    public void Flex_items_with_a_width_and_padding_shrink_to_fit()
    {
        var ops = Layout("""
            <div style="display: flex; width: 300px">
              <div style="width: 200px; padding: 0 20px; background: #ff0000">A</div>
              <div style="width: 200px; padding: 0 10px; background: #00ff00">B</div>
            </div>
            """);
        // 460px shrink to 300px, weighted by the 200px content widths: both content boxes become 120px.
        Near(120f, Box(ops, "#ff0000").W);
        Near(105f, Box(ops, "#00ff00").W);
        Near(120f, Box(ops, "#00ff00").X);
    }

    [Fact]
    public void Border_widths_snap_to_whole_pixels()
    {
        Assert.Equal(0.75f, StyleResolver.SnapBorderWidth(1f));      // 1.33px -> 1px
        Assert.Equal(0.75f, StyleResolver.SnapBorderWidth(0.3f));    // under 1px -> 1px
        Assert.Equal(1.5f, StyleResolver.SnapBorderWidth(2f));       // 2.67px -> 2px
        Assert.Equal(3f, StyleResolver.SnapBorderWidth(3f));         // 4px
        Assert.Equal(0f, StyleResolver.SnapBorderWidth(0f));
    }
    [Fact]
    public void Lengths_are_truncated_to_sixty_fourths_of_a_pixel()
    {
        // 0.7pt is 0.933px, which Chrome stores as 59/64 px, so ten such boxes stack 0.08pt short of 7pt.
        Near(59f / 64f * 0.75f, StyleResolver.ToLayoutUnits(0.7f), 0.0001f);
        var ops = Layout("""
            <div style="height: 0.7pt"></div><div style="height: 0.7pt"></div><div style="height: 0.7pt"></div>
            <div style="height: 0.7pt"></div><div style="height: 0.7pt"></div><div style="height: 0.7pt"></div>
            <div style="height: 0.7pt"></div><div style="height: 0.7pt"></div><div style="height: 0.7pt"></div>
            <div style="height: 0.7pt"></div><div style="height: 1pt; background: #ff0000"></div>
            """);
        Near(10 * 59f / 64f * 0.75f, Box(ops, "#ff0000").Y, 0.001f);
    }

    [Fact]
    public void An_empty_paragraph_collapses_its_margins_together()
    {
        // Chrome: the empty <p> adds one 12px margin, not two, and nothing for its own height.
        var ops = Layout("<div style='font: 12px Arial'><div>A</div><p style='margin: 12px 0'></p><div>B</div></div>").OfType<TextOp>().ToList();
        Near((14 + 12) * 0.75f, ops[1].Baseline - ops[0].Baseline);
    }

    [Fact]
    public void A_parent_and_its_first_and_last_child_share_their_margins()
    {
        var ops = Layout("""
            <div style="font: 12px Arial">
              <div>A</div>
              <div style="margin: 10px 0 5px"><p style="margin: 20px 0">x</p></div>
              <div>B</div>
            </div>
            """).OfType<TextOp>().ToList();
        // max(10, 20) above x and max(5, 20) below it.
        Near((14 + 20) * 0.75f, ops[1].Baseline - ops[0].Baseline);
        Near((14 + 20) * 0.75f, ops[2].Baseline - ops[1].Baseline);
    }

    [Fact]
    public void Padding_keeps_a_child_margin_inside_its_parent()
    {
        var ops = Layout("""
            <div style="font: 12px Arial"><div>A</div><div style="padding-top: 1px; margin-top: 10px"><p style="margin: 20px 0 0">x</p></div></div>
            """).OfType<TextOp>().ToList();
        Near((14 + 10 + 1 + 20) * 0.75f, ops[1].Baseline - ops[0].Baseline);
    }

    [Fact]
    public void Collapsed_table_columns_keep_fractional_widths_and_round_where_painted()
    {
        // Chrome keeps 50.6px + 1px of half borders as the column width; only the painted line is rounded,
        // so the borders between the columns land on 52px and 113px, as in Chrome (the ShSavingsCard head box).
        var content = PdfTestHelpers.AllContent(PdfTestHelpers.ConvertUncompressed("""
            <table style="border-collapse: collapse; width: 300px; table-layout: fixed">
              <tr><td style="width: 50.6px; padding: 0; border: 1px solid #000">a</td>
                  <td style="width: 60.6px; padding: 0; border: 1px solid #000">b</td><td style="padding: 0; border: 1px solid #000">c</td></tr>
            </table>
            """, new PdfOptions { Margins = new PageMargins(0) }));
        Assert.Matches(@"(^|\n)39 [\d.]+ 0\.75 [\d.]+ re f", content);
        Assert.Matches(@"(^|\n)84\.75 [\d.]+ 0\.75 [\d.]+ re f", content);
    }

    [Fact]
    public void A_column_with_a_width_keeps_it_next_to_a_column_of_long_text()
    {
        // The long text makes the table narrower than its content's width. Chrome first gives the column with a width
        // its 100px and only then shares out what is left, so that column does not shrink to "Item 1".
        var ops = Layout("""
            <table style="width: 400px; border-spacing: 0; font: 12px Arial"><tr>
              <td style="width: 100px; padding: 0; background: #f00">Item 1</td>
              <td style="padding: 0; background: #0f0">Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua</td>
              <td style="padding: 0; background: #00f">3</td></tr></table>
            """);
        Near(75, Box(ops, "#f00").W);
        Near(300, Box(ops, "#f00").W + Box(ops, "#0f0").W + Box(ops, "#00f").W);
    }

    [Fact]
    public void Superscripts_and_subscripts_shift_by_whole_pixels_of_the_parent_font()
    {
        // 12px parent: super is raised 12/3 + 1 = 5px, sub is lowered 12/5 + 1 = 3px.
        var ops = Layout("<p style='font: 12px Arial; margin: 0'>x<sup>2</sup>y<sub>3</sub></p>").OfType<TextOp>().ToList();
        var x = ops.Single(t => t.Text.StartsWith('x'));
        Near(x.Baseline - 3.75f, ops.Single(t => t.Text == "2").Baseline);
        Near(x.Baseline + 2.25f, ops.Single(t => t.Text == "3").Baseline);
    }

    [Fact]
    public void Box_edges_are_painted_on_whole_pixels()
    {
        // A background starting 10.4px down and 20.6px tall is painted from 10px to 31px, as Chrome does.
        var content = PdfTestHelpers.AllContent(PdfTestHelpers.ConvertUncompressed("""
            <body style="margin: 0"><div style="height: 10.4px"></div><div style="height: 20.6px; width: 100px; background: #ff0000"></div></body>
            """));
        Assert.Contains(" 75 15.75 re f", content);
    }
}
