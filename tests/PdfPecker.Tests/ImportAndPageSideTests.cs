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

    [Fact]
    public void Narrower_pages_set_the_width_of_the_whole_flow()
    {
        var pdf = ConvertUncompressed(@"<style>@page { size: 400pt 400pt; margin: 20pt } @page :first { margin-right: 120pt }</style>
            <div style='width:100%;background:#ff0000;height:900pt'></div>");
        // The first page's area is 260pt wide; every page's box is that wide, so none overflows.
        var widths = Regex.Matches(AllContent(pdf), @"1 0 0 rg\n[\d.]+ [\d.]+ ([\d.]+) [\d.]+ re f").Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(widths);
        Assert.All(widths, w => Assert.Equal(260, w, 0.5f));
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
