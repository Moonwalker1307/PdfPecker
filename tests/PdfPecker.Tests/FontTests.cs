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

    [Theory]
    [InlineData("WorkSans-Regular.woff2")]       // glyf, loca and hmtx transformed
    [InlineData("WorkSans-Regular-plain.woff2")] // no transforms
    public void Woff2MatchesTheTrueTypeFont(string file)
    {
        var ttf = Assert.Single(FontProgram.Load(Regular));
        var woff2 = Assert.Single(FontProgram.Load(Font(file)));
        AssertSameTrueTypeFont(ttf, woff2);
    }

    [Fact]
    public void Woff2CffFontKeepsItsCharStrings()
    {
        var otf = Assert.Single(FontProgram.Load(InterCff));
        var woff2 = Assert.Single(FontProgram.Load(Font("Inter-Subr.woff2")));
        Assert.True(woff2.IsCff);
        Assert.Equal(otf.FullName, woff2.FullName);
        var a = otf.Table("CFF ")!.Value;
        var b = woff2.Table("CFF ")!.Value;
        Assert.True(otf.Data.AsSpan(a.Offset, a.Length).SequenceEqual(woff2.Data.AsSpan(b.Offset, b.Length)));
        foreach (char c in "Hamburgefonstiv ÆØÅ") Assert.Equal(otf.Advance(otf.GlyphId(c)), woff2.Advance(woff2.GlyphId(c)));
    }

    [Fact]
    public void Woff2CollectionsLoadEveryFont()
    {
        var ttc = FontProgram.Load(Font("WorkSans.ttc"));
        var woff2 = FontProgram.Load(Font("WorkSans.woff2"));
        Assert.Equal(new[] { 400, 700 }, woff2.Select(f => f.WeightClass));
        for (int i = 0; i < 2; i++) AssertSameTrueTypeFont(ttc[i], woff2[i]);
        Assert.Equal(2, new FontCollection().Add(Font("WorkSans.woff2")).Count);
    }

    private static void AssertSameTrueTypeFont(FontProgram expected, FontProgram actual)
    {
        Assert.Equal(expected.NumGlyphs, actual.NumGlyphs);
        Assert.Equal(expected.FullName, actual.FullName);
        Assert.Equal((expected.XMin, expected.YMin, expected.XMax, expected.YMax), (actual.XMin, actual.YMin, actual.XMax, actual.YMax));
        foreach (char c in "Aéß€Ł") Assert.Equal(expected.GlyphId(c), actual.GlyphId(c));
        // Compare outlines as standalone fonts: a collection's FontProgram holds the whole file.
        static byte[] Sfnt(FontProgram p) => SfntBuilder.Build(0x00010000, p.Tables.Select(t => (t.Tag, p.Data.AsSpan(t.Offset, t.Length).ToArray())).ToList());
        var a = TrueTypeFont.Parse(Sfnt(expected))!;
        var b = TrueTypeFont.Parse(Sfnt(actual))!;
        for (int g = 0; g < expected.NumGlyphs; g++)
        {
            Assert.Equal(expected.Advance(g), actual.Advance(g));
            Assert.Equal(expected.LeftSideBearing(g), actual.LeftSideBearing(g));
            var pa = a.GetGlyph(g);
            var pb = b.GetGlyph(g);
            Assert.Equal(pa?.Verbs, pb?.Verbs);
            Assert.Equal(pa?.Points, pb?.Points);
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
        Assert.Throws<FormatException>(() => new FontCollection().Add(woff2));
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
        var text = Latin1(ConvertUncompressed("<p style='font-family: \"Work Sans\"'>Ærø – “quoted” €</p>"));
        Assert.DoesNotContain("/Type0", text);
        Assert.Contains("/BaseFont /Helvetica", text);
    }

    [Fact]
    public void LettersTheStandardFontsLackComeFromTheBundledLiberationFont()
    {
        var text = Latin1(ConvertUncompressed("<p style='font-family: Arial'>Łódź Ωμέγα Москва</p><p style='font: bold 12px serif'>Ł</p>"));
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+LiberationSans /", text);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+LiberationSerif-Bold /", text);
        Assert.DoesNotContain("PdfPeckerSymbols", text);
    }

    [Theory]
    [InlineData("<p>Done ✓ ★ ✈</p>")]
    [InlineData("<p>Done &#x2713; &starf;</p>")]
    [InlineData("<style>li::before { content: '\\2713  ' }</style><ul><li>Done</li></ul>")]
    [InlineData("<p title='✓'>Done</p><style>p::after { content: attr(title) }</style>")]
    public void SymbolsNoOtherFontHasComeFromTheBundledSymbolFont(string html)
    {
        var pdf = ConvertUncompressed(html);
        AssertStructurallyValid(pdf);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+PdfPeckerSymbols-Regular /", Latin1(pdf));
        Assert.Contains("<2713>", AllContent(pdf)); // ToUnicode maps the glyph back to the check mark
    }

    [Fact]
    public void SymbolFontIsTheLastFallbackAfterTheDocumentsFonts()
    {
        // Work Sans has no check mark, so it falls through to the symbol font, made bold like the text.
        var pdf = HtmlToPdf.Convert("<p style='font: bold 12px \"Work Sans\"'>Done ✓</p>", WithFonts(Regular, Bold));
        var text = Latin1(pdf);
        Assert.Contains("WorkSans-Bold", text);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+PdfPeckerSymbols-Regular /", text);
    }

    [Fact]
    public void SymbolsInHeadersAndFootersUseTheSymbolFont()
    {
        var pdf = ConvertUncompressed("<p>Body</p>", new PdfOptions { FooterText = "✓ Approved", Compress = false });
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+PdfPeckerSymbols-Regular /", Latin1(pdf));
    }

    [Fact]
    public void BundledSymbolFontIsRenamedAndKeepsItsLicense()
    {
        var font = BundledFonts.Symbols(false, false)!.Program;
        Assert.Equal("PdfPecker Symbols", font.FamilyName);
        Assert.Equal("PdfPeckerSymbols-Regular", font.PostScriptName);
        foreach (var name in new[] { font.FamilyName, font.FullName, font.PostScriptName })
        {
            Assert.DoesNotContain("Vera", name);
            Assert.DoesNotContain("Bitstream", name);
            Assert.DoesNotContain("DejaVu", name);
        }
        // The copyright and license records stay, as the Bitstream Vera license asks.
        Assert.True(font.Data.AsSpan().IndexOf(System.Text.Encoding.BigEndianUnicode.GetBytes("Copyright (c) 2003 by Bitstream, Inc.")) >= 0);
        Assert.True(font.Data.AsSpan().IndexOf(System.Text.Encoding.BigEndianUnicode.GetBytes("Bitstream Vera Fonts Copyright")) >= 0);
        foreach (int cp in new[] { 0x2713, 0x2605, 0x25B8, 0x2708, 0x2139, 0x26A0, 0x2192, 0x2212 })
            Assert.NotEqual(0, font.GlyphId(cp));
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
    public void FontFaceRuleWithWoff2DataUriIsEmbedded()
    {
        var uri = "data:font/woff2;base64," + Convert.ToBase64String(Font("WorkSans-Regular.woff2"));
        var html = $$"""
            <style>@font-face { font-family: Brand; src: url('{{uri}}') format('woff2'); }
            p { font-family: Brand; }</style><p>Grüße aus Łódź</p>
            """;
        var pdf = ConvertUncompressed(html);
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Contains("+WorkSans-Regular", text);
        Assert.Contains("/FontFile2", text);
        Assert.NotEmpty(PdfRenderer.Open(pdf).RenderPageToPng(0, new ImageOptions { Dpi = 36 }));
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
        Assert.InRange(font.NormalLineHeight(100), 100f, 150f);
        // Arial/Helvetica at 12px (9pt): ascent 10.86px -> 11, descent 2.54px -> 3, gap 0.39px -> 0, as in Chrome.
        var helvetica = StandardFont.Get(PdfPecker.Fonts.FontFamily.Helvetica, false, false);
        Assert.Equal(10.5f, helvetica.NormalLineHeight(9f), 3);
        Assert.Equal(13.5f, helvetica.NormalLineHeight(12f), 3);
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

    [Fact]
    public void PdfANeverShowsTheNotdefGlyph()
    {
        const string html = "<p style='font-family: Work Sans'>A中B</p>";
        Assert.Contains("<000100000002> Tj", AllContent(HtmlToPdf.Convert(html, WithFonts(Regular))));
        var options = WithFonts(Regular);
        options.Conformance = PdfConformance.PdfA2b;
        Assert.Matches(@"\[<0001>-[\d.]+<0002>\] TJ", AllContent(HtmlToPdf.Convert(html, options)));
    }

    // ------------------------------------------------------------------ bundled fonts

    private const string StandardFontsHtml = """
        <html><body>
        <h1>Heading</h1>
        <p style="font-family: Arial">Sans <b>bold</b> <i>italic</i> text, long enough to wrap a few times across the page width for sure.</p>
        <p style="font-family: Times New Roman">Serif <b><i>bold italic</i></b></p>
        <pre>Mono 0123</pre>
        <svg width="100" height="20"><text x="0" y="15" font-family="serif">SVG</text></svg>
        </body></html>
        """;

    [Fact]
    public void PdfAEmbedsTheBundledFontsWhenNoneAreRegistered()
    {
        var pdf = Latin1(HtmlToPdf.Convert(StandardFontsHtml, new PdfOptions { Compress = false, Conformance = PdfConformance.PdfA2b }));
        Assert.DoesNotContain("/Subtype /Type1", pdf);
        Assert.DoesNotContain("/BaseFont /Helvetica", pdf);
        foreach (var name in new[] { "LiberationSans", "LiberationSans-Bold", "LiberationSans-Italic", "LiberationSerif", "LiberationSerif-BoldItalic", "LiberationMono" })
            Assert.Matches(@"/BaseFont /[A-Z]{6}\+" + name + @"\b", pdf);
        Assert.Contains("/FontFile2", pdf);
    }

    [Fact]
    public void SkipInkFindsTheSameGlyphsAsTheEmbeddedBundledFonts()
    {
        // Underlines of standard-font text skip the ink of the bundled fonts' outlines, looked up in their own character
        // maps rather than through the full embeddable font, so the two must agree on every character.
        foreach (var sf in StandardFont.All)
        {
            var program = BundledFonts.Get(sf)!.Program;
            var trueType = TrueTypeFont.Parse(BundledFonts.Data(sf)!)!;
            for (int c = 0; c <= char.MaxValue; c++)
                if (program.GlyphId(c) != trueType.Lookup(3, 1, c)) Assert.Fail($"Font {sf.Index}, U+{c:X4}");
        }
    }

    [Fact]
    public void PdfAEmbedsTheWatermarkAndFooterFonts()
    {
        var options = new PdfOptions
        {
            Compress = false, Conformance = PdfConformance.PdfA2b, FooterText = "Page {page} of {total-pages}",
            Watermark = new PdfWatermark { Text = "DRAFT", Bold = true },
        };
        var pdf = Latin1(HtmlToPdf.Convert("<p>Body</p>", options));
        Assert.DoesNotContain("/Subtype /Type1", pdf);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+LiberationSans-Bold\b", pdf);
    }

    [Fact]
    public void EmbeddingStandardFontsKeepsTheLayout()
    {
        static string Positions(string content) => string.Join("\n", Regex.Matches(content, @"[\d.-]+ [\d.-]+ Td|[\d.-]+ [\d.-]+ [\d.-]+ [\d.-]+ [\d.-]+ [\d.-]+ Tm").Select(m => m.Value));
        var plain = HtmlToPdf.Convert(StandardFontsHtml, new PdfOptions { Compress = false });
        var embedded = HtmlToPdf.Convert(StandardFontsHtml, new PdfOptions { Compress = false, EmbedStandardFonts = true });
        Assert.Contains("/Subtype /Type1", Latin1(plain));
        Assert.DoesNotContain("/Subtype /Type1", Latin1(embedded));
        Assert.Equal(PageCount(plain), PageCount(embedded));
        Assert.Equal(Positions(AllContent(plain)), Positions(AllContent(embedded)));
    }

    [Fact]
    public void EmbeddedStandardFontsDrawCharactersOutsideWinAnsi()
    {
        var pdf = HtmlToPdf.Convert("<p>Łódź ΩΣ ₹</p>", new PdfOptions { Compress = false, EmbedStandardFonts = true });
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+LiberationSerif\b", text);
        Assert.DoesNotContain("/Subtype /Type1", text);
        // The ToUnicode map carries the characters, so they can be extracted and searched.
        Assert.Contains("<0141>", text);
        Assert.Contains("<03A9>", text);
    }

    [Fact]
    public void DefaultOutputStillUsesTheStandardFonts()
    {
        var text = Latin1(HtmlToPdf.Convert("<p>Hello</p>", new PdfOptions { Compress = false }));
        Assert.Contains("/BaseFont /Times-Roman", text);
        Assert.DoesNotContain("/FontFile2", text);
    }

    // ------------------------------------------------------------------ missing fonts and system fonts

    private static List<string> MissingFonts(string html, PdfOptions? options = null)
    {
        var missing = new List<string>();
        options ??= new PdfOptions();
        options.MissingFont = missing.Add;
        HtmlToPdf.Convert(html, options);
        return missing;
    }

    [Fact]
    public void ReportsFamiliesThatFallBackToStandardFonts()
    {
        var missing = MissingFonts("""
            <p style='font-family: "Open Sans", Sans-serif, Serif'>a</p>
            <p style='font-family: "open sans", Arial'>b</p>
            <p style='font-family: Verdana, "Segoe UI", sans-serif'>c</p>
            <p style='font-family: serif'>d</p>
            """);
        Assert.Equal(new[] { "Open Sans", "Verdana", "Segoe UI" }, missing);
    }

    [Fact]
    public void DoesNotReportRegisteredFontsOrFamiliesAfterAFoundOne()
    {
        var options = WithFonts(Regular);
        var missing = MissingFonts("""
            <p style='font-family: "Work Sans", "Open Sans"'>a</p>
            <p style='font-family: Courier, "Open Sans"'>b</p>
            <p style='font-family: Lato, "Work Sans"'>c</p>
            """, options);
        Assert.Equal(new[] { "Lato" }, missing);
    }

    [Fact]
    public void ReportsFontFaceRulesWhoseFontCannotBeLoaded()
    {
        var missing = MissingFonts("""
            <style>@font-face { font-family: Brand; src: url(missing.ttf) } @font-face { font-family: Ok; src: url(ok.ttf) }</style>
            <p style='font-family: Brand'>a</p><p style='font-family: Ok'>b</p>
            """, new PdfOptions { ResourceResolver = url => url == "ok.ttf" ? Regular : null });
        Assert.Equal(new[] { "Brand" }, missing);
    }

    [Fact]
    public void ReportsTheWatermarkFontAndUnusedStylesNot()
    {
        var missing = MissingFonts("<style>.never { font-family: Unused }</style><p>a</p>",
            new PdfOptions { Watermark = new PdfWatermark { Text = "DRAFT", FontFamily = "Impact, sans-serif" } });
        Assert.Equal(new[] { "Impact" }, missing);
    }

    [Fact]
    public void ThrowingFromTheCallbackFailsTheConversion()
    {
        var options = new PdfOptions { MissingFont = f => throw new InvalidOperationException("no " + f) };
        var ex = Assert.Throws<InvalidOperationException>(() => HtmlToPdf.Convert("<p style='font-family: Lato'>a</p>", options));
        Assert.Equal("no Lato", ex.Message);
    }

    [Fact]
    public void SystemFontDirectoriesAreListed()
        => Assert.NotEmpty(FontCollection.SystemFontDirectories());

    [Fact]
    public void UsesInstalledFontsOnlyWhenAllowed()
    {
        // Needs at least one installed font that is not a standard-font alias; CI images and desktops have some.
        var family = FontCollection.Installed.FamilyNames.FirstOrDefault(f => FontResolver.StandardFamily(f) == null
            && FontCollection.Installed.GetSnapshot().ByFamily[f].Any(face => face.Program?.GlyphId('A') > 0));
        if (family == null) return;
        var html = $"<p style='font-family: \"{family}\", sans-serif'>AB</p>";

        var missing = new List<string>();
        var off = Latin1(HtmlToPdf.Convert(html, new PdfOptions { Compress = false, MissingFont = missing.Add }));
        Assert.DoesNotContain("/FontFile", off);
        Assert.Equal(new[] { family }, missing);

        missing.Clear();
        var on = Latin1(HtmlToPdf.Convert(html, new PdfOptions { Compress = false, UseSystemFonts = true, MissingFont = missing.Add }));
        Assert.Contains("/FontFile", on);
        Assert.Empty(missing);
    }

    [Fact]
    public void SystemFontsDoNotReplaceStandardFontNames()
    {
        var text = Latin1(HtmlToPdf.Convert("<p style='font-family: Arial'>a</p>", new PdfOptions { Compress = false, UseSystemFonts = true }));
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.DoesNotContain("/FontFile", text);
    }
}
