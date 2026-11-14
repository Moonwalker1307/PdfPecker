using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    private sealed partial class Conversion
    {
        // The documents of the iframes, by element and viewport: a frame is laid out again in every layout pass.
        private Dictionary<(Element, float, float), FrameContent?>? _frameDocuments;

        /// <summary>
        /// The document of an iframe's srcdoc for a viewport of <paramref name="w"/> by <paramref name="h"/> points,
        /// parsed and styled on its own, as a browser shows a nested document: the styles around the iframe do not
        /// reach into it, and its own style sheets stay inside. A frame's src is a page to fetch, which conversion never
        /// does, so such a frame stays empty, as a frame that could not load is.
        /// </summary>
        private FrameContent? FrameDocument(Element frame, float w, float h)
        {
            if (frame.GetAttribute("srcdoc") is not { } html) return null;
            _frameDocuments ??= new();
            if (_frameDocuments.TryGetValue((frame, w, h), out var known)) return known;
            var doc = HtmlParser.Parse(html, out var used);
            if ((used & NamesUsed.Math) != 0) MathElements.Prepare(doc);
            // A frame's body keeps its margin when the srcdoc leaves its tags out, as every browser document's does; only
            // the outer document, a template, lays out a fragment without one.
            if (doc.FindFirst("body") is { } implied) implied.ImpliedBody = false;
            var media = new MediaContext { Width = w, Height = h };
            var sheet = ReadStyles(doc, media, _options, _baseDirectory, _resolver);
            if ((used & NamesUsed.Img) != 0)
            {
                ResponsiveImages.Prepare(doc, _images, new MediaContext { Width = w, Height = h, Screen = true }, w / 100, h / 100);
                MissingImageText.Prepare(doc, _images);
            }
            if ((used & NamesUsed.Embed) != 0) EmbeddedPictures.Prepare(doc, _images);
            // A srcdoc document is never in quirks mode (HTML §7.1.3), doctype or not.
            var root = new ComputedStyle
            {
                Display = Display.Block, Fonts = _fonts, MissingFonts = _missingFonts, Lang = _options.Language, Hyphenation = _hyphenation,
            };
            new StyleResolver(sheet, w, h) { Context = _context, StandardsMode = true }.ResolveTree(doc, root);
            // The frame's root background, or else its body's, fills the frame's viewport, as the page's does a page.
            var rootElement = doc.FindFirst("html");
            var bodyElement = doc.FindFirst("body");
            var canvas = HasBackground(rootElement) ? rootElement!.Style : HasBackground(bodyElement) ? bodyElement!.Style : null;
            if (canvas != null) canvas.CanvasBackground = true;
            var content = new FrameContent(doc, canvas, rootElement?.Style ?? canvas);
            _frameDocuments[(frame, w, h)] = content;
            return content;
        }
    }
}
