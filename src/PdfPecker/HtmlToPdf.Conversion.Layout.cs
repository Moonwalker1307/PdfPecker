using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    private sealed partial class Conversion
    {
        /// <summary>
        /// The most style passes and layouts that container queries may add: nested containers settle one level per
        /// pass, and the limit stops content that flips a query back and forth.
        /// </summary>
        private const int MaxContainerPasses = 6;

        /// <summary>How many times wider than the page content may be laid out to shrink it to fit, as in Chrome.</summary>
        private const float MaxShrink = 1.5f;

        private const float PointsPerPixel = 0.75f;

        private Element? _htmlElement, _body;
        private List<OutlineHeading>? _headings;
        /// <summary>Whether @page :first, :left or :right rules can give pages a setup of their own.</summary>
        private bool _pageVariants;
        /// <summary>Whether the root element is right to left, which makes the first page a left page.</summary>
        private bool _rtlPages;
        private readonly Dictionary<string, PageSetup> _namedSetups = new(StringComparer.Ordinal);
        private readonly Dictionary<(string?, bool, bool), PageSetup> _variants = new();
        private readonly Dictionary<string, ComputedStyle> _footnoteAreas = new(StringComparer.Ordinal);

        /// <summary>The canvas background's style, the root element's or else the body's, or null for none.</summary>
        private ComputedStyle? _canvasStyle;
        private StructureTree? _structure;
        private List<PageRun> _runs = null!;
        private Dictionary<string, int>? _targetPages;
        private Dictionary<int, int>? _footnotePages;

        private void PreparePageSetups()
        {
            _htmlElement = _doc.FindFirst("html");
            _body = _doc.FindFirst("body");
            _headings = _options.Bookmarks ? new List<OutlineHeading>() : null;
            _pageVariants = !_options.IgnoreCssPageRules
                            && _sheet.PageRules.Any(r => r.Selector is ":first" or ":left" or ":right" && r.Declarations.Count > 0);
            _rtlPages = _htmlElement?.Style is { Rtl: true };
        }

        /// <summary>
        /// Named pages (page: wide with @page wide { ... }): each run of content on one named page gets that page's
        /// setup, or the document's when no rule names it.
        /// </summary>
        private PageSetup SetupFor(string? name)
        {
            if (name == null || _options.IgnoreCssPageRules || !_sheet.PageRules.Any(r => r.Name == name && r.Selector.Length == 0)) return _page;
            if (_namedSetups.TryGetValue(name, out var cached)) return cached;
            var setup = PageOfOptions(_options);
            ApplyPageRules(_sheet.PageDeclarations, setup);
            foreach (var rule in _sheet.PageRules)
                if (rule.Name == name && rule.Selector.Length == 0) ApplyPageRules(rule.Declarations, setup);
            ScaleAndFit(setup, _scale, _options.Scale != 1);
            ReserveBands(setup);
            return _namedSetups[name] = setup;
        }

        /// <summary>
        /// The setup of a document page: @page :first, :left and :right rules that change the size, margins or
        /// background give those pages their own. The first page of the document is a right page, as in left-to-right
        /// books, and a left page when the root element is right to left, as in Chrome: the pages then progress from
        /// right to left (CSS Paged Media 3, 4.2). A first page with a header or footer of its own also gets its own
        /// setup, since the bands it reserves differ.
        /// </summary>
        private PageSetup VariantFor(string? name, int documentPage)
        {
            var plain = SetupFor(name);
            bool first = documentPage == 0, firstBands = first && FirstPageBands;
            if (!_pageVariants && !firstBands) return plain;
            bool right = documentPage % 2 == (_rtlPages ? 1 : 0);
            if (_variants.TryGetValue((name, first, right), out var cached)) return cached;
            var setup = PageOfOptions(_options);
            // The rules that match the page, of lower specificity first and in source order otherwise.
            var side = right ? ":right" : ":left";
            foreach (var rule in _sheet.PageRules
                         .Where(r => (r.Name.Length == 0 || r.Name == name) && (r.Selector.Length == 0 || r.Selector == side || first && r.Selector == ":first"))
                         .OrderBy(r => r.Specificity))
                ApplyPageRules(rule.Declarations, setup);
            if (!firstBands && setup.Width == plain.Width * plain.Scale && setup.Height == plain.Height * plain.Scale
                && setup.MarginTop == plain.MarginTop * plain.Scale && setup.MarginRight == plain.MarginRight * plain.Scale
                && setup.MarginBottom == plain.MarginBottom * plain.Scale && setup.MarginLeft == plain.MarginLeft * plain.Scale
                && setup.Background == plain.Background)
                return _variants[(name, first, right)] = plain;
            ScaleAndFit(setup, _scale, _options.Scale != 1);
            ReserveBands(setup, first);
            return _variants[(name, first, right)] = setup;
        }

        /// <summary>The footnote area of a run's pages: the @footnote rules of the document's @page rules and of its named page.</summary>
        private ComputedStyle FootnoteArea(string? name)
        {
            var key = name ?? "";
            if (_footnoteAreas.TryGetValue(key, out var cached)) return cached;
            var decls = _options.IgnoreCssPageRules ? new List<Declaration>()
                : _sheet.PageRules.Where(r => r.Footnote != null && r.Selector.Length == 0 && (r.Name.Length == 0 || r.Name == name))
                    .OrderBy(r => r.Specificity).SelectMany(r => r.Footnote!).ToList();
            return _footnoteAreas[key] = FootnoteSetup.AreaStyle(decls, InitialStyle(_htmlLang ?? _options.Language), _page.ContentWidth, _page.ContentHeight);
        }

        /// <summary>
        /// Lays the document out, again when generated content shows page numbers (target-counter) or footnotes are
        /// numbered per page: the pages are known after a layout, and a layout with them can move content again, so
        /// there is at most a third. Container queries can add passes of their own.
        /// </summary>
        private void LayOut()
        {
            int containerPasses = 0;
            for (int pass = 0; ; pass++)
            {
                LayOutRuns();
                // Container queries and cq units are evaluated against the sizes of the previous layout (none in the
                // first), so a layout that sizes a container differently is followed by another style pass and layout.
                if (containerPasses < MaxContainerPasses && _styles.ContainerSizesChanged())
                {
                    containerPasses++;
                    ResolveStyles();
                    pass--; // not a pass for the page numbers of target-counter()
                    continue;
                }
                if (pass == 2) break;
                // Both run, so that each records the pages of this layout.
                bool targetsMoved = UpdateTargetPages();
                bool footnotesMoved = UpdateFootnotePages();
                if (!targetsMoved && !footnotesMoved) break;
                ResolveStyles();
            }
        }

        /// <summary>One layout of the whole document, run by run.</summary>
        private void LayOutRuns()
        {
            // The root element's background, or else the body's, is the canvas background: it fills every page area (as
            // in Chrome, not the page margins), positioned against the root element's box, and that element's own box
            // does not paint it again.
            _canvasStyle = HasBackground(_htmlElement) ? _htmlElement!.Style : HasBackground(_body) ? _body!.Style : null;
            if (_canvasStyle != null) _canvasStyle.CanvasBackground = true;

            // The logical structure of a tagged PDF follows the HTML in document order, whatever the layout does.
            _structure = _options.Accessibility != PdfAccessibility.None ? StructureTree.Build(_doc) : null;
            _headings?.Clear();
            _runs = _styles.UsedPageNames ? SplitByPageName(_doc) : new List<PageRun> { new() };
            int documentPage = 0;
            foreach (var run in _runs) documentPage += LayOutRun(run, documentPage);
        }

        /// <summary>Lays out one run, whose first page is the given page of the document, and returns its number of pages.</summary>
        private int LayOutRun(PageRun run, int documentPage)
        {
            run.Page = SetupFor(run.Name);
            run.Grid = new PageGrid(VariantFor(run.Name, documentPage), VariantFor(run.Name, documentPage + 1), VariantFor(run.Name, documentPage + 2));
            // The layout starts at the first page's width; on pages of other widths the content takes their width.
            run.Width = run.Grid.SetupOf(0).ContentWidth;
            var restore = run.Keep is { } keep ? KeepOnly(keep) : null;
            try
            {
                run.Engine = new LayoutEngine(run.Grid, _images)
                {
                    Cancellation = _cancellation, MaxPages = Math.Max(1, _options.MaxPages), FixedAreaHeight = run.Page.ContentHeight,
                    CanvasRoot = _htmlElement, Tags = _structure, Headings = _headings,
                    Footnotes = _styles.Footnotes.Count == 0 ? null : new FootnoteSetup(_styles.Footnotes.ToArray(), FootnoteArea(run.Name)),
                };
                run.Height = run.Engine.Layout(_doc, run.Width);
            }
            finally
            {
                restore?.Invoke();
            }
            return run.Grid.PagesFor(run.Height);
        }

        /// <summary>
        /// Shrinks the pages to fit content that reaches past the right edge of their content area, as Chrome's printing
        /// does: the document is laid out again as much wider as the content reaches, at most 1.5 times the width, and
        /// drawn smaller by as much, margins keeping their size on paper. Like Chrome, it measures the width as a whole
        /// number of CSS pixels and does not measure again after the second layout.
        /// </summary>
        private void ShrinkToFit()
        {
            if (!_options.ShrinkToFit) return;
            float shrink = 1;
            foreach (var run in _runs)
            {
                // Pages of other widths lay their content out at their own width: the widest of them is the room.
                float width = Math.Max(run.Width, Math.Max(run.Grid.SetupOf(1).ContentWidth, run.Grid.SetupOf(2).ContentWidth));
                float room = MathF.Ceiling(width / PointsPerPixel - 0.001f) * PointsPerPixel;
                float reach = ContentReach.Of(run.Engine.Ops);
                if (reach > room + 0.01f) shrink = Math.Min(shrink, room / Math.Min(reach, room * MaxShrink));
            }
            if (shrink == 1) return;

            _scale *= shrink;
            _page = PageOfDocument();
            _namedSetups.Clear();
            _variants.Clear();
            _footnoteAreas.Clear();
            SetUpPage();
            ResolveStyles();
            PrepareBands();
            LayOut();
        }

        /// <summary>Records the pages of the elements that target-counter() refers to; true when they moved.</summary>
        private bool UpdateTargetPages()
        {
            if (!_paged.UsesTargets) return false;
            var pages = PagesOfAnchors(_runs);
            if (_targetPages != null && pages.Count == _targetPages.Count && !pages.Except(_targetPages).Any()) return false;
            _targetPages = pages;
            _paged.PageOfId = id => pages.TryGetValue(id, out int n) ? n : null;
            return true;
        }

        /// <summary>
        /// Records the pages of the footnote calls when footnotes are numbered per page, as their numbers depend on the
        /// pages their calls landed on; true when they moved.
        /// </summary>
        private bool UpdateFootnotePages()
        {
            if (_paged.FootnotePageReset == null || _styles.Footnotes.Count == 0) return false;
            var pages = PagesOfFootnotes(_runs);
            if (_footnotePages != null && pages.Count == _footnotePages.Count && !pages.Except(_footnotePages).Any()) return false;
            _footnotePages = pages;
            _paged.FootnotePageOf = k => pages.TryGetValue(k, out int n) ? n : null;
            return true;
        }

        /// <summary>The laid-out runs as the writer's page sections.</summary>
        private List<PageSection> BuildSections()
        {
            var sections = new List<PageSection>(_runs.Count);
            foreach (var run in _runs)
            {
                var engine = run.Engine;
                var canvas = CanvasOf(run);
                sections.Add(new PageSection
                {
                    Page = run.Page, Grid = run.Grid, Ops = engine.Ops, ContentHeight = run.Height, Anchors = engine.Anchors,
                    FixedOps = engine.FixedOps.Count > 0 ? engine.FixedOps : null, Canvas = canvas,
                    FootnoteOps = engine.FootnoteOps.Count > 0 ? engine.FootnoteOps : null,
                    FootnoteReserve = engine.FootnoteOps.Count > 0 ? engine.FootnoteReserve : null,
                });
            }
            return sections;
        }

        /// <summary>The canvas background of a run's pages, drawn per page, or null when the document has none.</summary>
        private Func<int, List<DrawOp>>? CanvasOf(PageRun run)
        {
            if (_canvasStyle == null) return null;
            var rootStyle = _htmlElement?.Style ?? _canvasStyle;
            var grid = run.Grid;
            // Chrome sizes the positioning area like the root element's border box but puts it at the page area's top
            // left corner, whatever the root element's margins.
            var (_, _, rw, rhBox) = run.Engine.CanvasRootBox ?? (0, 0, run.Width, run.Height);
            var painted = _canvasStyle;
            var images = _images;
            return p =>
            {
                // y = 0 is the top of the page area; flow y = 0 of page p sits BandTop below it.
                var setup = grid.SetupOf(p);
                // html { height: 100% } refers to the page area.
                float rh = rootStyle.Height.IsPercent ? rootStyle.Height.Resolve(setup.ContentHeight) : rhBox;
                var group = new GroupOp();
                BoxPainter.PaintCanvas(group, painted, rootStyle, 0, setup.BandTop - grid.Start(p), rw, rh,
                    0, 0, setup.ContentWidth, setup.ContentHeight, images, setup.ContentWidth);
                return group.Ops;
            };
        }
    }
}
