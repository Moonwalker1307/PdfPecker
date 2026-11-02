using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    private sealed partial class Conversion
    {
        /// <summary>
        /// Writes the laid-out sections as a PDF file. Signing is left to the caller, so that the asynchronous API can
        /// await the timestamp server instead of holding a thread pool thread while it answers.
        /// </summary>
        private byte[] Write(List<PageSection> sections)
        {
            var title = DocumentTitle(_options, _doc, (_namesUsed & NamesUsed.Title) != 0, _structure, _settings.IsPdfUA);
            var (marginFont, watermarkFont) = ChooseTextFonts();
            var writer = CreateWriter(marginFont, watermarkFont);
            var marginBoxes = CreateMarginBoxes();
            var pageBands = AnyBands || marginBoxes != null ? PageBands(writer, marginBoxes) : null;
            return writer.Write(sections, _page.Background, title, _options.Author, _options.Subject,
                _options.FooterText, _options.HeaderText, pageBands, _headings);
        }

        /// <summary>
        /// The document title: PdfOptions.Title, else the HTML title with its whitespace collapsed. PDF/UA needs one, and
        /// takes the first heading when neither gives it.
        /// </summary>
        private static string? DocumentTitle(PdfOptions options, Element doc, bool mayHaveTitle, StructureTree? structure, bool pdfua)
        {
            string? title = options.Title;
            if (title == null && mayHaveTitle)
            {
                var t = doc.FindFirst("title");
                if (t != null) title = LayoutEngine.CollapseWhitespace(t.InnerText());
            }
            if (pdfua && string.IsNullOrEmpty(title))
            {
                title = structure!.FirstHeading
                        ?? throw new ArgumentException("PDF/UA needs a document title: set PdfOptions.Title or give the HTML a <title>.", nameof(options));
            }
            return title;
        }

        /// <summary>
        /// The fonts of the header/footer text and of the watermark: the body's font family when it names an embedded
        /// font, else Helvetica.
        /// </summary>
        private (TextFont? Margin, TextFont? Watermark) ChooseTextFonts()
        {
            TextFont? marginFont = null, watermarkFont = null;
            if (_fonts != null)
            {
                var bodyFamily = (_body?.Style ?? _root).FontFamilyName;
                if (_options.FooterText != null || _options.HeaderText != null)
                    marginFont = _fonts.Resolve(bodyFamily, FontFamily.Helvetica, 400, false);
                if (_options.Watermark is { Text.Length: > 0 } w)
                    watermarkFont = _fonts.Resolve(w.FontFamily ?? bodyFamily, FontFamily.Helvetica, w.Bold ? 700 : 400, false);
            }
            else if (_options.Watermark is { FontFamily: { } family } w)
            {
                _missingFonts?.CheckStandardOnly(family);
                if (FontResolver.FirstStandardFamily(family) is FontFamily sf) watermarkFont = StandardFont.Get(sf, w.Bold, false);
            }
            return (marginFont, watermarkFont);
        }

        private PdfDocumentWriter CreateWriter(TextFont? marginFont, TextFont? watermarkFont) => new(_page, _options.Compress, _options.CompressionLevel, _context)
        {
            Watermark = _options.Watermark,
            RightToLeft = _rtlPages,
            Viewer = _options.Viewer,
            PageLabels = _options.PageLabels,
            Security = _options.Security,
            Linearize = _options.Linearize,
            StripImageMetadata = _options.StripImageMetadata,
            Conformance = _settings.Conformance,
            EInvoice = _options.EInvoice,
            Attachments = _settings.Attachments,
            WatermarkFont = watermarkFont,
            WatermarkSvg = _options.Watermark?.Image is { } wmData && Svg.SvgImage.IsSvg(wmData) ? _images.ParseSvg(wmData) : null,
            MarginFont = marginFont,
            EmbedStandardFonts = _options.EmbedStandardFonts,
            MissingCharacter = _options.MissingCharacter,
            FixedDate = ReproducibleDate.Resolve(_options.CreationDate),
            MaxPages = _options.MaxPages,
            Structure = _structure,
            PdfUAPart = _settings.PdfUAPart,
            Language = _structure == null ? null : _options.Language ?? _structure.Language ?? (_settings.IsPdfUA ? "en" : null),
            Properties = new DocumentProperties
            {
                Keywords = _options.Keywords,
                Creator = _options.Creator,
                Producer = _options.Producer,
                Custom = DocumentProperties.Validate(_options.CustomProperties),
            },
        };

        /// <summary>The page-margin boxes of the @page rules (@top-center and the like), or null when there are none.</summary>
        private PageMarginBoxes? CreateMarginBoxes()
            => !_options.IgnoreCssPageRules && PageMarginBoxes.AnyIn(_sheet.PageRules)
                ? new PageMarginBoxes(_sheet.PageRules, () => InitialStyle(_htmlLang ?? _options.Language),
                    _page, _images, _context, _paged, _styles.CounterStyles) { FirstPageLeft = _rtlPages }
                : null;

        /// <summary>Draws the margin boxes and the running header and footer of each page, laid out per page when they number it.</summary>
        private Func<int, int, List<DrawOp>> PageBands(PdfDocumentWriter writer, PageMarginBoxes? marginBoxes)
        {
            var strings = _styles.Strings;
            // The bands without page numbers, laid out once per page setup and drawn again on every page.
            var unnumbered = new Dictionary<(Element, PageSetup), List<DrawOp>>();
            Dictionary<string, int>? anchorPages = null;
            int? FirstPageOf(string id) => (anchorPages ??= PagesOfAnchors(_runs, _paged.SelfKeys)).TryGetValue(id, out int n) ? n : null;
            return (p, count) =>
            {
                var (section, local) = writer.SectionOfPage(p);
                var run = _runs[section];
                var setup = run.Grid.SetupOf(local);
                var ops = new List<DrawOp>();
                if (marginBoxes != null)
                {
                    // Running strings (string-set) for string() are known once the writer has placed the pages.
                    if (_paged.Running == null && strings.Count > 0) _paged.Running = PlaceRunningStrings(writer, strings);
                    if (_paged.RunningElements == null && _styles.Runnings.Count > 0) _paged.RunningElements = PlaceRunningElements(writer, _styles.Runnings);
                    ops.AddRange(marginBoxes.Layout(p, count, setup, run.Name));
                }
                var (header, footer) = BandsOf(run.Name, p == 0, local == 0, p % 2 == 1);
                if (header != null) ops.AddRange(LayoutBandForPage(header, setup, _images, p, count, alignBottom: false, unnumbered, FirstPageOf, _context));
                if (footer != null) ops.AddRange(LayoutBandForPage(footer, setup, _images, p, count, alignBottom: true, unnumbered, FirstPageOf, _context));
                return ops;
            };
        }

        /// <summary>The running strings of string-set, on the pages their elements landed on.</summary>
        private RunningValues<string> PlaceRunningStrings(PdfDocumentWriter writer, List<StringAssignment> strings)
            => new(strings.Select(a => (a.Name, PageOfKey(writer, a.Key), a.Value)).Where(a => a.Item2 != null)
                .Select(a => (a.Name, a.Item2!.Value.Page, a.Item2.Value.Top, a.Value)));

        /// <summary>
        /// The running elements, on the pages they were taken from. One whose place the layout did not record (inside a
        /// flex, grid or table layout) counts from the first page.
        /// </summary>
        private RunningValues<Element> PlaceRunningElements(PdfDocumentWriter writer, List<RunningAssignment> runnings)
            => new(runnings.Select(a => (a.Name, PageOfKey(writer, a.Key) ?? (0, 0f), a.Element))
                .Select(a => (a.Name, a.Item2.Page, a.Item2.Top, a.Element)));

        /// <summary>The page and the position on it of a layout key's anchor, or null when the layout recorded none.</summary>
        private (int Page, float Top)? PageOfKey(PdfDocumentWriter writer, string key)
        {
            for (int s = 0; s < _runs.Count; s++)
                if (_runs[s].Engine.Anchors.TryGetValue(key, out float y))
                    return writer.PageAt(s, y);
            return null;
        }
    }
}
