using System.Globalization;
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
        FormControls.Prepare(doc, interactive: false);
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

    /// <summary>The distance from the first baseline to the 41st of a paragraph in <paramref name="style"/>.</summary>
    private static float FortyLines(string style)
    {
        var text = Layout($"<p style='{style}; margin: 0'>{string.Join("<br>", Enumerable.Repeat("line", 41))}</p>").OfType<TextOp>().ToList();
        Assert.Equal(41, text.Count);
        return text[^1].Baseline - text[0].Baseline;
    }

    [Theory]
    // 11px × 1.45 = 15.95px is kept as 1020/64px; 20px × 1.15 stays 23px despite float error.
    [InlineData("font: 11px Arial; line-height: 1.45", 1020)]
    [InlineData("font: 20px Arial; line-height: 1.15", 1472)]
    // 8pt is 10.67px, which Chrome rounds to 683/64px before multiplying: 14.40625px, not 14.4px rounded down.
    [InlineData("font: 8pt Arial; line-height: 1.35", 922)]
    [InlineData("font: 10pt Arial; line-height: 1.2", 1023)]
    // Lengths are rounded to the nearest unit: 17.4px is 1113.6/64px.
    [InlineData("font: 12px Arial; line-height: 17.4px", 1114)]
    [InlineData("font: 11px Arial; line-height: 1.45em", 1021)]
    public void Line_heights_are_kept_in_chrome_layout_units(string style, int sixtyFourths)
    {
        // Chrome lays lines out in 1/64px; over a page the exact value drifts by a pixel from where Chrome puts them.
        Near(40 * sixtyFourths / 64f * 0.75f, FortyLines(style), 0.001f);
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

    [Theory]
    [InlineData("word-break: break-all")]
    [InlineData("overflow-wrap: anywhere")]
    public void A_table_cell_that_may_break_inside_words_fits_its_container(string wrap)
    {
        // The cell's min-content width is its widest letter, so the table stays within the 60px box, as in Chrome.
        var lines = Lines(Layout($"<div style='width: 60px; font: 12px Courier New'><table><tr><td style='{wrap}'>"
                                 + "url(data:image/png;base64,iVBORw0KGgo)</td></tr></table></div>"));
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 8, l));
    }

    [Theory]
    [InlineData("P*")]
    [InlineData("P(")]
    [InlineData("P&nbsp;")]
    [InlineData("P<span>*</span>")]
    public void A_line_breaks_before_an_inline_block_that_does_not_fit(string before)
    {
        // Chrome breaks before the box, even after an opening bracket or a no-break space, rather than carrying the
        // last word down with it.
        var ops = Layout($"<div style='width: 120px; font: 14px Arial'>AA {before}<span style='display: inline-block; width: 100px'>IB</span></div>");
        var text = ops.OfType<TextOp>().ToList();
        Assert.Equal("IB", text[^1].Text);
        Assert.All(text.Take(text.Count - 1), t => Near(text[0].Baseline, t.Baseline));
        Assert.True(text[^1].Baseline > text[0].Baseline);
    }

    [Theory]
    [InlineData(".")]
    [InlineData(")")]
    [InlineData("&nbsp;q")]
    public void A_line_breaks_after_an_inline_block_even_before_punctuation(string after)
    {
        var ops = Layout($"<div style='width: 120px; font: 14px Arial'><span style='display: inline-block; width: 117px'>IB</span>{after} ZZ</div>");
        var text = ops.OfType<TextOp>().ToList();
        Assert.Equal("IB", text[0].Text);
        Assert.All(text.Skip(1), t => Assert.True(t.Baseline > text[0].Baseline));
    }

    [Fact]
    public void Text_before_an_empty_inline_block_or_image_stays_on_its_line()
    {
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(4, 4, 0, 0, 255));
        var ops = Layout($"""
            <div style='width: 120px; font: 14px Arial'>AA P*<span style='display: inline-block; width: 100px; height: 8px; background: #ff0000'></span></div>
            <div style='width: 120px; font: 14px Arial'>AA P*<img src='{png}' style='width: 100px; height: 10px'></div>
            """);
        Assert.Equal(new[] { "AA P*", "AA P*" }, Lines(ops));
        var text = ops.OfType<TextOp>().ToList();
        Assert.True(Box(ops, "#ff0000").Y > text[0].Baseline);
        Assert.True(ops.OfType<ImageOp>().Single().Y > text[1].Baseline);
    }

    [Fact]
    public void No_line_breaks_around_an_inline_block_in_nowrap_text()
    {
        var ops = Layout("<div style='width: 120px; font: 14px Arial; white-space: nowrap'>AA P*<span style='display: inline-block; width: 100px'>IB</span>.</div>");
        var text = ops.OfType<TextOp>().ToList();
        Assert.All(text, t => Near(text[0].Baseline, t.Baseline));
    }

    [Fact]
    public void A_min_content_column_narrows_to_its_widest_inline_block_not_to_the_text_beside_it()
    {
        // The break before the inline-block makes the cell's min-content width the inline-block's 100px (75pt), as in
        // Chrome, instead of the text and the inline-block together.
        var ops = Layout("""
            <div style="display: grid; grid-template-columns: min-content 1fr; font: 14px Arial">
              <div style="background: #00ff00">AA*<span style="display: inline-block; width: 100px; height: 10px"></span></div><div></div>
            </div>
            """);
        Near(75f, Box(ops, "#00ff00").W);
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
        // so rows are 10.5 + 1.5 = 12pt apart in Chrome, not 10.5 + 0.75. A row's background starts where the line above
        // it, which the row above draws, ends, so it shows 12 - 0.75 = 11.25pt.
        var ops = Layout("""
            <table style="border-collapse: collapse; font: 12px Arial">
              <tr style="background: #ff0000"><td style="padding: 0; border-bottom: 2px solid #000">a</td></tr>
              <tr style="background: #00ff00"><td style="padding: 0; border-bottom: 2px solid #000">b</td></tr>
              <tr style="background: #0000ff"><td style="padding: 0; border-bottom: 2px solid #000">c</td></tr>
            </table>
            """);
        Near(11.25f, Box(ops, "#ff0000").H);
        Near(11.25f, Box(ops, "#00ff00").H);
        Near(11.25f, Box(ops, "#0000ff").H);
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
    public void Text_fields_with_a_percentage_width_count_with_their_own_width_in_table_columns()
    {
        // Chrome sizes the columns as if each field were as wide as its size attribute makes it, whatever its value, so
        // the three columns come out equal; the value "Example Ltd." no longer widens the first one.
        const string field = "style='width: 100%; box-sizing: border-box; font: 12px Arial'";
        var ops = Layout($"""
            <table style="width: 500px; border-collapse: collapse; font: 12px Arial">
              <tr><td style="padding: 0; background: #f00">Employer</td><td style="padding: 0; background: #0f0">From</td>
                  <td style="padding: 0; background: #00f">To</td></tr>
              <tr><td style="padding: 0"><input value="Example Ltd." {field}></td><td style="padding: 0"><input value="2015" {field}></td>
                  <td style="padding: 0"><input {field}></td></tr>
            </table>
            """);
        Near(125, Box(ops, "#f00").W, 0.5f);
        Near(125, Box(ops, "#0f0").W, 0.5f);
        Near(125, Box(ops, "#00f").W, 0.5f);
    }

    [Fact]
    public void A_text_field_with_a_percentage_width_lets_its_column_shrink()
    {
        // A field with a percentage width is compressible: next to long text Chrome narrows its column to 56px, below
        // the field's own width, instead of keeping the field whole.
        var ops = Layout("""
            <table style="width: 300px; border-spacing: 0; font: 12px Arial"><tr>
              <td style="padding: 0; background: #f00"><input value="Example Ltd." style="width: 100%; box-sizing: border-box; font: 12px Arial"></td>
              <td style="padding: 0; background: #0f0">Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua</td>
            </tr></table>
            """);
        Near(55.95f * 0.75f, Box(ops, "#f00").W, 1.5f);
        Near(225, Box(ops, "#f00").W + Box(ops, "#0f0").W);
    }

    [Fact]
    public void A_textarea_shows_its_rows_inside_its_padding_and_borders_whatever_its_box_sizing()
    {
        // Three rows of 14px plus 2px of padding and a 1px border on each side: 48px, as in Chrome, also with border-box.
        var ops = Layout("""
            <textarea rows="3" style="box-sizing: border-box; padding: 2px; border: 1px solid; font: 12px Arial; border-radius: 0; background: #ff0"></textarea>
            """);
        Near(36, Box(ops, "#ff0").H);
    }

    [Fact]
    public void A_check_box_hangs_its_bottom_margin_below_the_baseline()
    {
        // Chrome puts the 13px box with its 3px margins on the baseline by its border box, so an 8px line is 19px tall
        // (3 + 13 + 3), not 21px (3 + 13 + 3 above the baseline and the font's descent below it).
        var ops = Layout("<div style='font: 8px Arial; background: #f00'><input type='checkbox'> Text</div>");
        Near(19 * 0.75f, Box(ops, "#f00").H);
    }

    [Fact]
    public void A_drop_down_keeps_its_own_room_around_the_page_padding()
    {
        // Chrome keeps 4px before the text and 1px above and below it inside a drop-down even with no padding, so a
        // 12px Arial select with no padding or border is 16px tall and its text starts 4px in.
        var ops = Layout("""
            <select style="padding: 0; border: 0; border-radius: 0; font: 12px Arial; background: #f00"><option>Sweden</option></select>
            """);
        var box = Box(ops, "#f00");
        Near(16 * 0.75f, box.H);
        Near(box.X + 3, ops.OfType<TextOp>().Single(t => t.Text == "Sweden").X);
    }

    [Fact]
    public void A_date_field_keeps_a_pixel_above_and_below_its_value()
    {
        // Chrome's date fields are 2px taller than a text field of the same font: 16px for 12px Arial.
        var ops = Layout("""
            <input type="date" value="1990-05-17" style="padding: 0; border: 0; border-radius: 0; font: 12px Arial; background: #f00">
            """);
        Near(16 * 0.75f, Box(ops, "#f00").H);
    }

    [Fact]
    public void Superscripts_and_subscripts_shift_by_a_share_of_the_parent_font_in_layout_units()
    {
        // 12px parent: super is raised 12/3 + 1 = 5px, sub is lowered 12/5 + 1 = 3.4px, kept as 217/64px.
        var ops = Layout("<p style='font: 12px Arial; margin: 0'>x<sup>2</sup>y<sub>3</sub></p>").OfType<TextOp>().ToList();
        var x = ops.Single(t => t.Text.StartsWith('x'));
        Near(x.Baseline - 3.75f, ops.Single(t => t.Text == "2").Baseline);
        Near(x.Baseline + 217 / 64f * 0.75f, ops.Single(t => t.Text == "3").Baseline);
    }

    [Fact]
    public void A_superscript_inside_a_superscript_is_raised_again()
    {
        // 16px text: the outer sup rises 16/3 + 1 px, the inner one a further 13.33/3 + 1 px from the outer's baseline,
        // and so does text in an inline element inside a superscript.
        var ops = Layout("<p style='font: 16px Arial; margin: 0'>x<sup>y<sup>z</sup><b>w</b></sup></p>").OfType<TextOp>().ToList();
        float x = ops.Single(t => t.Text == "x").Baseline, y = ops.Single(t => t.Text == "y").Baseline;
        Near(x - 405 / 64f * 0.75f, y);
        Near(y - 348 / 64f * 0.75f, ops.Single(t => t.Text == "z").Baseline);
        Near(y, ops.Single(t => t.Text == "w").Baseline);
    }

    [Fact]
    public void Lines_with_superscripts_and_subscripts_are_as_tall_as_in_Chrome()
    {
        // Chrome: 18px for the plain 16px line (14px above the baseline, 4px below), 22.33px with a superscript and
        // 25.52px with both a superscript and a subscript, whose 13.33px text reaches 12px up and 3px down from shifts
        // kept in 1/64 pixels.
        string Para(string inner) => $"<p style='font: 16px Arial; margin: 0'>{inner}</p>";
        var ops = Layout(Para("a") + Para("E=mc<sup>2</sup>") + Para("a<sup>2</sup>b<sub>3</sub>") + Para("end")).OfType<TextOp>().ToList();
        float a = ops.First(t => t.Text == "a").Baseline, e = ops.Single(t => t.Text.StartsWith("E=mc")).Baseline;
        float end = ops.Single(t => t.Text == "end").Baseline;
        Near(a + (4 + 12 + 405 / 64f) * 0.75f, e);
        Near(e + (4 + 12 + 405 / 64f + 268 / 64f + 3 + 14) * 0.75f, end);
    }

    [Theory]
    [InlineData("<div style='display: inline-flex'>word{0}</div>|after")]
    [InlineData("<div style='display: inline-flex'><b>word{0}</b></div>|after")]
    [InlineData("<div style='display: flex'><span>word{0}</span>|after</div>")]
    [InlineData("<div style='float: left'>word{0}</div>|after")]
    [InlineData("<span style='display: inline-block'>word <b>x{0}</b></span>|after")]
    [InlineData("<table style='border-collapse: collapse'><tr><td style='padding: 0'>word{0}</td><td style='padding: 0'>|after</td></tr></table>")]
    public void A_trailing_space_is_not_part_of_a_shrink_to_fit_width(string html)
    {
        // The space at the end of the line hangs, so Chrome sizes the box as if it were not there.
        float After(string space) => Layout($"<div style='font: 16px Arial'>{string.Format(CultureInfo.InvariantCulture, html, space)}</div>")
            .OfType<TextOp>().Single(t => t.Text.StartsWith('|')).X;
        Near(After(""), After(" "));
    }

    [Fact]
    public void Img_align_right_floats_with_hspace()
    {
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(4, 4, 0, 0, 255));
        var img = Layout($"<p style='margin: 0'><img src='{png}' align='right' hspace='10' width='40' height='40'>Text</p>").OfType<ImageOp>().Single();
        Near(Width - 7.5f - 30, img.X); // 10px of hspace and 40px of image, in points
    }

    [Fact]
    public void List_type_and_reversed_attributes_number_the_items()
    {
        var lines = Lines(Layout("<ol type='I' reversed><li>a</li><li>b</li><li>c</li></ol><ol type='a' start='3'><li>d</li></ol>"));
        Assert.Contains("III.", lines);
        Assert.Contains("II.", lines);
        Assert.Contains("I.", lines);
        Assert.Contains("c.", lines);
    }

    [Fact]
    public void Align_center_centres_a_nested_table_but_not_its_text()
    {
        var ops = Layout("""
            <div align="center"><table cellspacing="0" style="width: 200px; background: #f00"><tr><td style="padding: 0">left</td></tr></table></div>
            """);
        var table = Box(ops, "#f00");
        Near(175, table.X);
        Near(175, ops.OfType<TextOp>().Single(t => t.Text == "left").X);
    }

    [Fact]
    public void Bordercolor_draws_solid_table_and_cell_borders_in_that_colour()
    {
        var ops = Layout("<table border='2' bordercolor='red'><tr><td>a</td></tr></table>");
        Assert.True(CssColor.TryParse("red", 0xFF000000, out var red));
        Assert.Equal(2, ops.OfType<BorderRectOp>().Count(b => b.Color == red));
    }

    [Fact]
    public void Vertical_align_lengths_and_percentages_shift_the_baseline()
    {
        // 5px raises by 5px; -30% lowers by 30% of the span's 20px line height, 6px.
        var ops = Layout("""
            <p style="font: 12px Arial; line-height: 20px; margin: 0">x<span style="vertical-align: 5px">a</span><span style="vertical-align: -30%">b</span></p>
            """).OfType<TextOp>().ToList();
        var x = ops.Single(t => t.Text.StartsWith('x'));
        Near(x.Baseline - 3.75f, ops.Single(t => t.Text == "a").Baseline);
        Near(x.Baseline + 4.5f, ops.Single(t => t.Text == "b").Baseline);
    }

    [Theory]
    // Arial 24px reaches 22px above and 5px below the baseline, and its 1px line gap makes the line reach 6px below;
    // Arial 12px reaches 11px and 3px. top hangs the small text from the line's top and text-top from the top of the
    // parent's font, while bottom stands it on the line's bottom and text-bottom on the bottom of the parent's font.
    [InlineData("top", -8.25f)]
    [InlineData("text-top", -8.25f)]
    [InlineData("bottom", 2.25f)]
    [InlineData("text-bottom", 1.5f)]
    // middle centres the 14px box on the baseline raised by half the parent's x-height (12.68px): 2.34px up. Chrome
    // puts it about a third of a pixel higher.
    [InlineData("middle", -1.755f)]
    public void Vertical_align_keywords_place_smaller_inline_text_as_chrome_does(string align, float shift)
    {
        var ops = Layout($"""
            <p style="font: 24px Arial; margin: 0">x<span style="font-size: 12px; vertical-align: {align}">a</span></p>
            """).OfType<TextOp>().ToList();
        var x = ops.Single(t => t.Text == "x");
        Near(16.5f, x.Baseline);
        Near(x.Baseline + shift, ops.Single(t => t.Text == "a").Baseline);
    }

    [Fact]
    public void Text_aligned_to_the_line_top_follows_a_taller_line()
    {
        // A 40px image makes the line 40px tall; the text aligned to its top still starts at the top.
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(4, 4, 0, 0, 255));
        var ops = Layout($"""
            <p style="font: 12px Arial; margin: 0"><img src="{png}" style="width: 40px; height: 40px"><span style="vertical-align: top">a</span></p>
            """).OfType<TextOp>().ToList();
        Near(8.25f, ops.Single(t => t.Text == "a").Baseline);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("bottom")]
    public void Boxes_inside_an_inline_box_aligned_to_the_line_edge_move_with_it(string align)
    {
        // As in Chrome, an inline-block on the baseline of a span aligned to the line's top or bottom moves with the
        // span: the span and all inside it hang from the line's top, or stand on its bottom, as one.
        var ops = Layout($"""
            <div style="font: 12px Arial; line-height: 60px">Base <span style="vertical-align: {align}; line-height: normal"><span style="display: inline-block; width: 20px; height: 20px; background: #ff0000"></span>edge</span></div>
            """);
        var box = Box(ops, "#ff0000");
        float baseline = ops.OfType<TextOp>().Single(t => t.Text == "edge").Baseline;
        Near(box.Y + box.H, baseline);
        if (align == "top") Near(0, box.Y);
        else Assert.True(baseline > 30, $"the span's baseline {baseline} is not near the line's bottom");
    }

    [Theory]
    [InlineData("text-align: center", "0 auto", 150)]
    [InlineData("text-align: center", "0", 150)]
    [InlineData("text-align: left", "0 0 0 auto", 0)]
    public void Auto_margins_on_inline_blocks_are_zero(string align, string margin, float x)
    {
        // text-align places an inline-block on its line; auto margins neither centre it nor push it to the right.
        var ops = Layout($"""
            <div style="width: 400pt; {align}"><div style="display: inline-block; width: 100pt; height: 10pt; margin: {margin}; background: #ff0000"></div></div>
            """);
        Near(x, Box(ops, "#ff0000").X);
    }

    [Fact]
    public void Images_aligned_to_top_and_bottom_share_one_line_box()
    {
        // Chrome hangs the top image from the line's top and stands the bottom one on its bottom: on a line made 30px
        // tall by them, both span the whole line.
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(4, 4, 0, 0, 255));
        var images = Layout($"""
            <p style="font: 12px Arial; margin: 0">a <img src="{png}" style="width: 30px; height: 30px; vertical-align: top"> b <img src="{png}" style="width: 30px; height: 30px; vertical-align: bottom"></p>
            """).OfType<ImageOp>().ToList();
        Assert.Equal(2, images.Count);
        Near(0, images[0].Y);
        Near(0, images[1].Y);
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
    [Theory]
    [InlineData("width: 100%")]
    [InlineData("width: 50%")]
    [InlineData("max-width: 100%")]
    public void A_percentage_sized_image_does_not_widen_a_fixed_width_table(string imageStyle)
    {
        // A 400px image with a percentage width has no min-content width, as in Chrome: the 200px table keeps its width
        // instead of growing to the image's natural size.
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(400, 250, 0, 0, 255));
        var ops = Layout($"""
            <table style="width: 200px; border-collapse: collapse; background: #00ff00"><tr><td style="padding: 0"><img src="{png}" style="{imageStyle}"></td><td style="padding: 0">text</td></tr></table>
            """);
        Near(150, Box(ops, "#00ff00").W);
    }

    [Fact]
    public void A_percentage_sized_svg_image_does_not_widen_a_fixed_width_table()
    {
        var svg = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='400' height='250'><rect width='400' height='250' fill='red'/></svg>"));
        var ops = Layout($"""
            <table style="width: 200px; border-collapse: collapse; background: #00ff00"><tr><td style="padding: 0"><img src="{svg}" style="width: 100%"></td><td style="padding: 0">text</td></tr></table>
            """);
        Near(150, Box(ops, "#00ff00").W);
    }

    [Theory]
    [InlineData("display: flex; flex-direction: column")]
    [InlineData("display: flex; align-items: flex-start")]
    [InlineData("display: grid")]
    public void Margins_of_the_first_and_last_child_stay_inside_a_flex_or_grid_item(string container)
    {
        // A flex or grid item is a formatting context of its own, so its child's 16px margins do not collapse through
        // it: the item is 16 + 20 + 16px tall, as in Chrome (a Bootstrap card body ending in a table).
        var ops = Layout($"""
            <div style="{container}"><div style="background: #00ff00"><div style="width: 50px; height: 20px; margin: 16px 0"></div></div></div>
            """);
        Near(39, Box(ops, "#00ff00").H);
    }

    [Fact]
    public void Margins_still_collapse_through_a_plain_block()
    {
        var ops = Layout("""
            <div style="background: #00ff00"><div style="height: 20px; margin: 16px 0"></div></div>
            """);
        Near(15, Box(ops, "#00ff00").H);
    }

    [Theory]
    [InlineData("box-shadow: 0 0 0 1px #888")]
    [InlineData("box-shadow: 0 0 0 1px #888; border-radius: 10px")]
    [InlineData("box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3)")]
    [InlineData("box-shadow: 0 0 0 1px #888; opacity: 0.9")]
    [InlineData("box-shadow: 0 200mm 0 0 #888")]
    public void A_box_shadow_below_a_box_as_tall_as_the_page_adds_no_page(string style)
    {
        // The shadow is ink overflow, which Chrome does not paginate: a shadow around a box exactly as tall as the page
        // area, or even one offset pages down, leaves the document one page long (a boarding pass with a 1px shadow
        // used as its border).
        var pdf = HtmlToPdf.Convert($$"""
            <style>@page { size: 210mm 90mm; margin: 5mm }</style>
            <body style="margin: 0"><div style="height: 80mm; {{style}}"></div></body>
            """);
        Assert.Equal(1, PdfPages.Count(pdf));
    }

    [Fact]
    public void A_box_continuing_onto_the_next_page_keeps_that_page()
    {
        // The second page holds the rest of the box as well as its shadow, so it stays.
        var pdf = HtmlToPdf.Convert("""
            <style>@page { size: 210mm 90mm; margin: 5mm }</style>
            <body style="margin: 0"><div style="height: 120mm; box-shadow: 0 0 6px 2px #ff0000"></div></body>
            """);
        Assert.Equal(2, PdfPages.Count(pdf));
    }
}
