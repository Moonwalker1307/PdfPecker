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

    [Fact]
    public void ManyFloatsStayBounded()
    {
        var html = "<div>" + string.Concat(Enumerable.Repeat("<span style='float:left;width:3px;height:3px'></span>w ", 2000)) + "</div>";
        var sw = Stopwatch.StartNew();
        AssertStructurallyValid(ConvertUncompressed(html));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
