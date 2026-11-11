namespace PdfPecker.Tests;

/// <summary>
/// font-variant-alternates and @font-feature-values: named alternates that become OpenType features, as in Chrome. The
/// font is a subset of Inter with its salt, ss01 to ss03 and cv01, cv05, cv08 and cv11 features (SIL Open Font License,
/// see Fixtures/fonts). Each case is drawn and compared with the same text under the font-feature-settings it stands for.
/// </summary>
public class FontVariantAlternatesTests
{
    private static readonly byte[] Inter = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "Inter-Alternates.otf"));

    private const string Values = "@font-feature-values Inter, \"Inter Alt\" { @styleset { open: 1; dis: 2; both: 1 2; Big: 3 } "
        + "@character-variant { flag: 1; storey: 11 } @stylistic { alt: 1 } @swash { s: 1 } } @font-feature-values Other { @styleset { other: 3 } }";

    private static byte[] Pixels(string style, string css = Values, string family = "Inter")
    {
        string html = $"<style>{css}</style><p style=\"margin:0;font-family:{family};font-size:40px;{style}\">a1l4I069, Gift</p>";
        var options = new PdfOptions { Margins = new PageMargins(0), Fonts = new FontCollection().Add(Inter, family.Trim('\'', '"')) };
        return PdfRenderer.Open(HtmlToPdf.Convert(html, options)).RenderPage(0, new ImageOptions { Dpi = 48 }).Rgba;
    }

    [Theory]
    [InlineData("font-variant-alternates: styleset(open)", "font-feature-settings: 'ss01'")]
    [InlineData("font-variant-alternates: styleset(open, dis)", "font-feature-settings: 'ss01', 'ss02'")]
    [InlineData("font-variant-alternates: styleset(both)", "font-feature-settings: 'ss01', 'ss02'")]
    [InlineData("font-variant-alternates: character-variant(flag, storey)", "font-feature-settings: 'cv01', 'cv11'")]
    [InlineData("font-variant-alternates: stylistic(alt)", "font-feature-settings: 'salt'")]
    [InlineData("font-variant-alternates: stylistic(alt) styleset(Big)", "font-feature-settings: 'salt', 'ss03'")]
    [InlineData("font-variant: styleset(open) tabular-nums", "font-feature-settings: 'ss01'")]
    [InlineData("font-variant-alternates: stylistic(alt); font-feature-settings: 'salt' 0, 'ss01'", "font-feature-settings: 'ss01'")]
    public void Named_alternates_turn_on_their_features(string style, string same)
    {
        var drawn = Pixels(style);
        Assert.NotEqual(Pixels(""), drawn);
        Assert.Equal(Pixels(same), drawn);
    }

    [Theory]
    [InlineData("font-variant-alternates: styleset(other)")]          // a name of another family
    [InlineData("font-variant-alternates: styleset(missing)")]        // no such name
    [InlineData("font-variant-alternates: styleset(OPEN)")]           // names are case-sensitive
    [InlineData("font-variant-alternates: stylistic(open)")]          // a name of another kind
    [InlineData("font-variant-alternates: stylistic(alt, dis)")]      // stylistic takes one name, so the value is invalid
    [InlineData("font-variant-alternates: styleset(open) styleset(dis)")] // each function at most once
    [InlineData("font-variant-alternates: swash(s)")]                 // the font has no swash feature
    [InlineData("font-variant-alternates: normal")]
    public void Alternates_that_name_nothing_change_nothing(string style) => Assert.Equal(Pixels(""), Pixels(style));

    [Fact]
    public void Names_resolve_for_the_family_and_inherit()
    {
        // The rule names a quoted family; the shorthand font resets the alternates, and a child of another family keeps
        // the value and resolves it for its own family.
        Assert.Equal(Pixels("font-feature-settings: 'ss02'", family: "'Inter Alt'"), Pixels("font-variant-alternates: styleset(dis)", family: "'Inter Alt'"));
        string html = $"<style>{Values}</style><div style=\"font-variant-alternates: styleset(open)\"><p style=\"margin:0;font-family:Inter;font-size:40px\">a1l4I069, Gift</p></div>";
        var options = new PdfOptions { Margins = new PageMargins(0), Fonts = new FontCollection().Add(Inter, "Inter") };
        var inherited = PdfRenderer.Open(HtmlToPdf.Convert(html, options)).RenderPage(0, new ImageOptions { Dpi = 48 }).Rgba;
        Assert.Equal(Pixels("font-feature-settings: 'ss01'"), inherited);
        Assert.Equal(Pixels(""), Pixels("font-variant-alternates: styleset(open); font: 40px Inter"));
    }

    [Fact]
    public void Feature_value_blocks_that_are_not_valid_are_left_out()
    {
        const string css = "@font-feature-values Inter { @styleset { a: 21; b: 0; c: x; d: 1 } @stylistic { e: 1 2 } @character-variant { f: 100; g: 1 2 3 } @unknown { h: 1 } }";
        Assert.Equal(Pixels(""), Pixels("font-variant-alternates: styleset(a, b, c)", css));
        Assert.Equal(Pixels(""), Pixels("font-variant-alternates: stylistic(e) character-variant(f, g)", css));
        Assert.Equal(Pixels("font-feature-settings: 'ss01'"), Pixels("font-variant-alternates: styleset(d)", css));
    }

    [Fact]
    public void Historical_forms_needs_no_rule()
    {
        var pdf = HtmlToPdf.Convert("<style>@supports (font-variant-alternates: historical-forms) { p { color: red } }</style>"
            + "<p style='font-variant-alternates: historical-forms'>Text</p>");
        Assert.Equal("Text", PdfText.Extract(pdf).Trim());
        Assert.Contains("1 0 0 rg", PdfTestHelpers.AllContent(pdf));
    }
}
