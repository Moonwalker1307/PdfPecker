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
    public void Page_numbers_outside_a_header_or_footer_fail()
        => Assert.Throws<InvalidOperationException>(() => Html.Page(c => c.Text(t => t.CurrentPageNumber())).GeneratePdf());

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
}
