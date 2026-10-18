using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Text;

namespace PdfPecker.Tests;

/// <summary>hyphens: auto with the TeX hyphenation patterns of the text's language (Liang's algorithm).</summary>
public class HyphenationTests
{
    private const float PageHeight = 800;

    private static List<DrawOp> Layout(string html, float width = 500, PdfOptions? options = null)
    {
        var doc = HtmlParser.Parse(html);
        var root = new ComputedStyle { Display = Display.Block, Lang = options?.Language };
        if (options?.Hyphenation is { } custom)
            root.Hyphenation = custom.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, root);
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
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

    private static float TextWidth(TextOp t) => t.Font.MeasureUnits(t.Text, 0, t.Text.Length) * t.Size / 1000f;

    private static string Hyphenate(string lang, string word)
    {
        var h = Hyphenator.For(lang, null);
        Assert.NotNull(h);
        var breaks = new List<int>();
        h!.Hyphenate(word, breaks, skipCapitalized: lang.StartsWith("en", StringComparison.Ordinal));
        for (int k = breaks.Count - 1; k >= 0; k--) word = word.Insert(breaks[k], "-");
        return word;
    }

    [Theory]
    // TeX's US English patterns (with lefthyphenmin 2 and righthyphenmin 3).
    [InlineData("en", "hyphenation", "hy-phen-ation")]
    [InlineData("en-US", "algorithm", "al-go-rithm")]
    [InlineData("en-GB", "typesetting", "type-set-ting")]
    [InlineData("en", "pattern", "pat-tern")]
    [InlineData("en", "manuscript", "man-u-script")]
    // Exceptions listed in the patterns file.
    [InlineData("en", "associate", "as-so-ciate")]
    [InlineData("en", "project", "project")]
    [InlineData("en", "table", "ta-ble")]
    // Danish.
    [InlineData("da", "arbejdsmarkedsbidrag", "ar-bejds-mar-keds-bi-drag")]
    [InlineData("da", "københavn", "kø-ben-havn")]
    [InlineData("da", "sætning", "sæt-ning")]
    // German, Dutch and Norwegian.
    [InlineData("de", "Silbentrennung", "Sil-ben-tren-nung")]
    [InlineData("nl", "woordenboek", "woor-den-boek")]
    [InlineData("nb", "orddeling", "ord-de-ling")]
    // Spanish and Swedish.
    [InlineData("es", "separación", "se-pa-ra-ción")]
    [InlineData("es-MX", "biblioteca", "bi-blio-te-ca")]
    [InlineData("es", "extraordinariamente", "ex-tra-or-di-na-ria-men-te")]
    [InlineData("sv", "avstavning", "av-stav-ning")]
    [InlineData("sv-FI", "sjukhuset", "sjuk-hu-set")]
    [InlineData("sv", "järnvägsstation", "järn-vägs-sta-tion")]
    // French, Italian, Portuguese and Polish; French and Italian elisions keep their apostrophe, typographic or not.
    [InlineData("fr", "ordinateur", "or-di-na-teur")]
    [InlineData("fr-CA", "l'ordinateur", "l'or-di-na-teur")]
    [InlineData("fr", "l’ordinateur", "l’or-di-na-teur")]
    [InlineData("it", "organizzazione", "or-ga-niz-za-zio-ne")]
    [InlineData("it", "dell'organizzazione", "del-l'or-ga-niz-za-zio-ne")]
    [InlineData("pt-BR", "computador", "com-pu-ta-dor")]
    [InlineData("pt", "desenvolvimento", "de-sen-vol-vi-mento")]
    [InlineData("pl", "przedsiębiorstwo", "przed-się-bior-stwo")]
    [InlineData("pl", "rzeczywistość", "rze-czy-wi-stość")]
    public void Words_break_where_the_patterns_allow(string lang, string word, string expected)
        => Assert.Equal(expected, Hyphenate(lang, word));

    [Theory]
    [InlineData("en", "Hyphenation")]  // Chrome leaves capitalized English words alone
    [InlineData("en", "HYPHENATION")]
    [InlineData("en", "abc123defgh")]  // digits
    [InlineData("en", "ward")]          // shorter than five letters
    [InlineData("da", "abc.defghijk")] // other characters inside
    [InlineData("en", "everybody's")]  // apostrophes only where the patterns cover them, as in French
    public void Some_words_are_never_hyphenated(string lang, string word) => Assert.Equal(word, Hyphenate(lang, word));

    [Fact]
    public void Punctuation_around_a_word_is_ignored_and_capitals_hyphenate_outside_english()
    {
        Assert.Equal("(hy-phen-ation),", Hyphenate("en", "(hyphenation),"));
        Assert.Equal("Kø-ben-havn", Hyphenate("da", "København"));
    }

    [Theory]
    [InlineData("da", "håndværkerne")]
    [InlineData("fr", "élémentaire")]
    [InlineData("de", "Übermäßigkeit")]
    public void Decomposed_words_break_where_their_composed_form_does(string lang, string word)
    {
        // Text pasted from macOS is often decomposed: a letter, then its accent as a combining mark, which the patterns
        // do not know. Such words were never hyphenated.
        string decomposed = word.Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual(word.Length, decomposed.Length);
        string expected = Hyphenate(lang, word);
        Assert.Contains("-", expected);
        Assert.Equal(expected, Hyphenate(lang, decomposed).Normalize(System.Text.NormalizationForm.FormC));
    }

    [Fact]
    public void Languages_without_patterns_have_no_hyphenator()
    {
        Assert.Null(Hyphenator.For("fi", null));
        Assert.Null(Hyphenator.For("", null));
        Assert.Null(Hyphenator.For("und", null));
    }

    private const string Narrow = "width: 70px; font: 12px Arial";
    private const string Text = "the hyphenation algorithm handles extraordinarily typographical considerations";

    [Fact]
    public void A_narrow_column_with_hyphens_auto_hyphenates_words_that_do_not_fit()
    {
        var auto = Lines(Layout($"<div lang='en' style='{Narrow}; hyphens: auto'>{Text}</div>"));
        var manual = Lines(Layout($"<div lang='en' style='{Narrow}'>{Text}</div>"));
        Assert.Contains(auto, l => l.EndsWith('-'));
        // Every line ends at a word end or a hyphenation point: the text is unchanged once the hyphens are removed.
        Assert.Equal(Text, string.Join(" ", auto).Replace("- ", ""));
        // Fewer words stick out of the column.
        var ops = Layout($"<div lang='en' style='{Narrow}; hyphens: auto'>{Text}</div>");
        int Overflowing(List<DrawOp> o) => o.OfType<TextOp>().Count(t => t.X + TextWidth(t) > 70 * 0.75f + 0.01f);
        Assert.True(Overflowing(ops) < Overflowing(Layout($"<div lang='en' style='{Narrow}'>{Text}</div>")));
        Assert.True(auto.Count >= manual.Count - 1);
    }

    [Fact]
    public void Lang_is_inherited_and_can_come_from_the_options()
    {
        var html = $"<html lang='en'><body><div style='{Narrow}; hyphens: auto'><span>{Text}</span></div></body></html>";
        Assert.Contains(Lines(Layout(html)), l => l.EndsWith('-'));
        var noLang = $"<div style='{Narrow}; hyphens: auto'>{Text}</div>";
        Assert.DoesNotContain(Lines(Layout(noLang)), l => l.EndsWith('-'));
        Assert.Contains(Lines(Layout(noLang, options: new PdfOptions { Language = "en-US" })), l => l.EndsWith('-'));
        // An element's own lang wins over the options' language; lang="" is an unknown language.
        Assert.DoesNotContain(Lines(Layout($"<div lang='' style='{Narrow}; hyphens: auto'>{Text}</div>", options: new PdfOptions { Language = "en" })), l => l.EndsWith('-'));
    }

    [Fact]
    public void Hyphens_manual_none_and_soft_hyphens_behave_as_before()
    {
        Assert.DoesNotContain(Lines(Layout($"<div lang='en' style='{Narrow}; hyphens: manual'>{Text}</div>")), l => l.EndsWith('-'));
        Assert.DoesNotContain(Lines(Layout($"<div lang='en' style='{Narrow}; hyphens: none'>{Text}</div>")), l => l.EndsWith('-'));
        // A word with soft hyphens breaks only at them, also with hyphens: auto.
        var ops = Layout("<div lang='da' style='width: 22mm; font: 12px Arial; hyphens: auto'>Arbejds&shy;markeds&shy;bidrag ok</div>");
        Assert.Equal(new[] { "Arbejds-", "markedsbidrag", "ok" }, Lines(ops));
        // white-space: nowrap does not hyphenate.
        Assert.Single(Lines(Layout($"<div lang='en' style='{Narrow}; hyphens: auto; white-space: nowrap'>{Text}</div>")));
    }

    [Fact]
    public void Danish_text_hyphenates()
    {
        var lines = Lines(Layout("<p lang='da' style='width: 90px; font: 12px Arial; hyphens: auto; margin: 0'>arbejdsmarkedsbidraget betales af lønmodtageren</p>"));
        Assert.Contains(lines, l => l.EndsWith('-'));
        Assert.Equal("arbejdsmarkedsbidraget betales af lønmodtageren", string.Join(" ", lines).Replace("- ", ""));
    }

    [Fact]
    public void Hyphenate_character_replaces_the_hyphen()
    {
        var lines = Lines(Layout($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-character: \"\\2010\"'>{Text}</div>"));
        Assert.Contains(lines, l => l.EndsWith('\u2010'));
        Assert.DoesNotContain(lines, l => l.EndsWith('-'));
    }

    [Fact]
    public void Justified_hyphenated_lines_fill_the_column()
    {
        var ops = Layout($"<div lang='en' style='width: 120px; font: 12px Arial; hyphens: auto; text-align: justify'>{Text} {Text}</div>");
        var lines = ops.OfType<TextOp>().ToList();
        Assert.Contains(lines, l => l.Text.EndsWith('-'));
        // Every line but the last with a space is stretched to the column's width.
        foreach (var l in lines.Take(lines.Count - 1).Where(l => l.Text.Contains(' ')))
            Assert.True(l.WordSpacing > 0 || Math.Abs(l.X + TextWidth(l) - 90) < 0.5f, $"'{l.Text}' is not justified");
    }

    [Fact]
    public void Users_can_supply_patterns()
    {
        // A tiny pattern set: break "abcdefgh" between "abc" and "defgh".
        var options = new PdfOptions
        {
            Hyphenation = new Dictionary<string, HyphenationPatterns> { ["xx"] = new HyphenationPatterns("c1d", leftMin: 2, rightMin: 2) },
        };
        var lines = Lines(Layout("<div lang='xx-YY' style='width: 40px; font: 12px Arial; hyphens: auto'>abcdefgh abcdefgh</div>", options: options));
        Assert.Equal("abc-", lines[0]);
        // A TeX file with \patterns and \hyphenation, % comments ignored; it overrides the bundled patterns.
        var tex = new HyphenationPatterns("% test\n\\patterns{ % none\n.ab1c\n}\n\\hyphenation{ hyph-enation }");
        var h = tex.Hyphenator;
        var breaks = new List<int>();
        h.Hyphenate("hyphenation", breaks, false);
        Assert.Equal(new[] { 4 }, breaks);
        h.Hyphenate("abcdef", breaks, false);
        Assert.Equal(new[] { 2 }, breaks);
    }

    [Theory]
    [InlineData("auto", 0, 0, 0)]
    [InlineData("6", 6, 0, 0)]             // one value: the word length, the others auto
    [InlineData("6 3", 6, 3, 3)]           // two values: after is the same as before
    [InlineData("auto 3 4", 0, 3, 4)]
    [InlineData("8 auto 4", 8, 0, 4)]
    [InlineData("auto auto", 0, 0, 0)]
    [InlineData("  +7   2  3 ", 7, 2, 3)]
    [InlineData("99999999999 2", 1000, 2, 2)] // huge integers are clamped, not rejected
    public void Hyphenate_limit_chars_parses(string value, int word, int before, int after)
        => Assert.Equal(new HyphenateLimits(word, before, after), StyleResolver.ParseHyphenateLimitChars(value));

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("5 0")]
    [InlineData("2.5")]
    [InlineData("1e2")]
    [InlineData("5 2 2 2")]
    [InlineData("5, 2, 2")]
    [InlineData("5px")]
    [InlineData("+")]
    [InlineData("auto auto auto auto")]
    public void Hyphenate_limit_chars_rejects_invalid_values(string value)
        => Assert.Null(StyleResolver.ParseHyphenateLimitChars(value));

    private static string Hyphenate(string lang, string word, int minWord, int minBefore, int minAfter)
    {
        var breaks = new List<int>();
        Hyphenator.For(lang, null)!.Hyphenate(word, breaks, skipCapitalized: true, minWord, minBefore, minAfter);
        for (int k = breaks.Count - 1; k >= 0; k--) word = word.Insert(breaks[k], "-");
        return word;
    }

    [Theory]
    [InlineData(0, 0, 0, "hy-phen-ation")]
    [InlineData(11, 0, 0, "hy-phen-ation")] // eleven letters
    [InlineData(12, 0, 0, "hyphenation")]
    [InlineData(0, 3, 0, "hyphen-ation")]
    [InlineData(0, 0, 5, "hy-phen-ation")]
    [InlineData(0, 0, 6, "hy-phenation")]
    [InlineData(0, 7, 0, "hyphenation")]   // no break has seven letters before it
    [InlineData(0, 6, 6, "hyphenation")]   // more letters needed than the word has
    [InlineData(0, 1, 1, "hy-phen-ation")] // never fewer than the patterns' own two before and three after
    public void Hyphenate_limit_chars_limits_the_breaks(int word, int before, int after, string expected)
        => Assert.Equal(expected, Hyphenate("en", "hyphenation", word, before, after));

    [Fact]
    public void Hyphenate_limit_chars_applies_to_exceptions_and_short_words()
    {
        Assert.Equal("asso-ciate", Hyphenate("en", "associate", 0, 3, 0)); // from the exception list "as-so-ciate"
        Assert.Equal("ta-ble", Hyphenate("en", "table", 0, 0, 0));
        Assert.Equal("table", Hyphenate("en", "table", 6, 0, 0));
        Assert.Equal("table", Hyphenate("en", "table", 0, 3, 0));
    }

    [Fact]
    public void Hyphenate_limit_chars_is_inherited_and_overridden_per_element()
    {
        bool Hyphenated(string html) => Lines(Layout(html)).Any(l => l.EndsWith('-'));
        Assert.True(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: auto'>{Text}</div>"));
        Assert.False(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: 20'>{Text}</div>"));
        // Inherited by a child, and a child's own value wins.
        Assert.False(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: 20'><span>{Text}</span></div>"));
        Assert.True(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: 20'><span style='hyphenate-limit-chars: initial'>{Text}</span></div>"));
        Assert.True(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: 20'><span style='hyphenate-limit-chars: auto'><b style='hyphenate-limit-chars: inherit'>{Text}</b></span></div>"));
        Assert.False(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto'><p style='margin: 0; hyphenate-limit-chars: 20'>{Text}</p></div>"));
        // An invalid value is ignored: the element keeps the value it would have had without it.
        Assert.False(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: 20'><span style='hyphenate-limit-chars: 0'>{Text}</span></div>"));
        Assert.False(Hyphenated($"<div lang='en' style='{Narrow}; hyphens: auto; hyphenate-limit-chars: 20; hyphenate-limit-chars: 5 2 2 2'>{Text}</div>"));
    }

    [Fact]
    public void Hyphenate_limit_chars_shows_in_supports()
    {
        Assert.True(CssSupports.Evaluate("(hyphenate-limit-chars: 6 3 2)"));
        Assert.True(CssSupports.Evaluate("(hyphenate-limit-chars: auto)"));
        Assert.False(CssSupports.Evaluate("(hyphenate-limit-chars: 0)"));
        Assert.False(CssSupports.Evaluate("(hyphenate-limit-chars: 1 2 3 4)"));
    }

    [Fact]
    public void A_hyphenated_document_converts_to_a_valid_pdf()
    {
        var pdf = HtmlToPdf.Convert($"<html lang='da'><body style='width: 60px; hyphens: auto'><p>arbejdsmarkedsbidraget betales af lønmodtageren</p><p lang='en'>{Text}</p></body></html>");
        PdfTestHelpers.AssertStructurallyValid(pdf);
    }
}
