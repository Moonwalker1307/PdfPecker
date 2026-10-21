using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// The dashed, dotted, double and wavy text decorations, drawn as Chrome draws them: a thickness of a tenth of the font
/// size, whole-pixel dashes and dots with the gap spread to end the line on a dash, two whole-pixel lines for double and
/// a wave four thicknesses (and 3px) long. The numbers are Chrome's for a 20px font (15pt), in points; a pixel is 0.75pt.
/// </summary>
public class TextDecorationStyleTests
{
    private const float PageHeight = 800, Px = 0.75f;

    private static List<DrawOp> Layout(string decoration, string text = "alone at home")
    {
        var doc = HtmlParser.Parse($"<p style='font-size: 20px; margin: 0'><span style='text-decoration: {decoration}; text-decoration-skip-ink: none'>{text}</span></p>");
        new StyleResolver(new Stylesheet(), 400, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, 400);
        return engine.Ops;
    }

    [Fact]
    public void A_dashed_line_is_a_tenth_of_the_size_thick_with_three_thicknesses_long_dashes()
    {
        var line = Assert.Single(Layout("underline dashed").OfType<LineOp>());
        Assert.Equal(2 * Px, line.Width, 0.01f);
        Assert.Equal(6 * Px, line.DashLength, 0.01f);
        // The gap is close to two thicknesses, spread so that the line starts and ends on a dash.
        Assert.InRange(line.DashGap, 3 * Px, 5 * Px);
        float length = line.X2 - line.X1, dashes = (length + line.DashGap) / (line.DashLength + line.DashGap);
        Assert.Equal(MathF.Round(dashes), dashes, 0.01f);
    }

    [Fact]
    public void A_dotted_line_has_square_dots_as_far_apart_as_they_are_wide()
    {
        var line = Assert.Single(Layout("underline dotted").OfType<LineOp>());
        Assert.Equal(2 * Px, line.DashLength, 0.01f);
        Assert.Equal(2 * Px, line.DashGap, 0.1f);
        Assert.Equal(2 * Px, line.Width, 0.01f);
    }

    [Fact]
    public void Dash_lengths_are_whole_pixels()
    {
        // 14px: a thickness of 1.4px, drawn 1.4px thick with 1px dots, as in Chrome.
        var doc = HtmlParser.Parse("<p style='font-size: 14px; margin: 0'><span style='text-decoration: underline dotted'>alone at home</span></p>");
        new StyleResolver(new Stylesheet(), 400, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, 400);
        var line = Assert.Single(engine.Ops.OfType<LineOp>());
        Assert.Equal(1.4f * Px, line.Width, 0.01f);
        Assert.Equal(1 * Px, line.DashLength, 0.01f);
    }

    [Fact]
    public void A_double_line_is_two_whole_pixel_lines_a_pixel_apart_more_than_their_thickness()
    {
        var lines = Layout("underline double").OfType<RectOp>().Where(r => r.IsDecoration).OrderBy(r => r.Y).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(2 * Px, l.H, 0.01f));
        Assert.Equal(3 * Px, lines[1].Y - lines[0].Y, 0.01f);
    }

    [Fact]
    public void A_wave_is_four_thicknesses_and_3px_long_and_one_and_a_half_thicknesses_and_1_4px_high()
    {
        var segments = Layout("underline wavy").OfType<LineOp>().Where(l => l.Solid).ToList();
        Assert.NotEmpty(segments);
        Assert.All(segments, l => Assert.Equal(2 * Px, l.Width, 0.01f));
        var ys = segments.SelectMany(l => new[] { l.Y1, l.Y2 }).ToList();
        Assert.Equal((1.5f * 2 + 1.4f) * Px, ys.Max() - ys.Min(), 0.1f);
        // The crests (the highest points) of neighbouring waves are one wavelength apart.
        var points = segments.Select(l => (X: l.X1, Y: l.Y1)).Append((X: segments[^1].X2, Y: segments[^1].Y2)).ToList();
        var crests = Enumerable.Range(1, points.Count - 2).Where(k => points[k].Y < points[k - 1].Y && points[k].Y <= points[k + 1].Y)
            .Select(k => points[k].X).ToList();
        Assert.True(crests.Count >= 2);
        Assert.Equal((4 * 2 + 3) * Px, crests[1] - crests[0], 0.05f);
    }

    [Fact]
    public void A_solid_line_keeps_its_own_thickness()
    {
        var line = Assert.Single(Layout("underline").OfType<RectOp>(), r => r.IsDecoration);
        Assert.Equal(0.055f * 15, line.H, 0.01f);
    }
}
