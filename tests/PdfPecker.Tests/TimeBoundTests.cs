using System.Diagnostics;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Tests that assert a wall-clock bound. They run in the hostile-input collection, after the parallel tests, so a busy
/// runner testing other classes at the same time cannot push them over their limit.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class TimeBoundTests
{
    [Theory]
    [MemberData(nameof(MarkdownTests.Pathological), MemberType = typeof(MarkdownTests))]
    public void Pathological_input_parses_in_linear_time(string name, string markdown)
    {
        var sw = Stopwatch.StartNew();
        var html = MarkdownToHtml.Convert(markdown);
        sw.Stop();
        Assert.NotEmpty(html);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{name} took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Rebasing_hostile_stylesheets_stays_linear()
    {
        var sw = Stopwatch.StartNew();
        HtmlToPdf.RebaseUrls(string.Concat(Enumerable.Repeat("url(", 200_000)) + ")", "css/a.css");
        HtmlToPdf.RebaseUrls(string.Concat(Enumerable.Repeat("url('", 200_000)), "css/a.css");
        HtmlToPdf.RebaseUrls(string.Concat(Enumerable.Repeat("url(\"a\" x", 100_000)) + ")", "css/a.css");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("display:grid;grid-template-columns:repeat(30,1fr);gap:10px;column-rule:2px solid #00f;column-rule-inset-cap-end:-1e6px")]
    [InlineData("display:grid;grid-template-columns:repeat(30,1fr);gap:10px;column-rule:2px dotted #00f;column-rule-inset-cap-end:-1e30px")]
    [InlineData("display:grid;grid-template-columns:repeat(30,1fr);gap:10px;row-rule:2px dashed #00f;row-rule-inset:-1e30px")]
    [InlineData("display:flex;flex-wrap:wrap;gap:10px;rule:2px solid #00f;rule-inset:-1e6px")]
    [InlineData("display:flex;flex-direction:column;flex-wrap:wrap;height:200px;gap:10px;rule:2px solid #00f;rule-inset:-1e30px")]
    [InlineData("columns:3;column-rule:2px solid #00f;column-rule-color:red,blue;column-rule-inset:-1e30px")]
    public void Huge_negative_gap_rule_insets_stay_on_one_page(string style)
    {
        // A negative cap inset used to stretch a rule far below its container, cut into a piece for every page on the way.
        var html = $"<div style='{style}'>" + string.Concat(Enumerable.Repeat("<div>x</div>", 60)) + "</div>";
        var sw = Stopwatch.StartNew();
        var pdf = ConvertUncompressed(html);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        AssertStructurallyValid(pdf);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void ManyFloatsStayBounded()
    {
        var html = "<div>" + string.Concat(Enumerable.Repeat("<span style='float:left;width:3px;height:3px'></span>w ", 2000)) + "</div>";
        var sw = Stopwatch.StartNew();
        AssertStructurallyValid(ConvertUncompressed(html));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
