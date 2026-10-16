using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>CSS footnotes: float: footnote, ::footnote-call, ::footnote-marker, the footnote counter and @footnote.</summary>
public class FootnoteTests
{
    private const string Style = @"<style>
        body { margin: 0; font-size: 11pt; line-height: 14pt }
        p { margin: 0 0 6pt }
        .fn { float: footnote; font-size: 8pt; line-height: 10pt }
        @page { @footnote { border-top: 1pt solid black; padding-top: 4pt; margin-top: 6pt } }
      </style>";

    private const string Lorem = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore "
                                 + "magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.";

    /// <summary>A small page, so a few paragraphs fill it: 260pt of flow, 260pt wide.</summary>
    private static PdfOptions SmallPage(PdfAccessibility accessibility = PdfAccessibility.None)
        => new() { PageSize = new PageSize(300, 300), Margins = new PageMargins(20), Accessibility = accessibility };

    private sealed record Run(int Page, float Y, string Text);

    /// <summary>Every text run with its page and baseline (from the bottom of the page), in content order.</summary>
    private static List<Run> Runs(byte[] pdf)
    {
        var text = Latin1(pdf);
        var runs = new List<Run>();
        int page = -1;
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+) >>\nstream\n"))
        {
            // Uncompressed, the page contents are the only streams with a plain length dictionary.
            var data = text.Substring(m.Index + m.Length, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            page++;
            foreach (Match t in Regex.Matches(data, @"1 0 0 1 [\d.-]+ ([\d.-]+) Tm (\((?:[^()\\]|\\.)*\) Tj|\[.*?\] TJ)"))
            {
                var s = string.Concat(Regex.Matches(t.Groups[2].Value, @"\(((?:[^()\\]|\\.)*)\)").Select(x => x.Groups[1].Value));
                runs.Add(new Run(page, float.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), s));
            }
        }
        return runs;
    }

    private static Run Find(List<Run> runs, string text)
        => runs.FirstOrDefault(r => r.Text.Contains(text, StringComparison.Ordinal)) ?? throw new Xunit.Sdk.XunitException($"No text run with '{text}'");

    [Fact]
    public void Footnotes_are_numbered_with_calls_in_the_text_and_markers_in_the_area()
    {
        var pdf = ConvertUncompressed(Style + "<p>Alpha<span class='fn'>First note.</span> beta<span class='fn'>Second note.</span> gamma.</p>"
                                      + "<p>Delta<span class='fn'>Third note.</span> end.</p>", SmallPage());
        AssertStructurallyValid(pdf);
        Assert.Equal(1, PageCount(pdf));
        var runs = Runs(pdf);
        // The calls are superscripts beside the words they follow, the footnote text is not in the paragraph.
        var alpha = Find(runs, "Alpha");
        var call1 = runs[runs.IndexOf(alpha) + 1];
        Assert.Equal("1", call1.Text);
        Assert.True(call1.Y > alpha.Y, "The call is raised as a superscript");
        Assert.Contains(runs, r => r.Text == "2");
        Assert.Contains(runs, r => r.Text == "3");
        // The footnotes are at the bottom of the page, in order, each after its marker.
        var first = Find(runs, "First note.");
        var second = Find(runs, "Second note.");
        var third = Find(runs, "Third note.");
        Assert.True(first.Y < Find(runs, "Delta").Y - 100, "The footnote area is at the bottom of the page");
        Assert.True(first.Y > second.Y && second.Y > third.Y);
        Assert.Equal("1. ", runs[runs.IndexOf(first) - 1].Text);
        Assert.Equal("3. ", runs[runs.IndexOf(third) - 1].Text);
        Assert.True(third.Y < 40, "The last footnote ends at the bottom of the page area");
    }

    [Fact]
    public void A_footnote_goes_on_the_page_where_its_call_falls()
    {
        var body = string.Concat(Enumerable.Repeat($"<p>{Lorem}</p>", 4));
        var pdf = ConvertUncompressed(Style + body + "<p>On the second page<span class='fn'>Note of page two.</span>.</p>", SmallPage());
        var runs = Runs(pdf);
        Assert.Equal(2, PageCount(pdf));
        Assert.Equal(1, Find(runs, "On the second page").Page);
        Assert.Equal(1, Find(runs, "Note of page two.").Page);
        Assert.DoesNotContain(runs, r => r.Page == 0 && r.Text.Contains("Note of page two", StringComparison.Ordinal));
    }

    [Fact]
    public void The_footnote_area_takes_its_height_from_the_page_flow()
    {
        var body = string.Concat(Enumerable.Repeat($"<p>{Lorem}</p>", 4));
        var plain = Runs(ConvertUncompressed(Style + "<p>Start.</p>" + body, SmallPage()));
        var noted = Runs(ConvertUncompressed(Style + "<p>Start.<span class='fn'>" + Lorem + "</span></p>" + body, SmallPage()));
        var marker = noted.Single(r => r.Text == "1. ");
        Assert.Equal(0, marker.Page);
        var bodyLines = noted.Where(r => r.Page == 0 && r.Y > marker.Y + 10 && r.Text != "1").ToList();
        // Fewer body lines fit on the first page, and the last of them stays above the area and its separator.
        Assert.True(bodyLines.Count < plain.Count(r => r.Page == 0), "The footnote area leaves less room for the body");
        Assert.True(bodyLines.Min(r => r.Y) > marker.Y + 20);
        Assert.DoesNotContain(noted, r => r.Page == 0 && r.Y < marker.Y + 10 && r.Y > marker.Y && r.Text.Contains("Lorem", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_footnotes_share_a_page_and_its_separator()
    {
        var pdf = ConvertUncompressed(Style + "<p>One<span class='fn'>Note A.</span> two<span class='fn'>Note B.</span></p>"
                                      + "<p>Three<span class='fn'>Note C.</span></p>", SmallPage());
        var runs = Runs(pdf);
        Assert.All(new[] { "Note A.", "Note B.", "Note C." }, t => Assert.Equal(0, Find(runs, t).Page));
        // One separator: the area's top border, drawn once as a filled rectangle the width of the page area.
        var content = AllContent(pdf);
        Assert.Single(Regex.Matches(content, @"\n20 [\d.]+ 260(\.\d+)? 0\.75 re f\n"));
    }

    [Fact]
    public void A_footnote_too_long_for_the_page_continues_in_the_next_page_area()
    {
        var body = string.Concat(Enumerable.Repeat($"<p>{Lorem}</p>", 2));
        var note = "<span class='fn'>Long note begins. " + Lorem + " " + Lorem + " " + Lorem + " It ends here.</span>";
        var pdf = ConvertUncompressed(Style + body + "<p>Call" + note + " in the third paragraph.</p>", SmallPage());
        var runs = Runs(pdf);
        var call = Find(runs, "Call");
        var begin = Find(runs, "Long note begins.");
        var end = Find(runs, "It ends here.");
        Assert.Equal(call.Page, begin.Page);
        Assert.Equal(call.Page + 1, end.Page);
        // On the next page the rest of the note is in the area at the bottom, below that page's text.
        Assert.True(Find(runs, "in the third paragraph.").Y > end.Y);
    }

    [Fact]
    public void Footnote_policy_line_moves_the_call_line_to_the_page_of_its_footnote()
    {
        // Below the call's line there is room for about three lines of a note that has more.
        string Doc(string policy) => Style + "<div style='height: 200pt'></div>"
            + $"<p>Moved line<span class='fn' style='footnote-policy: {policy}'>Policy note. {Lorem} {Lorem}</span></p>";
        var auto = Runs(ConvertUncompressed(Doc("auto"), SmallPage()));
        var line = Runs(ConvertUncompressed(Doc("line"), SmallPage()));
        // auto: the call stays where it falls and the note is split, starting on the same page.
        Assert.Equal(0, Find(auto, "Moved line").Page);
        Assert.Equal(0, Find(auto, "Policy note.").Page);
        Assert.Equal(1, Find(auto, "consequat.").Page);
        // line: the call's line moves to the next page, where the whole note fits below it.
        Assert.Equal(1, Find(line, "Moved line").Page);
        Assert.All(line.Where(r => r.Text.Contains("Lorem", StringComparison.Ordinal) || r.Text.Contains("Policy", StringComparison.Ordinal)),
            r => Assert.Equal(1, r.Page));
    }

    [Fact]
    public void A_footnote_that_does_not_fit_at_all_takes_its_call_to_the_next_page()
    {
        // Below the call's line there is no room for even one line of the note: auto moves the line too.
        var pdf = ConvertUncompressed(Style + "<div style='height: 236pt'></div><p>Late call<span class='fn'>Late note.</span></p>", SmallPage());
        var runs = Runs(pdf);
        Assert.Equal(1, Find(runs, "Late call").Page);
        Assert.Equal(1, Find(runs, "Late note.").Page);
    }

    [Fact]
    public void Page_counter_reset_numbers_footnotes_per_page()
    {
        var body = string.Concat(Enumerable.Repeat($"<p>{Lorem}</p>", 3));
        var pdf = ConvertUncompressed(Style.Replace("@page {", "@page { counter-reset: footnote;")
                                      + "<p>A<span class='fn'>Note on one.</span> B<span class='fn'>Second on one.</span></p>" + body
                                      + "<p>C<span class='fn'>Note on two.</span></p>", SmallPage());
        var runs = Runs(pdf);
        var two = Find(runs, "Note on two.");
        Assert.Equal(1, two.Page);
        Assert.Equal("1. ", runs[runs.IndexOf(two) - 1].Text);
        Assert.Equal("2. ", runs[runs.IndexOf(Find(runs, "Second on one.")) - 1].Text);
    }

    [Fact]
    public void Without_a_page_reset_numbering_runs_through_the_document_and_elements_can_reset_it()
    {
        var pdf = ConvertUncompressed(Style + "<style>section { counter-reset: footnote }</style>"
                                      + "<section><p>A<span class='fn'>Alpha.</span> B<span class='fn'>Beta.</span></p></section>"
                                      + "<section><p>C<span class='fn'>Gamma.</span></p></section><p>D<span class='fn'>Delta.</span></p>", SmallPage());
        var runs = Runs(pdf);
        string Marker(string note) => runs[runs.IndexOf(Find(runs, note)) - 1].Text;
        Assert.Equal("1. ", Marker("Alpha."));
        Assert.Equal("2. ", Marker("Beta."));
        Assert.Equal("1. ", Marker("Gamma.")); // the section reset the counter
        // The first section's counter is in scope for the sections and paragraph after it, as CSS counter scopes are.
        Assert.Equal("2. ", Marker("Delta."));
    }

    [Fact]
    public void Call_and_marker_content_can_be_styled()
    {
        var pdf = ConvertUncompressed(Style + @"<style>
            .fn::footnote-call { content: '[' counter(footnote, lower-roman) ']'; vertical-align: baseline; font-size: inherit }
            .fn::footnote-marker { content: counter(footnote, lower-roman) ') ' }
          </style><p>Text<span class='fn'>Roman.</span> and<span class='fn'>Second.</span></p>", SmallPage());
        var runs = Runs(pdf);
        Assert.Contains(runs, r => r.Text.Contains("[ii]", StringComparison.Ordinal));
        Assert.Equal("ii\\) ", runs[runs.IndexOf(Find(runs, "Second.")) - 1].Text);
    }

    [Fact]
    public void Inline_footnotes_share_lines_in_the_area()
    {
        var pdf = ConvertUncompressed(Style + "<style>.fn { footnote-display: inline; margin-right: 6pt }</style>"
                                      + "<p>A<span class='fn'>One.</span> B<span class='fn'>Two.</span> C<span class='fn'>Three.</span></p>", SmallPage());
        var runs = Runs(pdf);
        Assert.Equal(Find(runs, "One.").Y, Find(runs, "Three.").Y);
        Assert.True(Find(runs, "Three.").Y < 60);
    }

    [Fact]
    public void A_call_links_to_its_footnote()
    {
        var body = string.Concat(Enumerable.Repeat($"<p>{Lorem}</p>", 4));
        var pdf = ConvertUncompressed(Style + body + "<p>Linked<span class='fn'>Target note.</span></p>", SmallPage());
        var text = Latin1(pdf);
        var link = Regex.Match(text, @"/Subtype /Link /Rect \[[\d. ]+\] /F 4 /Border \[0 0 0\] /Dest \[(\d+) 0 R /XYZ 0 ([\d.]+) null\]");
        Assert.True(link.Success, "The call has an internal link");
        var pages = Regex.Matches(text, @"(\d+) 0 obj\n<< /Type /Page /Parent").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(1, Find(Runs(pdf), "Target note.").Page);
        Assert.Equal(1, pages.IndexOf(link.Groups[1].Value)); // the second page, where the note is
        // It goes to the note at the bottom of the page.
        Assert.True(float.Parse(link.Groups[2].Value, CultureInfo.InvariantCulture) < 80);
    }

    [Fact]
    public void Tagged_footnotes_are_notes_where_their_calls_are()
    {
        var options = SmallPage(PdfAccessibility.PdfUA1);
        options.Title = "Notes";
        var pdf = ConvertUncompressed(Style + "<p>Text<span class='fn'>The note.</span> after.</p>", options);
        AssertStructurallyValid(pdf);
        var text = Latin1(pdf);
        var types = string.Join(" ", Regex.Matches(text, @"/Type /StructElem /S /(\w+)").Select(m => m.Groups[1].Value));
        Assert.Contains("P Link Note", types);
        Assert.Matches(@"/S /Note /P \d+ 0 R /ID \(fn1\)", text);
        Assert.Matches(@"/IDTree << /Names \[\(fn1\) \d+ 0 R \] >>", text);
        Assert.Contains("/Contents (Footnote 1)", text);
    }

    [Fact]
    public void Pdf_ua_2_maps_notes_to_fenote()
    {
        var options = SmallPage(PdfAccessibility.PdfUA2);
        options.Title = "Notes";
        var text = Latin1(ConvertUncompressed(Style + "<p>Text<span class='fn'>The note.</span></p>", options));
        Assert.Contains("/Note [/FENote", text);
    }

    [Fact]
    public void Calls_inside_tables_flex_items_inline_blocks_and_columns_place_their_footnotes()
    {
        var pdf = ConvertUncompressed(Style + "<table><tr><td>Cell<span class='fn'>Table note.</span></td></tr></table>"
                                      + "<div style='display: flex'><div>Flex<span class='fn'>Flex note.</span></div></div>"
                                      + "<p>Inline <span style='display: inline-block'>block<span class='fn'>Box note.</span></span> after.</p>"
                                      + "<div style='columns: 2'>Columns<span class='fn'>Column note.</span></div>", SmallPage());
        var runs = Runs(pdf);
        var notes = new[] { "Table note.", "Flex note.", "Box note.", "Column note." }.Select(t => Find(runs, t)).ToList();
        Assert.All(notes, n => Assert.True(n.Y < 80, "In the footnote area"));
        Assert.Equal(notes.OrderByDescending(n => n.Y), notes); // in the order of their calls
        // The inline-block's line is drawn once, unchanged by the footnote laid out while it was placed.
        var block = Find(runs, "block");
        var after = Find(runs, " after.");
        Assert.Equal(block.Y, after.Y);
    }

    [Fact]
    public void A_footnote_with_paragraphs_starts_its_first_paragraph_with_the_marker()
    {
        var pdf = ConvertUncompressed(Style + "<p>Text.</p><aside class='fn'><p>First para.</p><p>Second para.</p></aside>", SmallPage());
        var runs = Runs(pdf);
        var first = Find(runs, "First para.");
        var marker = runs[runs.IndexOf(first) - 1];
        Assert.Equal("1. ", marker.Text);
        Assert.Equal(first.Y, marker.Y);
    }

    [Fact]
    public void Many_footnotes_lay_out_in_reasonable_time()
    {
        var sb = new System.Text.StringBuilder(Style);
        for (int i = 0; i < 400; i++) sb.Append("<p>Paragraph ").Append(i).Append("<span class='fn'>Note ").Append(i).Append(".</span> ends.</p>");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pdf = ConvertUncompressed(sb.ToString(), SmallPage());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        var runs = Runs(pdf);
        // Every note is on the page of its call.
        foreach (int i in new[] { 0, 57, 199, 399 })
            Assert.Equal(Find(runs, "Paragraph " + i).Page, Find(runs, "Note " + i + ".").Page);
    }

    [Fact]
    public void Supports_queries_know_footnotes()
    {
        Assert.True(CssSupports.Evaluate("(float: footnote)"));
        Assert.True(CssSupports.Evaluate("(footnote-display: inline)"));
        Assert.True(CssSupports.Evaluate("(footnote-policy: line)"));
        Assert.False(CssSupports.Evaluate("(footnote-policy: sometimes)"));
        Assert.True(CssSupports.Evaluate("selector(.note::footnote-call)"));
        Assert.True(CssSupports.Evaluate("selector(::footnote-marker)"));
        Assert.True(CssSupports.Evaluate("at-rule(@footnote)"));
    }

    [Fact]
    public void A_document_without_footnotes_is_unchanged_by_footnote_rules()
    {
        var plain = ConvertUncompressed("<p>Plain text.</p>", SmallPage());
        var withRule = ConvertUncompressed("<style>@page { @footnote { border-top: 1pt solid } }</style><p>Plain text.</p>", SmallPage());
        Assert.Equal(AllContent(plain), AllContent(withRule));
    }
}
