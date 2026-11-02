using FluentExamples;

namespace PdfPecker.Fluent.Tests;

/// <summary>How fluent chains become elements: merged while CSS allows, nested where order matters.</summary>
public sealed class LayoutTests
{
    [Fact]
    public void Text_alone_is_one_paragraph()
        => Assert.Equal("<main><p>Hello</p></main>", Html.Of(c => c.Text("Hello")));

    [Fact]
    public void The_first_styles_go_on_the_page_slot_itself()
        => Assert.Equal("<main class=\"c0\"><p>x</p></main>", Html.Of(c => c.Padding(10).Text("x")));

    [Fact]
    public void Decorators_in_box_model_order_merge_into_one_element()
        => Assert.Equal("<p class=\"c1\">x</p>", Html.Item(c => c.Width(100).Border(1).Background(Colors.Red.Medium).Padding(4).Text("x")));

    [Fact]
    public void Background_after_padding_nests_so_it_paints_inside_the_padding()
        => Assert.Equal("<div class=\"c1\"><p class=\"c2\">x</p></div>", Html.Item(c => c.Padding(4).Background(Colors.Red.Medium).Text("x")));

    [Fact]
    public void Repeating_a_property_nests()
        => Assert.Equal("<div class=\"c1\"><p class=\"c1\">x</p></div>", Html.Item(c => c.Padding(4).Padding(4).Text("x")));

    [Fact]
    public void Alignment_makes_a_flex_container_around_the_content()
    {
        Assert.Equal("<div class=\"c1\"><p>x</p></div>", Html.Item(c => c.AlignRight().AlignMiddle().Text("x")));
        Assert.Contains(".c1{display:flex;flex-direction:column;align-items:flex-end;text-align:right;justify-content:center}",
            Html.Css(Html.Page(c => c.Column(col => col.Item().AlignRight().AlignMiddle().Text("x")))));
    }

    [Fact]
    public void Column_and_row_items_get_their_flex_properties()
    {
        var document = Html.Page(c => c.Row(r =>
        {
            r.Spacing(5);
            r.RelativeItem(2).Text("a");
            r.ConstantItem(40).Text("b");
            r.AutoItem().Text("c");
        }));
        string css = Html.Css(document);
        Assert.Contains("{display:flex;column-gap:5pt}", css);
        Assert.Contains("{flex-grow:2;flex-shrink:1;flex-basis:0;min-width:0}", css);
        Assert.Contains("{flex-grow:0;flex-shrink:0;width:40pt}", css);
    }

    [Fact]
    public void Width_inside_a_row_item_gets_its_own_element()
        => Assert.Equal("<main><div class=\"c0\"><div class=\"c1\"><div class=\"c2\"></div></div></div></main>",
            Html.Of(c => c.Row(r => r.RelativeItem().Width(40).Background(Colors.Red.Medium))));

    [Fact]
    public void Heading_changes_the_paragraph_tag()
        => Assert.Equal("<h2>Title</h2>", Html.Item(c => c.Heading(2).Text("Title")));

    [Fact]
    public void Hyperlink_makes_the_box_a_link()
        => Assert.Equal("<a class=\"c1\" href=\"https://example.com/\"><p>x</p></a>", Html.Item(c => c.Hyperlink("https://example.com/").Text("x")));

    [Fact]
    public void Hidden_content_is_left_out()
        => Assert.Equal("<main></main>", Html.Of(c => c.ShowIf(false).Text("x")));

    [Fact]
    public void Table_places_cells_with_spans_and_fills_gaps()
    {
        string html = Html.Of(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => { d.ConstantColumn(50); d.RelativeColumn(); d.RelativeColumn(3); });
            t.Header(h => h.Cell().ColumnSpan(3).Text("head"));
            t.Cell().RowSpan(2).Text("a");
            t.Cell().ColumnSpan(2).Text("b");
            t.Cell().Text("c");
            t.Cell().Row(3).Column(3).Text("d");
        }));
        Assert.Equal(
            "<main><table><colgroup><col class=\"c0\"><col class=\"c1\"><col class=\"c2\"></colgroup>" +
            "<thead><tr><th scope=\"col\" colspan=\"3\"><p>head</p></th></tr></thead>" +
            "<tbody><tr><td rowspan=\"2\"><p>a</p></td><td colspan=\"2\"><p>b</p></td></tr>" +
            "<tr><td><p>c</p></td><td></td></tr>" +
            "<tr><td></td><td></td><td><p>d</p></td></tr></tbody></table></main>", html);
    }

    [Fact]
    public void A_cells_painted_content_fills_the_cell_whatever_the_call_order()
    {
        // QuestPDF draws a cell's content over the whole row, so a background inside the padding fills it; a cell with
        // an alignment keeps its content's own size, and a background on the cell itself needs nothing more.
        var document = Html.Page(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => { d.RelativeColumn(); d.RelativeColumn(); d.RelativeColumn(); });
            t.Cell().Padding(5).Background(Colors.Red.Medium).Text("a");
            t.Cell().Padding(5).AlignMiddle().Border(1).Text("b");
            t.Cell().Background(Colors.Red.Medium).Padding(5).Text("c");
        }));
        string html = Html.Body(DocumentGenerator.Html(document, false, out _));
        Assert.Equal(
            "<main><table><colgroup><col class=\"c0\"><col class=\"c0\"><col class=\"c0\"></colgroup><tbody><tr>" +
            "<td class=\"c1\"><p class=\"c2\">a</p></td>" +
            "<td class=\"c3\"><p class=\"c4\">b</p></td>" +
            "<td class=\"c5\"><p>c</p></td></tr></tbody></table></main>", html);
        string css = Html.Css(document);
        Assert.Contains(".c1{padding-top:5pt;padding-right:5pt;padding-bottom:5pt;padding-left:5pt;height:1px}", css);
        Assert.Contains(".c2{height:100%;background-color:", css);
        Assert.DoesNotContain("height", css[css.IndexOf(".c3{", StringComparison.Ordinal)..css.IndexOf(".c5{", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Linear_gradients_turn_the_QuestPDF_angle_into_the_CSS_one()
    {
        // QuestPDF's 0 runs left to right, which CSS calls 90deg.
        string css = Html.Css(Html.Page(c => c.Column(col =>
        {
            col.Item().BackgroundLinearGradient(0, Colors.Red.Medium, "#2196f380").Text("a");
            col.Item().BackgroundLinearGradient(-90, Colors.Red.Medium).Text("b");
            col.Item().BackgroundLinearGradient(405.5f, [Colors.Red.Medium, Colors.White, Colors.Black]).Text("c");
        })));
        Assert.Contains("{background-image:linear-gradient(90deg,#f44336,#2196f380)}", css);
        Assert.Contains("{background-image:linear-gradient(0deg,#f44336,#f44336)}", css);
        Assert.Contains("{background-image:linear-gradient(135.5deg,#f44336,#ffffff,#000000)}", css);
    }

    [Fact]
    public void Radial_gradients_start_in_the_middle_or_at_the_given_centre()
    {
        string css = Html.Css(Html.Page(c => c.Column(col =>
        {
            col.Item().BackgroundRadialGradient(Colors.White, Colors.Red.Medium).Text("a");
            col.Item().BackgroundRadialGradient(0, 0.25f, Colors.White, Colors.Red.Medium).Text("b");
        })));
        Assert.Contains("{background-image:radial-gradient(#ffffff,#f44336)}", css);
        Assert.Contains("{background-image:radial-gradient(at 0% 25%,#ffffff,#f44336)}", css);
    }

    [Fact]
    public void A_gradient_merges_with_the_background_colour_under_it()
        => Assert.Equal("<p class=\"c1\">x</p>", Html.Item(c => c.Background(Colors.Black).BackgroundRadialGradient(Colors.White).Padding(4).Text("x")));

    [Fact]
    public void Opacity_merges_before_paint_and_nests_after_it()
    {
        // Paint called before Opacity must not fade, so it stays on an element outside the faded one.
        Assert.Equal("<p class=\"c1\">x</p>", Html.Item(c => c.Width(100).Opacity(0.5f).Background(Colors.Red.Medium).Padding(4).Text("x")));
        Assert.Equal("<div class=\"c1\"><p class=\"c2\">x</p></div>", Html.Item(c => c.Background(Colors.Red.Medium).Opacity(0.5f).Text("x")));
        Assert.Contains(".c2{opacity:0.5}", Html.Css(Html.Page(c => c.Column(col => col.Item().Background(Colors.Red.Medium).Opacity(0.5f).Text("x")))));
    }

    [Fact]
    public void Full_opacity_writes_nothing()
        => Assert.Equal("<p>x</p>", Html.Item(c => c.Opacity(1).Text("x")));

    [Fact]
    public void A_faded_cell_fades_a_box_that_fills_the_row()
    {
        var document = Html.Page(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => d.RelativeColumn());
            t.Cell().Opacity(0.25f).Background(Colors.Red.Medium).Text("a");
        }));
        Assert.Equal("<main><table><colgroup><col class=\"c0\"></colgroup><tbody><tr><td class=\"c1\"><p class=\"c2\">a</p></td></tr></tbody></table></main>",
            Html.Body(DocumentGenerator.Html(document, false, out _)));
        string css = Html.Css(document);
        Assert.Contains(".c1{height:1px}", css);
        Assert.Contains(".c2{height:100%;opacity:0.25;background-color:#f44336}", css);
    }

    [Fact]
    public void Gradients_and_opacity_reach_the_PDF()
    {
        byte[] pdf = Html.Page(c => c.Column(col =>
        {
            col.Item().Height(20).BackgroundLinearGradient(0, Colors.Red.Medium, Colors.Blue.Medium);
            col.Item().Height(20).BackgroundRadialGradient(Colors.White, Colors.Green.Medium);
            col.Item().Opacity(0.5f).Text("faded");
        })).GeneratePdf();
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/ShadingType 2", text);
        Assert.Contains("/ShadingType 3", text);
        Assert.Contains("/Group << /S /Transparency", text);
        Assert.Contains("/ca 0.502", text);
        Assert.Contains("faded", Html.Text(pdf));
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public void Opacity_outside_zero_to_one_is_refused(float value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Opacity(value)));

    [Fact]
    public void Gradients_check_their_arguments()
    {
        Assert.Throws<ArgumentException>(() => Html.Of(c => c.BackgroundLinearGradient(0)));
        Assert.Throws<ArgumentNullException>(() => Html.Of(c => c.BackgroundRadialGradient(null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.BackgroundLinearGradient(float.PositiveInfinity, Colors.Red.Medium)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.BackgroundRadialGradient(float.NaN, 0, Colors.Red.Medium)));
    }

    [Fact]
    public void Relative_columns_share_what_constant_columns_leave()
    {
        var document = Html.Page(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => { d.ConstantColumn(50); d.RelativeColumn(); d.RelativeColumn(3); });
            t.Cell().Text("x");
        }));
        string css = Html.Css(document);
        Assert.Contains("{width:calc((100% - 50pt) * 0.25)}", css);
        Assert.Contains("{width:calc((100% - 50pt) * 0.75)}", css);
    }

    [Fact]
    public void Overlapping_cells_fail_with_a_clear_message()
    {
        var document = Html.Page(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => d.RelativeColumn());
            t.Cell().Row(1).Column(1).Text("a");
            t.Cell().Row(1).Column(1).Text("b");
        }));
        Assert.Contains("overlap", Assert.Throws<InvalidOperationException>(() => document.GenerateHtml()).Message);
    }

    [Fact]
    public void A_table_without_columns_fails()
        => Assert.Throws<InvalidOperationException>(() => Html.Page(c => c.Table(t => t.Cell().Text("x"))).GenerateHtml());

    [Fact]
    public void A_slot_takes_one_content_call()
        => Assert.Throws<InvalidOperationException>(() => Html.Page(c => { c.Text("a"); c.Text("b"); }).GenerateHtml());

    [Fact]
    public void BorderColor_must_follow_a_border()
        => Assert.Throws<InvalidOperationException>(() => Html.Page(c => c.Padding(2).BorderColor(Colors.Red.Medium)).GenerateHtml());

    [Fact]
    public void Page_numbers_in_the_content_refer_to_their_own_page()
    {
        var document = Html.Page(c => c.Column(col =>
        {
            col.Item().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            col.Item().PageBreak();
            col.Item().Text(t => { t.Span("Now "); t.CurrentPageNumber().Bold(); });
        }));
        string html = Html.Body(DocumentGenerator.Html(document, false, out _));
        Assert.Contains("<span id=\"pdfpecker-page-1\" data-section-page=\"#pdfpecker-page-1\"></span>", html);
        Assert.Contains("<span data-total-pages></span>", html);
        Assert.Contains("id=\"pdfpecker-page-2\" data-section-page=\"#pdfpecker-page-2\"", html);
        Assert.Contains("[data-total-pages]::after{content:counter(pages)}", Html.Css(document));
        var pages = PdfText.ExtractPages(document.GeneratePdf());
        Assert.Contains("Page 1 of 2", pages[0].Text);
        Assert.Contains("Now 2", pages[1].Text);
    }

    [Fact]
    public void Header_and_footer_repeat_with_page_numbers()
    {
        byte[] pdf = Document.Create(d => d.Page(p =>
        {
            p.Margin(30);
            p.Header().Text("Head");
            p.Content().Column(c => { c.Item().Text("one"); c.Item().PageBreak(); c.Item().Text("two"); });
            p.Footer().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
        })).GeneratePdf();
        var pages = PdfText.ExtractPages(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Contains("Page 1 of 2", pages[0].Text);
        Assert.Contains("Page 2 of 2", pages[1].Text);
        Assert.Contains("Head", pages[1].Text);
    }

    [Fact]
    public void Several_page_sets_make_one_pdf_with_their_own_sizes()
    {
        byte[] pdf = Document.Create(d =>
        {
            d.Page(p => { p.Size(PageSizes.A4); p.Content().Text("portrait"); });
            d.Page(p => { p.Size(PageSizes.A4.Landscape); p.Content().Text("landscape"); });
        }).GeneratePdf();
        var pages = PdfText.ExtractPages(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Contains("landscape", pages[1].Text);
        Assert.True(pages[0].Width < pages[0].Height);
        Assert.True(pages[1].Width > pages[1].Height);
    }

    [Fact]
    public void Page_sets_are_named_pages_of_one_html_document()
    {
        string html = Document.Create(d =>
        {
            d.Page(p => { p.Size(PageSizes.A5); p.Margin(10); p.Header().Text("H0"); p.Content().Text("a"); });
            d.Page(p => { p.Size(PageSizes.A4); p.PageColor(Colors.Grey.Lighten4); p.DefaultTextStyle(x => x.FontSize(9)); p.ContentFromRightToLeft(); p.Content().Text("b"); p.Footer().Text("F1"); });
        }).GenerateHtml();
        Assert.Contains("@page p0{size:419.53pt 595.28pt;margin:10pt 10pt 10pt 10pt}", html);
        Assert.Contains("@page p1{size:595.28pt 841.89pt;margin:0pt 0pt 0pt 0pt;background:#f5f5f5}", html);
        Assert.Contains("<body><div class=\"c0\"><header data-sticky-pdf-header=\"\" data-sticky-pdf-page=\"p0\"><p>H0</p></header><main><p>a</p></main></div>"
            + "<div class=\"c1\" dir=\"rtl\"><main><p>b</p></main><footer data-sticky-pdf-footer=\"\" data-sticky-pdf-page=\"p1\"><p>F1</p></footer></div></body>", html);
        Assert.Contains(".c0{page:p0}.c1{page:p1;font-size:9pt}", html);
    }

    [Fact]
    public void Page_numbers_run_on_through_the_page_sets_with_their_own_bands()
    {
        byte[] pdf = Document.Create(d =>
        {
            d.Page(p =>
            {
                p.Size(PageSizes.A5);
                p.Margin(20);
                p.Header().Text("Cover header");
                p.Content().Column(k => { for (int i = 0; i < 40; i++) k.Item().Text("Cover line " + i); });
                p.Footer().Text(t => { t.Span("Cover "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
            d.Page(p =>
            {
                p.Size(PageSizes.A4.Landscape);
                p.Margin(30);
                p.Header().Column(k => { k.Item().ShowOnce().Text("Report letterhead"); k.Item().SkipOnce().Text("Report continued"); });
                p.Content().Column(k => { for (int i = 0; i < 60; i++) k.Item().Text("Report line " + i); });
                p.Footer().Text(t => { t.Span("Report "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
        }).GeneratePdf();
        var pages = PdfText.ExtractPages(pdf);
        int cover = pages.Count(p => p.Text.Contains("Cover header", StringComparison.Ordinal));
        Assert.True(cover >= 2 && pages.Count >= cover + 2);
        for (int i = 0; i < pages.Count; i++)
        {
            string text = pages[i].Text;
            Assert.Contains($"{(i < cover ? "Cover" : "Report")} {i + 1} of {pages.Count}", text, StringComparison.Ordinal);
            Assert.Equal(i < cover, text.Contains("Cover header", StringComparison.Ordinal));
            Assert.Equal(i == cover, text.Contains("Report letterhead", StringComparison.Ordinal));
            Assert.Equal(i > cover, text.Contains("Report continued", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_section_link_reaches_into_another_page_set()
    {
        byte[] pdf = Document.Create(d =>
        {
            d.Page(p => p.Content().Text(t => { t.SectionLink("Go to the appendix on page ", "appendix"); t.BeginPageNumberOfSection("appendix"); }));
            d.Page(p => p.Content().Section("appendix").Text("Appendix"));
        }).GeneratePdf();
        Assert.Contains("appendix on page 2", PdfText.ExtractPages(pdf)[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_page_numbers_give_its_last_page_its_page_count_and_the_page_within_it()
    {
        var document = Document.Create(d => d.Page(p =>
        {
            p.Content().Column(c =>
            {
                c.Item().Text(t =>
                {
                    t.Span("Terms: pages "); t.BeginPageNumberOfSection("terms & more"); t.Span("-");
                    t.EndPageNumberOfSection("terms & more"); t.Span(", "); t.TotalPagesWithinSection("terms & more");
                    t.Span(" pages");
                });
                c.Item().PageBreak();
                c.Item().Section("terms & more").Column(terms =>
                {
                    terms.Item().Height(500).Text("Terms");
                    terms.Item().Height(500).Text("More terms");
                    terms.Item().Text(t => { t.Span("Terms page "); t.PageNumberWithinSection("terms & more"); });
                });
            });
            p.Footer().Text(t => { t.Span("Within "); t.PageNumberWithinSection("terms & more"); });
        }));
        string html = DocumentGenerator.Html(document, false, out _);
        Assert.Contains("data-section-end=\"#", html);
        Assert.Contains("data-section-pages=\"#", html);
        Assert.Contains("{section-page:", html);
        var pages = PdfText.ExtractPages(document.GeneratePdf());
        Assert.Equal(3, pages.Count);
        Assert.Contains("Terms: pages 2-3, 2 pages", pages[0].Text);
        Assert.Contains("Terms page 2", pages[2].Text);
        Assert.Contains("Within 1", pages[1].Text);
        Assert.Contains("Within 2", pages[2].Text);
    }

    /// <summary>An invoice of two pages: page numbers in the footer and the content, and a section with a link to it.</summary>
    private static Document Invoice(string number) => Document.Create(d => d.Page(p =>
    {
        p.Size(PageSizes.A5);
        p.Content().Column(c =>
        {
            c.Item().Text(t => { t.Span("Invoice " + number + ", see "); t.SectionLink("the terms", "terms"); t.Span(". Pages: "); t.TotalPages(); });
            c.Item().PageBreak();
            c.Item().Section("terms").Text(t => { t.Span("Terms of " + number + " on page "); t.CurrentPageNumber(); });
        });
        p.Footer().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
    }));

    [Fact]
    public void Merged_documents_number_their_pages_on_their_own_by_default()
    {
        var merged = Document.Merge(Invoice("A"), Invoice("B"));
        string html = DocumentGenerator.Html(merged, false, out _);
        Assert.Contains("<div id=\"pdfpecker-document-1\">", html);
        Assert.Contains("<div id=\"pdfpecker-document-2\">", html);
        // Each document's section is its own, so the same name can be used in both.
        Assert.Contains("id=\"pdfpecker-document-1-terms\"", html);
        Assert.Contains("href=\"#pdfpecker-document-2-terms\"", html);
        Assert.Contains("{section-page:pdfpecker-document-2}", html);
        Assert.DoesNotContain("{page}", html);

        var pages = PdfText.ExtractPages(merged.GeneratePdf());
        Assert.Equal(4, pages.Count);
        Assert.Contains("Invoice A", pages[0].Text);
        Assert.Contains("Pages: 2", pages[0].Text);
        Assert.Contains("Page 1 of 2", pages[0].Text);
        Assert.Contains("Terms of A on page 2", pages[1].Text);
        Assert.Contains("Page 2 of 2", pages[1].Text);
        Assert.Contains("Invoice B", pages[2].Text);
        Assert.Contains("Pages: 2", pages[2].Text);
        Assert.Contains("Page 1 of 2", pages[2].Text);
        Assert.Contains("Terms of B on page 2", pages[3].Text);
        Assert.Contains("Page 2 of 2", pages[3].Text);
    }

    [Fact]
    public void Merged_documents_with_continuous_page_numbers_count_on()
    {
        var merged = Document.Merge([Invoice("A"), Invoice("B")]).UseContinuousPageNumbers();
        string html = DocumentGenerator.Html(merged, false, out _);
        Assert.DoesNotContain("pdfpecker-document-", html);
        Assert.Contains("{page}", html);

        var pages = PdfText.ExtractPages(merged.GeneratePdf());
        Assert.Equal(4, pages.Count);
        Assert.Contains("Page 1 of 4", pages[0].Text);
        Assert.Contains("Pages: 4", pages[2].Text);
        Assert.Contains("Page 3 of 4", pages[2].Text);
        Assert.Contains("Terms of B on page 4", pages[3].Text);
    }

    [Fact]
    public void Merging_one_document_writes_what_the_document_writes_alone()
    {
        var invoice = Invoice("A");
        Assert.Equal(DocumentGenerator.Html(invoice, false, out _), DocumentGenerator.Html(Document.Merge(invoice), false, out _));
        Assert.Equal(DocumentGenerator.Html(invoice, false, out _),
            DocumentGenerator.Html(Document.Merge(invoice).UseContinuousPageNumbers(), false, out _));
    }

    [Fact]
    public void A_merge_takes_the_first_documents_metadata_unless_it_has_its_own()
    {
        var first = Invoice("A").WithMetadata(new DocumentMetadata { Title = "First" });
        var merged = Document.Merge(first, Invoice("B"));
        Assert.Equal("First", merged.GetMetadata().Title);
        Assert.Equal("Both", merged.WithMetadata(new DocumentMetadata { Title = "Both" }).GetMetadata().Title);
        Assert.Contains("<title>Both</title>", merged.GenerateHtml());
    }

    [Fact]
    public void A_merge_inside_a_merge_with_continuous_numbers_counts_on_with_it()
    {
        var inner = Document.Merge(Invoice("A"), Invoice("B"));
        var merged = Document.Merge(inner, Invoice("C")).UseContinuousPageNumbers();
        Assert.DoesNotContain("pdfpecker-document-", DocumentGenerator.Html(merged, false, out _));

        var apart = Document.Merge(Document.Merge(Invoice("A"), Invoice("B")).UseContinuousPageNumbers(), Invoice("C"));
        var pages = PdfText.ExtractPages(apart.GeneratePdf());
        Assert.Equal(6, pages.Count);
        Assert.Contains("Page 3 of 4", pages[2].Text);
        Assert.Contains("Page 1 of 2", pages[4].Text);
    }

    [Fact]
    public void Merge_checks_its_documents()
    {
        Assert.Throws<ArgumentException>(() => Document.Merge());
        Assert.Throws<ArgumentNullException>(() => Document.Merge((IEnumerable<IDocument>)null!));
        Assert.Throws<ArgumentNullException>(() => Document.Merge(Invoice("A"), null!));
    }

    [Fact]
    public void An_empty_document_is_one_blank_page()
        => Assert.Equal(1, PdfToImage.GetPageCount(Document.Create(_ => { }).GeneratePdf()));

    [Fact]
    public void The_same_image_is_stored_once()
    {
        byte[] logo = File.ReadAllBytes(Examples.AssetPath("logo.png"));
        string html = Html.Of(c => c.Column(col => { col.Item().Image(logo); col.Item().Image(logo); }));
        Assert.Equal(2, html.Split("src=\"fluent:img/0\"").Length - 1);
    }

    [Fact]
    public void Exported_html_carries_images_as_data_uris()
    {
        byte[] logo = File.ReadAllBytes(Examples.AssetPath("logo.png"));
        Assert.Contains("src=\"data:image/png;base64,", Html.Page(c => c.Image(logo)).GenerateHtml());
    }

    [Fact]
    public void A_leader_is_an_empty_span_with_one_rule_for_each_pattern()
    {
        var document = Html.Page(c => c.Text(t =>
        {
            t.Span("Intro");
            t.Leader();
            t.Span("1");
            t.Leader(LeaderStyle.Solid).FontColor(Colors.Grey.Medium);
            t.Leader();
            t.Leader("-</style>");
        }));
        string body = Html.Body(document.GenerateHtml());
        Assert.Equal("<main><p>Intro<span data-leader=\"0\"></span>1<span class=\"c0\" data-leader=\"1\"></span><span data-leader=\"0\"></span><span data-leader=\"2\"></span></p></main>", body);
        string css = Html.Css(document);
        Assert.Contains("[data-leader=\"0\"]::after{content:leader(dotted)}", css);
        Assert.Contains("[data-leader=\"1\"]::after{content:leader(solid)}", css);
        Assert.Contains("[data-leader=\"2\"]::after{content:leader(\"-\\3c /style\\3e \")}", css);
    }

    [Fact]
    public void A_leader_puts_the_text_after_it_at_the_end_of_the_line()
    {
        byte[] pdf = Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(50);
            p.Content().Column(c =>
            {
                c.Item().Text(t => { t.Span("Introduction"); t.Leader(); t.Span("3"); });
                c.Item().Text(t => { t.Span("Price list"); t.Leader("-"); t.Span("12.50"); });
            });
        })).GeneratePdf();
        var page = PdfText.ExtractPages(pdf)[0];
        Assert.Contains(". . .", page.Text);
        Assert.Contains("---", page.Text);
        foreach (string last in new[] { "3", "12.50" })
        {
            var word = page.Lines.SelectMany(l => l.Words).Last(w => w.Text.EndsWith(last, StringComparison.Ordinal));
            Assert.InRange(word.X + word.Width, page.Width - 50 - 2, page.Width - 50 + 0.5);
        }
    }

    [Fact]
    public void Bad_leader_patterns_throw()
    {
        Assert.ThrowsAny<ArgumentException>(() => Html.Of(c => c.Text(t => t.Leader(""))));
        Assert.ThrowsAny<ArgumentException>(() => Html.Of(c => c.Text(t => t.Leader(new string('.', 33)))));
        Assert.ThrowsAny<ArgumentException>(() => Html.Of(c => c.Text(t => t.Leader("a\nb"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Text(t => t.Leader((LeaderStyle)9))));
    }

    [Fact]
    public void Even_page_bands_replace_the_header_and_footer_on_even_pages()
    {
        var document = Document.Create(d => d.Page(p =>
        {
            p.Margin(30);
            p.Header().AlignRight().Text(t => { t.Span("Odd "); t.CurrentPageNumber(); });
            p.EvenPageHeader().Text(t => { t.Span("Even "); t.CurrentPageNumber(); });
            p.EvenPageFooter().Text("Even footer");
            p.Content().Column(c => { for (int i = 0; i < 3; i++) { c.Item().Text("Page body"); c.Item().PageBreak(); } c.Item().Text("Last"); });
        }));
        string html = document.GenerateHtml();
        Assert.Contains("<header data-sticky-pdf-even-header", html);
        Assert.Contains("<footer data-sticky-pdf-even-footer", html);
        var pages = PdfText.ExtractPages(document.GeneratePdf());
        Assert.Equal(4, pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            bool even = i % 2 == 1;
            Assert.Contains((even ? "Even " : "Odd ") + (i + 1), pages[i].Text);
            Assert.Equal(even, pages[i].Text.Contains("Even footer", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Even_page_bands_belong_to_their_page_set_and_count_through_the_document()
    {
        var pages = PdfText.ExtractPages(Document.Create(d =>
        {
            d.Page(p => { p.Header().Text("Cover"); p.Content().Text("cover"); });
            d.Page(p =>
            {
                p.Header().Text("Body odd");
                p.EvenPageHeader().Text("Body even");
                p.Content().Column(c => { c.Item().Text("a"); c.Item().PageBreak(); c.Item().Text("b"); });
            });
        }).GeneratePdf());
        Assert.Equal(3, pages.Count);
        Assert.Contains("Cover", pages[0].Text);
        Assert.Contains("Body even", pages[1].Text);
        Assert.Contains("Body odd", pages[2].Text);
    }

    [Fact]
    public void Language_writes_the_lang_attribute_and_an_inner_language_nests()
    {
        Assert.Equal("<p lang=\"da\">x</p>", Html.Item(c => c.Language("da").Text("x")));
        Assert.Equal("<div lang=\"da\"><p lang=\"en-GB\">x</p></div>", Html.Item(c => c.Language("da").Language("en-GB").Text("x")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-da")]
    [InlineData("da-")]
    [InlineData("da--DK")]
    [InlineData("1da")]
    [InlineData("da DK")]
    [InlineData("da\"><script>")]
    [InlineData("toolongsubtag")]
    public void Language_refuses_what_is_not_a_language_tag(string language)
        => Assert.Throws<ArgumentException>(() => Html.Item(c => c.Language(language).Text("x")));

    [Fact]
    public void Hyphenate_writes_hyphens_on_the_paragraph_and_on_spans()
    {
        Assert.Contains("{hyphens:auto}", Html.Css(Html.Page(c => c.DefaultTextStyle(s => s.Hyphenate()).Text("x"))));
        string css = Html.Css(Html.Page(c => c.Text(t =>
        {
            t.Span("a");
            t.Span("b").Hyphenate(false);
        })));
        Assert.Contains("hyphens:manual", css);
    }

    [Fact]
    public void Hyphenate_breaks_long_words_by_the_rules_of_the_language()
    {
        const string words = "Arbejdsmarkedsuddannelserne og sundhedsplejerskerne";
        string Render(bool hyphenate, string? language) => Html.Text(Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A6);
            var content = p.Content().Width(90);
            if (language != null) content = content.Language(language);
            content.Text(words).Hyphenate(hyphenate);
        })).GeneratePdf());

        Assert.Contains("-", Render(hyphenate: true, "da"), StringComparison.Ordinal);
        Assert.DoesNotContain("-", Render(hyphenate: false, "da"), StringComparison.Ordinal);
        Assert.DoesNotContain("-", Render(hyphenate: true, language: null), StringComparison.Ordinal);
    }
}
