using System.Diagnostics;
using PdfSharp.Fonts;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using TheArtOfDev.HtmlRenderer.PdfSharp;

/// <summary>One HTML-to-PDF converter, set up the way its documentation suggests for converting many documents.</summary>
interface IEngine : IAsyncDisposable
{
    /// <summary>Converts an HTML file on A4 paper, loading relative images from its folder. Must be thread-safe.</summary>
    Task<byte[]> Convert(string htmlPath);
}

static class Engines
{
    static string ChromiumPath => Environment.GetEnvironmentVariable("CHROMIUM_PATH") ?? "/opt/pw-browsers/chromium";
    static readonly string[] ChromiumArgs = ["--no-sandbox", "--disable-dev-shm-usage"];

    public static async Task<IEngine> Create(string name) => name switch
    {
        "pdfpecker" => new PdfPeckerEngine(),
        "htmlrenderer" => new HtmlRendererEngine(),
        "wkhtmltopdf" => new WkHtmlToPdfEngine(),
        "puppeteer" => new PuppeteerEngine(await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = ChromiumPath, Args = ChromiumArgs })),
        "playwright" => await PlaywrightEngine.Create(ChromiumPath, ChromiumArgs),
        "itext" => new ITextEngine(),
        "syncfusion" => new SyncfusionEngine(),
        _ => throw new ArgumentException($"Unknown engine '{name}'"),
    };

    sealed class PdfPeckerEngine : IEngine
    {
        public Task<byte[]> Convert(string htmlPath) => Task.FromResult(PdfPecker.HtmlToPdf.Convert(File.ReadAllText(htmlPath),
            new PdfPecker.PdfOptions { BaseDirectory = Path.GetDirectoryName(htmlPath) }));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // HtmlRenderer.PdfSharp: managed, draws through PDFsharp, supports HTML 4 and CSS level 2 only.
    sealed class HtmlRendererEngine : IEngine
    {
        static HtmlRendererEngine()
        {
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new LiberationFontResolver();
        }

        public Task<byte[]> Convert(string htmlPath)
        {
            var dir = Path.GetDirectoryName(htmlPath)!;
            using var doc = PdfGenerator.GeneratePdf(File.ReadAllText(htmlPath), PdfSharp.PageSize.A4, 20, null, null,
                (_, e) => { if (!e.Src.Contains(':')) e.Callback(Path.Combine(dir, e.Src)); });
            using var ms = new MemoryStream();
            doc.Save(ms);
            return Task.FromResult(ms.ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // PDFsharp has no font lookup on Linux, so every family maps to Liberation Sans (metric-compatible with Arial).
    sealed class LiberationFontResolver : IFontResolver
    {
        const string Dir = "/usr/share/fonts/truetype/liberation";

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
            => new((bold, italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" });

        public byte[] GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Dir, $"LiberationSans-{faceName}.ttf"));
    }

    // wkhtmltopdf (Qt WebKit, archived in 2023): one process per document, as with the CLI or Rotativa.
    sealed class WkHtmlToPdfEngine : IEngine
    {
        public async Task<byte[]> Convert(string htmlPath)
        {
            var output = Path.Combine(Path.GetTempPath(), $"wk-{Guid.NewGuid():N}.pdf");
            var psi = new ProcessStartInfo("wkhtmltopdf") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--quiet", "--enable-local-file-access", "--page-size", "A4", htmlPath, output }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            try { return await File.ReadAllBytesAsync(output); }
            finally { File.Delete(output); }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // PuppeteerSharp: one shared Chromium, a new tab per document (closed afterwards).
    sealed class PuppeteerEngine(IBrowser browser) : IEngine
    {
        public async Task<byte[]> Convert(string htmlPath)
        {
            await using var page = await browser.NewPageAsync();
            await page.GoToAsync(new Uri(htmlPath).AbsoluteUri);
            return await page.PdfDataAsync(new PdfOptions { Format = PaperFormat.A4, PrintBackground = true, PreferCSSPageSize = true, Tagged = false });
        }

        public async ValueTask DisposeAsync() => await browser.DisposeAsync();
    }

    // Microsoft.Playwright: one shared Chromium and browser context, a new tab per document (closed afterwards).
    sealed class PlaywrightEngine : IEngine
    {
        readonly Microsoft.Playwright.IPlaywright _playwright;
        readonly Microsoft.Playwright.IBrowser _browser;
        readonly Microsoft.Playwright.IBrowserContext _context;

        PlaywrightEngine(Microsoft.Playwright.IPlaywright playwright, Microsoft.Playwright.IBrowser browser, Microsoft.Playwright.IBrowserContext context)
            => (_playwright, _browser, _context) = (playwright, browser, context);

        public static async Task<IEngine> Create(string chromium, string[] args)
        {
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new() { ExecutablePath = chromium, Args = args });
            return new PlaywrightEngine(playwright, browser, await browser.NewContextAsync());
        }

        public async Task<byte[]> Convert(string htmlPath)
        {
            var page = await _context.NewPageAsync();
            try
            {
                await page.GotoAsync(new Uri(htmlPath).AbsoluteUri);
                return await page.PdfAsync(new() { Format = "A4", PrintBackground = true, PreferCSSPageSize = true, Tagged = false });
            }
            finally { await page.CloseAsync(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
    }

    // iText pdfHTML (AGPL or commercial): managed, its own layout engine. Building a font provider is expensive, so
    // as iText's knowledge base suggests it is created once and reused (one per thread, as it is not thread-safe).
    sealed class ITextEngine : IEngine
    {
        readonly ThreadLocal<iText.Html2pdf.Resolver.Font.DefaultFontProvider> _fonts = new(() => new());

        public Task<byte[]> Convert(string htmlPath)
        {
            using var ms = new MemoryStream();
            var props = new iText.Html2pdf.ConverterProperties().SetBaseUri(Path.GetDirectoryName(htmlPath) + "/")
                .SetFontProvider(_fonts.Value);
            iText.Html2pdf.HtmlConverter.ConvertToPdf(File.ReadAllText(htmlPath), ms, props);
            return Task.FromResult(ms.ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Syncfusion HTML to PDF (commercial; runs as a trial without a licence key): drives its bundled Chrome (Blink).
    // Its support forum advises one converter per thread and ReuseBrowserProcess to keep a browser between
    // conversions, so converters are pooled, one per concurrent conversion.
    sealed class SyncfusionEngine : IEngine
    {
        readonly System.Collections.Concurrent.ConcurrentBag<Syncfusion.HtmlConverter.HtmlToPdfConverter> _pool = new();
        readonly System.Collections.Concurrent.ConcurrentBag<Syncfusion.HtmlConverter.HtmlToPdfConverter> _all = new();

        static Syncfusion.HtmlConverter.HtmlToPdfConverter NewConverter()
        {
            // AdditionalDelay defaults to 2 seconds of waiting for scripts; these documents have none, and the other
            // Chromium engines don't wait either.
            var settings = new Syncfusion.HtmlConverter.BlinkConverterSettings { PdfPageSize = Syncfusion.Pdf.PdfPageSize.A4, AdditionalDelay = 0 };
            foreach (var a in ChromiumArgs) settings.CommandLineArguments.Add(a);
            settings.CommandLineArguments.Add("--disable-setuid-sandbox");
            return new Syncfusion.HtmlConverter.HtmlToPdfConverter { ConverterSettings = settings, ReuseBrowserProcess = true };
        }

        public Task<byte[]> Convert(string htmlPath)
        {
            if (!_pool.TryTake(out var converter)) { converter = NewConverter(); _all.Add(converter); }
            try
            {
                var doc = converter.Convert(htmlPath);
                using var ms = new MemoryStream();
                doc.Save(ms);
                doc.Close(true);
                return Task.FromResult(ms.ToArray());
            }
            finally { _pool.Add(converter); }
        }

        public ValueTask DisposeAsync()
        {
            foreach (var c in _all) c.Close();
            return ValueTask.CompletedTask;
        }
    }
}
