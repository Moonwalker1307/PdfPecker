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
        private NamesUsed _namesUsed;
        private Stylesheet _sheet = null!;
        /// <summary>The document's page: the options' size and margins with the @page rules applied, scaled and fitted.</summary>
        private PageSetup _page = null!;
        /// <summary>The viewport height that vh units refer to: the space left for the flowing content.</summary>
        private float _viewport;
        /// <summary>The scale the pages are laid out at: the options' scale, times the shrink of a page that is too wide.</summary>
        private float _scale;

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

        /// <summary>
        /// The running header and footer (data-sticky-pdf-header / data-sticky-pdf-footer), out of the flow, and the
        /// first page's own (data-sticky-pdf-first-header / data-sticky-pdf-first-footer).
        /// </summary>
        private BandSet _bands = new();

        /// <summary>
        /// The bands of named pages (data-sticky-pdf-page="name" on the band), which take the place of the document's
        /// bands on the pages of that name; null when there are none.
        /// </summary>
        private Dictionary<string, BandSet>? _namedBands;

        /// <summary>Whether some page has a header or footer.</summary>
        private bool AnyBands => _bands.Any || _namedBands != null;

        /// <summary>Whether the source mentions a band attribute at all; when it does not, the tree is not searched for one.</summary>
        private bool _mayHaveBands;

        public Conversion(string html, PdfOptions options, ConversionSettings settings, string? baseDirectory, CancellationToken cancellation)
        {
            _html = html;
            _options = options;
            _settings = settings;
            _baseDirectory = baseDirectory;
            _resolver = options.ResourceResolver;
            _cancellation = cancellation;
            _scale = options.Scale;
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
            ShrinkToFit();
            return Write(BuildSections());
        }

        private void Parse()
        {
            _doc = HtmlParser.Parse(_html, out _namesUsed);
            // Most documents have no form controls and no images, and the parser tells which, sparing walks of the tree.
            if ((_namesUsed & NamesUsed.FormControls) != 0) FormControls.Prepare(_doc, _options.FormFields);
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

            _page = PageOfDocument();
            if (media.UsedSize && (Math.Abs(media.Width - _page.ContentWidth) > 0.01f || Math.Abs(media.Height - _page.ContentHeight) > 0.01f))
                _sheet = ReadStyles(_doc, new MediaContext { Width = _page.ContentWidth, Height = _page.ContentHeight }, _options, _baseDirectory, _resolver);
        }

        /// <summary>The page of the options with the document's @page rules applied, before it is scaled.</summary>
        private PageSetup PageOfDocument()
        {
            var page = PageOfOptions(_options);
            if (!_options.IgnoreCssPageRules) ApplyPageRules(_sheet.PageDeclarations, page);
            return page;
        }

        /// <summary>
        /// Scales and fits the page, and estimates the viewport from the height attributes of the running header and
        /// footer (e.g. height="17mm"), so that vh units can usually be resolved in one pass.
        /// </summary>
        private void SetUpPage()
        {
            ScaleAndFit(_page, _scale, _options.Scale != 1);

            // Most documents have no bands: one search of the source spares walking the whole tree four times.
            _mayHaveBands = _html.Contains("data-sticky-pdf-", StringComparison.OrdinalIgnoreCase);
            var headerCandidate = _mayHaveBands ? FindByAttribute(_doc, "data-sticky-pdf-header") : null;
            var footerCandidate = _mayHaveBands ? FindByAttribute(_doc, "data-sticky-pdf-footer") : null;
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
                systemFonts: _options.UseSystemFonts, report: _missingFonts, needsFallback: NeedsFallbackFonts);
            _images.Fonts = _fonts;
            if ((_namesUsed & NamesUsed.Img) != 0)
            {
                ResponsiveImages.Prepare(_doc, _images, new MediaContext { Width = _page.ContentWidth, Height = _page.ContentHeight, Screen = true },
                    _page.ContentWidth / 100, _viewport / 100);
                MissingImageText.Prepare(_doc, _images);
            }
            if ((_namesUsed & NamesUsed.Embed) != 0) EmbeddedPictures.Prepare(_doc, _images);
            _paged = new PagedContent();
            if (!_options.IgnoreCssPageRules) _paged.FootnotePageReset = FootnotePageReset(_sheet.PageRules);
            // hyphens: auto: the language of the text is its nearest lang attribute, else PdfOptions.Language.
            _hyphenation = HyphenationTable(_options.Hyphenation);
            _htmlLang = _doc.Children.OfType<Element>().FirstOrDefault(c => c.Name == "html") is { } htmlElement
                ? htmlElement.GetAttribute("lang") ?? htmlElement.GetAttribute("xml:lang") : null;
        }

        /// <summary>
        /// Whether some text needs the bundled fallback fonts: the document's text and attributes, generated content in
        /// the style sheets, and the header, footer and watermark text. Other documents keep the standard fonts alone.
        /// </summary>
        private bool NeedsFallbackFonts()
        {
            if (BundledFonts.BeyondStandard(_options.HeaderText) || BundledFonts.BeyondStandard(_options.FooterText)
                || BundledFonts.BeyondStandard(_options.Watermark?.Text))
                return true;
            var stack = new Stack<Node>();
            stack.Push(_doc);
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case TextNode t when BundledFonts.BeyondStandard(t.Text):
                        return true;
                    case Element e:
                        foreach (var a in e.Attributes)
                            if (BundledFonts.BeyondStandard(a.Value) || (a.Key == "style" && (CssEscapesBeyondStandard(a.Value)
                                || a.Value.Contains("emphasis", StringComparison.OrdinalIgnoreCase)))) return true;
                        foreach (var child in e.Children) stack.Push(child);
                        break;
                }
            }
            foreach (var rule in _sheet.Rules)
                foreach (var d in rule.Declarations)
                    if (BundledFonts.BeyondStandard(d.Value) || CssEscapesBeyondStandard(d.Value) || DrawsEmphasisMarks(d.Property, d.Value)) return true;
            foreach (var page in _sheet.PageRules)
                foreach (var box in page.MarginBoxes.Values)
                    foreach (var d in box)
                        if (BundledFonts.BeyondStandard(d.Value) || CssEscapesBeyondStandard(d.Value)) return true;
            return false;
        }

        // A CSS escape such as \2713 in a string, which the text of the style sheet does not show as the character.
        /// <summary>Whether a declaration draws emphasis marks, most of which are symbols beyond the standard fonts.</summary>
        private static bool DrawsEmphasisMarks(string property, string value)
            => property.Contains("emphasis", StringComparison.Ordinal) && !value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

        private static bool CssEscapesBeyondStandard(string css)
        {
            for (int i = css.IndexOf('\\'); i >= 0 && i + 1 < css.Length; i = css.IndexOf('\\', i + 1))
            {
                int end = i + 1, code = 0;
                for (; end < css.Length && end - i <= 6 && char.IsAsciiHexDigit(css[end]); end++)
                    code = code * 16 + (css[end] <= '9' ? css[end] - '0' : (css[end] | 0x20) - 'a' + 10);
                if (end > i + 1 && code is > 0x7F and <= 0xFFFF && BundledFonts.BeyondStandard((char)code)) return true;
            }
            return false;
        }

        /// <summary>A style pass over the whole document, at the current page width and viewport.</summary>
        private void ResolveStyles()
        {
            _root = InitialStyle(_options.Language);
            var styles = new StyleResolver(_sheet, _page.ContentWidth, _viewport) { Cancellation = _cancellation, Paged = _paged, StandardsMode = (_namesUsed & NamesUsed.StandardsMode) != 0 };
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
            _bands = new BandSet();
            _namedBands = null;
            if (_mayHaveBands) CollectBands(_doc);
            _bands.DropFootersThatAreHeaders();
            if (_namedBands != null)
                foreach (var set in _namedBands.Values) set.DropFootersThatAreHeaders();
            ReserveBands(_page, null, false, false);

            if (_styles.UsedViewportHeight && Math.Abs(_page.FlowHeight - _viewport) > 0.01f)
            {
                _viewport = _page.FlowHeight;
                ResolveStyles();
            }
        }

        /// <summary>
        /// Finds the bands: of each kind, the first displayed element with its attribute, for the document and for each
        /// named page. An element that is both a header and a footer is the header, and an element that is not a band in
        /// use is left in the flow.
        /// </summary>
        private void CollectBands(Element parent)
        {
            foreach (var child in parent.Children)
            {
                if (child is not Element e || e.Style == null || e.Style.Display == Display.None) continue;
                if (e.HasAttribute("data-sticky-pdf-header") || e.HasAttribute("data-sticky-pdf-footer")
                    || e.HasAttribute("data-sticky-pdf-first-header") || e.HasAttribute("data-sticky-pdf-first-footer"))
                {
                    var set = _bands;
                    if (e.GetAttribute("data-sticky-pdf-page")?.Trim() is { Length: > 0 } name)
                    {
                        _namedBands ??= new Dictionary<string, BandSet>(StringComparer.Ordinal);
                        if (!_namedBands.TryGetValue(name, out set)) _namedBands[name] = set = new BandSet();
                    }
                    if (e.HasAttribute("data-sticky-pdf-header")) set.Header ??= e;
                    if (e.HasAttribute("data-sticky-pdf-footer")) set.Footer ??= e;
                    if (e.HasAttribute("data-sticky-pdf-first-header")) set.FirstHeader ??= e;
                    if (e.HasAttribute("data-sticky-pdf-first-footer")) set.FirstFooter ??= e;
                    if (set.Header == e || set.Footer == e || set.FirstHeader == e || set.FirstFooter == e) e.Detached = true;
                }
                CollectBands(e);
            }
        }

        /// <summary>
        /// Reserves the heights of the header and footer on a page, together at most 60% of its content area: the bands
        /// that <see cref="BandsOf"/> gives the page.
        /// </summary>
        private void ReserveBands(PageSetup setup, string? pageName, bool documentFirst, bool runFirst)
        {
            var (header, footer) = BandsOf(pageName, documentFirst, runFirst);
            if (header == null && footer == null) return;
            bool truncate = _options.TruncateBandsToWholePoints;
            if (header != null) setup.BandTop = Math.Max(DeclaredHeight(header, setup, truncate), BandHeight(header, setup, _images, _cancellation));
            if (footer != null) setup.BandBottom = Math.Max(DeclaredHeight(footer, setup, truncate), BandHeight(footer, setup, _images, _cancellation));
            float limit = setup.ContentHeight * 0.6f;
            float bands = setup.BandTop + setup.BandBottom;
            if (bands > limit)
            {
                setup.BandTop *= limit / bands;
                setup.BandBottom *= limit / bands;
            }
        }

        /// <summary>
        /// The header and footer of a page. A named page's own header or footer takes the place of the document's, and
        /// on the first page of each run of that named page, its first-page bands take the place of both. Otherwise the
        /// document's first-page bands take the place of the running ones on the first page of the document.
        /// </summary>
        private (Element? Header, Element? Footer) BandsOf(string? pageName, bool documentFirst, bool runFirst)
        {
            BandSet? named = null;
            if (pageName != null) _namedBands?.TryGetValue(pageName, out named);
            var header = named?.Header ?? _bands.Header;
            var footer = named?.Footer ?? _bands.Footer;
            if (runFirst && named is { HasFirst: true }) return (named.FirstHeader ?? header, named.FirstFooter ?? footer);
            if (documentFirst && _bands.HasFirst) return (_bands.FirstHeader ?? header, _bands.FirstFooter ?? footer);
            return (header, footer);
        }

        /// <summary>
        /// The header and footer of the pages of the document or of one named page. An empty first-page band leaves the
        /// first page without that band.
        /// </summary>
        private sealed class BandSet
        {
            public Element? Header, Footer, FirstHeader, FirstFooter;

            public bool HasFirst => FirstHeader != null || FirstFooter != null;

            public bool Any => Header != null || Footer != null || HasFirst;

            /// <summary>An element that is both the header and the footer is the header only.</summary>
            public void DropFootersThatAreHeaders()
            {
                if (Footer == Header) Footer = null;
                if (FirstFooter == FirstHeader) FirstFooter = null;
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
    }
}
