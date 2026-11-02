using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// -pdfpecker-repeat: each-page, PdfPecker's own property, for QuestPDF's Repeat. A box with it is drawn again at the
/// top of each later page its parent's content box continues on: a label in a row beside a text pages long, a term
/// beside its long definition. The copies are artifacts, as repeated table headers are, and form fields are not
/// copied, since a field has one place.
/// </summary>
/// <remarks>
/// A box is recorded once laid out on pages, with a copy of what it drew; its parent draws the copies when it knows how
/// far down its content box reaches, from the page after the one the box ends on. A box that spans pages itself is
/// not repeated, and neither is one laid out away from the pages, such as a flex item measured before it is placed.
/// </remarks>
internal sealed partial class LayoutEngine
{
    private sealed record RepeatedBox(Element Box, Element Parent, float Top, float Bottom, List<DrawOp> Ops);

    // The boxes to repeat whose parents have not drawn their copies yet, in the order they were laid out.
    private List<RepeatedBox>? _repeats;

    /// <summary>
    /// Whether a child of <paramref name="e"/> is drawn again on later pages: the box then needs the height it is
    /// stretched to, to know the pages it reaches.
    /// </summary>
    private static bool HasRepeatedChild(Element e)
    {
        foreach (var c in e.Children)
            if (c is Element { Style.RepeatOnPages: true }) return true;
        return false;
    }

    /// <summary>How many boxes to repeat were recorded, so that a parent can tell the ones laid out inside it.</summary>
    private int RepeatMark => _repeats?.Count ?? 0;

    /// <summary>Records a box with -pdfpecker-repeat that drew the ops from <paramref name="opsStart"/> between the two y.</summary>
    private void RecordRepeat(Element e, float top, float bottom, int opsStart)
    {
        if (!_paginate || e.Parent is not { } parent || _ops.Count <= opsStart || PageOf(top) != PageOf(Math.Max(top, bottom - 0.5f))) return;
        var ops = new List<DrawOp>(_ops.Count - opsStart);
        for (int i = opsStart; i < _ops.Count; i++) ops.Add(_ops[i].DeepCopy());
        (_repeats ??= new List<RepeatedBox>()).Add(new RepeatedBox(e, parent, top, bottom, ops));
    }

    /// <summary>
    /// Draws the copies of the boxes inside <paramref name="parent"/> recorded since <paramref name="mark"/> on each page
    /// after their own that the parent's content box, which ends at <paramref name="contentBottom"/>, continues on, and
    /// forgets them. A copy that would reach past the end of its page, or past the content box on the content box's last
    /// page, is left out.
    /// </summary>
    private void DrawRepeats(Element parent, int mark, float contentBottom)
    {
        if (_repeats == null || _repeats.Count <= mark) return;
        // A box laid out more than once inside the parent, when content around it moved, counts as it was last.
        var latest = new Dictionary<Element, RepeatedBox>();
        int kept = mark;
        for (int i = mark; i < _repeats.Count; i++)
        {
            var r = _repeats[i];
            if (ReferenceEquals(r.Parent, parent)) latest[r.Box] = r;
            else _repeats[kept++] = r; // a table cell's, which its row draws when it finishes the cell
        }
        _repeats.RemoveRange(kept, _repeats.Count - kept);
        if (!_paginate) return;
        int last = PageOf(contentBottom - 0.5f);
        foreach (var r in latest.Values)
        {
            float height = r.Bottom - r.Top;
            for (int p = PageOf(r.Bottom - 0.5f) + 1; p <= last; p++)
            {
                float top = FlowStart(p);
                if (top + height > FlowEnd(p) + Eps || p == last && top + height > contentBottom + Eps) continue;
                var copies = new List<DrawOp>(r.Ops.Count);
                foreach (var op in r.Ops)
                {
                    if (op is FieldOp) continue;
                    var copy = op.DeepCopy();
                    copy.Offset(0, top - r.Top);
                    copies.Add(copy);
                }
                // The structure has the box once: its copies are artifacts.
                Untag(copies);
                _ops.AddRange(copies);
            }
        }
    }

    /// <summary>Draws the copies of the boxes inside a table cell, whose content may have been laid out among other cells'.</summary>
    private void DrawCellRepeats(Element td, float contentBottom) => DrawRepeats(td, 0, contentBottom);
}
