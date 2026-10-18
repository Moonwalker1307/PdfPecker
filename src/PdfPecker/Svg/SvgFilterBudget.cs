namespace PdfPecker.Svg;

/// <summary>
/// The work that the filters of one document have spent, shared by the writer of the document and the writers that
/// render filtered content for it, so that filters inside filters and feImage count too.
/// </summary>
internal sealed class SvgFilterBudget
{
    private long _spent;

    /// <summary>
    /// Whether a filter may run: while the document's filters have spent less than <see cref="SvgFilter.MaxDocumentWork"/>.
    /// Its work is counted when it may. The last one that runs may go beyond the budget, so that the first filter of a
    /// document runs whenever it is within its own budget.
    /// </summary>
    public bool TrySpend(long work)
    {
        if (_spent >= SvgFilter.MaxDocumentWork) return false;
        _spent += work;
        return true;
    }
}
