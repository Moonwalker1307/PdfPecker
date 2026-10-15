using PdfPecker.Css;
using PdfPecker.Fonts;
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

    /// <summary>
    /// Converts an HTML string to a PDF document, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled. Use it with a timeout when the HTML comes from untrusted users.
    /// </summary>
    public static byte[] Convert(string html, PdfOptions? options, CancellationToken cancellationToken)
    {
        if (html == null) throw new ArgumentNullException(nameof(html));
        return Render(html, options ?? new PdfOptions(), options?.BaseDirectory, cancellationToken);
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
    /// <remarks>Cancelling <paramref name="cancellationToken"/> stops a conversion that is already running.</remarks>
    public static Task<byte[]> ConvertAsync(string html, PdfOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(html, options, cancellationToken), cancellationToken);

    internal static byte[] Render(string html, PdfOptions options, string? baseDirectory, CancellationToken cancellationToken = default)
    {
        if (!(options.Scale >= 0.1f && options.Scale <= 2f))
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "PdfOptions.Scale must be between 0.1 and 2.");
        var conformance = options.Conformance;
        if (options.EInvoice != null)
        {
            // Factur-X / ZUGFeRD files are PDF/A-3, the part that allows attachments.
            if (conformance == PdfConformance.None) conformance = PdfConformance.PdfA3b;
            else if (conformance != PdfConformance.PdfA3b)
                throw new ArgumentException("An e-invoice needs PDF/A-3: set PdfOptions.Conformance to PdfA3b or leave it at None.", nameof(options));
        }
        var attachments = EmbeddedFiles.Prepare(options.Attachments, options.EInvoice);
        if (conformance == PdfConformance.PdfA2b && attachments.Count > 0)
            throw new ArgumentException("PDF/A-2b allows attaching only PDF/A files, which PdfPecker does not check: "
                + "set PdfOptions.Conformance to PdfA3b, which allows any attachment, or to None for a plain PDF.", nameof(options));
        if (conformance != PdfConformance.None && options.Security != null)
            throw new ArgumentException(options.EInvoice != null
                ? "An e-invoice is a PDF/A file, which does not allow encryption: remove PdfOptions.Security."
                : "PDF/A does not allow encryption: set PdfOptions.Conformance or PdfOptions.Security, not both.", nameof(options));
        bool pdfua = options.Accessibility == PdfAccessibility.PdfUA1;
        if (pdfua && options.Security is { } security && (security.Permissions & PdfPermissions.ExtractForAccessibility) == 0)
            throw new ArgumentException("PDF/UA needs PdfPermissions.ExtractForAccessibility, so screen readers can read an encrypted file.", nameof(options));
        if (options.Watermark is { } wm && string.IsNullOrEmpty(wm.Text) && wm.Image == null)
            throw new ArgumentException("The watermark needs a Text or an Image.", nameof(options));
        var doc = HtmlParser.Parse(html);
        if (MayHaveControls(html)) FormControls.Prepare(doc, options.FormFields);
        var resolver = options.ResourceResolver;

        // Stylesheets in document order: <style> blocks and <link rel="stylesheet">.
        // Media queries see the page area, as in Chrome's print preview: first as the options give it, then, when an
        // @page rule changes the size or margins and a query depends on it, as the rule makes it.
        var media = new MediaContext
        {
            Width = options.PageSize.Width - options.Margins.Left - options.Margins.Right,
            Height = options.PageSize.Height - options.Margins.Top - options.Margins.Bottom,
        };
        var sheet = ReadStyles(doc, media, options, baseDirectory, resolver);

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
        if (media.UsedSize && (Math.Abs(media.Width - page.ContentWidth) > 0.01f || Math.Abs(media.Height - page.ContentHeight) > 0.01f))
            sheet = ReadStyles(doc, new MediaContext { Width = page.ContentWidth, Height = page.ContentHeight }, options, baseDirectory, resolver);
        if (options.Scale != 1)
        {
            // Lay the page out at its paper size divided by the scale, as Chrome's print scale does; the writer then
            // draws it scaled, so the margins come out at their paper size again.
            float s = options.Scale;
            page.Scale = s;
            page.Width /= s; page.Height /= s;
            page.MarginTop /= s; page.MarginRight /= s; page.MarginBottom /= s; page.MarginLeft /= s;
        }
        if (page.ContentWidth < 10 || page.ContentHeight < 10)
            throw new ArgumentException("The page margins leave no room for content.", nameof(options));

        // Running header and footer: elements marked data-sticky-pdf-header / data-sticky-pdf-footer are taken out of
        // the flow and drawn at the top and bottom of every page; the flowing content gets the space in between.
        // Their height attributes (e.g. height="17mm") give the first estimate of that space, so that vh units can
        // usually be resolved in one pass.
        // Most documents have no bands: one search of the source spares walking the whole tree four times.
        bool mayHaveBands = html.Contains("data-sticky-pdf-", StringComparison.OrdinalIgnoreCase);
        var headerCandidate = mayHaveBands ? FindByAttribute(doc, "data-sticky-pdf-header") : null;
        var footerCandidate = mayHaveBands ? FindByAttribute(doc, "data-sticky-pdf-footer") : null;
        bool truncateBands = options.TruncateBandsToWholePoints;
        float declaredTop = DeclaredHeight(headerCandidate, page, truncateBands), declaredBottom = DeclaredHeight(footerCandidate, page, truncateBands);
        float viewportH = Math.Max(10, page.ContentHeight - declaredTop - declaredBottom);

        var images = new ImageLoader(baseDirectory, resolver, options.AllowFileAccess);
        var missingFonts = options.MissingFont is { } onMissing ? new MissingFontReport(onMissing) : null;
        var fonts = FontResolver.Create(sheet.FontFaces, options.Fonts, FetchFont(images),
            embedStandard: options.EmbedStandardFonts || conformance != PdfConformance.None || pdfua,
            systemFonts: options.UseSystemFonts, report: missingFonts);
        images.Fonts = fonts;
        // Pagination results that generated content uses: the pages of target-counter() and the running strings.
        var paged = new PagedContent();
        float viewport = viewportH;
        ComputedStyle root = null!;
        StyleResolver Resolve()
        {
            root = new ComputedStyle { Display = Display.Block, Fonts = fonts, MissingFonts = missingFonts };
            var r = new StyleResolver(sheet, page.ContentWidth, viewport) { Cancellation = cancellationToken, Paged = paged };
            r.ResolveTree(doc, root);
            return r;
        }
        var styles = Resolve();

        var header = FindBand(doc, "data-sticky-pdf-header");
        var footer = FindBand(doc, "data-sticky-pdf-footer");
        if (footer == header) footer = null;
        if (header != null) header.Detached = true;
        if (footer != null) footer.Detached = true;
        void ReserveBands(PageSetup setup)
        {
            if (header == null && footer == null) return;
            if (header != null) setup.BandTop = Math.Max(DeclaredHeight(header, setup, truncateBands), BandHeight(header, setup, images, cancellationToken));
            if (footer != null) setup.BandBottom = Math.Max(DeclaredHeight(footer, setup, truncateBands), BandHeight(footer, setup, images, cancellationToken));
            float limit = setup.ContentHeight * 0.6f;
            float bands = setup.BandTop + setup.BandBottom;
            if (bands > limit)
            {
                setup.BandTop *= limit / bands;
                setup.BandBottom *= limit / bands;
            }
        }
        ReserveBands(page);

        // vh units refer to the space left for the flowing content.
        if (styles.UsedViewportHeight && Math.Abs(page.FlowHeight - viewportH) > 0.01f)
        {
            viewport = page.FlowHeight;
            styles = Resolve();
        }

        var htmlEl = doc.FindFirst("html");
        var body = doc.FindFirst("body");
        var headings = options.Bookmarks ? new List<OutlineHeading>() : null;

        // Named pages (page: wide with @page wide { ... }): each run of content on one named page gets that page's setup.
        var setups = new Dictionary<string, PageSetup>(StringComparer.Ordinal);
        PageSetup SetupFor(string? name)
        {
            if (name == null || options.IgnoreCssPageRules || !sheet.PageRules.Any(r => r.Name == name && r.Selector.Length == 0)) return page;
            if (setups.TryGetValue(name, out var cached)) return cached;
            var setup = new PageSetup
            {
                Width = options.PageSize.Width, Height = options.PageSize.Height,
                MarginTop = options.Margins.Top, MarginRight = options.Margins.Right, MarginBottom = options.Margins.Bottom, MarginLeft = options.Margins.Left,
            };
            ApplyPageRules(sheet.PageDeclarations, setup);
            foreach (var rule in sheet.PageRules)
                if (rule.Name == name && rule.Selector.Length == 0) ApplyPageRules(rule.Declarations, setup);
            if (options.Scale != 1)
            {
                float s = options.Scale;
                setup.Scale = s;
                setup.Width /= s; setup.Height /= s;
                setup.MarginTop /= s; setup.MarginRight /= s; setup.MarginBottom /= s; setup.MarginLeft /= s;
            }
            if (setup.ContentWidth < 10 || setup.ContentHeight < 10)
                throw new ArgumentException($"The margins of @page {name} leave no room for content.", nameof(options));
            ReserveBands(setup);
            return setups[name] = setup;
        }

        // Layout, repeated when generated content shows page numbers (target-counter): they are known after a layout,
        // and a layout with them can move content again, so there is at most a third.
        ComputedStyle? canvasStyle;
        StructureTree? structure;
        List<PageRun> runs;
        Dictionary<string, int>? targetPages = null;
        for (int pass = 0; ; pass++)
        {
            // The root element's background, or else the body's, is the canvas background: it fills every page area (as
            // in Chrome, not the page margins), positioned against the root element's box, and that element's own box
            // does not paint it again.
            canvasStyle = HasBackground(htmlEl) ? htmlEl!.Style : HasBackground(body) ? body!.Style : null;
            if (canvasStyle != null) canvasStyle.CanvasBackground = true;

            // The logical structure of a tagged PDF follows the HTML in document order, whatever the layout does.
            structure = options.Accessibility != PdfAccessibility.None ? StructureTree.Build(doc) : null;
            headings?.Clear();
            runs = styles.UsedPageNames ? SplitByPageName(doc) : new List<PageRun> { new() };
            foreach (var run in runs)
            {
                run.Page = SetupFor(run.Name);
                var restore = run.Keep is { } keep ? KeepOnly(keep) : null;
                try
                {
                    run.Engine = new LayoutEngine(run.Page.FlowHeight, images)
                    {
                        Cancellation = cancellationToken, MaxPages = Math.Max(1, options.MaxPages), FixedAreaHeight = run.Page.ContentHeight,
                        CanvasRoot = htmlEl, Tags = structure, Headings = headings,
                    };
                    run.Height = run.Engine.Layout(doc, run.Page.ContentWidth);
                }
                finally
                {
                    restore?.Invoke();
                }
            }
            if (!paged.UsesTargets || pass == 2) break;
            var pages = PagesOfAnchors(runs);
            if (targetPages != null && pages.Count == targetPages.Count && !pages.Except(targetPages).Any()) break;
            targetPages = pages;
            paged.PageOfId = id => pages.TryGetValue(id, out int n) ? n : null;
            styles = Resolve();
        }

        var sections = new List<PageSection>(runs.Count);
        foreach (var run in runs)
        {
            var engine = run.Engine;
            Func<int, List<DrawOp>>? pageCanvas = null;
            if (canvasStyle != null)
            {
                var rootStyle = htmlEl?.Style ?? canvasStyle;
                var setup = run.Page;
                // Chrome sizes the positioning area like the root element's border box but puts it at the page area's top
                // left corner, whatever the root element's margins.
                var (_, _, rw, rh) = engine.CanvasRootBox ?? (0, 0, setup.ContentWidth, run.Height);
                // html { height: 100% } refers to the page area.
                if (rootStyle.Height.IsPercent) rh = rootStyle.Height.Resolve(setup.ContentHeight);
                float flowH = setup.FlowHeight;
                var painted = canvasStyle;
                pageCanvas = p =>
                {
                    // y = 0 is the top of the page area; flow y = 0 of page p sits BandTop below it.
                    var group = new GroupOp();
                    BoxPainter.PaintCanvas(group, painted, rootStyle, 0, setup.BandTop - p * flowH, rw, rh,
                        0, 0, setup.ContentWidth, setup.ContentHeight, images, setup.ContentWidth);
                    return group.Ops;
                };
            }
            sections.Add(new PageSection
            {
                Page = run.Page, Ops = engine.Ops, ContentHeight = run.Height, Anchors = engine.Anchors,
                FixedOps = engine.FixedOps.Count > 0 ? engine.FixedOps : null, Canvas = pageCanvas,
            });
        }

        string? title = options.Title;
        if (title == null)
        {
            var t = doc.FindFirst("title");
            if (t != null) title = System.Text.RegularExpressions.Regex.Replace(t.InnerText(), @"\s+", " ").Trim();
        }
        if (pdfua && string.IsNullOrEmpty(title))
        {
            title = structure!.FirstHeading
                    ?? throw new ArgumentException("PDF/UA needs a document title: set PdfOptions.Title or give the HTML a <title>.", nameof(options));
        }

        // The watermark and the header/footer text use the body's font family when it names an embedded font, else Helvetica.
        TextFont? marginFont = null, watermarkFont = null;
        if (fonts != null)
        {
            var bodyFamily = (body?.Style ?? root).FontFamilyName;
            if (options.FooterText != null || options.HeaderText != null)
                marginFont = fonts.Resolve(bodyFamily, FontFamily.Helvetica, 400, false);
            if (options.Watermark is { Text.Length: > 0 } w)
                watermarkFont = fonts.Resolve(w.FontFamily ?? bodyFamily, FontFamily.Helvetica, w.Bold ? 700 : 400, false);
        }
        else if (options.Watermark is { FontFamily: { } family } w)
        {
            missingFonts?.CheckStandardOnly(family);
            if (FontResolver.FirstStandardFamily(family) is FontFamily sf) watermarkFont = StandardFont.Get(sf, w.Bold, false);
        }
        var writer = new PdfDocumentWriter(page, options.Compress, options.CompressionLevel)
        {
            Watermark = options.Watermark,
            Security = options.Security,
            Conformance = conformance,
            EInvoice = options.EInvoice,
            Attachments = attachments,
            WatermarkFont = watermarkFont,
            MarginFont = marginFont,
            EmbedStandardFonts = options.EmbedStandardFonts,
            MissingCharacter = options.MissingCharacter,
            MaxPages = options.MaxPages,
            Cancellation = cancellationToken,
            Structure = structure,
            PdfUA = pdfua,
            Language = structure == null ? null : options.Language ?? structure.Language ?? (pdfua ? "en" : null),
            Properties = new DocumentProperties
            {
                Keywords = options.Keywords,
                Creator = options.Creator,
                Producer = options.Producer,
                Custom = DocumentProperties.Validate(options.CustomProperties),
            },
        };
        // Page-margin boxes of @page rules (@top-center and the like), laid out per page like the bands.
        var marginBoxes = !options.IgnoreCssPageRules && PageMarginBoxes.AnyIn(sheet.PageRules)
            ? new PageMarginBoxes(sheet.PageRules, () => new ComputedStyle { Display = Display.Block, Fonts = fonts, MissingFonts = missingFonts },
                page, images, cancellationToken, paged)
            : null;
        Func<int, int, List<DrawOp>>? pageBands = null;
        if (header != null || footer != null || marginBoxes != null)
        {
            var strings = styles.Strings;
            pageBands = (p, count) =>
            {
                var run = runs[writer.SectionOfPage(p).Section];
                var ops = new List<DrawOp>();
                if (marginBoxes != null)
                {
                    // Running strings (string-set) for string(), from the pages their elements landed on.
                    if (paged.Running == null && strings.Count > 0)
                    {
                        var assignments = new List<(string, int, float, string)>(strings.Count);
                        foreach (var a in strings)
                            for (int s = 0; s < runs.Count; s++)
                                if (runs[s].Engine.Anchors.TryGetValue(a.Key, out float y))
                                {
                                    var (page, top) = writer.PageAt(s, y);
                                    assignments.Add((a.Name, page, top, a.Value));
                                    break;
                                }
                        paged.Running = new RunningStrings(assignments);
                    }
                    ops.AddRange(marginBoxes.Layout(p, count, run.Page, run.Name));
                }
                if (header != null) ops.AddRange(LayoutBandForPage(header, run.Page, images, p, count, alignBottom: false, cancellationToken));
                if (footer != null) ops.AddRange(LayoutBandForPage(footer, run.Page, images, p, count, alignBottom: true, cancellationToken));
                return ops;
            };
        }
        var pdf = writer.Write(sections, page.Background, title, options.Author, options.Subject,
            options.FooterText, options.HeaderText, pageBands, headings);
        if (options.Signature != null)
            pdf = PdfSigner.Sign(pdf, options.Signature, options.Security?.OwnerPassword ?? options.Security?.UserPassword);
        return pdf;
    }

    /// <summary>A run of content laid out on one page setup: a named page's, or the document's.</summary>
    private sealed class PageRun
    {
        public string? Name;
        /// <summary>The run's top-level nodes and the elements that contain them; null for the whole document.</summary>
        public (HashSet<Node> Units, HashSet<Element> Ancestors)? Keep;
        public PageSetup Page = null!;
        public LayoutEngine Engine = null!;
        public float Height;
    }

    /// <summary>
    /// Splits the document where the page name (the page property) of its block-level boxes changes, as CSS Paged
    /// Media puts boxes with another page name on a new page. Content without a page of its own (whitespace, boxes
    /// out of the flow) stays with the run it is in.
    /// </summary>
    private static List<PageRun> SplitByPageName(Element doc)
    {
        var runs = new List<PageRun>();
        var units = new List<List<Node>>();
        var pending = new List<Node>();
        void Add(Node n, string? name, bool any)
        {
            if (any && runs.Count == 0) { pending.Add(n); return; }
            if (!any && (runs.Count == 0 || runs[^1].Name != name))
            {
                runs.Add(new PageRun { Name = name });
                units.Add(new List<Node>(pending));
                pending.Clear();
            }
            units[^1].Add(n);
        }
        void Walk(Element e)
        {
            foreach (var c in e.Children)
            {
                if (c is Element ce && InFlowBlock(ce))
                {
                    if (IsBlockContainer(ce.Style) && !Uniform(ce, ce.Style.PageName)) Walk(ce);
                    else Add(ce, ce.Style.PageName, false);
                }
                else Add(c, e.Style?.PageName, HasNoPage(c));
            }
        }
        Walk(doc);
        if (runs.Count <= 1) return new List<PageRun> { new() { Name = runs.Count == 1 ? runs[0].Name : null } };
        for (int i = 0; i < runs.Count; i++)
        {
            var ancestors = new HashSet<Element>();
            foreach (var n in units[i])
                for (var a = n.Parent; a != null && ancestors.Add(a); a = a.Parent) { }
            runs[i].Keep = (new HashSet<Node>(units[i]), ancestors);
        }
        return runs;

        static bool InFlowBlock(Element e) => e.Style is { } s && s.Display != Display.None && !e.Detached && s.IsBlockLevel
                                              && s.Position is not (Position.Absolute or Position.Fixed);
        static bool IsBlockContainer(ComputedStyle s) => s.Display is Display.Block or Display.ListItem;
        static bool Uniform(Element e, string? name)
        {
            foreach (var c in e.Children)
                if (c is Element ce && InFlowBlock(ce) && (ce.Style.PageName != name || IsBlockContainer(ce.Style) && !Uniform(ce, name))) return false;
            return true;
        }
        // Whitespace, hidden elements and boxes out of the flow go with whatever page they fall on.
        static bool HasNoPage(Node n) => n is TextNode t
            ? string.IsNullOrWhiteSpace(t.Text)
            : n is not Element { Detached: false, Style: { Display: not Display.None, Position: not (Position.Absolute or Position.Fixed) } };
    }

    /// <summary>Takes the nodes of other runs out of the tree; the returned action puts them back.</summary>
    private static Action KeepOnly((HashSet<Node> Units, HashSet<Element> Ancestors) keep)
    {
        var removed = new List<(Element Parent, Node[] Children)>();
        void Filter(Element e)
        {
            removed.Add((e, e.Children.ToArray()));
            e.Children.RemoveAll(c => !keep.Units.Contains(c) && !(c is Element ce && keep.Ancestors.Contains(ce)));
            foreach (var c in e.Children)
                if (c is Element ce && keep.Ancestors.Contains(ce)) Filter(ce);
        }
        foreach (var a in keep.Ancestors)
            if (a.Parent == null) Filter(a);
        return () =>
        {
            foreach (var (parent, children) in removed)
            {
                parent.Children.Clear();
                parent.Children.AddRange(children);
            }
        };
    }

    /// <summary>The page number (1-based) of each id's element in the laid-out runs, for target-counter().</summary>
    private static Dictionary<string, int> PagesOfAnchors(List<PageRun> runs)
    {
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        int first = 0;
        foreach (var run in runs)
        {
            float h = run.Page.FlowHeight;
            foreach (var (key, y) in run.Engine.Anchors)
                if (key.Length > 0 && key[0] != '\u0001') pages.TryAdd(key, first + Math.Max(0, (int)Math.Floor((y + 0.01) / h)) + 1);
            first += PageSection.PagesFor(run.Height, h);
        }
        return pages;
    }

    private static Func<string, byte[]?> FetchFont(ImageLoader images) => url =>
    {
        try
        {
            return images.Fetch(url.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    };

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

    /// <summary>Whether the source can hold form controls: most documents have none, which spares a walk of the tree.</summary>
    private static bool MayHaveControls(string html)
        => html.Contains("<input", StringComparison.OrdinalIgnoreCase) || html.Contains("<select", StringComparison.OrdinalIgnoreCase)
           || html.Contains("<textarea", StringComparison.OrdinalIgnoreCase) || html.Contains("<button", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The height a band reserves with its height attribute, in layout points. With <paramref name="truncate"/>, it is
    /// rounded down to whole points on paper, as Chrome rounds page margins.
    /// </summary>
    private static float DeclaredHeight(Element? band, PageSetup page, bool truncate)
    {
        float height = DeclaredHeight(band);
        if (!truncate || height <= 0) return height;
        // The small allowance keeps a height that is already whole on paper (48pt) from losing a point to float error.
        return MathF.Floor(height * page.Scale + 0.001f) / page.Scale;
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
    private static float BandHeight(Element band, PageSetup page, ImageLoader images, CancellationToken cancellationToken)
    {
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        foreach (var (node, original) in texts) node.Text = ReplacePlaceholders(original, "99", "99");
        try
        {
            return Math.Max(0, new LayoutEngine(page.ContentHeight, images) { Cancellation = cancellationToken }.LayoutBand(band, page.ContentWidth));
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    private static string ReplacePlaceholders(string text, string page, string pages)
        => text.Replace("{page}", page).Replace("{total-pages}", pages).Replace("{pages}", pages);

    /// <summary>Lays out a band for one page, with {page}, {pages} and {total-pages} replaced in its text.</summary>
    private static List<DrawOp> LayoutBandForPage(Element band, PageSetup page, ImageLoader images, int pageIndex, int pageCount, bool alignBottom, CancellationToken cancellationToken)
    {
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        string number = (pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string total = pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (node, original) in texts)
            node.Text = ReplacePlaceholders(original, number, total);
        try
        {
            var engine = new LayoutEngine(page.ContentHeight, images) { Cancellation = cancellationToken };
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

    private static Stylesheet ReadStyles(Element doc, MediaContext media, PdfOptions options, string? baseDirectory, Func<string, byte[]?>? resolver)
    {
        var sheet = new Stylesheet { Media = media };
        CollectStyles(doc, sheet, baseDirectory, resolver, options.AllowFileAccess);
        if (!string.IsNullOrEmpty(options.UserStyleSheet)) CssParser.ParseStylesheet(options.UserStyleSheet!, sheet);
        return sheet;
    }

    private static void CollectStyles(Element parent, Stylesheet sheet, string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e) continue;
            if (e.Name == "style")
            {
                var media = e.GetAttribute("media");
                if (media == null || MediaQuery.Matches(media, sheet.Media)) CssParser.ParseStylesheet(e.InnerText(), sheet);
                continue;
            }
            if (e.Name == "link" && (e.GetAttribute("rel") ?? "").IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var media = e.GetAttribute("media");
                var href = e.GetAttribute("href");
                var css = media == null || MediaQuery.Matches(media, sheet.Media) ? LoadText(href, baseDirectory, resolver, allowFiles) : null;
                if (css != null) CssParser.ParseStylesheet(RebaseUrls(css, href!), sheet);
                continue;
            }
            CollectStyles(e, sheet, baseDirectory, resolver, allowFiles);
        }
    }

    /// <summary>
    /// Makes the relative url() references of a linked stylesheet relative to the document, as they are relative to the
    /// stylesheet itself: url(img/bg.png) in css/site.css becomes url("css/img/bg.png").
    /// </summary>
    internal static string RebaseUrls(string css, string href)
    {
        int slash = href.LastIndexOfAny(new[] { '/', '\\' });
        if (slash < 0 || css.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0) return css;
        if (IsAbsoluteReference(href)) return css; // remote sheets only load through the resolver, which sees the URLs as written
        var folder = href.Substring(0, slash + 1);
        var sb = new System.Text.StringBuilder(css.Length + 64);
        int copied = 0, pos = 0;
        // Closing quote positions only move forward: caching them keeps the scan linear on hostile input like "url('url('".
        int nextDouble = -2, nextSingle = -2;
        int Next(ref int cache, char c, int from)
        {
            if (cache != -1 && cache < from) cache = css.IndexOf(c, from);
            return cache;
        }
        while ((pos = css.IndexOf("url(", pos, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int start = pos + 4, i = start;
            pos = start;
            while (i < css.Length && char.IsWhiteSpace(css[i])) i++;
            if (i >= css.Length) break;
            char q = css[i] is '"' or '\'' ? css[i] : '\0';
            int urlStart = q == '\0' ? i : i + 1, urlEnd, close;
            if (q == '\0')
            {
                // Unquoted: up to ")" with no whitespace, quotes or "(" inside; the scan stops at the first bad character.
                urlEnd = urlStart;
                while (urlEnd < css.Length && css[urlEnd] is not (')' or '(' or '"' or '\'') && !char.IsWhiteSpace(css[urlEnd])) urlEnd++;
                close = urlEnd;
                while (close < css.Length && char.IsWhiteSpace(css[close])) close++;
                if (close >= css.Length || css[close] != ')') continue;
            }
            else
            {
                urlEnd = q == '"' ? Next(ref nextDouble, '"', urlStart) : Next(ref nextSingle, '\'', urlStart);
                if (urlEnd < 0) continue;
                close = urlEnd + 1;
                while (close < css.Length && char.IsWhiteSpace(css[close])) close++;
                if (close >= css.Length || css[close] != ')') continue;
            }
            var url = css.Substring(urlStart, urlEnd - urlStart);
            if (url.Length == 0 || IsAbsoluteReference(url) || url.IndexOf('"') >= 0) continue;
            sb.Append(css, copied, pos - 4 - copied).Append("url(\"").Append(folder).Append(url).Append("\")");
            copied = pos = close + 1;
        }
        if (copied == 0) return css;
        return sb.Append(css, copied, css.Length - copied).ToString();
    }

    // A scheme (data:, http:, file:, C:), a rooted path or a fragment.
    private static bool IsAbsoluteReference(string url)
        => url.Length == 0 || url[0] is '/' or '\\' or '#' || System.Text.RegularExpressions.Regex.IsMatch(url, "^[A-Za-z][A-Za-z0-9+.-]*:");



    private static string? LoadText(string? href, string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        try
        {
            var bytes = resolver?.Invoke(href!);
            if (bytes != null) return System.Text.Encoding.UTF8.GetString(bytes);
            if (href!.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;
            var path = LocalFiles.Resolve(href, baseDirectory, allowFiles);
            return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
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
                case "background-color":
                case "background":
                    // The page box's own background covers the whole sheet, margins included.
                    if (CssColor.TryParse(v.Trim(), CssColor.Black, out uint color)) page.Background = color;
                    break;
            }
        }
    }

    private static bool HasBackground(Element? e) =>
        e?.Style is { } s && (CssColor.IsVisible(s.BackgroundColor) || s.BackgroundImages?.Any(l => l != null) == true);

    private static void SetMargin(string v, ref float target, float basis)
    {
        if (StyleResolver.TryParseLength(v, 12, 12, out var l) && !l.IsAuto) target = Math.Max(0, l.Resolve(basis));
    }
}
