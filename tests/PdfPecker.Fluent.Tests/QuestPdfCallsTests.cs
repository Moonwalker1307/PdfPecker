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
    public void Semantic_calls_write_the_matching_element(string tag)
    {
        Action<IContainer> content = tag switch
        {
            "section" => c => c.SemanticSection().Text("s"),
            _ => c => c.SemanticArticle().Text("s"),
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

    [Fact]
    public void ShowOnce_and_SkipOnce_in_a_band_write_a_first_page_band()
    {
        string html = Document.Create(d => d.Page(p =>
        {
            p.Header().Column(k =>
            {
                k.Item().ShowOnce().Text("Letterhead");
                k.Item().SkipOnce().Text("Continued");
            });
            p.Content().Text("x");
        })).GenerateHtml();
        Assert.Contains("<header data-sticky-pdf-header=\"\"><div class=\"c0\"><p>Continued</p></div></header>", html, StringComparison.Ordinal);
        Assert.Contains("<header data-sticky-pdf-first-header=\"\"><div class=\"c0\"><p>Letterhead</p></div></header>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_band_without_ShowOnce_or_SkipOnce_has_no_first_page_band()
    {
        string html = Document.Create(d => d.Page(p =>
        {
            p.Header().Text("Header");
            p.Footer().Text("Footer");
            p.Content().Text("x");
        })).GenerateHtml();
        Assert.DoesNotContain("data-sticky-pdf-first", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowOnce_and_SkipOnce_choose_the_band_per_page()
    {
        byte[] pdf = Document.Create(d => d.Page(p =>
        {
            p.Header().Column(k =>
            {
                k.Item().ShowOnce().Text("LETTERHEAD").FontSize(30);
                k.Item().SkipOnce().Text("CONTINUED");
            });
            p.Content().Column(k => { for (int i = 0; i < 120; i++) k.Item().Text("Line " + i); });
            p.Footer().SkipOnce().Text("PAGE FOOTER");
        })).GeneratePdf();
        var pages = PdfText.ExtractPages(pdf);
        Assert.True(pages.Count > 2);
        Assert.Contains("LETTERHEAD", pages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTINUED", pages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PAGE FOOTER", pages[0].Text, StringComparison.Ordinal);
        foreach (var page in pages.Skip(1))
        {
            Assert.DoesNotContain("LETTERHEAD", page.Text, StringComparison.Ordinal);
            Assert.Contains("CONTINUED", page.Text, StringComparison.Ordinal);
            Assert.Contains("PAGE FOOTER", page.Text, StringComparison.Ordinal);
        }
        Assert.Contains("Line 119", string.Concat(pages.Select(p => p.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_the_bands_ShowOnce_keeps_the_content_and_SkipOnce_leaves_it_out()
    {
        Assert.Equal("<p>x</p>", Html.Item(c => c.ShowOnce().Text("x")));
        Assert.Equal("", Html.Item(c => c.SkipOnce().Text("x")));
    }

    [Fact]
    public void ScaleToFit_takes_the_height_before_it_or_the_slot_around_it()
    {
        Assert.Contains("{height:40pt;-pdfpecker-scale-to-fit:shrink}", Css(c => c.Height(40).ScaleToFit().Text("x")));
        Assert.Contains("{max-height:40pt;-pdfpecker-scale-to-fit:shrink}", Css(c => c.MaxHeight(40).ScaleToFit().Text("x")));
        Assert.Contains("{-pdfpecker-scale-to-fit:shrink;max-height:100%}", Css(c => c.Height(40).Padding(5).ScaleToFit().Text("x")));
    }

    [Fact]
    public void ScaleToFit_draws_long_text_smaller_inside_the_box()
    {
        string name = string.Join(' ', Enumerable.Repeat("Bartholomew", 12));
        PdfTextPage Page(bool fit) => PdfText.ExtractPages(Html.Page(c => c.Column(k =>
        {
            var box = k.Item().Width(200).Height(50);
            (fit ? box.ScaleToFit() : box).Text(name).FontSize(20);
            k.Item().Text("AFTER");
        })).GeneratePdf())[0];

        var fitted = Page(true);
        var plain = Page(false);
        double FontHeight(PdfTextPage p) => p.Lines[0].Height;
        Assert.True(FontHeight(fitted) < FontHeight(plain) * 0.8);
        double bottom = fitted.Lines.Where(l => l.Text.Contains("Bartholomew", StringComparison.Ordinal)).Max(l => l.Y + l.Height);
        var after = fitted.Lines.Single(l => l.Text == "AFTER");
        Assert.True(bottom <= after.Y + 0.5, $"text ends at {bottom}, the next item starts at {after.Y}");
    }

    [Fact]
    public void Overline_joins_the_other_text_decoration_lines()
    {
        string css = Html.Css(Html.Page(c => c.Text(t =>
        {
            t.Span("a").Overline();
            t.Span("b").Underline().Overline().Strikethrough();
            t.Span("c").Overline(false);
        })));
        Assert.Contains("text-decoration-line:overline}", css);
        Assert.Contains("text-decoration-line:underline overline line-through}", css);
        Assert.Contains("text-decoration-line:none}", css);
        Assert.Contains("text-decoration-line:overline}", Html.Css(Html.Page(c => c.DefaultTextStyle(s => s.Overline()).Text("x"))));
    }

    [Fact]
    public void Break_anywhere_breaks_between_any_two_letters()
    {
        Assert.Contains("word-break:break-all", Html.Css(Html.Page(c => c.Text("x").BreakAnywhere())));
        Assert.Contains("word-break:normal", Html.Css(Html.Page(c => c.Text("x").BreakAnywhere(false))));

        // Without it the long word moves to a line of its own; with it, the word starts on the first line.
        string Render(bool anywhere) => Html.Text(Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A6);
            p.Content().Width(80).Text("ab 0123456789012345678901234567890123456789").BreakAnywhere(anywhere);
        })).GeneratePdf());
        Assert.StartsWith("ab\n", Render(anywhere: false).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.StartsWith("ab 0", Render(anywhere: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_direction_calls_isolate_the_text_in_its_direction()
    {
        string css = Html.Css(Html.Page(c => c.Text(t =>
        {
            t.Span("a").DirectionFromRightToLeft();
            t.Span("b").DirectionFromLeftToRight();
            t.Span("c").DirectionFromRightToLeft().DirectionAuto();
        })));
        Assert.Contains("{direction:rtl;unicode-bidi:isolate}", css);
        Assert.Contains("{direction:ltr;unicode-bidi:isolate}", css);
        Assert.Contains("{unicode-bidi:plaintext}", css);
    }

    [Fact]
    public void Document_content_direction_applies_to_pages_without_their_own()
    {
        var settings = new DocumentSettings { ContentDirection = ContentDirection.RightToLeft };
        string one = DocumentGenerator.Html(Html.Page(c => c.Text("x")).WithSettings(settings), false, out _);
        Assert.Contains("<body dir=\"rtl\">", one, StringComparison.Ordinal);

        string two = DocumentGenerator.Html(Document.Create(d =>
        {
            d.Page(p => p.Content().Text("a"));
            d.Page(p => { p.ContentFromLeftToRight(); p.Content().Text("b"); });
        }).WithSettings(settings), false, out _);
        Assert.Equal(1, Count(two, " dir=\"rtl\""));
        Assert.DoesNotContain(" dir=", DocumentGenerator.Html(Html.Page(c => c.Text("x")), false, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_code_and_quotes_keep_the_font_and_add_no_marks()
    {
        Assert.Equal("<code class=\"c1\"><p>x</p></code>", Html.Item(c => c.SemanticCode().Text("x")));
        Assert.Contains(".c1{display:block;font-family:inherit}", Css(c => c.SemanticCode().Text("x")));
        Assert.Equal("<q class=\"c1\"><p>x</p></q>", Html.Item(c => c.SemanticQuote().Text("x")));
        Assert.Contains(".c1{display:block;quotes:none}", Css(c => c.SemanticQuote().Text("x")));
        Assert.Equal("x", Html.Text(Html.Page(c => c.SemanticQuote().Text("x")).GeneratePdf()).Trim());
    }

    [Fact]
    public void Semantic_headings_and_language_are_the_heading_and_language_calls()
    {
        Assert.Equal(Html.Item(c => c.Heading(2).Text("h")), Html.Item(c => c.SemanticHeading2().Text("h")));
        Assert.Equal(Html.Item(c => c.Heading(6).Text("h")), Html.Item(c => c.SemanticHeading6().Text("h")));
        Assert.Equal(Html.Item(c => c.Language("de").Text("h")), Html.Item(c => c.SemanticLanguage("de").Text("h")));
    }

    [Fact]
    public void Semantic_division_span_and_table_write_nothing_of_their_own()
    {
        string plain = Html.Item(c => c.Text("x"));
        Assert.Equal(plain, Html.Item(c => c.SemanticDivision().Text("x")));
        Assert.Equal(plain, Html.Item(c => c.SemanticSpan("expansion").Text("x")));
        Assert.Equal(plain, Html.Item(c => c.SemanticSpan().Text("x")));
        Assert.Equal(plain, Html.Item(c => c.SemanticTable().Text("x")));
    }

    [Fact]
    public void A_horizontal_header_cell_is_the_header_of_its_row()
    {
        string html = Html.Of(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => { d.RelativeColumn(); d.RelativeColumn(); });
            t.Header(h => { h.Cell().Text("h"); h.Cell().SemanticHorizontalHeader().Text("h2"); });
            t.Cell().SemanticHorizontalHeader().Text("r");
            t.Cell().Text("v");
        }));
        Assert.Contains("<th scope=\"col\"><p>h2</p></th>", html);
        Assert.Contains("<tr><th scope=\"row\"><p>r</p></th><td><p>v</p></td></tr>", html);
    }

    [Fact]
    public void Semantic_code_quote_and_row_headers_are_tagged_in_pdf_ua()
    {
        byte[] pdf = Html.Page(c => c.Column(k =>
        {
            k.Item().SemanticCode().Text("code");
            k.Item().SemanticQuote().Text("quote");
            k.Item().Table(t =>
            {
                t.ColumnsDefinition(d => { d.RelativeColumn(); d.RelativeColumn(); });
                t.Cell().SemanticHorizontalHeader().Text("r");
                t.Cell().Text("v");
            });
        })).GeneratePdf(new PdfOptions { Title = "Tags", Language = "en", Accessibility = PdfAccessibility.PdfUA1, Compress = false });
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/S /Code", text, StringComparison.Ordinal);
        Assert.Contains("/S /Quote", text, StringComparison.Ordinal);
        Assert.Contains("/S /TH", text, StringComparison.Ordinal);
        Assert.Contains("/Scope /Row", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_table_of_contents_formula_index_and_labels_write_what_pdf_ua_tags()
    {
        Assert.Equal("<nav class=\"c0\" role=\"doc-toc\"><li class=\"c1\"><p>e</p></li></nav>",
            Html.Item(c => c.SemanticTableOfContents().Column(k => k.Item().SemanticTableOfContentsItem().Text("e"))));
        Assert.Equal("<p role=\"math\" aria-label=\"a &lt;b&gt;\">x</p>", Html.Item(c => c.SemanticFormula("a <b>").Text("x")));
        Assert.Equal("<p role=\"doc-index\">i</p>", Html.Item(c => c.SemanticIndex().Text("i")));
        Assert.Equal("<p data-pdf-list-label=\"\">1.</p>", Html.Item(c => c.SemanticListLabel().Text("1.")));
        Assert.Equal(Html.Item(c => c.Text("b")), Html.Item(c => c.SemanticListItemBody().Text("b")));
        // Two tags on one slot need two elements.
        Assert.Equal("<div role=\"doc-index\"><p role=\"math\" aria-label=\"f\">x</p></div>",
            Html.Item(c => c.SemanticIndex().SemanticFormula("f").Text("x")));
        Assert.Throws<ArgumentException>(() => Html.Item(c => c.SemanticFormula(" ").Text("x")));
    }

    [Fact]
    public void Semantic_table_of_contents_formula_index_and_labels_are_tagged_in_pdf_ua()
    {
        byte[] pdf = Html.Page(c => c.Column(k =>
        {
            k.Item().SemanticTableOfContents().Column(toc =>
            {
                toc.Item().SemanticTableOfContentsItem().Text("Areas");
                toc.Item().SemanticTableOfContentsItem().Text("Index");
            });
            k.Item().SemanticFormula("pi r squared").Text("πr²");
            k.Item().SemanticList().Column(list => list.Item().SemanticListItem().Row(row =>
            {
                row.ConstantItem(20).SemanticListLabel().Text("1.");
                row.RelativeItem().SemanticListItemBody().Text("Measure the radius.");
            }));
            k.Item().SemanticIndex().Text("Area, 1");
        })).GeneratePdf(new PdfOptions { Title = "Tags", Language = "en", Accessibility = PdfAccessibility.PdfUA1, Compress = false });
        var types = System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type /StructElem /S /(\w+)")
            .Select(m => m.Groups[1].Value);
        Assert.Equal("Document Sect TOC TOCI P TOCI P Formula L LI Lbl LBody P Index P", string.Join(" ", types));
        Assert.Contains("/Alt (pi r squared)", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
