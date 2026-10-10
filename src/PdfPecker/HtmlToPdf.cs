using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker;

/// <summary>
/// Converts HTML with CSS to PDF. Everything (HTML parsing, CSS cascade, layout, PDF writing) is implemented in
/// this library; it has no dependencies beyond the .NET base class library.
/// </summary>
/// <remarks>Instances are not needed: all methods are static and thread-safe.</remarks>
public static class HtmlToPdf
{
    /// <summary>Converts an HTML string to a PDF document.</summary>
    public static byte[] Convert(string html, PdfOptions? options = null)
    {
        if (html == null) throw new ArgumentNullException(nameof(html));
        return Render(html, options ?? new PdfOptions(), options?.BaseDirectory);
    }

    /// <summary>Converts an HTML string and writes the PDF to <paramref name="output"/>.</summary>
    public static void Convert(string html, Stream output, PdfOptions? options = null)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        var bytes = Convert(html, options);
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Converts an HTML file to a PDF file. Relative image and stylesheet paths resolve against the HTML file's folder.</summary>
    public static void ConvertFile(string htmlPath, string pdfPath, PdfOptions? options = null)
    {
        var html = File.ReadAllText(htmlPath);
        options ??= new PdfOptions();
        var baseDir = options.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(htmlPath));
        File.WriteAllBytes(pdfPath, Render(html, options, baseDir));
    }

    /// <summary>Converts an HTML string to a PDF asynchronously on the thread pool.</summary>
    public static Task<byte[]> ConvertAsync(string html, PdfOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(html, options), cancellationToken);

    private static byte[] Render(string html, PdfOptions options, string? baseDirectory)
    {
        var doc = HtmlParser.Parse(html);
        var resolver = options.ResourceResolver;

        // Stylesheets in document order: <style> blocks and <link rel="stylesheet">.
        var sheet = new Stylesheet();
        CollectStyles(doc, sheet, options.AllowFileAccess ? baseDirectory : null, resolver, options.AllowFileAccess);
        if (!string.IsNullOrEmpty(options.UserStyleSheet)) CssParser.ParseStylesheet(options.UserStyleSheet!, sheet);

        var page = new PageSetup
        {
            Width = options.PageSize.Width,
            Height = options.PageSize.Height,
            MarginTop = options.Margins.Top,
            MarginRight = options.Margins.Right,
            MarginBottom = options.Margins.Bottom,
            MarginLeft = options.Margins.Left,
        };
        if (!options.IgnoreCssPageRules) ApplyPageRules(sheet.PageDeclarations, page);
        if (page.ContentWidth < 10 || page.ContentHeight < 10)
            throw new ArgumentException("The page margins leave no room for content.", nameof(options));

        var root = new ComputedStyle { Display = Display.Block };
        new StyleResolver(sheet).ResolveTree(doc, root);

        var engine = new LayoutEngine(page.ContentHeight, new ImageLoader(baseDirectory, resolver, options.AllowFileAccess));
        float height = engine.Layout(doc, page.ContentWidth);

        uint background = 0;
        var htmlEl = doc.FindFirst("html");
        var body = doc.FindFirst("body");
        if (htmlEl?.Style != null && CssColor.IsVisible(htmlEl.Style.BackgroundColor)) background = htmlEl.Style.BackgroundColor;
        else if (body?.Style != null && CssColor.IsVisible(body.Style.BackgroundColor)) background = body.Style.BackgroundColor;

        string? title = options.Title;
        if (title == null)
        {
            var t = doc.FindFirst("title");
            if (t != null) title = System.Text.RegularExpressions.Regex.Replace(t.InnerText(), @"\s+", " ").Trim();
        }

        var writer = new PdfDocumentWriter(page, options.Compress, options.CompressionLevel);
        return writer.Write(engine.Ops, height, engine.Anchors, background, title, options.Author, options.Subject,
            options.FooterText, options.HeaderText);
    }

    private static void CollectStyles(Element parent, Stylesheet sheet, string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e) continue;
            if (e.Name == "style")
            {
                var media = e.GetAttribute("media");
                if (media == null || MediaMatches(media)) CssParser.ParseStylesheet(e.InnerText(), sheet);
                continue;
            }
            if (e.Name == "link" && (e.GetAttribute("rel") ?? "").IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var media = e.GetAttribute("media");
                var css = media == null || MediaMatches(media) ? LoadText(e.GetAttribute("href"), baseDirectory, resolver, allowFiles) : null;
                if (css != null) CssParser.ParseStylesheet(css, sheet);
                continue;
            }
            CollectStyles(e, sheet, baseDirectory, resolver, allowFiles);
        }
    }

    private static bool MediaMatches(string media)
    {
        var m = media.ToLowerInvariant();
        return m.Length == 0 || m.Contains("print") || m.Contains("all");
    }

    private static string? LoadText(string? href, string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        try
        {
            var bytes = resolver?.Invoke(href!);
            if (bytes != null) return System.Text.Encoding.UTF8.GetString(bytes);
            if (href!.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;
            if (!allowFiles) return null;
            var path = href;
            if (!Path.IsPathRooted(path))
            {
                if (baseDirectory == null) return null;
                path = Path.Combine(baseDirectory, path);
            }
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void ApplyPageRules(List<Declaration> decls, PageSetup page)
    {
        foreach (var d in decls)
        {
            var v = d.Value.Trim().ToLowerInvariant();
            switch (d.Property)
            {
                case "size":
                {
                    var tokens = CssParser.Tokens(v);
                    bool landscape = tokens.Remove("landscape");
                    bool portrait = tokens.Remove("portrait");
                    PageSize? size = null;
                    if (tokens.Count == 1)
                    {
                        size = tokens[0] switch
                        {
                            "a3" => PageSize.A3,
                            "a4" => PageSize.A4,
                            "a5" => PageSize.A5,
                            "letter" => PageSize.Letter,
                            "legal" => PageSize.Legal,
                            _ => StyleResolver.TryParseLength(tokens[0], 12, 12, out var l) && !l.IsAuto && !l.IsPercent && l.Value > 0
                                ? new PageSize(l.Value, l.Value) : null,
                        };
                    }
                    else if (tokens.Count == 2
                             && StyleResolver.TryParseLength(tokens[0], 12, 12, out var w) && !w.IsAuto && !w.IsPercent && w.Value > 0
                             && StyleResolver.TryParseLength(tokens[1], 12, 12, out var h) && !h.IsAuto && !h.IsPercent && h.Value > 0)
                    {
                        size = new PageSize(w.Value, h.Value);
                    }
                    var final = size ?? new PageSize(page.Width, page.Height);
                    if (landscape) final = final.Landscape;
                    else if (portrait && final.Width > final.Height) final = new PageSize(final.Height, final.Width);
                    page.Width = final.Width;
                    page.Height = final.Height;
                    break;
                }
                case "margin-top": SetMargin(v, ref page.MarginTop, page.Height); break;
                case "margin-right": SetMargin(v, ref page.MarginRight, page.Width); break;
                case "margin-bottom": SetMargin(v, ref page.MarginBottom, page.Height); break;
                case "margin-left": SetMargin(v, ref page.MarginLeft, page.Width); break;
            }
        }
    }

    private static void SetMargin(string v, ref float target, float basis)
    {
        if (StyleResolver.TryParseLength(v, 12, 12, out var l) && !l.IsAuto) target = Math.Max(0, l.Resolve(basis));
    }
}
