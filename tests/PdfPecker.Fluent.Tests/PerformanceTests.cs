using FluentExamples;

using System.Diagnostics;

namespace PdfPecker.Fluent.Tests;

/// <summary>
/// The fluent layer must stay linear in what the caller adds and cheap next to the conversion itself. The bounds are
/// generous, for slow CI machines; the benchmarks in bench/ measure the real cost.
/// </summary>
[Collection(nameof(HostileInputCollection))]
public sealed class PerformanceTests
{
    private static IDocument Table(int rows) => Html.Page(c => c.Table(t =>
    {
        t.ColumnsDefinition(d => { d.RelativeColumn(3); d.ConstantColumn(60); d.ConstantColumn(80); });
        for (int i = 0; i < rows; i++)
        {
            t.Cell().BorderBottom(0.5f).Padding(2).Text("Line " + i);
            t.Cell().BorderBottom(0.5f).Padding(2).AlignRight().Text("1");
            t.Cell().BorderBottom(0.5f).Padding(2).AlignRight().Text("9.95");
        }
    }));

    private static TimeSpan Time(Action action, int repeat = 3)
    {
        action();
        var best = TimeSpan.MaxValue;
        for (int i = 0; i < repeat; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            if (watch.Elapsed < best) best = watch.Elapsed;
        }
        return best;
    }

    [Fact]
    public void Html_generation_is_linear()
    {
        var small = Time(() => DocumentGenerator.Html(Table(20_000), false, out _));
        var large = Time(() => DocumentGenerator.Html(Table(80_000), false, out _));
        // Four times the rows: linear is about 4x; quadratic would be 16x.
        Assert.True(large.TotalMilliseconds < Math.Max(small.TotalMilliseconds, 5) * 8, $"20k rows {small}, 80k rows {large}.");
    }

    [Fact]
    public void A_hundred_thousand_rows_build_quickly()
    {
        var time = Time(() => DocumentGenerator.Html(Table(100_000), false, out _), repeat: 1);
        Assert.True(time < TimeSpan.FromSeconds(10), $"Took {time}.");
    }

    [Fact]
    public void Html_generation_is_small_next_to_the_conversion()
    {
        // The report has no barcodes, whose encoding is PdfPecker's work rather than the fluent layer's.
        // The two are timed in turns, so that the other test assemblies running beside this one slow both alike: timed one
        // after the other, a busy spell during only the conversion's runs failed the test.
        var document = Examples.All["report"]();
        DocumentGenerator.Html(document, false, out _);
        document.GeneratePdf();
        TimeSpan html = TimeSpan.MaxValue, pdf = TimeSpan.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            DocumentGenerator.Html(document, false, out _);
            if (watch.Elapsed < html) html = watch.Elapsed;
            watch.Restart();
            document.GeneratePdf();
            if (watch.Elapsed < pdf) pdf = watch.Elapsed;
        }
        Assert.True(html.TotalMilliseconds * 3 < pdf.TotalMilliseconds, $"HTML {html}, PDF {pdf}.");
    }
}
