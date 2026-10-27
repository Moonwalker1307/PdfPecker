using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>@import, and @page :first, :left and :right rules that change a page's margins, size or background.</summary>
public class ImportAndPageSideTests
{
    private static PdfOptions WithFiles(Dictionary<string, string> files) => new()
    {
        ResourceResolver = url => files.TryGetValue(url, out var css) ? Encoding.UTF8.GetBytes(css) : null,
    };

    /// <summary>The fill colour the text is drawn in, as "r g b".</summary>
    private static string ColorOf(byte[] pdf, string text)
    {
        string fill = "0 0 0";
        foreach (var line in AllContent(pdf).Split('\n'))
        {
            if (line == "BT") fill = "0 0 0";
            var rg = Regex.Match(line, @"^([\d.]+ [\d.]+ [\d.]+) rg$");
            if (rg.Success) fill = rg.Groups[1].Value;
            // Kerned text is shown as [(gr)18(een)] TJ.
            if (Regex.Replace(line, @"\)-?[\d.]+\(", "").Contains("(" + text + ")", StringComparison.Ordinal)) return fill;
        }
        Assert.Fail($"No text {text}");
        return "";
    }

    /// <summary>The x and y of each text run in page order (uncompressed output), per page.</summary>
    private static List<List<(float X, float Y, string Text)>> TextPositions(byte[] pdf)
    {
        var text = Latin1(pdf);
        var pages = new List<List<(float, float, string)>>();
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+) >>\nstream\n"))
        {
            var data = text.Substring(m.Index + m.Length, int.Parse(m.Groups[1].Value));
            if (!data.Contains(" Tf", StringComparison.Ordinal)) continue;
            // Kerned text is shown as [(li)18(ne)] TJ.
            pages.Add(Regex.Matches(data, @"1 0 0 1 (-?[\d.]+) (-?[\d.]+) Tm (?:\((.*?)\) Tj|\[\((.*?)\)\] TJ)")
                .Select(t => (float.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), float.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture),
                    t.Groups[3].Success ? t.Groups[3].Value : Regex.Replace(t.Groups[4].Value, @"\)-?[\d.]+\(", "")))
                .ToList());
        }
        return pages;
    }

    private static List<string> PageSizes(byte[] pdf)
        => Regex.Matches(Latin1(pdf), @"/Type /Page /Parent \d+ 0 R /MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")
            .Select(m => $"{Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))}x{Math.Round(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))}")
            .ToList();

    // ---- @import ----

    [Fact]
    public void Import_loads_a_sheet_from_a_style_block()
    {
        var files = new Dictionary<string, string> { ["base.css"] = "p { color: #ff0000 }" };
        var pdf = ConvertUncompressed("<style>@import url(base.css);</style><p>red</p>", WithFiles(files));
        Assert.Equal("1 0 0", ColorOf(pdf, "red"));
    }

    [Fact]
    public void Import_with_a_plain_string_resolves_against_the_importing_sheet()
    {
        var files = new Dictionary<string, string>
        {
            ["css/site.css"] = "@import \"colors.css\"; @import url(more.css); em { font-weight: bold }",
            ["css/colors.css"] = "p { color: #0000ff }",
            ["css/more.css"] = "b { color: #00ff00 }",
        };
        var pdf = ConvertUncompressed("<link rel='stylesheet' href='css/site.css'><p>blue <b>green</b></p>", WithFiles(files));
        Assert.Equal("0 0 1", ColorOf(pdf, "blue "));
        Assert.Equal("0 1 0", ColorOf(pdf, "green"));
    }

    [Fact]
    public void Data_uri_stylesheets_load_without_a_resolver()
    {
        // As in Acid2, whose preferred sheet is a percent-encoded data URI; a base64 one imports another.
        var css = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("b { color: #00ff00 }"));
        var pdf = ConvertUncompressed(
            "<link rel='appendix stylesheet' href='data:text/css,p%20%7B%20color%3A%20%230000ff%20%7D'>"
            + "<style>@import url(\"data:text/css;base64," + css + "\");</style>"
            + "<link rel='stylesheet' href='data:text/css;base64,!!'><p>blue <b>green</b></p>");
        Assert.Equal("0 0 1", ColorOf(pdf, "blue "));
        Assert.Equal("0 1 0", ColorOf(pdf, "green"));
    }

    [Fact]
    public void Imported_rules_come_before_the_rest_of_the_sheet()
    {
        var files = new Dictionary<string, string> { ["a.css"] = "p { color: #ff0000 }" };
        var pdf = ConvertUncompressed("<style>@import 'a.css'; p { color: #0000ff }</style><p>later wins</p>", WithFiles(files));
        Assert.Equal("0 0 1", ColorOf(pdf, "later wins"));
    }

    [Fact]
    public void Import_after_a_rule_is_ignored()
    {
        var files = new Dictionary<string, string> { ["a.css"] = "p { color: #ff0000 }" };
        var pdf = ConvertUncompressed("<style>p { margin: 0 } @import 'a.css';</style><p>black</p>", WithFiles(files));
        Assert.Equal("0 0 0", ColorOf(pdf, "black"));
    }

    [Fact]
    public void Import_conditions_and_layers_apply()
    {
        var files = new Dictionary<string, string>
        {
            ["screen.css"] = "p { color: #ff0000 }",
            ["print.css"] = ".p { color: #00ff00 }",
            ["nogrid.css"] = ".s { color: #ff0000 }",
            ["layered.css"] = ".l { color: #ff0000 }",
        };
        var pdf = ConvertUncompressed(@"<style>
            @import 'screen.css' screen;
            @import 'print.css' print;
            @import 'nogrid.css' supports(display: nonsense);
            @import 'layered.css' layer(base);
            .l { color: #0000ff }
          </style><p class='p'>print</p><p class='s'>plain</p><p class='l'>unlayered wins</p>", WithFiles(files));
        Assert.Equal("0 1 0", ColorOf(pdf, "print"));
        Assert.Equal("0 0 0", ColorOf(pdf, "plain"));
        Assert.Equal("0 0 1", ColorOf(pdf, "unlayered wins"));
    }

    [Fact]
    public void Import_cycles_stop()
    {
        var files = new Dictionary<string, string>
        {
            ["a.css"] = "@import 'b.css'; p { color: #ff0000 }",
            ["b.css"] = "@import 'a.css'; p { font-weight: bold }",
        };
        var pdf = ConvertUncompressed("<style>@import 'a.css';</style><p>done</p>", WithFiles(files));
        Assert.Equal("1 0 0", ColorOf(pdf, "done"));
    }

    // ---- @page :first, :left and :right ----

    [Fact]
    public void First_page_margins_move_and_shorten_only_the_first_page()
    {
        var lines = string.Concat(Enumerable.Range(1, 120).Select(i => $"<p style='margin:0;line-height:15pt'>line{i}</p>"));
        var pdf = ConvertUncompressed("<style>@page { size: A4; margin: 50pt } @page :first { margin-top: 250pt } body { margin: 0 }</style>" + lines);
        var pages = TextPositions(pdf);
        // The first line sits 250pt below the top of page 1 and 50pt below the top of page 2.
        Assert.InRange(842 - pages[0][0].Y, 250, 270);
        Assert.InRange(842 - pages[1][0].Y, 50, 70);
        // Page 1 holds (842 - 300) / 15 = 36 lines, later pages (842 - 100) / 15 = 49.
        Assert.Equal(36, pages[0].Count);
        Assert.Equal("line37", pages[1][0].Text);
        Assert.Equal(49, pages[1].Count);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void Left_and_right_pages_mirror_their_margins()
    {
        var lines = string.Concat(Enumerable.Range(1, 120).Select(i => $"<p style='margin:0;line-height:20pt'>line{i}</p>"));
        var pdf = ConvertUncompressed(@"<style>@page { size: A4; margin: 50pt }
            @page :left { margin-left: 90pt; margin-right: 50pt } @page :right { margin-left: 50pt; margin-right: 90pt }</style>" + lines);
        var pages = TextPositions(pdf);
        Assert.True(pages.Count >= 3);
        Assert.Equal(50, pages[0][0].X, 0.5f); // page 1 is a right page
        Assert.Equal(90, pages[1][0].X, 0.5f);
        Assert.Equal(50, pages[2][0].X, 0.5f);
    }

    [Fact]
    public void First_page_size_and_background_apply_to_the_first_page_only()
    {
        var lines = string.Concat(Enumerable.Range(1, 60).Select(i => $"<p style='margin:0;line-height:20pt'>line{i}</p>"));
        var pdf = ConvertUncompressed("<style>@page { size: A4; margin: 50pt } @page :first { size: A4 landscape; background: #ffff00 }</style>" + lines);
        var sizes = PageSizes(pdf);
        Assert.Equal("842x595", sizes[0]);
        Assert.All(sizes.Skip(1), s => Assert.Equal("595x842", s));
        var content = AllContent(pdf);
        Assert.Single(Regex.Matches(content, @"1 1 0 rg\n0 0 [\d.]+ [\d.]+ re f"));
    }

    /// <summary>The widths of the rectangles filled in red, in page order.</summary>
    private static List<float> RedWidths(byte[] pdf)
        => Regex.Matches(AllContent(pdf), @"1 0 0 rg\n[\d.]+ [\d.]+ ([\d.]+) [\d.]+ re f").Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    [Fact]
    public void Percentage_widths_are_resolved_on_each_page()
    {
        var pdf = ConvertUncompressed(@"<style>@page { size: 400pt 400pt; margin: 20pt } @page :first { margin-right: 120pt }</style>
            <div style='width:100%;background:#ff0000;height:900pt'></div>");
        // As in Chrome, the box is as wide as each page's area: 260pt on the first page, 360pt on the others.
        Assert.Equal(new[] { 260f, 360f, 360f }, RedWidths(pdf).Select(w => MathF.Round(w)));
    }

    [Fact]
    public void Lines_take_the_width_of_the_page_they_are_on()
    {
        var words = string.Join(" ", Enumerable.Range(0, 1500).Select(i => $"word{i}"));
        var pdf = ConvertUncompressed(@"<!doctype html><style>@page { size: 400px 300px; margin: 20px } @page :left { margin-left: 120px }
            body { margin: 0; font: 12px/16px sans-serif }</style><body><p>" + words + "</p>");
        var pages = TextPositions(pdf);
        // Right pages are 360px wide and left pages 260px, and each page's lines fill its width: the same words per page
        // as in Chrome.
        Assert.Equal("word0", pages[0][0].Text.Split(' ')[0]);
        Assert.Equal("word119", pages[1][0].Text.Split(' ')[0]);
        Assert.Equal("word199", pages[2][0].Text.Split(' ')[0]);
        Assert.Equal("word311", pages[3][0].Text.Split(' ')[0]);
        Assert.Equal(90, pages[1][0].X, 0.5f); // 120px
        Assert.Equal(15, pages[0][0].X, 0.5f);
        AssertStructurallyValid(pdf);
    }

    [Fact]
    public void A_line_pushed_to_a_narrower_page_is_measured_for_it()
    {
        // Courier 10px is 6px a character. The first page is 360px wide and the second 260px. The sixteenth line starts
        // 240px down the 260px page, where the line height fits but the tall T does not: the line moves to the second
        // page and must be broken for its width.
        var lines = string.Concat(Enumerable.Repeat("abcd<br>", 15));
        var words = string.Join(" ", Enumerable.Repeat("abcd", 60));
        var pdf = ConvertUncompressed(@"<!doctype html><style>@page { size: 400px 300px; margin: 20px } @page :left { margin-left: 120px }
            body { margin: 0; font: 10px/16px Courier } p { margin: 0 }</style><body><p>" + lines
            + "<span style='font-size:40px;line-height:44px'>T</span> " + words + "</p>");
        var pages = TextPositions(pdf);
        Assert.Equal(15, pages[0].Count);
        var tall = pages[1].Where(t => Math.Abs(t.Y - pages[1][0].Y) < 0.5f).ToList();
        Assert.Equal("T", tall[0].Text);
        // The small words after the T must end within the page's 260px, which starts at 120px (90pt).
        Assert.Equal(90, tall[0].X, 0.5f);
        var last = tall[^1];
        float right = (last.X - 90) / 0.75f + last.Text.TrimEnd().Length * 6;
        Assert.InRange(right, 200, 260.5f);
    }

    [Fact]
    public void Boxes_spanning_pages_of_different_widths_are_painted_per_page()
    {
        var text = string.Join(" ", Enumerable.Range(0, 600).Select(i => $"w{i}"));
        var pdf = ConvertUncompressed(@"<!doctype html><style>@page { size: 400px 300px; margin: 20px } @page :left { margin-left: 120px }
            body { margin: 0; font: 12px/16px sans-serif } div { background: #ff0000 }</style><body><div>" + text + "</div>");
        // The red background is 360px (270pt) wide on right pages and 260px (195pt) on left pages.
        var widths = RedWidths(pdf).Select(w => MathF.Round(w)).ToList();
        Assert.True(widths.Count >= 3);
        Assert.Equal(270, widths[0]);
        Assert.Equal(195, widths[1]);
        Assert.Equal(270, widths[2]);
    }

    [Fact]
    public void Tables_are_sized_again_on_each_page()
    {
        var rows = string.Concat(Enumerable.Range(0, 40).Select(i => $"<tr><td>r{i} a</td><td>b</td></tr>"));
        var pdf = ConvertUncompressed(@"<!doctype html><html><head><style>@page { size: 400px 300px; margin: 20px } @page :left { margin-left: 120px }
            body { margin: 0; font: 12px/16px sans-serif } table { width: 100%; border-collapse: collapse } td { border: 1px solid #000 }</style></head>
            <body><table>" + rows + "</table></body></html>");
        var pages = TextPositions(pdf);
        // As in Chrome, the second column starts at 294px on the 360px right pages and at 318px on the 260px left pages,
        // where the columns shrink in proportion.
        Assert.Equal(220.5f, pages[0].First(t => t.Text == "b").X, 0.5f);
        Assert.Equal(238.5f, pages[1].First(t => t.Text == "b").X, 0.5f);
        Assert.Equal(220.5f, pages[2].First(t => t.Text == "b").X, 0.5f);
    }

    [Fact]
    public void Centred_boxes_are_centred_on_each_page()
    {
        var text = string.Join(" ", Enumerable.Range(0, 600).Select(i => $"w{i}"));
        var pdf = ConvertUncompressed(@"<!doctype html><style>@page { size: 400px 300px; margin: 20px } @page :left { margin-left: 60px; margin-right: 40px }
            body { margin: 0; font: 12px/16px sans-serif } main { width: 200px; margin: 0 auto }</style><body><main>" + text + "</main>");
        var pages = TextPositions(pdf);
        // Right pages: 20px + (360 - 200) / 2 = 100px; left pages: 60px + (300 - 200) / 2 = 110px.
        Assert.Equal(75, pages[0][0].X, 0.5f);
        Assert.Equal(82.5f, pages[1][0].X, 0.5f);
        Assert.Equal(75, pages[2][0].X, 0.5f);
    }

    [Fact]
    public void A_right_to_left_document_starts_on_a_left_page()
    {
        var words = string.Join(" ", Enumerable.Range(0, 600).Select(i => $"word{i}"));
        const string css = "<style>@page { size: 400px 300px; margin: 20px } @page :left { margin-left: 120px } @page :right { margin-right: 80px } body { margin: 0; font: 12px/16px sans-serif }</style>";
        // As in Chrome, the root element's direction decides: its pages progress from right to left, so the first is a
        // left page. The body's direction does not.
        var rtl = ConvertUncompressed("<!doctype html><html dir=rtl>" + css + "<body><p>" + words + "</p></html>");
        var rtlPages = TextPositions(rtl);
        Assert.True(rtlPages[0].Min(t => t.X) >= 89.5f); // a left page: its area starts at 120px
        Assert.True(rtlPages[1].Min(t => t.X) < 30);
        Assert.Contains("/ViewerPreferences << /Direction /R2L >>", Latin1(rtl));

        var bodyOnly = ConvertUncompressed("<!doctype html><html>" + css + "<body dir=rtl><p>" + words + "</p></body></html>");
        var ltrPages = TextPositions(bodyOnly);
        Assert.True(ltrPages[0].Min(t => t.X) < 30);
        Assert.True(ltrPages[1].Min(t => t.X) >= 89.5f);
        Assert.DoesNotContain("/Direction", Latin1(bodyOnly));
        AssertStructurallyValid(rtl);
    }

    [Fact]
    public void Right_to_left_documents_put_left_page_margin_boxes_on_the_first_page()
    {
        const string css = "<style>@page :left { @top-center { content: 'LEFT' } } @page :right { @top-center { content: 'RIGHT' } }</style>";
        var html = "<p>a</p><p style='break-before:page'>b</p>";
        var rtl = AllContent(ConvertUncompressed("<html dir=rtl>" + css + html + "</html>"));
        Assert.True(rtl.IndexOf("(LEFT)", StringComparison.Ordinal) < rtl.IndexOf("(RIGHT)", StringComparison.Ordinal));
        var ltr = AllContent(ConvertUncompressed("<html>" + css + html + "</html>"));
        Assert.True(ltr.IndexOf("(RIGHT)", StringComparison.Ordinal) < ltr.IndexOf("(LEFT)", StringComparison.Ordinal));
    }

    [Fact]
    public void Page_side_rules_with_only_margin_boxes_keep_one_setup()
    {
        var html = "<p>a</p><p style='break-before:page'>b</p>";
        var plain = ConvertUncompressed(html);
        var withBoxes = ConvertUncompressed("<style>@page :first { @top-center { content: 'x' } }</style>" + html);
        Assert.Equal(PageSizes(plain), PageSizes(withBoxes));
        Assert.Equal(TextPositions(plain)[1][0].Y, TextPositions(withBoxes)[1].Last().Y);
    }

    [Fact]
    public void Links_and_page_numbers_follow_the_first_page_margin()
    {
        var lines = string.Concat(Enumerable.Range(1, 50).Select(i => $"<p style='margin:0;line-height:20pt'>line{i}</p>"));
        var pdf = ConvertUncompressed(@"<style>@page { size: A4; margin: 50pt } @page :first { margin-top: 300pt } body { margin: 0 }
            a::after { content: ' p' target-counter(attr(href), page) }</style>
            <p style='margin:0;line-height:20pt'><a href='#t'>to</a></p>" + lines + "<p id='t' style='margin:0'>target</p>");
        var content = AllContent(pdf);
        // Page 1 holds (842 - 350) / 20 = 24 lines, page 2 holds 37, so the target (line 52) is on page 2.
        Assert.Contains("( p2)", content);
        AssertStructurallyValid(pdf);
    }
}
