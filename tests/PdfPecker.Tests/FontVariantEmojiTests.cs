using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// font-variant-emoji and the variation selectors U+FE0E and U+FE0F: a character that has both forms is drawn with a
/// colour font as an emoji or with another font as text, as in Chrome. The fixture is a subset of Noto Color Emoji with
/// ©, ®, ™, ❤ and ☺ (text by default) and 🎫 (emoji by default); the standard Helvetica draws ©, ® and ™ as text, and
/// the bundled symbol font ❤.
/// </summary>
public class FontVariantEmojiTests
{
    private static readonly byte[] Emoji = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoColorEmoji-Presentation.ttf"));

    // Whether the text was drawn with the colour font, which PdfPecker writes as a Type 3 font.
    private static bool DrawnAsEmoji(string family, string style, string text)
    {
        var pdf = HtmlToPdf.Convert($"<p style='margin:0;font:40px {family};{style}'>{text}</p>", new PdfOptions
        {
            Compress = false,
            Fonts = new FontCollection().Add(Emoji, "E"),
        });
        return Encoding.Latin1.GetString(pdf).Contains("/Subtype /Type3", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("font-variant-emoji: normal", false)]
    [InlineData("font-variant-emoji: text", false)]
    [InlineData("font-variant-emoji: unicode", false)]
    [InlineData("font-variant-emoji: emoji", true)]
    [InlineData("font-variant: emoji", true)]
    public void Emoji_takes_a_colour_font_for_a_character_the_text_font_has(string style, bool emoji)
        => Assert.Equal(emoji, DrawnAsEmoji("Helvetica", style, "©"));

    [Theory]
    [InlineData("", true)]
    [InlineData("font-variant-emoji: emoji", true)]
    [InlineData("font-variant-emoji: unicode", false)]
    [InlineData("font-variant-emoji: text", false)]
    public void Text_takes_a_font_without_colour_glyphs_after_a_colour_font(string style, bool emoji)
        => Assert.Equal(emoji, DrawnAsEmoji("E, Helvetica", style, "©"));

    [Fact]
    public void Unicode_follows_the_default_presentation_of_each_character()
        => Assert.True(DrawnAsEmoji("Helvetica", "font-variant-emoji: unicode", "\U0001F3AB"));

    [Theory]
    [InlineData("Helvetica", "font-variant-emoji: text", "©️", true)]
    [InlineData("E, Helvetica", "font-variant-emoji: emoji", "©︎", false)]
    [InlineData("E, Helvetica", "", "©︎", false)]
    public void A_variation_selector_wins_over_the_property(string family, string style, string text, bool emoji)
        => Assert.Equal(emoji, DrawnAsEmoji(family, style, text));

    [Fact]
    public void Text_falls_back_to_the_bundled_symbol_font()
        => Assert.False(DrawnAsEmoji("E", "font-variant-emoji: text", "❤"));

    [Fact]
    public void Without_a_font_of_the_form_asked_for_the_character_keeps_its_font()
        => Assert.True(DrawnAsEmoji("E", "font-variant-emoji: text", "\U0001F3AB"));

    [Fact]
    public void The_text_is_kept_for_extraction()
    {
        var pdf = HtmlToPdf.Convert("<p style='font:40px Helvetica;font-variant-emoji:emoji'>a©b</p>",
            new PdfOptions { Fonts = new FontCollection().Add(Emoji, "E") });
        Assert.Equal("a©b", PdfText.Extract(pdf).Trim());
    }

    [Fact]
    public void Supports_inheritance_and_the_font_shorthand()
    {
        Assert.True(DrawnAsEmoji("Helvetica", "", "<span style='font-variant-emoji: emoji'><b>©</b></span>"));
        Assert.True(DrawnAsEmoji("Helvetica", "", "<style>@supports (font-variant-emoji: emoji) { b { font-variant-emoji: emoji } }</style><b>©</b>"));
        Assert.False(DrawnAsEmoji("Helvetica", "font-variant-emoji: emoji; font: 40px Helvetica", "©"));
    }
}
