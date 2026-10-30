using PdfPecker.Layout;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker;

/// <summary>
/// What the steps of one HTML-to-PDF conversion share besides the document itself: the caller's cancellation token and
/// the limits on the work the whole document may cause. The conversion creates it once and hands it to the steps that
/// create or spend those limits, so that this is the one place that lists every limit of an HTML conversion.
/// </summary>
/// <remarks>
/// <para>
/// Each limit keeps its own unit and its own answer when it runs out, as it had before it moved here: some leave out
/// the rest of the work (a drawing, a filter, a tiled gradient), one stops placing page breaks better, and one stops
/// the document with a <see cref="PdfLimitException"/>. The limits held here are:
/// </para>
/// <list type="bullet">
/// <item><see cref="SharedRelayouts"/>, the base of the relayout budget, which the layouts of every run of named
/// pages, of the page numbers of target-counter() and of shrinking to fit share; each layout adds a share per element
/// of its own (see <see cref="LayoutEngine.BaseRelayouts"/>).</item>
/// <item><see cref="SvgDrawing"/>, the elements and path points of every inline SVG and SVG image of the
/// document.</item>
/// <item><see cref="SvgFilters"/>, the work of the document's SVG and CSS filters and the boxes that filter their
/// backdrop, which the writers that render filtered content for the document's writer spend too.</item>
/// <item><see cref="GradientTiling"/>, the colour function pieces of tiled repeating gradients, shared in the same
/// way.</item>
/// <item><see cref="NumberedBands"/>, the work of laying out a header or footer with page numbers again for each
/// page.</item>
/// </list>
/// <para>
/// Other limits apply to a part of the conversion rather than to all of it, and stay with that part: the generated
/// content of a style pass (<see cref="Css.GeneratedContent.MaxDocumentLength"/>, which each pass gets in full because
/// each evaluates the same content again), the backtracking patterns of :valid and :invalid that may time out in a
/// style pass (counted in a thread static of <see cref="Css.FormValidity"/>, which selector matching reaches through
/// static methods), the imports of one reading of the style sheets, the colour glyph paints of one writer, the drawing
/// of a clip path or mask of an HTML box, and the steps of one OpenType shaping call. <see cref="PdfOptions.MaxPages"/>
/// is the caller's limit and is read from the options.
/// </para>
/// </remarks>
internal sealed class ConversionContext
{
    public ConversionContext(CancellationToken cancellation) => Cancellation = cancellation;

    /// <summary>The caller's token, which stops the conversion when it is cancelled.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>
    /// What the layouts so far have left of the base of the relayout budget: each layout of the document starts from it
    /// and takes what it used past its share per element.
    /// </summary>
    public long SharedRelayouts { get; set; } = LayoutEngine.BaseRelayouts;

    /// <summary>The drawing work left for the document's inline SVGs and SVG images.</summary>
    public SvgBudget SvgDrawing { get; } = new();

    /// <summary>The work the document's filters have spent, and the boxes that have filtered their backdrop.</summary>
    public SvgFilterBudget SvgFilters { get; } = new();

    /// <summary>The colour function pieces the document's tiled repeating gradients have written.</summary>
    public GradientTilingBudget GradientTiling { get; } = new();

    /// <summary>The work that headers and footers with page numbers have taken, laid out again for each page.</summary>
    public HtmlToPdf.NumberedBandBudget NumberedBands { get; } = new();
}
