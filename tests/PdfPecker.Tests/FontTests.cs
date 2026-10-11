using System.Text.RegularExpressions;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>TrueType/OpenType parsing, subsetting and embedding, @font-face and font fallback.</summary>
public class FontTests
{
    // Subsets of Work Sans and Inter (SIL Open Font License, see Fixtures/fonts).
    private static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name));

    private static readonly byte[] Regular = Font("WorkSans-Regular.ttf");
    private static readonly byte[] Bold = Font("WorkSans-Bold.ttf");
    private static readonly byte[] InterCff = Font("Inter-Subr.otf");

    private static string DataUri(byte[] font) => "data:font/ttf;base64," + Convert.ToBase64String(font);

    private static PdfOptions WithFonts(params byte[][] fonts)
    {
        var o = new PdfOptions { Compress = false };
        foreach (var f in fonts) o.Fonts.Add(f);
        return o;
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void ParsesTrueTypeMetadataAndCharacterMap()
    {
        var f = Assert.Single(FontProgram.Load(Regular));
        Assert.Equal("Work Sans", f.FamilyName);
        Assert.Equal(400, f.WeightClass);
        Assert.False(f.IsItalic);
        Assert.False(f.IsCff);
        Assert.NotEqual(0, f.GlyphId('Ł'));
        Assert.NotEqual(0, f.GlyphId('Ω'));
        Assert.Equal(0, f.GlyphId('中'));
        Assert.InRange(f.Advance1000(f.GlyphId('M')), 500, 1000);
        Assert.Equal(f.Advance1000(f.GlyphId('M')), f.CharWidths['M']);
    }

    [Fact]
    public void WoffMatchesTheTrueTypeFont()
    {
        var ttf = Assert.Single(FontProgram.Load(Regular));
        var woff = Assert.Single(FontProgram.Load(Font("WorkSans-Regular.woff")));
        Assert.Equal(ttf.NumGlyphs, woff.NumGlyphs);
        Assert.Equal(ttf.FullName, woff.FullName);
        foreach (char c in "Aéß€Ł")
        {
            Assert.Equal(ttf.GlyphId(c), woff.GlyphId(c));
            Assert.Equal(ttf.Advance(ttf.GlyphId(c)), woff.Advance(woff.GlyphId(c)));
        }
    }

    [Fact]
    public void CollectionsLoadEveryFont()
    {
        var fonts = FontProgram.Load(Font("WorkSans.ttc"));
        Assert.Equal(new[] { 400, 700 }, fonts.Select(f => f.WeightClass));
        var collection = new FontCollection().Add(Font("WorkSans.ttc"));
        Assert.Equal(2, collection.Count);
        Assert.Contains("Work Sans", collection.FamilyNames);
    }

    [Fact]
    public void RejectsDataThatIsNotAFont()
    {
        Assert.Throws<FormatException>(() => new FontCollection().Add(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));
        Assert.Throws<FormatException>(() => new FontCollection().Add(Regular.AsSpan(0, 200).ToArray()));
        var woff2 = new byte[64];
        "wOF2"u8.CopyTo(woff2);
        Assert.Throws<NotSupportedException>(() => new FontCollection().Add(woff2));
    }

    [Fact]
    public void UnicodeRangeParsesRangesAndWildcards()
    {
        var r = UnicodeRange.Parse("U+0000-00FF, U+0131, u+4??")!;
        Assert.True(r.Contains('A'));
        Assert.True(r.Contains(0x131));
        Assert.True(r.Contains(0x4AB));
        Assert.False(r.Contains(0x141));
        Assert.Null(UnicodeRange.Parse("nonsense"));
    }

    // ------------------------------------------------------------------ subsetting

    [Fact]
    public void TrueTypeSubsetKeepsOutlinesAndPullsInCompositeComponents()
    {
        var program = Assert.Single(FontProgram.Load(Regular));
        var subset = new GlyphSubset(program);
        const string text = "AÅéŁx";
        foreach (char c in text) subset.Cid(c);
        var file = FontSubsetter.Build(subset);
        Assert.True(file.Length < Regular.Length / 2);

        var original = TrueTypeFont.Parse(Regular)!;
        var sub = TrueTypeFont.Parse(file)!;
        Assert.True(sub.GlyphCount > text.Length + 1, "composite glyphs bring their components");
        foreach (char c in text)
        {
            int cid = subset.Cid(c);
            Assert.Equal(original.Advance(program.GlyphId(c)), sub.Advance(cid));
            AssertSamePath(original.GetGlyph(program.GlyphId(c)), sub.GetGlyph(cid));
        }
        // The subset is a font file in its own right.
        Assert.NotNull(Assert.Single(FontProgram.Load(file)));
    }

    [Fact]
    public void CffSubsetKeepsGlyphIdsAndTheSubroutinesTheyCall()
    {
        var program = Assert.Single(FontProgram.Load(InterCff));
        Assert.True(program.IsCff);
        var subset = new GlyphSubset(program);
        const string text = "Hamburgefonstiv ÆØÅ";
        foreach (char c in text) Assert.Equal(program.GlyphId(c), subset.Cid(c)); // CIDs are glyph ids

        var cff = program.Table("CFF ")!.Value;
        var originalCff = InterCff.AsSpan(cff.Offset, cff.Length).ToArray();
        var subsetCff = CffSubsetter.Subset(originalCff, subset.Glyphs);
        Assert.NotNull(subsetCff);
        Assert.True(subsetCff!.Length < originalCff.Length);

        var a = CffFont.Parse(originalCff)!;
        var b = CffFont.Parse(subsetCff)!;
        Assert.Equal(a.GlyphCount, b.GlyphCount);
        foreach (char c in text) AssertSamePath(a.GetGlyph(program.GlyphId(c)), b.GetGlyph(program.GlyphId(c)));
        var unused = b.GetGlyph(program.GlyphId('Z'));
        Assert.True(unused == null || unused.IsEmpty);
    }

    private static void AssertSamePath(PathData? expected, PathData? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Verbs, actual!.Verbs);
        Assert.Equal(expected.Points, actual.Points);
    }

    // ------------------------------------------------------------------ embedding

    [Fact]
    public void WithoutRegisteredFontsTheOutputUsesOnlyStandardFonts()
    {
        var text = Latin1(ConvertUncompressed("<p style='font-family: \"Work Sans\"'>Ærø Ł</p>"));
        Assert.DoesNotContain("/Type0", text);
        Assert.Contains("/BaseFont /Helvetica", text);
    }

    [Fact]
    public void RegisteredFontIsEmbeddedAsUnicodeSubset()
    {
        var pdf = HtmlToPdf.Convert("<p style='font-family: \"Work Sans\", sans-serif'>Łódź Ærø Ω</p>", WithFonts(Regular));
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Matches(@"/Subtype /Type0 /BaseFont /[A-Z]{6}\+WorkSans-Regular /Encoding /Identity-H", text);
        Assert.Contains("/Subtype /CIDFontType2", text);
        Assert.Contains("/CIDToGIDMap /Identity", text);
        Assert.Contains("/FontFile2", text);
        Assert.DoesNotContain("/BaseFont /Helvetica", text);

        var content = AllContent(pdf);
        Assert.Contains("/E1 ", content);
        Assert.Contains("<0141>", content); // ToUnicode: a CID maps back to Ł
        Assert.Contains("<03A9>", content);

        // The PDF renders with our own renderer, which reads the embedded font program.
        var renderer = PdfRenderer.Open(pdf);
        Assert.Equal(1, renderer.PageCount);
        Assert.NotEmpty(renderer.RenderPageToPng(0, new ImageOptions { Dpi = 36 }));
    }

    [Fact]
    public void FontFaceRuleWithDataUriIsUsed()
    {
        var html = $$"""
            <style>@font-face { font-family: Brand; src: url('{{DataUri(Regular)}}') format('truetype'); }
            p { font-family: Brand; }</style><p>Grüße aus Łódź</p>
            """;
        var text = Latin1(ConvertUncompressed(html));
        Assert.Contains("+WorkSans-Regular", text);
    }

    [Fact]
    public void FontFaceSkipsUnreadableSourcesAndFallsBackToStandardFonts()
    {
        var html = """
            <style>@font-face { font-family: Brand; src: url(missing.woff2) format('woff2'), url(missing.ttf), local('Nope'); }
            p { font-family: Brand, serif; }</style><p>Text</p>
            """;
        var text = Latin1(ConvertUncompressed(html, new PdfOptions { BaseDirectory = AppContext.BaseDirectory }));
        Assert.DoesNotContain("/Type0", text);
        Assert.Contains("/BaseFont /Times-Roman", text);
    }

    [Fact]
    public void BoldUsesTheBoldFaceAndItalicIsSynthesized()
    {
        var pdf = HtmlToPdf.Convert("<p style='font-family: Work Sans'>Regular <b>Bold</b> <i>Italic</i></p>", WithFonts(Regular, Bold));
        var text = Latin1(pdf);
        Assert.Contains("+WorkSans-Bold", text);
        Assert.Contains("+WorkSans-Regular", text);
        var content = AllContent(pdf);
        Assert.Contains("1 0 0.2 1 ", content);     // slanted text matrix for the missing italic
        Assert.DoesNotContain("2 Tr", content);     // the bold face exists, so no synthetic bold
    }

    [Fact]
    public void MissingBoldFaceIsSynthesizedByStroking()
    {
        var content = AllContent(HtmlToPdf.Convert("<p style='font-family: Work Sans'><b>Bold</b></p>", WithFonts(Regular)));
        Assert.Contains("2 Tr", content);
        Assert.Matches(@"[\d.]+ w\n", content);
    }

    [Fact]
    public void FontWeightPicksTheNearestFace()
    {
        var html = $$"""
            <style>
            @font-face { font-family: F; src: url('{{DataUri(Regular)}}'); font-weight: 400; }
            @font-face { font-family: F; src: url('{{DataUri(Bold)}}'); font-weight: 700; }
            .a { font-family: F; font-weight: 300; } .b { font-family: F; font-weight: 600; }
            </style><p class=a>light</p>
            """;
        Assert.Contains("+WorkSans-Regular", Latin1(ConvertUncompressed(html)));
        Assert.Contains("+WorkSans-Bold", Latin1(ConvertUncompressed(html.Replace("class=a", "class=b"))));
        // 600 has a real face at 700, so it is not synthesized.
        Assert.DoesNotContain("2 Tr", AllContent(ConvertUncompressed(html.Replace("class=a", "class=b"))));
    }

    [Fact]
    public void CharactersMissingFromTheStandardFontFallBackToRegisteredFonts()
    {
        var pdf = HtmlToPdf.Convert("<p style='font-family: Helvetica'>Omega Ω and ✓</p>", WithFonts(Regular));
        var text = Latin1(pdf);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Contains("+WorkSans-Regular", text);
        var content = AllContent(pdf);
        Assert.Contains("(Omega ) Tj", content);
        Assert.Contains("/E1 ", content);
    }

    [Fact]
    public void UnicodeRangeSplitsAFamilyOverSeveralFiles()
    {
        var html = $$"""
            <style>
            @font-face { font-family: F; src: url('{{DataUri(Regular)}}'); unicode-range: U+0000-00FF; }
            @font-face { font-family: F; src: url('{{DataUri(InterCff)}}'); unicode-range: U+0100-024F; }
            p { font-family: F; }
            </style><p>Łx</p>
            """;
        var pdf = ConvertUncompressed(html);
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("+WorkSans-Regular", text);
        Assert.Contains("+Inter-Regular", text);
        Assert.Contains("/Subtype /CIDFontType0", text);
        Assert.Contains("/FontFile3", text);
        Assert.Contains("/Subtype /OpenType", text);
    }

    [Fact]
    public void JustifiedTextMovesSpacesExplicitly()
    {
        var words = string.Join(" ", Enumerable.Repeat("Łódź gør", 40));
        var content = AllContent(HtmlToPdf.Convert($"<p style='font-family: Work Sans; text-align: justify'>{words}</p>", WithFonts(Regular)));
        Assert.Matches(new Regex(@"\[<[0-9A-F]+>-?[\d.]+<[0-9A-F]+>.*\] TJ"), content);
    }

    [Fact]
    public void LineHeightNormalComesFromTheFontMetrics()
    {
        var font = new EmbeddedFont(Assert.Single(FontProgram.Load(Regular)), false, false);
        Assert.InRange(font.NormalLineHeight, 1.0f, 1.5f);
        Assert.Equal(1.2f, StandardFont.Get(PdfPecker.Fonts.FontFamily.Helvetica, false, false).NormalLineHeight);
    }

    [Fact]
    public void FontCollectionIsSafeToShareAcrossParallelConversions()
    {
        var options = WithFonts(Regular, Bold);
        var results = new byte[16][];
        Parallel.For(0, results.Length, i => results[i] = HtmlToPdf.Convert($"<p style='font-family: Work Sans'>Doc {i % 2} Łódź <b>ß</b></p>", options));
        static string WithoutDate(byte[] pdf) => Regex.Replace(Latin1(pdf), @"/(CreationDate|ModDate) \(D:\d+Z\)|/ID \[<[0-9A-F]+><[0-9A-F]+>\]", "");
        for (int i = 2; i < results.Length; i++) Assert.Equal(WithoutDate(results[i % 2]), WithoutDate(results[i]));
    }
}
