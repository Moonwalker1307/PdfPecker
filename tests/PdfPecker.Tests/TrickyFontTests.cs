using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Tests;

/// <summary>
/// The "tricky" TrueType fonts (MingLiU, PMingLiU, DFKai-SB and other older CJK fonts), whose composite glyphs are
/// only right after their instructions have run. The fixtures come from tools/TrickyFonts/generate.py.
/// </summary>
public class TrickyFontTests
{
    private static TrueTypeFont Font(string name)
        => TrueTypeFont.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name)))!;

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(PathData path)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < path.Points.Count; i += 2)
        {
            minX = Math.Min(minX, path.Points[i]);
            maxX = Math.Max(maxX, path.Points[i]);
            minY = Math.Min(minY, path.Points[i + 1]);
            maxY = Math.Max(maxY, path.Points[i + 1]);
        }
        return (minX, minY, maxX, maxY);
    }

    [Fact]
    public void Glyph_programs_place_the_strokes_of_tricky_fonts()
    {
        // "A" puts its stroke at 500, 500 and its glyph program moves it back to the origin, as FreeType draws it.
        var font = Font("Tricky.ttf");
        Assert.True(font.IsTricky);
        var a = font.GetGlyph(font.GetCmap(3, 1)!['A'])!;
        Assert.Equal((0.0, 0.0, 100.0, 100.0), Bounds(a));
    }

    [Fact]
    public void Point_matched_components_land_on_their_anchor_point()
    {
        // The second square's point 0 lands on point 2 of the first, its top right corner.
        var font = Font("Tricky.ttf");
        var b = font.GetGlyph(font.GetCmap(3, 1)!['B'])!;
        Assert.Equal((0.0, 0.0, 200.0, 200.0), Bounds(b));
    }

    [Theory]
    [InlineData("MingLiU", true)]
    [InlineData("PMingLiU", true)]
    [InlineData("ABCDEF+PMingLiU", true)]
    [InlineData("KGDFLP+DFKaiShu-SB-Estd-BF", true)]
    [InlineData("DFKai-SB", true)]
    [InlineData("MingLiU_HKSCS", true)]
    [InlineData("Arial", false)]
    [InlineData("NotoSansTC-Regular", false)]
    [InlineData("XMingLiU", false)]
    public void Tricky_fonts_are_known_by_name(string name, bool tricky)
        => Assert.Equal(tricky, TrueTypeFont.IsTrickyName(name));

    [Fact]
    public void Other_fonts_are_drawn_without_their_instructions()
    {
        var font = TrueTypeFont.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf")))!;
        Assert.False(font.IsTricky);
        font.CheckTrickyName("WorkSans-Regular");
        Assert.False(font.IsTricky);
    }
}
