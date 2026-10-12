using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Kerning, fixed table layout, cell alignment, collapsed borders, list markers, box shadows, the watermark's letter
/// spacing and the scaled paper size. Expected positions were measured in Chromium's PDF output.
/// </summary>
public class KerningAndBoxTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
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

    private static uint Color(string css)
    {
        Assert.True(CssColor.TryParse(css, 0xFF000000, out var c));
        return c;
    }

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    // ---- Kerning ----

    [Fact]
    public void Standard_fonts_are_kerned_like_the_fonts_browsers_use_for_them()
    {
        var helvetica = StandardFont.Get(Fonts.FontFamily.Helvetica, false, false);
        int unkerned = helvetica.CharUnits('A') * 3 + helvetica.CharUnits('V') + helvetica.CharUnits('T') + helvetica.CharUnits('R');
        Assert.True(helvetica.MeasureUnits("AVATAR", 0, 6) < unkerned);
        Assert.Equal(0, StandardFont.Get(Fonts.FontFamily.Courier, false, false).KernUnits('A', 'V'));
    }

    [Fact]
    public void Right_aligned_kerned_text_ends_where_chrome_puts_it()
    {
        // Chrome: "AVATAR" at 20px Arial, right-aligned in 400px, starts at 244.43pt; unkerned it would be 239.99pt.
        var ops = Layout("<div style='width:400px;font:20px Arial;text-align:right'>AVATAR</div>");
        Near(244.43f, ops.OfType<TextOp>().Single().X);
    }

    [Fact]
    public void Pairs_with_a_space_are_kerned_too()
    {
        // Chrome kerns " Y" as well: "Tower Yes" right-aligned starts at 231.62pt.
        var ops = Layout("<div style='width:400px;font:20px Arial;text-align:right'>Tower Yes</div>");
        Near(231.62f, ops.OfType<TextOp>().Single().X);
    }

    [Fact]
    public void Kerned_pairs_move_glyphs_in_the_content_stream()
    {
        var standard = AllContent(ConvertUncompressed("<p style='font-family:Arial'>AVATAR</p>"));
        Assert.Matches(new Regex(@"\[\(A\)-?\d+\(V\)-?\d+\(A\)"), standard);

        // Embedded (bundled Liberation) fonts kern from the font's own GPOS table.
        var embedded = AllContent(ConvertUncompressed("<p style='font-family:Arial'>AVATAR</p>", new PdfOptions { EmbedStandardFonts = true }));
        Assert.Matches(new Regex(@"\[<[0-9A-F]{4}>-?\d+<[0-9A-F]{4}>"), embedded);
    }

    // ---- Tables ----

    [Fact]
    public void Fixed_table_layout_ignores_the_content_width()
    {
        // The long word overflows its 50px column instead of widening it.
        var ops = Layout("<table style='width:300px;table-layout:fixed;border-collapse:collapse'><tr>" +
                         "<td style='width:50px;padding:0'>Udbetalingsperiodeogmere</td><td style='padding:0;background:red'>x</td></tr></table>");
        var second = ops.OfType<RectOp>().Single(r => r.Color == Color("red"));
        Near(37.5f, second.X);
        Near(187.5f, second.W);
    }

    [Fact]
    public void Fixed_table_layout_shares_the_rest_between_columns_without_a_width()
    {
        var ops = Layout("<table style='width:400px;table-layout:fixed;border-collapse:collapse'><tr>" +
                         "<td style='width:100px;padding:0'>a</td><td style='padding:0;background:red'>b</td><td style='padding:0;background:blue'>c</td></tr></table>");
        Near(112.5f, ops.OfType<RectOp>().Single(r => r.Color == Color("red")).W);
        Near(112.5f, ops.OfType<RectOp>().Single(r => r.Color == Color("blue")).W);
    }

    [Fact]
    public void Cell_content_is_centred_in_a_cell_made_taller_by_its_height()
    {
        // Arial 12px: a 14px line in a 100px cell starts 43px down, so the baseline is at 54px.
        var ops = Layout("<table style='border-collapse:collapse'><tr><td style='height:100px;padding:0;font:12px Arial;vertical-align:middle'>x</td></tr></table>");
        Near(40.5f, ops.OfType<TextOp>().Single().Baseline);
    }

    [Fact]
    public void Collapsed_borders_are_drawn_once()
    {
        // A 2x2 grid of 1px borders: three horizontal lines of two cells, and three vertical lines of two rows.
        var ops = Layout("<table style='border-collapse:collapse;border:1px solid red'>" +
                         "<tr><td style='border:1px solid red'>a</td><td style='border:1px solid red'>b</td></tr>" +
                         "<tr><td style='border:1px solid red'>c</td><td style='border:1px solid red'>d</td></tr></table>");
        Assert.Equal(12, ops.OfType<RectOp>().Count(r => r.Color == Color("red")));
    }

    [Fact]
    public void A_wider_table_border_wins_over_the_cells()
    {
        var ops = Layout("<table style='border-collapse:collapse;border:3px solid blue'>" +
                         "<tr><td style='border:1px solid red'>a</td><td style='border:1px solid red'>b</td></tr></table>");
        Assert.Equal(4, ops.OfType<RectOp>().Count(r => r.Color == Color("blue")));
        // Only the line between the two cells is red.
        Assert.Single(ops.OfType<RectOp>(), r => r.Color == Color("red"));
    }

    [Fact]
    public void Uniform_box_borders_are_one_stroked_rectangle()
    {
        var ops = Layout("<div style='border:1px solid red;height:20px'>x</div><div style='border:1px solid blue;border-left-color:green;height:20px'>y</div>");
        var stroke = ops.OfType<BorderRectOp>().Single();
        Assert.Equal(Color("red"), stroke.Color);
        Near(0.75f, stroke.Width);
        Assert.Equal(3, ops.OfType<RectOp>().Count(r => r.Color == Color("blue")));
    }

    // ---- List markers ----

    [Fact]
    public void Disc_markers_sit_where_chrome_draws_them()
    {
        // Chrome, Arial 16px, 60px padding: a 5px disc from x = 44px.
        var ops = Layout("<ul style='margin:0;padding-left:60px;font:16px Arial'><li>x</li></ul>");
        var disc = ops.OfType<CircleOp>().Single();
        Near(1.875f, disc.R);
        Near(46.5f * 0.75f, disc.Cx);
    }

    [Fact]
    public void Ordinal_markers_end_a_space_before_the_content()
    {
        // Chrome, Arial 16px: "2." ends 4.45px (a space) before the content edge.
        var ops = Layout("<ol style='margin:0;padding-left:60px;font:16px Arial'><li>x</li></ol>");
        var marker = ops.OfType<TextOp>().First(t => t.Text == "1.");
        float end = marker.X + marker.Font.Measure("1.", marker.Size);
        Near(45f - 3.336f, end, 0.02f);
    }

    // ---- Box shadows ----

    [Fact]
    public void Box_shadow_is_parsed()
    {
        var shadows = BoxShadow.Parse("2px 3px 4px 5px red, inset 0 0 1px #00f", 12, 12, 5, 8)!;
        Assert.Equal(2, shadows.Length);
        Near(1.5f, shadows[0].X);
        Near(2.25f, shadows[0].Y);
        Near(3f, shadows[0].Blur);
        Near(3.75f, shadows[0].Spread);
        Assert.Equal(Color("red"), shadows[0].Color);
        Assert.True(shadows[1].Inset);
        Assert.Empty(BoxShadow.Parse("none", 12, 12, 5, 8)!);
        Assert.Null(BoxShadow.Parse("2px", 12, 12, 5, 8));
        Assert.Null(BoxShadow.Parse("1px 2px -3px red", 12, 12, 5, 8));
    }

    [Fact]
    public void A_spread_shadow_frames_the_box()
    {
        var ops = Layout("<div style='width:100px;height:40px;box-shadow:0 0 0 2px red'></div>");
        var red = ops.OfType<RectOp>().Where(r => r.Color == Color("red")).ToList();
        Assert.Equal(4, red.Count); // drawn outside the box only
        Near(-1.5f, red.Min(r => r.X));
        Near(76.5f, red.Max(r => r.X + r.W));
        Near(-1.5f, red.Min(r => r.Y));
        Near(31.5f, red.Max(r => r.Y + r.H));
    }

    [Fact]
    public void A_blurred_shadow_fades_out()
    {
        var ops = Layout("<div style='width:100px;height:40px;box-shadow:0 0 8px black'></div>");
        var greys = ops.OfType<RectOp>().Select(r => r.Color & 0xFF).Distinct().OrderBy(v => v).ToList();
        Assert.True(greys.Count > 4);
        Assert.True(greys[^1] > 240); // the outer band is nearly white
    }

    [Fact]
    public void Inline_highlights_get_their_shadow()
    {
        var ops = Layout("<p style='font:12px Arial'>a <mark style='background:yellow;box-shadow:0 0 0 2px orange'>hit</mark> b</p>");
        Assert.Contains(ops.OfType<RectOp>(), r => r.Color == Color("orange"));
        Assert.Contains(ops.OfType<RectOp>(), r => r.Color == Color("yellow"));
    }

    // ---- Watermark and paper size ----

    [Fact]
    public void Watermark_letter_spacing_sets_the_character_spacing()
    {
        var spaced = ConvertUncompressed("<p>x</p>", new PdfOptions { Watermark = new PdfWatermark { Text = "KLADDE", LetterSpacing = 12, FontSize = 40 } });
        Assert.Contains("12 Tc", Latin1(spaced));
    }

    [Fact]
    public void A_scaled_page_is_written_at_chromes_paper_size()
    {
        var scaled = Latin1(ConvertUncompressed("<p>x</p>", new PdfOptions { Scale = 0.99916f }));
        Assert.Contains("/MediaBox [0 0 594.96 841.92]", scaled);
        var plain = Latin1(ConvertUncompressed("<p>x</p>"));
        Assert.Contains("/MediaBox [0 0 595.28 841.89]", plain);
        Assert.Equal(1191.12f, PdfDocumentWriter.PaperSize(1190.55f), 2); // A3 height, as Chrome writes it
    }
}
