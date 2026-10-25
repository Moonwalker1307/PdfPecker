using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>PDF to HTML in the structured mode: tables, headings, paragraphs and lists in reading order.</summary>
public class PdfToHtmlStructuredTests
{
    private static readonly PdfHtmlOptions Structured = new() { Mode = PdfHtmlMode.Structured };

    private static byte[] Pdf(string html) => HtmlToPdf.Convert(html);

    private static string Html(byte[] pdf) => PdfToHtml.Convert(pdf, Structured);

    private static string Html(string html) => Html(Pdf(html));

    /// <summary>The body of the output, from its first page section on.</summary>
    private static string Body(string html) => html[html.IndexOf("<section", StringComparison.Ordinal)..];

    /// <summary>The cells of each table row as plain text, with tags removed and spaces collapsed.</summary>
    private static List<List<string>> Rows(string html)
        => Regex.Matches(html, "<tr>(.*?)</tr>").Select(r => Regex.Matches(r.Groups[1].Value, "<t[hd][^>]*>(.*?)</t[hd]>")
            .Select(c => Text(c.Groups[1].Value)).ToList()).ToList();

    private static string Text(string html)
        => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html.Replace("<br>", " ", StringComparison.Ordinal), "<[^>]+>", "")), @"\s+", " ").Trim();

    private static void HasRow(string html, params string[] cells)
        => Assert.Contains(Rows(html), r => r.SequenceEqual(cells));

    [Fact]
    public void A_bordered_table_becomes_a_table_with_its_cells()
    {
        var html = Html("""
            <style>td,th{border:1px solid #000;padding:4px}table{border-collapse:collapse}</style>
            <table><tr><th>Item</th><th>Qty</th><th>Price</th></tr>
            <tr><td>Apples</td><td>3</td><td>12.00</td></tr>
            <tr><td>Pears</td><td>5</td><td>7.50</td></tr></table>
            """);
        Assert.Single(Regex.Matches(html, "<table"));
        Assert.Contains("<thead>", html);
        HasRow(html, "Item", "Qty", "Price");
        HasRow(html, "Apples", "3", "12.00");
        HasRow(html, "Pears", "5", "7.50");
        Assert.Contains("<th>", html);
    }

    [Fact]
    public void Lines_with_amounts_aligned_on_the_right_become_a_table()
    {
        var html = Html("""
            <style>td{padding:2px 12px}.r{text-align:right}</style>
            <p>Invoice 1001</p>
            <table><tr><td><b>Description</b></td><td class="r"><b>Hours</b></td><td class="r"><b>Amount</b></td></tr>
            <tr><td>Design work</td><td class="r">12</td><td class="r">9,600.00</td></tr>
            <tr><td>Development</td><td class="r">40</td><td class="r">32,000.00</td></tr>
            <tr><td>Testing</td><td class="r">8</td><td class="r">6,400.00</td></tr>
            <tr><td>Project management</td><td class="r">6</td><td class="r">4,800.00</td></tr></table>
            """);
        Assert.Contains("<table", html);
        HasRow(html, "Design work", "12", "9,600.00");
        HasRow(html, "Project management", "6", "4,800.00");
        Assert.Contains("<p>Invoice 1001</p>", html);
    }

    [Fact]
    public void Spanned_cells_keep_their_spans()
    {
        var html = Html("""
            <style>td{border:1px solid #000;padding:4px}table{border-collapse:collapse}</style>
            <table><tr><td colspan="2">Both</td><td>C</td></tr>
            <tr><td rowspan="2">Tall</td><td>B1</td><td>C1</td></tr>
            <tr><td>B2</td><td>C2</td></tr></table>
            """);
        Assert.Matches("<td colspan=\"2\">Both</td>", html);
        Assert.Matches("<td rowspan=\"2\">Tall</td>", html);
        HasRow(html, "B2", "C2");
    }

    [Fact]
    public void Text_flows_with_nothing_placed_at_a_position()
    {
        var html = Html("""
            <h1>Annual report</h1>
            <h2>Summary</h2>
            <p>The year went well. Sales grew in every region and the new products sold better than planned, so the
            company ends the year with more customers than ever before.</p>
            <p>Next year we will open two new offices.</p>
            """);
        var body = Body(html);
        Assert.DoesNotContain("position:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("left:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Annual report</h1>", body);
        Assert.Matches("<h[23]>Summary</h[23]>", body);
        // The wrapped paragraph is one paragraph again, with no line breaks in it.
        Assert.Contains("<p>The year went well. Sales grew in every region and the new products sold better than planned, so the company ends the year with more customers than ever before.</p>", body);
        Assert.Contains("<p>Next year we will open two new offices.</p>", body);
    }

    [Fact]
    public void Lists_become_lists()
    {
        // Bullets written as text; bullets HtmlToPdf draws as shapes have no text to find.
        var html = Body(Html("""
            <div>• Milk</div><div>• Bread</div>
            <p>Then:</p>
            <ol start="3"><li>Pay at the till</li><li>Go home</li></ol>
            """));
        Assert.Matches("<ul>\\s*<li>Milk</li>\\s*<li>Bread</li>\\s*</ul>", html);
        Assert.Matches("<ol>\\s*<li value=\"3\">Pay at the till</li>\\s*<li value=\"4\">Go home</li>\\s*</ol>", html);
    }

    [Fact]
    public void Bullets_drawn_as_shapes_make_list_items()
    {
        var html = Body(Html("<ul><li>Milk</li><li>Bread</li></ul><ul style=\"list-style:square\"><li>Butter</li><li>Cheese</li></ul>"));
        Assert.Matches("<ul>\\s*<li>Milk</li>\\s*<li>Bread</li>\\s*<li>Butter</li>\\s*<li>Cheese</li>\\s*</ul>", html);
    }

    [Fact]
    public void Dot_leaders_and_letter_spacing_are_removed()
    {
        var html = Body(Html("""
            <h2 style="letter-spacing:6px">SERVICE AGREEMENT</h2>
            <table><tr><td>Introduction . . . . . . . . . . . . . . . .</td><td>3</td></tr>
            <tr><td>Terms and conditions . . . . . . . . . .</td><td>7</td></tr>
            <tr><td>Signatures . . . . . . . . . . . . . . . . . .</td><td>12</td></tr></table>
            """));
        Assert.Contains(">SERVICE AGREEMENT</h", html);
        HasRow(html, "Introduction", "3");
        HasRow(html, "Terms and conditions", "7");
    }

    [Fact]
    public void A_label_over_two_columns_makes_a_two_row_header()
    {
        var html = Html("""
            <style>table{border-collapse:collapse}th,td{border:1px solid #000;padding:3px}</style>
            <table><tr><th rowspan="2">State</th><th colspan="2">Revenue</th><th colspan="2">Capital</th></tr>
            <tr><th>Medical</th><th>Family</th><th>Medical</th><th>Family</th></tr>
            <tr><td>North</td><td>10</td><td>20</td><td>30</td><td>40</td></tr>
            <tr><td>South</td><td>11</td><td>21</td><td>31</td><td>41</td></tr></table>
            """);
        Assert.Matches("<th[^>]*colspan=\"2\"[^>]*>Revenue</th>", html);
        HasRow(html, "Medical", "Family", "Medical", "Family");
        HasRow(html, "South", "11", "21", "31", "41");
    }

    [Fact]
    public void Lists_side_by_side_are_read_one_after_the_other()
    {
        var html = Body(Html("""
            <div style="display:flex;gap:40px"><div><b>Delivery terms</b><ol><li>Incoterms DAP Aarhus.</li><li>Delivery by week 46.</li></ol></div>
            <div><b>Payment terms</b><ol><li>30 days net.</li><li>2% discount within 10 days.</li></ol></div></div>
            """));
        Assert.Matches("<li value=\"1\">Incoterms DAP Aarhus.</li>\\s*<li value=\"2\">Delivery by week 46.</li>", html);
        Assert.Matches("<li value=\"1\">30 days net.</li>\\s*<li value=\"2\">2% discount within 10 days.</li>", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public void Links_are_kept()
    {
        var html = Body(Html("<p>Read the <a href=\"https://example.com/terms\">terms</a> first.</p>"));
        Assert.Contains("<a href=\"https://example.com/terms\">terms</a>", html);
    }

    [Fact]
    public void Each_page_is_a_section_with_an_id()
    {
        var html = Html("<p>First page</p><div style=\"page-break-before:always\">Second page</div>");
        Assert.Contains("<section class=\"pdf-page\" id=\"page-1\">", html);
        Assert.Contains("<section class=\"pdf-page\" id=\"page-2\">", html);
        Assert.True(html.IndexOf("First page", StringComparison.Ordinal) < html.IndexOf("id=\"page-2\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Second page", StringComparison.Ordinal) > html.IndexOf("id=\"page-2\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Prose_in_two_columns_is_not_a_table()
    {
        const string text = "This is running text that fills its column and wraps onto the next line in lower case, as articles do. ";
        var html = Html($"""
            <div style="columns:2;column-gap:24pt"><p>{string.Concat(Enumerable.Repeat(text, 6))}</p><p>{string.Concat(Enumerable.Repeat(text, 6))}</p></div>
            """);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
        Assert.Contains("articles do. This is running", html, StringComparison.Ordinal);
    }

    [Fact]
    public void No_word_is_lost()
    {
        var pdf = Pdf("""
            <style>td{border:1px solid #000;padding:3px}.n td{border:0}.n td+td{text-align:right}</style>
            <h1>Statement</h1>
            <div style="display:flex;justify-content:space-between"><div>Bank of Example<br>1 Main Street</div><div>Account 1234<br>Date 2026-10-01</div></div>
            <table><tr><td>Date</td><td>Text</td><td>Amount</td></tr><tr><td>01.10</td><td>Rent</td><td>-8,000.00</td></tr>
            <tr><td>02.10</td><td>Salary</td><td>31,250.00</td></tr></table>
            <table class="n"><tr><td>Opening balance</td><td>1,000.00</td></tr><tr><td>Closing balance</td><td>24,250.00</td></tr><tr><td>Fees</td><td>0.00</td></tr></table>
            <ul><li>Questions? Call us.</li><li>Keep this statement.</li></ul>
            <p>Thank you for banking with us, ﬁne print and all, æøå.</p>
            """);
        static Dictionary<char, int> Count(string s)
            => s.Where(char.IsLetterOrDigit).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var html = Body(Html(pdf));
        html = Regex.Replace(html, "<li value=\"(\\d+)\">", " $1 ");
        var fromHtml = Count(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));
        var fromText = Count(PdfText.Extract(pdf));
        Assert.Equal(fromText.OrderBy(p => p.Key), fromHtml.OrderBy(p => p.Key));
    }

    [Fact]
    public void Visual_stays_the_default()
    {
        var pdf = Pdf("<p>Hello</p>");
        Assert.Equal(PdfHtmlMode.Visual, new PdfHtmlOptions().Mode);
        var visual = PdfToHtml.Convert(pdf);
        Assert.Equal(visual, PdfToHtml.Convert(pdf, new PdfHtmlOptions { Mode = PdfHtmlMode.Visual }));
        Assert.Contains("<div class=\"pdf-page\"", visual);
        Assert.DoesNotContain("<section", visual, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_options_do_not_apply_to_structured_html()
    {
        var pdf = Pdf("<p>Hello</p>");
        Assert.Throws<ArgumentException>(() => PdfToHtml.Convert(pdf, new PdfHtmlOptions { ImageFormat = PdfImageFormat.Tiff }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfToHtml.Convert(pdf, new PdfHtmlOptions { Dpi = 0 }));
        var html = PdfToHtml.Convert(pdf, new PdfHtmlOptions { Mode = PdfHtmlMode.Structured, ImageFormat = PdfImageFormat.Tiff, Dpi = 0 });
        Assert.Contains("<p>Hello</p>", html);
    }

    [Fact]
    public void Output_is_the_same_every_time()
    {
        var pdf = Pdf("<table><tr><td>a</td><td>1</td></tr><tr><td>b</td><td>2</td></tr></table><p>End</p>");
        Assert.Equal(Html(pdf), Html(pdf));
    }

    private static string Tagged(string html) => Html(HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.Tagged }));

    [Fact]
    public void A_tagged_table_keeps_its_header_spans_and_empty_cells()
    {
        // No borders and an empty cell: the layout alone could not tell all of this, the tags do.
        var html = Tagged("""
            <table><thead><tr><th rowspan="2">Region</th><th colspan="2">Sales</th></tr><tr><th>2025</th><th>2026</th></tr></thead>
            <tbody><tr><td>North</td><td></td><td>12</td></tr><tr><td>South</td><td>7</td><td>9</td></tr></tbody>
            <tfoot><tr><td>Total</td><td>7</td><td>21</td></tr></tfoot></table>
            """);
        Assert.Matches("<thead>\\s*<tr><th rowspan=\"2\">Region</th><th colspan=\"2\">Sales</th></tr>\\s*<tr><th>2025</th><th>2026</th></tr>\\s*</thead>", html);
        HasRow(html, "North", "", "12");
        HasRow(html, "South", "7", "9");
        HasRow(html, "Total", "7", "21");
    }

    [Fact]
    public void Tagged_lists_keep_their_numbering()
    {
        var html = Body(Tagged("""
            <ol start="9"><li>nine</li><li>ten</li></ol>
            <ol type="a"><li>alpha</li><li>beta</li></ol>
            <ol type="I"><li>one</li><li>two</li><li>three</li></ol>
            <ul><li>milk</li></ul>
            """));
        Assert.Matches("<ol>\\s*<li value=\"9\">nine</li>\\s*<li value=\"10\">ten</li>\\s*</ol>", html);
        Assert.Matches("<ol type=\"a\">\\s*<li value=\"1\">alpha</li>\\s*<li value=\"2\">beta</li>\\s*</ol>", html);
        Assert.Matches("<ol type=\"I\">\\s*<li value=\"1\">one</li>\\s*<li value=\"2\">two</li>\\s*<li value=\"3\">three</li>\\s*</ol>", html);
        Assert.Matches("<ul>\\s*<li>milk</li>\\s*</ul>", html);
    }

    [Fact]
    public void Tagged_headings_and_paragraphs_follow_the_tags()
    {
        // Headings no larger than the text, and paragraphs with no space between them, which the layout would merge.
        var html = Body(Tagged("""
            <style>h1,h2{font-size:12pt;margin:0}p{margin:0}</style>
            <h1>Offer</h1><h2>Terms</h2><p>Payment is due in thirty days.</p><p>Goods remain ours until paid.</p>
            """));
        Assert.Contains("<h1>Offer</h1>", html);
        Assert.Contains("<h2>Terms</h2>", html);
        Assert.Contains("<p>Payment is due in thirty days.</p>", html);
        Assert.Contains("<p>Goods remain ours until paid.</p>", html);
    }

    [Fact]
    public void A_text_shadow_in_a_tagged_pdf_is_read_once()
    {
        var html = Body(Tagged("<p style=\"text-shadow:1px 1px 0 red\">Shadowed words</p>"));
        Assert.Single(Regex.Matches(html, "Shadowed words"));
    }
}
