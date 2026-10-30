using FluentExamples;

using System.Diagnostics;

namespace PdfPecker.Fluent.Tests;

/// <summary>
/// The fluent layer must stay linear in what the caller adds and cheap next to the conversion itself. Both are checked
/// by the bytes the calls allocate, which are the same on every machine: timing them failed whenever the other test
/// assemblies, which CI runs beside this one on three runtimes, slowed one of the two measurements more than the other.
/// The benchmarks in bench/ measure the real speed, and the nightly Performance job watches it.
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

    // The bytes the second of two calls allocates, so that one-time work such as static tables is left out. The
    // conversion runs on the calling thread, so the thread's own count covers it.
    private static long Allocated(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Html_generation_is_linear()
    {
        long small = Allocated(() => DocumentGenerator.Html(Table(20_000), false, out _));
        long large = Allocated(() => DocumentGenerator.Html(Table(80_000), false, out _));
        // Four times the rows: linear is about 4x (4.0 when this was written); quadratic would be 16x.
        Assert.True(large < small * 5, $"20k rows {small:N0} bytes, 80k rows {large:N0} bytes.");
    }

    [Fact]
    public void A_hundred_thousand_rows_build_quickly()
    {
        // Only a guard against a hang or a quadratic slip, with room for a slow and busy machine: this takes about
        // 3 s on a quiet one.
        DocumentGenerator.Html(Table(1_000), false, out _);
        var watch = Stopwatch.StartNew();
        DocumentGenerator.Html(Table(100_000), false, out _);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void Html_generation_is_small_next_to_the_conversion()
    {
        // The report has no barcodes, whose encoding is PdfPecker's work rather than the fluent layer's. Writing its
        // HTML allocated 1.5 MB against 2.9 MB for the whole PDF, of which the HTML is a part.
        var document = Examples.All["report"]();
        long html = Allocated(() => DocumentGenerator.Html(document, false, out _));
        long pdf = Allocated(() => document.GeneratePdf());
        Assert.True(html * 3 < pdf * 2, $"HTML {html:N0} bytes, PDF {pdf:N0} bytes.");
    }
}
