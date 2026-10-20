using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    /// <summary>
    /// One conversion of an HTML document: the steps of the pipeline in the order <see cref="Run"/> calls them, and the
    /// state they hand on to each other. Each step reads only what the steps before it have set.
    /// </summary>
    private sealed partial class Conversion
    {
        private readonly string _html;
        private readonly PdfOptions _options;
        private readonly ConversionSettings _settings;
        private readonly string? _baseDirectory;
        private readonly Func<string, byte[]?>? _resolver;
        private readonly CancellationToken _cancellation;

        private Element _doc = null!;
        private Stylesheet _sheet = null!;
        /// <summary>The document's page: the options' size and margins with the @page rules applied, scaled and fitted.</summary>
        private PageSetup _page = null!;
        /// <summary>The viewport height that vh units refer to: the space left for the flowing content.</summary>
        private float _viewport;

        private ImageLoader _images = null!;
        private MissingFontReport? _missingFonts;
        private FontResolver? _fonts;
        /// <summary>Pagination results that generated content uses: the pages of target-counter() and the running strings.</summary>
        private PagedContent _paged = null!;
        private Dictionary<string, HyphenationPatterns>? _hyphenation;
        /// <summary>The root element's lang attribute, the language of boxes outside the document tree.</summary>
        private string? _htmlLang;

        /// <summary>The initial style of the latest style pass, which the body falls back to.</summary>
        private ComputedStyle _root = null!;
        private StyleResolver _styles = null!;

        /// <summary>The running header and footer (data-sticky-pdf-header / data-sticky-pdf-footer), out of the flow.</summary>
        private Element? _header, _footer;

        public Conversion(string html, PdfOptions options, ConversionSettings settings, string? baseDirectory, CancellationToken cancellation)
        {
            _html = html;
            _options = options;
            _settings = settings;
            _baseDirectory = baseDirectory;
            _resolver = options.ResourceResolver;
            _cancellation = cancellation;
        }

        public byte[] Run()
        {
            Parse();
            PrepareStyles();
            SetUpPage();
            LoadResources();
            ResolveStyles();
            PrepareBands();
            PreparePageSetups();
            LayOut();
            return Write(BuildSections());
        }

        private void Parse()
        {
            _doc = HtmlParser.Parse(_html);
            if (MayHaveControls(_html)) FormControls.Prepare(_doc, _options.FormFields);
        }

        /// <summary>
        /// Reads the stylesheets in document order (style blocks and link rel="stylesheet") and applies the document's
        /// @page rules to the page. Media queries see the page area, as in Chrome's print preview: first as the options
        /// give it, then, when an @page rule changes the size or margins and a query depends on it, as the rule makes it.
        /// </summary>
        private void PrepareStyles()
        {
            var media = new MediaContext
            {
                Width = _options.PageSize.Width - _options.Margins.Left - _options.Margins.Right,
                Height = _options.PageSize.Height - _options.Margins.Top - _options.Margins.Bottom,
            };
            _sheet = ReadStyles(_doc, media, _options, _baseDirectory, _resolver);

            _page = PageOfOptions(_options);
            if (!_options.IgnoreCssPageRules) ApplyPageRules(_sheet.PageDeclarations, _page);
            if (media.UsedSize && (Math.Abs(media.Width - _page.ContentWidth) > 0.01f || Math.Abs(media.Height - _page.ContentHeight) > 0.01f))
                _sheet = ReadStyles(_doc, new MediaContext { Width = _page.ContentWidth, Height = _page.ContentHeight }, _options, _baseDirectory, _resolver);
        }

        /// <summary>
        /// Scales and fits the page, and estimates the viewport from the height attributes of the running header and
        /// footer (e.g. height="17mm"), so that vh units can usually be resolved in one pass.
        /// </summary>
        private void SetUpPage()
        {
            ScaleAndFit(_page, _options.Scale);

            // Most documents have no bands: one search of the source spares walking the whole tree four times.
            bool mayHaveBands = _html.Contains("data-sticky-pdf-", StringComparison.OrdinalIgnoreCase);
            var headerCandidate = mayHaveBands ? FindByAttribute(_doc, "data-sticky-pdf-header") : null;
            var footerCandidate = mayHaveBands ? FindByAttribute(_doc, "data-sticky-pdf-footer") : null;
            bool truncateBands = _options.TruncateBandsToWholePoints;
            float declaredTop = DeclaredHeight(headerCandidate, _page, truncateBands), declaredBottom = DeclaredHeight(footerCandidate, _page, truncateBands);
            _viewport = Math.Max(10, _page.ContentHeight - declaredTop - declaredBottom);
        }

        /// <summary>Sets up the loaders of images and fonts, and what the style passes need besides the stylesheet.</summary>
        private void LoadResources()
        {
            _images = new ImageLoader(_baseDirectory, _resolver, _options.AllowFileAccess);
            _missingFonts = _options.MissingFont is { } onMissing ? new MissingFontReport(onMissing) : null;
            _fonts = FontResolver.Create(_sheet.FontFaces, _options.Fonts, FetchFont(_images),
                embedStandard: _options.EmbedStandardFonts || _settings.Conformance != PdfConformance.None || _settings.IsPdfUA,
                systemFonts: _options.UseSystemFonts, report: _missingFonts);
            _images.Fonts = _fonts;
            _paged = new PagedContent();
            if (!_options.IgnoreCssPageRules) _paged.FootnotePageReset = FootnotePageReset(_sheet.PageRules);
            // hyphens: auto: the language of the text is its nearest lang attribute, else PdfOptions.Language.
            _hyphenation = HyphenationTable(_options.Hyphenation);
            _htmlLang = _doc.Children.OfType<Element>().FirstOrDefault(c => c.Name == "html") is { } htmlElement
                ? htmlElement.GetAttribute("lang") ?? htmlElement.GetAttribute("xml:lang") : null;
        }

        /// <summary>A style pass over the whole document, at the current page width and viewport.</summary>
        private void ResolveStyles()
        {
            _root = InitialStyle(_options.Language);
            var styles = new StyleResolver(_sheet, _page.ContentWidth, _viewport) { Cancellation = _cancellation, Paged = _paged };
            styles.ResolveTree(_doc, _root);
            _styles = styles;
        }

        /// <summary>The style that boxes inherit from when nothing in the document sets a property.</summary>
        private ComputedStyle InitialStyle(string? lang)
            => new() { Display = Display.Block, Fonts = _fonts, MissingFonts = _missingFonts, Lang = lang, Hyphenation = _hyphenation };

        /// <summary>
        /// Takes the running header and footer out of the flow and reserves their space on the page: they are drawn at
        /// the top and bottom of every page, and the flowing content gets the space in between. When that space differs
        /// from the estimate and vh units were used, the styles are resolved again with the right viewport.
        /// </summary>
        private void PrepareBands()
        {
            _header = FindBand(_doc, "data-sticky-pdf-header");
            _footer = FindBand(_doc, "data-sticky-pdf-footer");
            if (_footer == _header) _footer = null;
            if (_header != null) _header.Detached = true;
            if (_footer != null) _footer.Detached = true;
            ReserveBands(_page);

            if (_styles.UsedViewportHeight && Math.Abs(_page.FlowHeight - _viewport) > 0.01f)
            {
                _viewport = _page.FlowHeight;
                ResolveStyles();
            }
        }

        /// <summary>Reserves the heights of the running header and footer on a page, together at most 60% of its content area.</summary>
        private void ReserveBands(PageSetup setup)
        {
            if (_header == null && _footer == null) return;
            bool truncate = _options.TruncateBandsToWholePoints;
            if (_header != null) setup.BandTop = Math.Max(DeclaredHeight(_header, setup, truncate), BandHeight(_header, setup, _images, _cancellation));
            if (_footer != null) setup.BandBottom = Math.Max(DeclaredHeight(_footer, setup, truncate), BandHeight(_footer, setup, _images, _cancellation));
            float limit = setup.ContentHeight * 0.6f;
            float bands = setup.BandTop + setup.BandBottom;
            if (bands > limit)
            {
                setup.BandTop *= limit / bands;
                setup.BandBottom *= limit / bands;
            }
        }

        /// <summary>PdfOptions.Hyphenation with its language tags in lower case with hyphens, as the layout looks them up.</summary>
        private static Dictionary<string, HyphenationPatterns>? HyphenationTable(IDictionary<string, HyphenationPatterns>? patterns)
        {
            if (patterns == null || patterns.Count == 0) return null;
            var table = new Dictionary<string, HyphenationPatterns>(StringComparer.Ordinal);
            foreach (var (lang, p) in patterns)
                if (!string.IsNullOrWhiteSpace(lang) && p != null) table[lang.Trim().Replace('_', '-').ToLowerInvariant()] = p;
            return table;
        }

        /// <summary>Fetches the file of an @font-face rule; a font that cannot be read is left to the fallback fonts.</summary>
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

        /// <summary>Whether the source can hold form controls: most documents have none, which spares a walk of the tree.</summary>
        private static bool MayHaveControls(string html)
            => html.Contains("<input", StringComparison.OrdinalIgnoreCase) || html.Contains("<select", StringComparison.OrdinalIgnoreCase)
               || html.Contains("<textarea", StringComparison.OrdinalIgnoreCase) || html.Contains("<button", StringComparison.OrdinalIgnoreCase)
               || html.Contains("<progress", StringComparison.OrdinalIgnoreCase) || html.Contains("<meter", StringComparison.OrdinalIgnoreCase);
    }
}
