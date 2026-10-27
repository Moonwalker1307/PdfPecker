using System.Text.RegularExpressions;
using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Named pages, running headers (string-set), target-counter() with leaders, and conic gradients.</summary>
public class PagedMediaTests
{
    /// <summary>The page sizes in page order, as "width x height" rounded to points.</summary>
    private static List<string> PageSizes(byte[] pdf)
        => Regex.Matches(Latin1(pdf), @"/Type /Page /Parent \d+ 0 R /MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")
            .Select(m => $"{Math.Round(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))}x"
                         + $"{Math.Round(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))}")
            .ToList();

    /// <summary>The text-drawing content streams in page order (uncompressed output).</summary>
    private static List<string> PageTexts(byte[] pdf)
    {
        var text = Latin1(pdf);
        var pages = new List<string>();
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+) >>\nstream\n"))
        {
            var data = text.Substring(m.Index + m.Length, int.Parse(m.Groups[1].Value));
            if (data.Contains(" Tf", StringComparison.Ordinal)) pages.Add(data);
        }
        return pages;
    }

    // ---- named pages ----

    [Fact]
    public void Named_pages_get_their_own_size_and_start_a_new_page()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { size: A4 }
            @page wide { size: A4 landscape; margin: 10mm }
            .wide { page: wide }
          </style><p>portrait</p><div class='wide'><p>landscape one</p></div><div class='wide'><p>landscape two</p></div><p>back</p>");
        Assert.Equal(new[] { "595x842", "842x595", "595x842" }, PageSizes(pdf));
        var pages = PageTexts(pdf);
        Assert.Contains("(landscape one)", pages[1]);
        Assert.Contains("(landscape two)", pages[1]); // consecutive boxes on the same named page share it
        Assert.Contains("(back)", pages[2]);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void A_page_name_without_a_rule_still_breaks_the_page()
    {
        var pdf = ConvertUncompressed("<p>one</p><p style='page: other'>two</p><p>three</p>");
        Assert.Equal(3, PageCount(pdf));
        Assert.Equal(new[] { "595x842", "595x842", "595x842" }, PageSizes(pdf));
    }

    [Fact]
    public void Named_pages_inside_containers_split_only_where_the_name_changes()
    {
        var pdf = ConvertUncompressed(@"<style>@page land { size: 300pt 200pt; margin: 10pt } section { page: land }</style>
            <main><h1>Title</h1><section><p>a</p></section><section><p>b</p></section></main><footer>end</footer>");
        Assert.Equal(new[] { "595x842", "300x200", "595x842" }, PageSizes(pdf));
    }

    [Fact]
    public void A_forced_break_at_the_start_of_a_page_run_adds_no_blank_page()
    {
        var pdf = ConvertUncompressed(@"<style>@page wide { size: A4 landscape } h2 { break-before: page }</style>
            <h2>first</h2><p>text</p><div style='page: wide'><h2>wide</h2></div>");
        Assert.Equal(new[] { "595x842", "842x595" }, PageSizes(pdf));
    }

    [Fact]
    public void Named_page_rules_apply_their_margin_boxes_and_background()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { @top-center { content: 'Plain header' } }
            @page cover { background: #ff0000; @top-center { content: none } }
          </style><div style='page: cover'>Cover</div><p>Body</p>");
        var pages = PageTexts(pdf);
        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain("Plain header", pages[0]);
        Assert.Contains("1 0 0 rg", pages[0]);
        Assert.Contains("(Plain header)", pages[1]);
        Assert.DoesNotContain("1 0 0 rg", pages[1]);
    }

    [Fact]
    public void Page_selectors_parse_names_pseudo_classes_and_lists()
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet("@page wide, chapter:first { margin: 1cm } @page :left { margin: 2cm } @page bad name { margin: 0 }", sheet);
        Assert.Equal(new[] { ("wide", ""), ("chapter", ":first"), ("", ":left") }, sheet.PageRules.Select(r => (r.Name, r.Selector)));
        Assert.Equal(4, sheet.PageRules[0].Specificity);
        Assert.Equal(6, sheet.PageRules[1].Specificity);
        Assert.Empty(sheet.PageDeclarations);
    }

    [Fact]
    public void Links_and_bookmarks_point_into_named_page_runs()
    {
        var pdf = ConvertUncompressed(@"<style>@page wide { size: A4 landscape }</style>
            <p><a href='#t'>to the table</a></p><div style='page: wide'><h1 id='t'>Table</h1></div>",
            new PdfOptions { Bookmarks = true });
        var text = Latin1(pdf);
        // The destination is the second page, at the top of a landscape page (595pt high).
        var page2 = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page ")[1].Groups[1].Value;
        Assert.Matches($@"/Dest \[{page2} 0 R /XYZ 0 5\d\d(\.\d+)? null\]", text);
        Assert.Contains("/Title (Table)", text);
    }

    // ---- running headers ----

    [Fact]
    public void Running_strings_follow_the_chapters()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { @top-center { content: 'H:' string(chapter) } @bottom-center { content: 'L:' string(chapter, last) } }
            h1 { string-set: chapter content() }
            .b { break-before: page }
          </style><h1>Alpha</h1><p>a</p><p class='b'>still alpha</p><h1>Beta</h1><h1>Gamma</h1>");
        var pages = PageTexts(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Contains("(H:Alpha)", pages[0]);
        Assert.Contains("(L:Alpha)", pages[0]);
        Assert.Contains("(H:Beta)", pages[1]); // first: the first assignment on the page
        Assert.Contains("(L:Gamma)", pages[1]);
    }

    [Fact]
    public void Running_string_policies_start_and_first_except()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { @top-left { content: '[' string(t, start) ']' } @top-right { content: '<' string(t, first-except) '>' } }
            h1 { string-set: t content(); break-before: page; margin-top: 0 }
            body { margin: 0 }
          </style><h1>One</h1><p>x</p><h1>Six</h1><p style='break-before: page'>y</p>");
        var pages = PageTexts(pdf);
        Assert.Equal(3, pages.Count);
        Assert.Contains("([One])", pages[0]); // start: the assignment at the top of the page
        Assert.Contains("(<>)", pages[0]);    // first-except: empty on a page with an assignment
        Assert.Contains("([Six])", pages[1]);
        Assert.Contains("(<>)", pages[1]);
        Assert.Contains("([Six])", pages[2]);
        Assert.Contains("(<Six>)", pages[2]);
    }

    [Fact]
    public void String_set_takes_literal_and_attribute_values()
    {
        var pdf = ConvertUncompressed(@"<style>
            @page { @top-center { content: string(doc) ' / ' string(sec) } }
            body { string-set: doc 'Manual' }
            section { string-set: sec attr(data-title) ' (' counter(s) ')'; counter-increment: s }
          </style><body><section data-title='Setup'>x</section></body>");
        Assert.Contains("(Manual / Setup \\(1\\))", AllContent(pdf));
    }

    // ---- target-counter and leaders ----

    [Fact]
    public void Target_counter_shows_the_page_of_the_linked_element()
    {
        var pdf = ConvertUncompressed(@"<style>
            .toc a::after { content: ' p. ' target-counter(attr(href), page) }
            .toc a.r::after { content: ' ' target-counter(attr(href), page, lower-roman) }
            h2 { break-before: page }
          </style>
          <div class='toc'><a href='#a'>A</a> <a href='#b'>B</a> <a class='r' href='#c'>C</a> <a href='#missing'>M</a></div>
          <h2 id='a'>A</h2><h2 id='b'>B</h2><p style='height: 900px'>tall</p><h2 id='c'>C</h2>");
        var content = AllContent(pdf);
        Assert.Contains("( p. 2)", content);
        Assert.Contains("( p. 3)", content);
        Assert.Contains("( iv)", content); // the tall paragraph after B takes a page of its own
        Assert.Contains("( p. 0)", content); // a link to no element
    }

    [Fact]
    public void Target_counter_reads_other_counters_and_target_text_reads_the_text()
    {
        var pdf = ConvertUncompressed(@"<style>
            h2 { counter-increment: chap }
            a.n::after { content: 'Chapter ' target-counter(attr(href), chap) }
            a.t::after { content: '<' target-text(attr(href)) '>' }
          </style><a class='n' href='#z'></a><a class='t' href='#z'></a><h2>One</h2><h2 id='z'>Six</h2>");
        var content = AllContent(pdf);
        Assert.Contains("(Chapter 2)", content);
        Assert.Contains("(<Six>)", content);
    }

    [Fact]
    public void Leaders_fill_the_line_up_to_the_page_number()
    {
        var pdf = ConvertUncompressed(@"<style>
            body { margin: 0; font: 12pt Helvetica } @page { size: 400pt 400pt; margin: 0 }
            a::after { content: leader(dotted) target-counter(attr(href), page) }
            a.s::after { content: leader('-') target-counter(attr(href), page) }
          </style><body><div><a href='#x'>Intro</a></div><div><a class='s' href='#x'>Next</a></div><p id='x'>x</p></body>");
        var content = AllContent(pdf);
        // The number ends at the right edge: its "1" starts one digit's width (6.672pt) before 400pt.
        Assert.Matches(@"393\.3\d* \d+(\.\d+)? Tm \(1\)", content);
        var dots = Regex.Match(content, @"\(((?:\. )+)\.? ?\)");
        Assert.True(dots.Success && dots.Groups[1].Length > 100, "dot leader missing");
        Assert.Matches(@"\(-{20,}\)", content);
    }

    [Fact]
    public void Leaders_outside_generated_content_are_plain_text()
    {
        var pdf = ConvertUncompressed("<p>a￹b￻c</p>");
        Assert.Equal(1, PageCount(pdf));
    }

    // ---- conic gradients ----

    [Fact]
    public void Conic_gradients_are_function_shadings()
    {
        var pdf = ConvertUncompressed(@"<div style='width: 100px; height: 100px; background: conic-gradient(from 90deg at 25% 50%, red, blue 50%, red)'></div>
            <div style='width: 100px; height: 100px; background: repeating-conic-gradient(#000 0 25%, #fff 0 50%)'></div>");
        var text = Latin1(pdf);
        Assert.Equal(2, Regex.Matches(text, "/ShadingType 1 ").Count);
        Assert.Equal(2, Regex.Matches(text, "/FunctionType 4 ").Count);
        Assert.Contains("atan", text);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Conic_gradient_parsing_accepts_angles_and_percentages()
    {
        static CssGradient? Parse(string v) => CssGradient.Parse(v, 0xFF000000, 12, 12, 5, 5);
        Assert.True(Parse("conic-gradient(red 0deg, blue 0.5turn, green 75%, red)")!.Conic);
        Assert.True(Parse("conic-gradient(from 45deg at left top in oklch, red, blue)")!.Conic);
        Assert.True(Parse("repeating-conic-gradient(red 0 10%, blue 0 20%)") is { Conic: true, Repeating: true });
        Assert.Null(Parse("conic-gradient(from sideways, red)"));
        Assert.True(CssSupports.Evaluate("(background: conic-gradient(red, blue))"));
        Assert.True(CssSupports.Evaluate("(string-set: x content())"));
        Assert.True(CssSupports.Evaluate("(page: wide)"));
    }

    [Fact]
    public void Named_pages_work_with_bands_scale_tagging_forms_and_watermarks()
    {
        const string html = @"<html lang='en'><head><title>Mixed</title><style>@page wide { size: A4 landscape }</style></head><body>
            <div data-sticky-pdf-header='true' height='20pt'>Header {page} of {total-pages}</div>
            <h1>One</h1><p><input name='a' value='x'></p>
            <section style='page: wide'><h2>Wide</h2><p><input name='b' value='y'></p></section>
            <p>Back</p></body></html>";
        var pdf = ConvertUncompressed(html, new PdfOptions
        {
            Scale = 0.9f, Accessibility = PdfAccessibility.PdfUA1, Bookmarks = true,
            Watermark = new PdfWatermark { Text = "DRAFT" },
        });
        Assert.Equal(new[] { "595x842", "842x595", "595x842" }, PageSizes(pdf));
        Assert.Equal(2, Regex.Matches(Latin1(pdf), "/FT /Tx").Count);
        AssertStructurallyValid(pdf);
        // Without PDF/UA's embedded fonts the band text is readable in the content.
        var plain = ConvertUncompressed(html);
        Assert.Contains("(Header 2 of 3)", AllContent(plain));
        Assert.Equal(new[] { "595x842", "842x595", "595x842" }, PageSizes(plain));
    }

    // ---- first-page header and footer ----

    /// <summary>The text of each page, with the page breaks of a long document of numbered paragraphs.</summary>
    private static string[] TextOfPages(string bands, PdfOptions? options = null)
    {
        var body = string.Concat(Enumerable.Range(1, 80).Select(i => $"<p>Line {i}</p>"));
        return PdfText.ExtractPages(HtmlToPdf.Convert($"<html><body>{bands}{body}</body></html>", options ?? new PdfOptions()))
            .Select(p => p.Text).ToArray();
    }

    [Fact]
    public void First_page_header_replaces_the_running_header_on_page_one_only()
    {
        var pages = TextOfPages("<div data-sticky-pdf-header>Running {page}</div><div data-sticky-pdf-first-header><h1>Letterhead</h1></div>");
        Assert.True(pages.Length >= 2);
        Assert.Contains("Letterhead", pages[0]);
        Assert.DoesNotContain("Running", pages[0]);
        for (int p = 1; p < pages.Length; p++)
        {
            Assert.Contains($"Running {p + 1}", pages[p]);
            Assert.DoesNotContain("Letterhead", pages[p]);
        }
    }

    [Fact]
    public void Empty_first_page_bands_leave_page_one_without_header_and_footer()
    {
        var pages = TextOfPages("<div data-sticky-pdf-header>Head</div><div data-sticky-pdf-footer>Foot {page}</div>"
                                + "<div data-sticky-pdf-first-header></div><div data-sticky-pdf-first-footer></div>");
        Assert.DoesNotContain("Head", pages[0]);
        Assert.DoesNotContain("Foot", pages[0]);
        Assert.Contains("Head", pages[1]);
        Assert.Contains("Foot 2", pages[1]);
    }

    [Fact]
    public void First_page_band_alone_shows_only_on_page_one_and_the_flow_uses_the_space_after()
    {
        var pages = TextOfPages("<div data-sticky-pdf-first-footer style='height: 200pt'>Only first</div>");
        var plain = TextOfPages("");
        Assert.Contains("Only first", pages[0]);
        Assert.DoesNotContain("Only first", string.Concat(pages.Skip(1)));
        // Page one holds fewer lines than without the footer; the second page holds as many as a plain page.
        int Lines(string page) => Regex.Matches(page, @"Line \d+").Count;
        Assert.True(Lines(pages[0]) < Lines(plain[0]));
        Assert.Equal(Lines(plain[1]), Lines(pages[1]));
    }

    [Fact]
    public void First_page_bands_work_with_first_page_rules_and_named_pages()
    {
        const string html = @"<html><head><style>@page :first { margin-top: 30mm } @page wide { size: A4 landscape }</style></head><body>
            <div data-sticky-pdf-header>Running {page} of {total-pages}</div><div data-sticky-pdf-first-header>First</div>
            <p>One</p><section style='page: wide'><p>Wide</p></section></body></html>";
        var pdf = HtmlToPdf.Convert(html);
        var pages = PdfText.ExtractPages(pdf).Select(p => p.Text).ToArray();
        Assert.Equal(2, pages.Length);
        Assert.Contains("First", pages[0]);
        Assert.Contains("Running 2 of 2", pages[1]);
        Assert.Equal(new[] { "595x842", "842x595" }, PageSizes(ConvertUncompressed(html)));
    }

    private static string Lines(string prefix, int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"<p>{prefix} {i}</p>"));

    [Fact]
    public void Named_pages_have_bands_of_their_own_and_numbers_run_on()
    {
        string html = "<html><head><style>@page cover { size: A5 } @page body { size: A4 landscape }</style></head><body>"
                      + "<div data-sticky-pdf-header data-sticky-pdf-page='cover'>Cover header</div>"
                      + "<div data-sticky-pdf-footer data-sticky-pdf-page='cover'>Cover page {page} of {total-pages}</div>"
                      + "<div data-sticky-pdf-header data-sticky-pdf-page='body'>Body header</div>"
                      + "<div data-sticky-pdf-footer data-sticky-pdf-page='body'>Body page {page} of {total-pages}</div>"
                      + "<section style='page: cover'>" + Lines("Cover", 40) + "</section>"
                      + "<section style='page: body'>" + Lines("Body", 60) + "</section></body></html>";
        var pages = PdfText.ExtractPages(HtmlToPdf.Convert(html)).Select(p => p.Text).ToArray();
        int coverPages = pages.Count(p => p.Contains("Cover 1", StringComparison.Ordinal) || p.Contains("Cover header", StringComparison.Ordinal));
        Assert.True(coverPages >= 2 && pages.Length > coverPages + 1);
        for (int p = 0; p < pages.Length; p++)
        {
            bool cover = p < coverPages;
            Assert.Contains(cover ? "Cover header" : "Body header", pages[p]);
            Assert.DoesNotContain(cover ? "Body header" : "Cover header", pages[p]);
            Assert.Contains($"{(cover ? "Cover" : "Body")} page {p + 1} of {pages.Length}", pages[p]);
        }
        var sizes = PageSizes(ConvertUncompressed(html));
        Assert.All(sizes.Take(coverPages), s => Assert.Equal("420x595", s));
        Assert.All(sizes.Skip(coverPages), s => Assert.Equal("842x595", s));
    }

    [Fact]
    public void A_named_page_without_a_band_of_its_own_uses_the_documents()
    {
        string html = "<html><body><div data-sticky-pdf-header>Document header</div><div data-sticky-pdf-footer>Document footer</div>"
                      + "<div data-sticky-pdf-header data-sticky-pdf-page='b'>B header</div>"
                      + "<section style='page: a'><p>A</p></section><section style='page: b'><p>B</p></section>"
                      + "<section><p>C</p></section></body></html>";
        var pages = PdfText.ExtractPages(HtmlToPdf.Convert(html)).Select(p => p.Text).ToArray();
        Assert.Equal(3, pages.Length);
        Assert.Contains("Document header", pages[0]);
        Assert.Contains("B header", pages[1]);
        Assert.DoesNotContain("Document header", pages[1]);
        Assert.Contains("Document footer", pages[1]);
        Assert.Contains("Document header", pages[2]);
    }

    [Fact]
    public void A_named_pages_first_page_bands_show_on_the_first_page_of_each_of_its_runs()
    {
        string html = "<html><body>"
                      + "<div data-sticky-pdf-header data-sticky-pdf-page='letter'>Continued</div>"
                      + "<div data-sticky-pdf-first-header data-sticky-pdf-page='letter'>Letterhead</div>"
                      + "<section style='page: letter'>" + Lines("First letter", 70) + "</section>"
                      + "<section style='page: other'><p>Between</p></section>"
                      + "<section style='page: letter'>" + Lines("Second letter", 70) + "</section></body></html>";
        var pages = PdfText.ExtractPages(HtmlToPdf.Convert(html)).Select(p => p.Text).ToArray();
        int between = Array.FindIndex(pages, p => p.Contains("Between", StringComparison.Ordinal));
        Assert.True(between >= 2 && pages.Length >= between + 3);
        Assert.Contains("Letterhead", pages[0]);
        Assert.Contains("Continued", pages[1]);
        Assert.DoesNotContain("Letterhead", pages[between]);
        Assert.DoesNotContain("Continued", pages[between]);
        Assert.Contains("Letterhead", pages[between + 1]);
        Assert.DoesNotContain("Continued", pages[between + 1]);
        Assert.Contains("Continued", pages[between + 2]);
    }

    [Fact]
    public void A_named_pages_band_does_not_reach_the_flow_of_other_pages()
    {
        string html = "<html><body><div data-sticky-pdf-header data-sticky-pdf-page='x' style='height: 300pt'>X header</div>"
                      + Lines("Plain", 80) + "</body></html>";
        var withBand = PdfText.ExtractPages(HtmlToPdf.Convert(html)).Select(p => p.Text).ToArray();
        var plain = PdfText.ExtractPages(HtmlToPdf.Convert("<html><body>" + Lines("Plain", 80) + "</body></html>")).Select(p => p.Text).ToArray();
        Assert.Equal(plain, withBand);
    }

    [Theory]
    [InlineData("<div class='i' style='break-after: page'>A</div><div class='i'>B</div><div class='i'>C</div><div class='i'>D</div>")]
    [InlineData("<div class='i'>A</div><div class='i'>B</div><div class='i' style='break-before: page'>C</div><div class='i'>D</div>")]
    public void A_forced_break_on_a_flex_item_breaks_between_its_line_and_the_next(string items)
    {
        // As in Chrome, the break is between the lines: B stays beside A, and the line with C opens the next page.
        var html = "<style>@page { size: 300pt 300pt; margin: 0 } body { margin: 0 } .i { width: 45% }</style>"
                   + $"<p>Before</p><div style='display: flex; flex-wrap: wrap'>{items}</div><p>After</p>";
        var pages = PdfText.ExtractPages(HtmlToPdf.Convert(html)).Select(p => p.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        Assert.Equal(2, pages.Length);
        Assert.Equal(new[] { "Before", "A", "B" }, pages[0]);
        Assert.Equal(new[] { "C", "D", "After" }, pages[1]);
    }
}
