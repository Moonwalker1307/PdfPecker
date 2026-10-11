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

    internal static byte[] Render(string html, PdfOptions options, string? baseDirectory)
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

        // Running header and footer: elements marked data-sticky-pdf-header / data-sticky-pdf-footer are taken out of
        // the flow and drawn at the top and bottom of every page; the flowing content gets the space in between.
        // Their height attributes (e.g. height="17mm") give the first estimate of that space, so that vh units can
        // usually be resolved in one pass.
        var headerCandidate = FindByAttribute(doc, "data-sticky-pdf-header");
        var footerCandidate = FindByAttribute(doc, "data-sticky-pdf-footer");
        float declaredTop = DeclaredHeight(headerCandidate), declaredBottom = DeclaredHeight(footerCandidate);
        float viewportH = Math.Max(10, page.ContentHeight - declaredTop - declaredBottom);

        var root = new ComputedStyle { Display = Display.Block };
        var styles = new StyleResolver(sheet, page.ContentWidth, viewportH);
        styles.ResolveTree(doc, root);
        var images = new ImageLoader(baseDirectory, resolver, options.AllowFileAccess);

        var header = FindBand(doc, "data-sticky-pdf-header");
        var footer = FindBand(doc, "data-sticky-pdf-footer");
        if (footer == header) footer = null;
        if (header != null || footer != null)
        {
            if (header != null) { header.Detached = true; page.BandTop = Math.Max(DeclaredHeight(header), BandHeight(header, page, images)); }
            if (footer != null) { footer.Detached = true; page.BandBottom = Math.Max(DeclaredHeight(footer), BandHeight(footer, page, images)); }
            float limit = page.ContentHeight * 0.6f;
            float bands = page.BandTop + page.BandBottom;
            if (bands > limit)
            {
                page.BandTop *= limit / bands;
                page.BandBottom *= limit / bands;
            }
        }

        // vh units refer to the space left for the flowing content.
        if (styles.UsedViewportHeight && Math.Abs(page.FlowHeight - viewportH) > 0.01f)
        {
            root = new ComputedStyle { Display = Display.Block };
            new StyleResolver(sheet, page.ContentWidth, page.FlowHeight).ResolveTree(doc, root);
        }

        var engine = new LayoutEngine(page.FlowHeight, images);
        float height = engine.Layout(doc, page.ContentWidth);

        Func<int, int, List<DrawOp>>? pageBands = null;
        if (header != null || footer != null)
        {
            pageBands = (p, count) =>
            {
                var ops = new List<DrawOp>();
                if (header != null) ops.AddRange(LayoutBandForPage(header, page, images, p, count, alignBottom: false));
                if (footer != null) ops.AddRange(LayoutBandForPage(footer, page, images, p, count, alignBottom: true));
                return ops;
            };
        }

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
            options.FooterText, options.HeaderText, pageBands);
    }

    private static Element? FindByAttribute(Element parent, string attribute)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e) continue;
            if (e.HasAttribute(attribute)) return e;
            var found = FindByAttribute(e, attribute);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>The band height given by a height attribute (e.g. height="17mm"; plain numbers are pixels), or 0.</summary>
    private static float DeclaredHeight(Element? band)
    {
        var attr = band?.GetAttribute("height")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(attr)) return 0;
        if (float.TryParse(attr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) return Math.Max(0, px * 0.75f);
        return StyleResolver.TryParseLength(attr!, 12, 12, out var len) && !len.IsAuto && !len.IsPercent ? Math.Max(0, len.Value) : 0;
    }

    private static Element? FindBand(Element parent, string attribute)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e || e.Style == null || e.Style.Display == Display.None) continue;
            if (e.HasAttribute(attribute)) return e;
            var found = FindBand(e, attribute);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>The laid-out height of a band, measured with two-digit page numbers in place of the placeholders.</summary>
    private static float BandHeight(Element band, PageSetup page, ImageLoader images)
    {
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        foreach (var (node, original) in texts) node.Text = ReplacePlaceholders(original, "99", "99");
        try
        {
            return Math.Max(0, new LayoutEngine(page.ContentHeight, images).LayoutBand(band, page.ContentWidth));
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    private static string ReplacePlaceholders(string text, string page, string pages)
        => text.Replace("{page}", page).Replace("{total-pages}", pages).Replace("{pages}", pages);

    /// <summary>Lays out a band for one page, with {page}, {pages} and {total-pages} replaced in its text.</summary>
    private static List<DrawOp> LayoutBandForPage(Element band, PageSetup page, ImageLoader images, int pageIndex, int pageCount, bool alignBottom)
    {
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        string number = (pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string total = pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (node, original) in texts)
            node.Text = ReplacePlaceholders(original, number, total);
        try
        {
            var engine = new LayoutEngine(page.ContentHeight, images);
            float h = engine.LayoutBand(band, page.ContentWidth);
            if (alignBottom)
            {
                float dy = page.ContentHeight - h;
                foreach (var op in engine.Ops) op.Offset(0, dy);
            }
            return engine.Ops;
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    private static void CollectPlaceholders(Element e, List<(TextNode, string)> texts)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t && t.Text.IndexOf('{') >= 0) texts.Add((t, t.Text));
            else if (c is Element ce) CollectPlaceholders(ce, texts);
        }
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
