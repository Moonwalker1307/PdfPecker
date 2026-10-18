namespace PdfPecker.Pdf;

/// <summary>
/// The colour function pieces that the tiled repeating gradients of one document have written, shared by the writer of
/// the document and the writers that render filtered content for it. A repeating gradient is written as one copy of its
/// stops per period, so without a document-wide limit a few bytes of markup per box, repeated over thousands of boxes,
/// would write gigabytes.
/// </summary>
internal sealed class GradientTilingBudget
{
    /// <summary>
    /// The most pieces a document's tiled gradients write, about 30 MB of PDF; past that, repeating gradients are painted
    /// once, padded, as one that would need more than its own limit of periods already is.
    /// </summary>
    internal const long MaxDocumentPieces = 250_000;

    private long _spent;

    /// <summary>Whether a gradient may be tiled with <paramref name="pieces"/> function pieces; they are counted when it may.</summary>
    public bool TrySpend(long pieces)
    {
        if (_spent + pieces > MaxDocumentPieces) return false;
        _spent += pieces;
        return true;
    }
}
