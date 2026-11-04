using Microsoft.Extensions.DependencyInjection;
using PdfPecker.Razor.Tests.Components;

namespace PdfPecker.Razor.Tests;

public sealed class RazorToPdfTests
{
    private static Dictionary<string, object?> Name(string name) => new() { [nameof(Greeting.Name)] = name };

    [Fact]
    public async Task RendersParameters()
    {
        string html = await RazorToPdf.RenderHtmlAsync<Greeting>(Name("Ada"));
        Assert.Equal("<p>Hello Ada</p>", html);
    }

    [Fact]
    public async Task EncodesCallerText()
    {
        string html = await RazorToPdf.RenderHtmlAsync<Greeting>(Name("<b>Tom & Jerry</b>"));
        Assert.Equal("<p>Hello &lt;b&gt;Tom &amp; Jerry&lt;/b&gt;</p>", html);
    }

    [Fact]
    public async Task MarkupStringIsInsertedAsItIs()
    {
        string html = await RazorToPdf.RenderHtmlAsync<Markup>(new Dictionary<string, object?> { ["Html"] = "<em>trusted</em>" });
        Assert.Equal("<em>trusted</em>", html.Trim());
    }

    [Fact]
    public async Task ConvertsToPdf()
    {
        byte[] pdf = await RazorToPdf.ConvertAsync<Greeting>(Name("Ada"));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("Hello Ada", PdfText.Extract(pdf));
    }

    [Fact]
    public async Task GivesTheSamePdfAsHtmlToPdf()
    {
        var options = new PdfOptions { CreationDate = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), Title = "Greeting" };
        byte[] pdf = await RazorToPdf.ConvertAsync<Greeting>(Name("Ada"), options);
        byte[] expected = await HtmlToPdf.ConvertAsync(await RazorToPdf.RenderHtmlAsync<Greeting>(Name("Ada")), options);
        Assert.Equal(expected, pdf);
    }

    [Fact]
    public async Task WritesToStream()
    {
        using var stream = new MemoryStream();
        await RazorToPdf.ConvertAsync<Greeting>(stream, Name("Ada"));
        Assert.Contains("Hello Ada", PdfText.Extract(stream.ToArray()));
    }

    [Fact]
    public async Task ConvertsByType()
    {
        byte[] pdf = await RazorToPdf.ConvertAsync(typeof(Greeting), Name("Ada"));
        Assert.Contains("Hello Ada", PdfText.Extract(pdf));
        Assert.Equal("<p>Hello Ada</p>", await RazorToPdf.RenderHtmlAsync(typeof(Greeting), Name("Ada")));
    }

    [Fact]
    public async Task InjectsServices()
    {
        using var services = new ServiceCollection().AddSingleton<GreetingService>().BuildServiceProvider();
        string html = await RazorToPdf.RenderHtmlAsync<Injected>(services: services);
        Assert.Equal("<p>Hello from a service</p>", html.Trim());
    }

    [Fact]
    public async Task MissingServiceNamesIt()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RazorToPdf.RenderHtmlAsync<Injected>());
        Assert.Contains(nameof(GreetingService), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitsForAsynchronousData()
    {
        string html = await RazorToPdf.RenderHtmlAsync<Loaded>();
        Assert.Contains("<li>Apples</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>Pears</li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RendersLayouts()
    {
        string html = await RazorToPdf.RenderHtmlAsync<WithLayout>();
        Assert.Contains("<header>Letterhead</header><p>Hello Grace</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownParameterIsRefused()
    {
        var parameters = new Dictionary<string, object?> { ["Nmae"] = "Ada" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RazorToPdf.ConvertAsync<Greeting>(parameters));
        Assert.Contains("Nmae", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComponentExceptionReachesTheCaller()
    {
        var error = await Assert.ThrowsAsync<FormatException>(() => RazorToPdf.ConvertAsync<Failing>());
        Assert.Equal("The template failed.", error.Message);
    }

    [Fact]
    public async Task CancelledBeforeStarting()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RazorToPdf.ConvertAsync<Greeting>(Name("Ada"), cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RazorToPdf.RenderHtmlAsync<Greeting>(Name("Ada"), cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task ArgumentsAreChecked()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => RazorToPdf.ConvertAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => RazorToPdf.ConvertAsync(typeof(string)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RazorToPdf.ConvertAsync<Greeting>((Stream)null!));
    }

    [Fact]
    public async Task ParallelConversionsAgree()
    {
        var options = new PdfOptions { CreationDate = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero) };
        var pdfs = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => RazorToPdf.ConvertAsync<Loaded>(options: options))));
        Assert.All(pdfs, pdf => Assert.Equal(pdfs[0], pdf));
    }
}
