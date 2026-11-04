using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// The @font-face descriptors size-adjust, ascent-override, descent-override and line-gap-override. The expected line
/// heights were measured in Chromium with the same font and HTML.
/// </summary>
public class FontFaceMetricsTests
{
    private static readonly byte[] Regular = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf"));
    private static readonly string Uri = "data:font/ttf;base64," + System.Convert.ToBase64String(Regular);
    private static readonly DateTime Fixed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Face(string family, string descriptors = "")
        => $"@font-face {{ font-family: {family}; src: url('{Uri}'); {descriptors} }}";

    private static byte[] Convert(string css, string body)
        => ConvertUncompressed($"<style>{css} body {{ margin: 0; font-size: 16px }} p {{ margin: 0; line-height: normal }}</style>{body}",
            new PdfOptions { CreationDate = Fixed });

    /// <summary>The font sizes of the text the page shows, in the order it shows them.</summary>
    private static List<float> FontSizes(byte[] pdf)
        => Regex.Matches(AllContent(pdf), @"/E\d+ ([\d.]+) Tf").Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    /// <summary>The baselines of the page's lines of text, in points from the top.</summary>
    private static List<double> Baselines(byte[] pdf) => PdfText.ExtractPages(pdf)[0].Lines.Select(l => l.Y).ToList();

    [Theory]
    [InlineData("150%", false, 1.5f)]
    [InlineData("0%", false, 0f)]
    [InlineData(" 87.5% ", false, 0.875f)]
    [InlineData("normal", true, float.NaN)]
    [InlineData("NORMAL", true, float.NaN)]
    [InlineData("1e400%", false, null)]
    [InlineData("-10%", false, null)]
    [InlineData("normal", false, null)]
    [InlineData("1.5", false, null)]
    [InlineData("%", false, null)]
    [InlineData("10px", true, null)]
    [InlineData("50% 50%", true, null)]
    public void ParsesPercentagesAndNormal(string value, bool allowNormal, float? expected)
        => Assert.Equal(expected, FontResolver.ParseFacePercent(value, allowNormal));

    [Fact]
    public void HugeSizeAdjustIsCapped() => Assert.Equal(100f, FontResolver.ParseFacePercent("1e30%", false));

    [Fact]
    public void SizeAdjustScalesWidthsAndEveryMetric()
    {
        var program = Assert.Single(FontProgram.Load(Regular));
        var plain = new EmbeddedFont(program, false, false);
        var big = new EmbeddedFont(program, false, false, faceMetrics: FaceMetrics.None with { SizeAdjust = 1.5f });
        Assert.Equal(1.5f, big.SizeAdjust);
        Assert.Equal(plain.Ascent * 1.5f, big.Ascent, 5);
        Assert.Equal(plain.Descent * 1.5f, big.Descent, 5);
        Assert.Equal(plain.LineGap * 1.5f, big.LineGap, 5);
        Assert.Equal(plain.Metric(FontMetric.XHeight) * 1.5f, big.Metric(FontMetric.XHeight), 5);
        Assert.Equal(plain.Metric(FontMetric.CapHeight) * 1.5f, big.Metric(FontMetric.CapHeight), 5);
        Assert.Equal(plain.CharWidth('W', 10) * 1.5f, big.CharWidth('W', 10), 4);
        int width = plain.MeasureUnits("Hamburgefonstiv", 0, 15);
        Assert.Equal((int)MathF.Round(width * 1.5f), big.MeasureUnits("Hamburgefonstiv", 0, 15));
        // Copies made for other settings keep the face's adjustment.
        Assert.Equal(1.5f, ((EmbeddedFont)big.WithoutKerning()).SizeAdjust);
        Assert.Equal(big.Ascent, big.WithoutLigatures().Ascent);
    }

    [Fact]
    public void OverridesReplaceTheLineMetricsAndSizeAdjustScalesThem()
    {
        var program = Assert.Single(FontProgram.Load(Regular));
        var overridden = new EmbeddedFont(program, false, false, faceMetrics: new FaceMetrics(1, 0.8f, 0.2f, 0.1f));
        Assert.Equal(0.8f, overridden.Ascent, 5);
        Assert.Equal(-0.2f, overridden.Descent, 5);
        Assert.Equal(0.1f, overridden.LineGap, 5);
        var both = new EmbeddedFont(program, false, false, faceMetrics: new FaceMetrics(0.5f, 0.8f, float.NaN, 0));
        var plain = new EmbeddedFont(program, false, false);
        Assert.Equal(0.4f, both.Ascent, 5);
        Assert.Equal(plain.Descent * 0.5f, both.Descent, 5); // normal keeps the font's own metric
        Assert.Equal(0f, both.LineGap);
    }

    [Fact]
    public void LineHeightsMatchChrome()
    {
        // Chrome at 16px: Work Sans 19px a line, 24px with size-adjust 130%, 16px with the overrides (13 + 3 + 0)
        // and 19px for size-adjust 75% with overrides (13 + 5 + 1), each part rounded to whole pixels.
        var css = Face("Plain") + Face("Big", "size-adjust: 130%;")
            + Face("Tight", "ascent-override: 80%; descent-override: 20%; line-gap-override: 0%;")
            + Face("Both", "size-adjust: 75%; ascent-override: 110%; descent-override: 40%; line-gap-override: 10%;");
        foreach (var (family, px) in new[] { ("Plain", 19), ("Big", 24), ("Tight", 16), ("Both", 19) })
        {
            var lines = Baselines(Convert(css, $"<p style='font-family: {family}'>Hx<br>Hx<br>Hx</p>"));
            Assert.Equal(3, lines.Count);
            Assert.Equal(px * 0.75, lines[1] - lines[0], 2);
            Assert.Equal(px * 0.75, lines[2] - lines[1], 2);
        }
    }

    [Fact]
    public void AscentOverrideMovesTheBaseline()
    {
        var css = Face("Plain") + Face("High", "ascent-override: 150%;");
        double plain = Baselines(Convert(css, "<p style='font-family: Plain'>Hx</p>"))[0];
        double high = Baselines(Convert(css, "<p style='font-family: High'>Hx</p>"))[0];
        Assert.True(high > plain + 5, $"{high} should be well below {plain}");
    }

    [Fact]
    public void SizeAdjustDrawsTheGlyphsAtTheirOwnSizeAndKeepsTheText()
    {
        var css = Face("Plain") + Face("Big", "size-adjust: 150%;");
        var plain = Convert(css, "<p style='font-family: Plain'>Hamburg</p>");
        var big = Convert(css, "<p style='font-family: Big'>Hamburg</p>");
        Assert.Equal([12f], FontSizes(plain));
        Assert.Equal([18f], FontSizes(big));
        var plainWord = PdfText.ExtractPages(plain)[0].Lines[0].Words[0];
        var bigWord = PdfText.ExtractPages(big)[0].Lines[0].Words[0];
        Assert.Equal("Hamburg", bigWord.Text);
        Assert.Equal(plainWord.Width * 1.5, bigWord.Width, 1);
    }

    [Fact]
    public void EmLengthsStayAndFontRelativeUnitsScale()
    {
        // The em is the font size, but ex, ch and cap are metrics of the font, which size-adjust scales.
        var css = Face("Plain") + Face("Big", "size-adjust: 150%;")
            + "i, b { display: block; height: 1px } i { width: 1em; background: red } b { width: 4ex; background: blue }";
        string Boxes(string family) => $"<div style='font-family: {family}'><i></i><b></b></div>";
        var plain = AllContent(Convert(css, Boxes("Plain")));
        var big = AllContent(Convert(css, Boxes("Big")));
        var rect = new Regex(@"[\d.]+ [\d.]+ ([\d.]+) [\d.]+ re");
        var plainWidths = rect.Matches(plain).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var bigWidths = rect.Matches(big).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(plainWidths[^2], bigWidths[^2], 2);
        Assert.Equal(plainWidths[^1] * 1.5, bigWidths[^1], 1);
    }

    [Fact]
    public void FontSizeAdjustAppliesAfterSizeAdjust()
    {
        // font-size-adjust holds the x-height at its value whatever size-adjust did to it, as in Chrome.
        var css = Face("Plain") + Face("Big", "size-adjust: 130%;") + "p { font-size-adjust: 0.5 }";
        float plain = Assert.Single(FontSizes(Convert(css, "<p style='font-family: Plain'>x</p>")));
        float big = Assert.Single(FontSizes(Convert(css, "<p style='font-family: Big'>x</p>")));
        Assert.Equal(plain, big, 1);
    }

    [Fact]
    public void EachFaceOfAUnicodeRangeSplitUsesItsOwnSizeAdjust()
    {
        var css = $"@font-face {{ font-family: Split; src: url('{Uri}'); unicode-range: U+0041-005A; size-adjust: 150%; }}"
            + $"@font-face {{ font-family: Split; src: url('{Uri}'); unicode-range: U+0020, U+0061-007A; }}";
        var sizes = FontSizes(Convert(css, "<p style='font-family: Split'>Ab Cd</p>"));
        Assert.Equal([18f, 12f, 18f, 12f], sizes);
    }

    [Fact]
    public void DescriptorsAtTheirInitialValuesChangeNothing()
    {
        var plain = Convert(Face("F"), "<p style='font-family: F'>Hamburg</p>");
        var initial = Convert(Face("F", "size-adjust: 100%; ascent-override: normal; descent-override: normal; line-gap-override: normal;"),
            "<p style='font-family: F'>Hamburg</p>");
        var invalid = Convert(Face("F", "size-adjust: -5%; ascent-override: 10px; descent-override: auto; line-gap-override: 1 2;"),
            "<p style='font-family: F'>Hamburg</p>");
        Assert.Equal(plain, initial);
        Assert.Equal(plain, invalid);
    }
}
