namespace PdfPecker.Fluent.Tests;

/// <summary>Decoration, lists, semantic tags, text elements, section page numbers, font features and placeholders.</summary>
public sealed class QuestPdfCallsTests
{
    private static string Css(Action<IContainer> content) => Html.Css(Html.Page(c => c.Column(col => content(col.Item()))));

    [Fact]
    public void Decoration_is_a_table_whose_header_and_footer_repeat()
        => Assert.Equal(
            "<table><thead><tr><td><p>B</p></td></tr></thead><tbody><tr><td><p>1</p></td></tr><tr><td><p>2</p></td></tr></tbody>"
            + "<tfoot><tr><td><p>A</p></td></tr></tfoot></table>",
            Html.Item(c => c.Decoration(d =>
            {
                d.Before().Text("B");
                d.Content().Column(k => { k.Item().Text("1"); k.Item().Text("2"); });
                d.After().Text("A");
            })));

    [Fact]
    public void Decoration_repeats_before_and_after_on_every_page()
    {
        byte[] pdf = Html.Page(c => c.Decoration(d =>
        {
            d.Before().Text("BEFORE");
            d.Content().Column(k => { for (int i = 0; i < 120; i++) k.Item().Text("Line " + i); });
            d.After().Text("AFTER");
        })).GeneratePdf();
        var pages = PdfText.ExtractPages(pdf);
        Assert.True(pages.Count > 1);
        Assert.All(pages, p => Assert.Contains("BEFORE", p.Text, StringComparison.Ordinal));
        Assert.All(pages, p => Assert.Contains("AFTER", p.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_decoration_slot_can_be_set_only_once()
        => Assert.Throws<InvalidOperationException>(() => Html.Of(c => c.Decoration(d => { d.Before().Text("a"); d.Before().Text("b"); })));

    [Fact]
    public void Lists_write_list_elements_with_spacing_between_items()
    {
        Assert.Equal("<ul class=\"c1\"><li><p>a</p></li><li class=\"c2\"><p>b</p></li></ul>",
            Html.Item(c => c.List(l => { l.Spacing(4); l.Item().Text("a"); l.Item().Text("b"); })));
        string css = Css(c => c.List(l => { l.Spacing(4); l.Item().Text("a"); l.Item().Text("b"); }));
        Assert.Contains(".c1{margin:0;padding:0 0 0 18pt;list-style-type:disc}", css);
        Assert.Contains(".c2{margin-top:4pt}", css);
    }

    [Fact]
    public void Numbered_lists_start_where_asked()
    {
        Assert.Equal("<ol class=\"c1\" start=\"3\"><li><p>a</p></li></ol>",
            Html.Item(c => c.List(l => { l.Numbered(ListNumbering.LowerRoman, 3); l.Item().Text("a"); })));
        Assert.Contains("list-style-type:lower-roman", Css(c => c.List(l => { l.Numbered(ListNumbering.LowerRoman, 3); l.Item().Text("a"); })));
    }

    [Fact]
    public void A_list_marker_is_a_css_string()
    {
        string css = Css(c => c.List(l => { l.Marker("\"</style><x-evil>"); l.Item().Text("a"); }));
        Assert.Contains("list-style-type:\"\\22 \\3c /style\\3e \\3c x-evil\\3e  \"", css);
        Assert.Throws<ArgumentException>(() => Html.Of(c => c.List(l => l.Marker(new string('x', 21)))));
    }

    [Fact]
    public void Semantic_lists_and_captions_write_the_elements_pdf_ua_tags()
    {
        Assert.Equal("<ul class=\"c1\"><li class=\"c2\"><p>a</p></li></ul>",
            Html.Item(c => c.SemanticList().Column(k => k.Item().SemanticListItem().Text("a"))));
        Assert.Equal("<figcaption class=\"c1\"><p>c</p></figcaption>", Html.Item(c => c.SemanticCaption().Text("c")));
        byte[] pdf = Html.Page(c => c.List(l => l.Item().Text("a")))
            .GeneratePdf(new PdfOptions { Title = "List", Language = "en", Accessibility = PdfAccessibility.PdfUA1 });
        Assert.Contains("/StructTreeRoot", System.Text.Encoding.Latin1.GetString(pdf));
    }

    [Theory]
    [InlineData("section")]
    [InlineData("article")]
    [InlineData("nav")]
    public void Semantic_calls_write_the_matching_element(string tag)
    {
        Action<IContainer> content = tag switch
        {
            "section" => c => c.SemanticSection().Text("s"),
            "article" => c => c.SemanticArticle().Text("s"),
            _ => c => c.SemanticTableOfContents().Text("s"),
        };
        Assert.Equal($"<{tag}><p>s</p></{tag}>", Html.Item(content));
    }

    [Fact]
    public void Semantic_quotations_lose_the_browser_margins()
    {
        Assert.Equal("<blockquote class=\"c1\"><p>q</p></blockquote>", Html.Item(c => c.SemanticBlockQuotation().Text("q")));
        Assert.Contains(".c1{margin:0;padding:0}", Css(c => c.SemanticBlockQuotation().Text("q")));
    }

    [Fact]
    public void Semantic_ignore_hides_the_content_from_screen_readers()
        => Assert.Equal("<p aria-hidden=\"true\">i</p>", Html.Item(c => c.SemanticIgnore().Text("i")));

    [Fact]
    public void Semantic_image_gives_images_inside_their_alternative_text()
    {
        Assert.Equal("<img class=\"c1\" src=\"fluent:img/0\" alt=\"alt &lt;x&gt;\">",
            Html.Item(c => c.SemanticImage("alt <x>").Image(Placeholders.Image(4, 4))));
        Assert.Contains("alt=\"own\"", Html.Item(c => c.SemanticImage("outer").Image(Placeholders.Image(4, 4)).AlternativeText("own")));
    }

    [Fact]
    public void Shrink_sizes_the_slot_to_its_content()
    {
        Assert.Contains(".c1{width:fit-content;height:auto;background-color:#f44336}",
            Css(c => c.Shrink().Background(Colors.Red.Medium).Text("x")));
        Assert.Contains("{height:auto}", Css(c => c.ShrinkVertical().Text("x")));
    }

    [Fact]
    public void Paragraph_spacing_splits_the_text_into_paragraphs()
    {
        Assert.Equal("<div class=\"c1\"><p>a</p><p class=\"c2\">b</p><p class=\"c2\"><br></p><p class=\"c2\">c</p></div>",
            Html.Item(c => c.Text(t => { t.ParagraphSpacing(6); t.Span("a\nb\n\nc"); })));
        Assert.Contains(".c2{margin-top:6pt}", Css(c => c.Text(t => { t.ParagraphSpacing(6); t.Span("a\nb"); })));
    }

    [Fact]
    public void Text_elements_are_inline_blocks_in_the_line()
    {
        Assert.Equal("<div class=\"c1\">x <div class=\"c2\"></div> y</div>",
            Html.Item(c => c.Text(t => { t.Span("x "); t.Element().Width(10).Height(10).Background(Colors.Red.Medium); t.Span(" y"); })));
        Assert.Contains("{display:inline-block;vertical-align:middle;",
            Css(c => c.Text(t => t.Element(TextInjectedElementAlignment.Middle).Width(10).Height(10))));
    }

    [Fact]
    public void Section_page_numbers_print_the_page_the_section_starts_on()
    {
        Assert.Equal("<p><span data-section-page=\"#a&quot;&lt;b&gt;\"></span></p>", Html.Item(c => c.Text(t => t.BeginPageNumberOfSection("a\"<b>"))));
        byte[] pdf = Html.Page(c => c.Column(col =>
        {
            col.Item().Text(t => { t.Span("Chapter on page "); t.BeginPageNumberOfSection("chapter"); });
            col.Item().PageBreak();
            col.Item().Text("Page 2");
            col.Item().PageBreak();
            col.Item().Section("chapter").Text("Chapter");
        })).GeneratePdf();
        Assert.Contains("Chapter on page 3", PdfText.ExtractPages(pdf)[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Font_features_become_font_feature_settings()
    {
        Assert.Contains("{font-feature-settings:\"smcp\" 1,\"liga\" 0}",
            Css(c => c.Text("x").EnableFontFeature(FontFeatures.SmallCapitals).DisableFontFeature(FontFeatures.StandardLigatures)));
        // A later call for the same feature replaces the earlier one.
        Assert.Contains("{font-feature-settings:\"liga\" 1}",
            Css(c => c.Text("x").DisableFontFeature(FontFeatures.StandardLigatures).EnableFontFeature(FontFeatures.StandardLigatures)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lig")]
    [InlineData("ligat")]
    [InlineData("li\"g")]
    [InlineData("l;g}")]
    public void Bad_font_feature_tags_throw(string tag)
        => Assert.Throws<ArgumentException>(() => TextStyle.Default.EnableFontFeature(tag));

    [Fact]
    public void Placeholders_repeat_on_each_run()
    {
        static string Take() => Placeholders.Name() + Placeholders.Sentence() + Placeholders.Decimal() + Placeholders.LongDate();
        string a = "", b = "";
        var t1 = new Thread(() => a = Take());
        var t2 = new Thread(() => b = Take());
        t1.Start(); t1.Join(); t2.Start(); t2.Join();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Placeholder_images_render()
    {
        byte[] pdf = Html.Page(c => c.Width(100).Height(50).Image(Placeholders.Image(200, 100))).GeneratePdf();
        Assert.True(pdf.Length > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Placeholders.Image(0, 10));
    }
}
