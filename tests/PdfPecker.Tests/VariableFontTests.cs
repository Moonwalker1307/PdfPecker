using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Variable fonts: static instances for font-weight, font-stretch, italic, optical size and font-variation-settings.
/// The expected advances come from fontTools' instancer (varLib.instancer) on the same fonts.
/// </summary>
public class VariableFontTests
{
    // Subsets of Inter (wght 100-900), Roboto Flex (opsz, wght, wdth, slnt) and Source Sans 3; SIL Open Font License,
    // see Fixtures/fonts.
    private static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));
    private static readonly byte[] Inter = Font("Inter-Variable-Subset.ttf");
    private static readonly byte[] Flex = Font("RobotoFlex-Subset.ttf");
    private static readonly byte[] Cff2 = Font("VarTestCFF-Subset.otf"); // Source Sans 3, renamed: wght 200-900, CFF2 outlines

    private static string DataUri(byte[] font) => "data:font/ttf;base64," + Convert.ToBase64String(font);

    private static Dictionary<uint, float> At(params (string Axis, float Value)[] values)
        => values.ToDictionary(v => FontProgram.Tag(v.Axis), v => v.Value);

    [Fact]
    public void Reads_the_axes()
    {
        var inter = FontProgram.Load(Inter)[0];
        var axis = Assert.Single(inter.Axes);
        Assert.Equal("wght", axis.Name);
        Assert.Equal((100f, 400f, 900f), (axis.Min, axis.Default, axis.Max));
        var flex = FontProgram.Load(Flex)[0];
        Assert.Equal(new[] { "opsz", "wght", "wdth", "slnt" }, flex.Axes.Select(a => a.Name));
        Assert.Empty(FontProgram.Load(Font("WorkSans-Regular.ttf"))[0].Axes);
    }

    [Theory]
    [InlineData(400, 1522, 1210, 1292)]
    [InlineData(700, 1530, 1275, 1381)]
    [InlineData(900, 1536, 1330, 1456)]
    public void Weight_instances_have_the_advances_fontTools_gives(int weight, int h, int n, int zero)
    {
        var p = FontProgram.Load(Inter)[0].Instance(At(("wght", weight)));
        Assert.InRange(p.Advance(p.GlyphId('H')), h - 1, h + 1);
        Assert.InRange(p.Advance(p.GlyphId('n')), n - 1, n + 1);
        Assert.InRange(p.Advance(p.GlyphId('0')), zero - 1, zero + 1);
        if (weight != 400) Assert.Empty(p.Axes); // an instance is a static font; 400 is the font itself
    }

    [Theory]
    [InlineData(25, 1302, 1011)]
    [InlineData(151, 1617, 1310)]
    public void Width_instances_have_the_advances_fontTools_gives(float width, int h, int n)
    {
        var p = FontProgram.Load(Flex)[0].Instance(At(("wdth", width)));
        Assert.InRange(p.Advance(p.GlyphId('H')), h - 1, h + 1);
        Assert.InRange(p.Advance(p.GlyphId('n')), n - 1, n + 1);
    }

    [Fact]
    public void The_default_and_repeated_instances_are_shared()
    {
        var p = FontProgram.Load(Inter)[0];
        Assert.Same(p, p.Instance(At(("wght", 400))));
        Assert.Same(p, p.Instance(At(("wdth", 50)))); // not an axis of this font
        Assert.Same(p.Instance(At(("wght", 700))), p.Instance(At(("wght", 700))));
        Assert.Same(p.Instance(At(("wght", 950))), p.Instance(At(("wght", 900)))); // clamped to the axis
    }

    [Fact]
    public void Instances_embed_as_valid_fonts_that_round_trip()
    {
        var p = FontProgram.Load(Inter)[0].Instance(At(("wght", 800)));
        var again = FontProgram.Load(p.Data)[0];
        Assert.Equal(p.NumGlyphs, again.NumGlyphs);
        Assert.Equal(p.Advance(p.GlyphId('Å')), again.Advance(again.GlyphId('Å')));
        Assert.Equal(800, again.WeightClass);
        Assert.Null(again.Table("gvar"));
        Assert.EndsWith("-wght800", p.PostScriptName);
    }

    [Fact]
    public void Font_weight_picks_real_weights_instead_of_synthetic_bold()
    {
        var pdf = ConvertUncompressed($$"""
            <style>@font-face { font-family: Inter; src: url('{{DataUri(Inter)}}'); font-weight: 100 900; }</style>
            <p style='font-family: Inter'>Regular <b>Bold</b> <span style='font-weight: 200'>Thin</span></p>
            """);
        var text = Latin1(pdf);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+Inter[^/\s]*-wght700\b", text);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+Inter[^/\s]*-wght200\b", text);
        Assert.DoesNotContain("2 Tr", AllContent(pdf));
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void A_face_without_a_weight_descriptor_covers_the_weight_axis()
    {
        var pdf = ConvertUncompressed($$"""
            <style>@font-face { font-family: Inter; src: url('{{DataUri(Inter)}}'); }</style><p style='font-family: Inter'><b>Bold</b></p>
            """);
        Assert.Matches(@"\+Inter[^/\s]*-wght700\b", Latin1(pdf));
        Assert.DoesNotContain("2 Tr", AllContent(pdf));
    }

    [Fact]
    public void A_face_declared_with_one_weight_stays_there_and_synthesizes_bold()
    {
        var pdf = ConvertUncompressed($$"""
            <style>@font-face { font-family: Inter; src: url('{{DataUri(Inter)}}'); font-weight: 400; }</style><p style='font-family: Inter'><b>Bold</b></p>
            """);
        Assert.DoesNotMatch(@"-wght\d", Latin1(pdf));
        Assert.Contains("2 Tr", AllContent(pdf));
    }

    [Fact]
    public void Registered_variable_fonts_cover_their_weights()
    {
        var options = new PdfOptions { Compress = false };
        options.Fonts.Add(Inter);
        var pdf = HtmlToPdf.Convert("<p style='font-family: Inter'>a <b>b</b> <span style='font-weight:900'>c</span></p>", options);
        var text = Latin1(pdf);
        Assert.Matches(@"-wght700\b", text);
        Assert.Matches(@"-wght900\b", text);
        Assert.DoesNotContain("2 Tr", AllContent(pdf));
    }

    [Fact]
    public void Font_stretch_italic_optical_size_and_variation_settings_pick_instances()
    {
        var pdf = Latin1(ConvertUncompressed($$"""
            <style>@font-face { font-family: Flex; src: url('{{DataUri(Flex)}}'); font-weight: 100 1000; font-stretch: 25% 151%; }
            p { font-family: Flex; font-size: 10.5pt }</style>
            <p style='font-stretch: condensed'>narrow</p>
            <p style='font-style: italic'>slanted</p>
            <p style='font-size: 36pt'>big</p>
            <p style='font-variation-settings: "wght" 650, "wdth" 110'>set</p>
            <p style='font-optical-sizing: none; font-size: 36pt; font-weight: 500'>fixed</p>
            """));
        Assert.Matches(@"\+RobotoFlex[^/\s]*-wdth75\b", pdf);
        Assert.Matches(@"\+RobotoFlex[^/\s]*-slnt-10\b", pdf);
        Assert.Matches(@"\+RobotoFlex[^/\s]*-opsz48\b", pdf);          // 36pt is 48px
        Assert.Matches(@"\+RobotoFlex[^/\s]*-wght650-wdth110\b", pdf); // opsz 14 (10.5pt) is the default
        Assert.Matches(@"\+RobotoFlex[^/\s]*-wght500\b", pdf);
        Assert.DoesNotMatch(@"-wght500-opsz|opsz48-wght500", pdf);
    }

    [Fact]
    public void Italic_from_a_slant_axis_is_not_slanted_again()
    {
        var content = AllContent(ConvertUncompressed($$"""
            <style>@font-face { font-family: Flex; src: url('{{DataUri(Flex)}}'); }</style><p style='font-family: Flex'><i>slanted</i></p>
            """));
        Assert.DoesNotContain("1 0 0.2 1 ", content);
    }

    [Fact]
    public void Wider_instances_make_wider_text()
    {
        float Width(string stretch)
        {
            var pdf = ConvertUncompressed($$"""
                <style>@font-face { font-family: Flex; src: url('{{DataUri(Flex)}}'); font-stretch: 25% 151%; }</style>
                <p style='font-family: Flex; font-stretch: {{stretch}}'><span style='background:#ff0000'>Hamburgefonstiv</span></p>
                """);
            var m = Regex.Match(AllContent(pdf), @"1 0 0 rg\n[\d.]+ [\d.]+ ([\d.]+) [\d.]+ re f");
            Assert.True(m.Success);
            return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        float condensed = Width("50%"), normal = Width("100%"), expanded = Width("150%");
        Assert.True(condensed < normal * 0.92f, $"{condensed} vs {normal}");
        Assert.True(expanded > normal * 1.05f, $"{expanded} vs {normal}");
    }

    [Fact]
    public void Damaged_variation_data_falls_back_to_the_default_instance()
    {
        var damaged = (byte[])Inter.Clone();
        var p = FontProgram.Load(damaged)[0];
        var gvar = p.Table("gvar")!.Value;
        // Point the glyph variation data far past the table.
        for (int i = gvar.Offset + 16; i < gvar.Offset + 20; i++) damaged[i] = 0x7F;
        var broken = FontProgram.Load(damaged)[0];
        var instance = broken.Instance(At(("wght", 700)));
        Assert.Same(broken, instance);
        var pdf = ConvertUncompressed($$"""<style>@font-face { font-family: X; src: url('{{DataUri(damaged)}}'); }</style><p style='font-family: X'><b>b</b></p>""");
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Pdfa_documents_embed_variable_font_instances()
    {
        var pdf = HtmlToPdf.Convert($$"""
            <style>@font-face { font-family: Inter; src: url('{{DataUri(Inter)}}'); }</style><p style='font-family: Inter'>a <b>b</b></p>
            """, new PdfOptions { Conformance = PdfConformance.PdfA2b, Compress = false });
        Assert.Matches(@"/FontFile2 \d+ 0 R", Latin1(pdf));
        Assert.Matches(@"-wght700\b", Latin1(pdf));
    }

    [Theory]
    [InlineData(200, 634, 526, 520)]
    [InlineData(900, 682, 582, 584)]
    public void Cff2_instances_become_cff_fonts_with_the_advances_fontTools_gives(int weight, int h, int n, int a)
    {
        var p = FontProgram.Load(Cff2)[0].Instance(At(("wght", weight)));
        Assert.NotNull(p.Table("CFF "));
        Assert.Null(p.Table("CFF2"));
        Assert.Empty(p.Axes);
        Assert.Equal(1, p.Data[p.Table("CFF ")!.Value.Offset]); // CFF version 1
        Assert.InRange(p.Advance(p.GlyphId('H')), h - 1, h + 1);
        Assert.InRange(p.Advance(p.GlyphId('n')), n - 1, n + 1);
        Assert.InRange(p.Advance(p.GlyphId('A')), a - 1, a + 1);
    }

    [Fact]
    public void Cff2_fonts_embed_as_cff_and_render()
    {
        var pdf = ConvertUncompressed($$"""
            <style>@font-face { font-family: V; src: url('{{DataUri(Cff2)}}'); }</style><p style='font-family: V'>Hen <b>Hen</b></p>
            """);
        var text = Latin1(pdf);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+VarTestCFF[^/\s]*-wght700\b", text);
        Assert.Contains("/Subtype /OpenType", text);
        Assert.DoesNotContain("2 Tr", AllContent(pdf));
        AssertStructurallyValid(pdf);
        // The renderer reads the converted outlines: the bold word has more ink than the regular one.
        var png = PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 100 });
        Assert.True(png.Length > 1000);
    }

    [Theory]
    [InlineData("Inter-Variable-Subset.ttf", 900, "AV", -180, -140)]   // values from fontTools' instancer
    [InlineData("Inter-Variable-Subset.ttf", 900, "Yo", -282, -157)]
    [InlineData("VarTestCFF-Subset.otf", 900, "r.", -100, -32)]
    [InlineData("VarTestCFF-Subset.otf", 300, "P.", -100, -96)]
    public void Kerning_follows_the_instance(string font, int weight, string pair, int expected, int atDefault)
    {
        var p = FontProgram.Load(Font(font))[0];
        int Kern(FontProgram f) => f.Kerning!.Pair(f.GlyphId(pair[0]), f.GlyphId(pair[1]));
        var instance = p.Instance(At(("wght", weight)));
        Assert.Equal(expected, Kern(instance));
        Assert.Equal(atDefault, Kern(p));
    }
}
