using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Page-break control (break-*, orphans, widows), links and bookmarks.</summary>
public class PaginationTests
{
    private const float Width = 400, PageHeight = 200;

    private static List<DrawOp> Layout(string html, float pageHeight = PageHeight)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, pageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(pageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).ToList();
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static int Page(float y) => (int)Math.Floor((y + 0.01f) / PageHeight);

    private static List<TextOp> Lines(List<DrawOp> ops, string prefix)
        => ops.OfType<TextOp>().Where(t => t.Text.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    private const string Body = "body{margin:0} p{margin:0; font: 10pt/20pt Helvetica}";

    [Fact]
    public void Heading_moves_to_the_next_page_with_its_paragraph()
    {
        // 170pt of filler leaves 30pt: room for the heading but not for the paragraph's first line after it.
        const string html = "<style>" + Body + " h2{margin:0; font: bold 12pt/20pt Helvetica}</style>" +
                            "<div style='height:170pt'></div><h2>Heading</h2><p>Para text</p>";
        var ops = Layout(html);
        Assert.Equal(1, Page(Lines(ops, "Heading").Single().LineTop));
        Assert.Equal(1, Page(Lines(ops, "Para").Single().LineTop));

        // Without break-after: avoid the heading stays behind.
        var loose = Layout(html.Replace("h2{", "h2{break-after:auto; "));
        Assert.Equal(0, Page(Lines(loose, "Heading").Single().LineTop));
    }

    [Fact]
    public void Break_before_avoid_keeps_a_box_with_the_previous_one()
    {
        var ops = Layout("<style>" + Body + "</style><div style='height:170pt'></div>" +
                         "<p>First</p><p style='break-before:avoid'>Second</p>");
        Assert.Equal(1, Page(Lines(ops, "First").Single().LineTop));
        Assert.Equal(1, Page(Lines(ops, "Second").Single().LineTop));
    }

    [Fact]
    public void Keep_with_next_does_not_loop_when_the_chain_cannot_fit()
    {
        var ops = Layout("<style>" + Body + "</style><div style='height:170pt'></div>" +
                         "<p style='page-break-after:avoid'>Lead</p><div style='height:500pt'><p>Tall</p></div>");
        Assert.Single(Lines(ops, "Lead"));
        Assert.Single(Lines(ops, "Tall"));
    }

    [Fact]
    public void Orphans_move_a_paragraph_that_would_leave_one_line_behind()
    {
        string words = string.Join(" ", Enumerable.Repeat("word", 120)); // several 20pt lines
        var ops = Layout("<style>" + Body + "</style><div style='height:175pt'></div><p>" + words + "</p>");
        var lines = Lines(ops, "word");
        Assert.True(lines.Count >= 3);
        Assert.All(lines, l => Assert.Equal(1, Page(l.LineTop)));

        var single = Layout("<style>" + Body + " p{orphans:1}</style><div style='height:175pt'></div><p>" + words + "</p>");
        Assert.Equal(0, Page(Lines(single, "word")[0].LineTop));
    }

    [Fact]
    public void Widows_carry_two_lines_to_the_next_page()
    {
        // 10 lines of 20pt on a 200pt page after 20pt of filler: 9 fit, so 1 would be left alone on page 2.
        string text = string.Join("<br>", Enumerable.Range(1, 10).Select(i => "line" + i));
        var ops = Layout("<style>" + Body + "</style><div style='height:20pt'></div><p>" + text + "</p>");
        var lines = Lines(ops, "line");
        Assert.Equal(10, lines.Count);
        Assert.Equal(2, lines.Count(l => Page(l.LineTop) == 1));
        Assert.Equal(8, lines.Count(l => Page(l.LineTop) == 0));

        var none = Layout("<style>" + Body + " p{widows:1}</style><div style='height:20pt'></div><p>" + text + "</p>");
        Assert.Equal(1, Lines(none, "line").Count(l => Page(l.LineTop) == 1));
    }

    [Fact]
    public void Break_inside_avoid_moves_a_whole_table()
    {
        string rows = string.Concat(Enumerable.Range(1, 4).Select(i => $"<tr><td>row{i}</td></tr>"));
        var ops = Layout("<style>" + Body + " td{font: 10pt/20pt Helvetica}</style><div style='height:150pt'></div>" +
                         "<table style='page-break-inside:avoid'>" + rows + "</table>");
        Assert.All(Lines(ops, "row"), l => Assert.Equal(1, Page(l.LineTop)));
    }

    [Fact]
    public void Bookmarks_keep_document_order_when_a_grid_row_breaks_across_pages()
    {
        // The second row is laid out on the pages to see where its content starts before the first row is placed.
        string paras = string.Concat(Enumerable.Range(1, 6).Select(i => $"<p>Paragraph {i}</p>"));
        var pdf = ConvertUncompressed("<div style='display:grid'><div><h2>First</h2><div style='height:640pt'></div></div>" +
                                      "<div><h2>Second</h2>" + paras + "</div></div>");
        var text = Latin1(pdf);
        Assert.Matches(@"/Title \(First\) /Parent \d+ 0 R /Next \d+ 0 R", text);
        // The row starts on the first page and breaks there, so its heading does too.
        var pages = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page ").Select(m => m.Groups[1].Value).ToList();
        Assert.True(pages.Count > 1);
        Assert.Matches($@"/Title \(Second\) /Parent \d+ 0 R /Prev \d+ 0 R /Dest \[{pages[0]} 0 R", text);
    }

    [Fact]
    public void Bookmarks_follow_the_heading_levels()
    {
        var pdf = ConvertUncompressed("<h1>Intro</h1><h2>Part A</h2><h3>Detail</h3><h2>Part B</h2>" +
                                      "<h1 style='page-break-before:always'>Second</h1>");
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        Assert.Matches(@"/Type /Catalog /Pages \d+ 0 R /Outlines \d+ 0 R", text);
        Assert.Matches(@"/Type /Outlines /First \d+ 0 R /Last \d+ 0 R /Count 5", text);
        // "Intro" holds Part A (with Detail) and Part B: three descendants.
        Assert.Matches(@"/Title \(Intro\) /Parent \d+ 0 R /Next \d+ 0 R /First \d+ 0 R /Last \d+ 0 R /Count 3 /Dest \[\d+ 0 R /XYZ 0", text);
        Assert.Contains("/Title (Detail)", text);

        // The second h1 points at page 2.
        var pages = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page ").Select(m => m.Groups[1].Value).ToList();
        Assert.Matches($@"/Title \(Second\) /Parent \d+ 0 R /Prev \d+ 0 R /Dest \[{pages[1]} 0 R", text);

        var none = Latin1(ConvertUncompressed("<h1>Intro</h1>", new PdfOptions { Bookmarks = false }));
        Assert.DoesNotContain("/Outlines", none);
    }

    [Fact]
    public void Bookmark_titles_keep_non_latin_text()
    {
        var text = Latin1(ConvertUncompressed("<h1>Lønseddel  for\n Ærø</h1>"));
        // UTF-16 with a byte order mark, whitespace collapsed.
        Assert.Contains("/Title <FEFF", text);
    }

    [Fact]
    public void Block_level_links_are_clickable_and_split_across_pages()
    {
        var pdf = ConvertUncompressed("<style>body{margin:0}</style>" +
            "<a href='https://example.com/card' style='display:block'><div>Card</div></a>" +
            "<div style='height:700pt'></div>" +
            "<a href='https://example.com/long' style='display:block; height:300pt'>Long</a>");
        var text = Latin1(pdf);
        Assert.Single(Regex.Matches(text, @"/URI \(https://example.com/card\)"));
        Assert.Equal(2, Regex.Matches(text, @"/URI \(https://example.com/long\)").Count);
    }

    [Fact]
    public void Internal_links_jump_to_the_target_page()
    {
        var pdf = ConvertUncompressed("<p><a href='#s%C3%A6t'>go</a></p><h2 id='sæt' style='page-break-before:always'>Target</h2>");
        var text = Latin1(pdf);
        var pages = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page ").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Matches($@"/Subtype /Link /Rect \[[^\]]+\] /F 4 /Border \[0 0 0\] /Dest \[{pages[1]} 0 R /XYZ 0", text);
    }
}
