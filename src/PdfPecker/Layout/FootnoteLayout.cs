using System.Globalization;
using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// What a layout needs for CSS footnotes (<c>float: footnote</c>, CSS Generated Content for Paged Media): the
/// document's footnotes in order, and the style of the footnote area that <c>@page { @footnote { ... } }</c> gives.
/// </summary>
internal sealed class FootnoteSetup
{
    public FootnoteSetup(IReadOnlyList<Element> bodies, ComputedStyle area)
    {
        Bodies = bodies;
        Area = area;
    }

    /// <summary>The footnotes, by their <see cref="Element.FootnoteIndex"/>.</summary>
    public IReadOnlyList<Element> Bodies { get; }

    /// <summary>The style of the footnote area box at the bottom of each page that has footnotes.</summary>
    public ComputedStyle Area { get; }

    /// <summary>The anchor key that the layout gives the place of a footnote's call in the text.</summary>
    public static string CallKey(int index) => "\u0001c" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The anchor key of a footnote's place in the footnote area: the call links to it.</summary>
    public static string BodyKey(int index) => "\u0001n" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads the footnote index back from a <see cref="CallKey"/>.</summary>
    public static bool TryParseCallKey(string id, out int index)
    {
        index = -1;
        return id.Length > 2 && id[0] == '\u0001' && id[1] == 'c'
               && int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// Computes the footnote area's style from the @footnote declarations, inheriting from the page context (a style
    /// with initial values, as for page-margin boxes).
    /// </summary>
    public static ComputedStyle AreaStyle(IEnumerable<Declaration> declarations, ComputedStyle initial, float viewportWidth, float viewportHeight)
    {
        var sb = new StringBuilder();
        foreach (var d in declarations)
        {
            sb.Append(d.Property).Append(':').Append(d.Value);
            if (d.Important) sb.Append(" !important");
            sb.Append(';');
        }
        var doc = new Element("#document");
        var area = new Element("div");
        area.AddAttribute("style", "display:block;" + sb);
        doc.AppendChild(area);
        new StyleResolver(new Stylesheet(), viewportWidth, viewportHeight).ResolveTree(doc, initial);
        return area.Style;
    }
}

/// <summary>
/// CSS footnotes. A footnote's call registers it when the line holding the call is placed: the footnote goes to the
/// footnote area at the bottom of that page, which takes its height from the page's flow, so later content breaks
/// earlier. Pages keep their place in the flow: the area's space is a band at the bottom of the page that content
/// skips, as it skips the space after a page break. When a footnote does not fit, the call's line moves to the next
/// page (footnote-policy: line, or auto when not even the footnote's first line fits), or the footnote is split
/// between line boxes and continues in the next page's footnote area. The areas are laid out once the flow is done.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>The document's footnotes and footnote area style; null when the document has none.</summary>
    public FootnoteSetup? Footnotes { get; init; }

    /// <summary>The footnote areas of all pages, in flow coordinates at the bottom of their pages, once laid out.</summary>
    public List<DrawOp> FootnoteOps { get; } = new();

    /// <summary>
    /// A footnote placed on a page: the page of its call, where in the ops the call's line was laid out (so a rewind of
    /// the layout takes the footnote back), and the pieces of it that go into the footnote areas.
    /// </summary>
    private sealed class FootnoteCall
    {
        public int Index, Page, OpsIndex;
        public List<DrawOp> Owner = null!;
        public readonly List<FootnotePart> Parts = new(1);
    }

    /// <summary>
    /// A piece of a footnote in one page's area: the band of its own layout from <see cref="From"/> to <see cref="To"/>
    /// (infinity: to its end). A part continued or moved from an earlier page goes before the page's own footnotes.
    /// <see cref="Basis"/> is the page whose area width the footnote is laid out in: its own page when it is whole, and
    /// for a split footnote the page of its first piece, so all its pieces come from one layout.
    /// </summary>
    private readonly record struct FootnotePart(int Index, int Page, float From, float To, bool Carried, int Basis)
    {
        public bool Whole => From == 0 && float.IsPositiveInfinity(To);
    }

    /// <summary>A footnote laid out on its own in the area's width: its height and the tops of its lines, where it can split.</summary>
    private sealed record FootnoteMeasure(float Height, float[] Cuts);

    private readonly List<FootnoteCall> _footnoteCalls = new();
    private readonly Dictionary<int, float> _footnoteReserve = new(); // page -> height of its footnote area
    private readonly Dictionary<(int, float), FootnoteMeasure> _footnoteMeasures = new(); // by footnote and area width
    private readonly Dictionary<string, float> _footnoteGroups = new(StringComparer.Ordinal); // inline footnotes laid out together
    private float _rootWidth;
    private Element? _footnoteArea;

    /// <summary>
    /// The height the footnote area takes at the bottom of a page (0 without footnotes). The columns of a multi-column
    /// box are not pages: the box takes the room on its page before it lays out its columns.
    /// </summary>
    public float FootnoteReserve(int page) => _footnoteReserve.Count > 0 && _columnNotes == null && _footnoteReserve.TryGetValue(page, out float h) ? h : 0;

    /// <summary>The page (0-based, in this layout's flow) of each footnote's call.</summary>
    public IEnumerable<(int Index, int Page)> FootnoteCallPages => _footnoteCalls.Select(c => (c.Index, c.Page));

    /// <summary>
    /// The page's last flow position content may reach: its end, less the space of its footnote area and the room kept
    /// for the bottom edges of the boxes around the content.
    /// </summary>
    private float FlowEnd(int page) => _grid.Start(page + 1) - FootnoteReserve(page) - EdgeReserve(page);

    /// <summary>The bottom of the last page a footnote area is on, for the page count; 0 without footnotes.</summary>
    private float FootnotesBottom()
    {
        int last = -1;
        foreach (var page in _footnoteReserve.Keys) last = Math.Max(last, page);
        return last < 0 ? 0 : _grid.Start(last) + 1;
    }

    private bool IsPlacedFootnote(int index)
    {
        foreach (var c in _footnoteCalls)
            if (c.Index == index) return true;
        return false;
    }

    /// <summary>
    /// Places the footnotes whose calls are in a line about to be placed at <paramref name="y"/>, and returns where the
    /// line goes: on the next page when footnote-policy moves it there.
    /// </summary>
    private float PlaceFootnoteCalls(List<Item> items, float y, float lineH)
    {
        List<int>? fresh = null;
        foreach (var it in items)
            if (it.Kind == ItemKind.Anchor && FootnoteSetup.TryParseCallKey(it.AnchorId!, out int k) && k < Footnotes!.Bodies.Count
                && !IsPlacedFootnote(k) && (fresh == null || !fresh.Contains(k)))
                (fresh ??= new List<int>()).Add(k);
        if (fresh == null) return y;
        // A line moves at most once for its footnotes: at the top of the next page it stays, and they split or move on.
        if (!AtPageTop(y))
        {
            if (RegisterFootnotes(fresh, y, y + lineH, allowMove: true)) return y;
            y = FitOnPage(_grid.Start(PageOf(y) + 1), lineH);
        }
        RegisterFootnotes(fresh, y, y + lineH, allowMove: false);
        return y;
    }

    private bool _deferFootnoteCalls; // a line is being drawn: calls wait until it is done
    private List<(string Id, float Y)>? _deferredFootnoteCalls;

    /// <summary>A call whose anchor was set outside line placement (content laid out on its own and moved into place).</summary>
    private void PlaceFootnoteCallAt(string id, float y)
    {
        if (_deferFootnoteCalls)
        {
            (_deferredFootnoteCalls ??= new List<(string, float)>()).Add((id, y));
            return;
        }
        if (!FootnoteSetup.TryParseCallKey(id, out int k) || k >= Footnotes!.Bodies.Count || IsPlacedFootnote(k)) return;
        RegisterFootnotes(new List<int>(1) { k }, y, y, allowMove: false);
    }

    private void PlaceDeferredFootnoteCalls()
    {
        var calls = _deferredFootnoteCalls!;
        _deferredFootnoteCalls = null;
        foreach (var (id, y) in calls) PlaceFootnoteCallAt(id, y);
    }

    /// <summary>
    /// Puts the footnotes of a line spanning <paramref name="lineTop"/> to <paramref name="lineBottom"/> into the
    /// footnote areas. Returns false, having changed nothing, when the line should move to the next page instead.
    /// </summary>
    private bool RegisterFootnotes(List<int> fresh, float lineTop, float lineBottom, bool allowMove)
    {
        int page = PageOf(lineTop);
        float frame = AreaFrameHeight(page);
        var onPage = PartsOn(page);
        // The room the area's content may take on this page: below the line, and within the area's maximum height.
        float cap = Math.Min(_grid.Start(page + 1) - lineBottom - frame, MaxAreaContent(page));
        var planned = new List<FootnotePart>(); // parts given to later pages by this call
        var calls = new List<FootnoteCall>(fresh.Count);
        bool deferRest = false;
        foreach (int k in fresh)
        {
            var call = new FootnoteCall { Index = k, Page = page, Owner = _ops, OpsIndex = _ops.Count };
            calls.Add(call);
            var body = Footnotes!.Bodies[k];
            if (!deferRest)
            {
                var whole = new FootnotePart(k, page, 0, float.PositiveInfinity, false, page);
                onPage.Add(whole);
                if (AreaContent(onPage) <= cap + Eps)
                {
                    call.Parts.Add(whole);
                    continue;
                }
                onPage.RemoveAt(onPage.Count - 1);
                if (allowMove && body.Style.FootnotePolicy != FootnotePolicy.Auto) return false;
                float cut = body.Style.Display == Display.Inline ? 0 : BestCut(k, page, 0, cap - AreaContent(onPage));
                if (cut <= 0 && allowMove) return false; // not even its first line fits: the call goes with it
                deferRest = true;
                if (cut > 0)
                {
                    var first = new FootnotePart(k, page, 0, cut, false, page);
                    onPage.Add(first);
                    call.Parts.Add(first);
                    Continue(call, cut, page + 1, planned);
                    continue;
                }
            }
            Continue(call, 0, page + 1, planned);
        }
        _footnoteCalls.AddRange(calls);
        UpdateFootnoteReserves();
        return true;
    }

    /// <summary>Gives the rest of a footnote, from <paramref name="from"/>, to the footnote areas from page <paramref name="page"/> on.</summary>
    private void Continue(FootnoteCall call, float from, int page, List<FootnotePart> planned)
    {
        int k = call.Index;
        bool inline = Footnotes!.Bodies[k].Style.Display == Display.Inline;
        // A footnote moved whole is laid out in the width of the page it lands on; the rest of a split one keeps the
        // layout its first piece came from.
        int basis = from == 0 ? -1 : call.Page;
        for (int guard = 0; guard < 1000; guard++, page++)
        {
            int b = basis < 0 ? page : basis;
            var m = inline ? null : Measure(k, b);
            var parts = PartsOn(page);
            foreach (var p in planned)
                if (p.Page == page) parts.Add(p);
            float used = AreaContent(parts), room = MaxAreaContent(page) - used;
            if (m == null || m.Height - from <= room + Eps)
            {
                Add(new FootnotePart(k, page, from, float.PositiveInfinity, true, b));
                return;
            }
            float cut = BestCut(k, b, from, room);
            if (cut <= from)
            {
                // Not one more line fits here: try the next page, unless this one has nothing else on it.
                if (used > 0) continue;
                cut = m.Cuts.FirstOrDefault(c => c > from + Eps);
                if (cut <= from) cut = float.PositiveInfinity;
            }
            Add(new FootnotePart(k, page, from, cut, true, b));
            if (float.IsPositiveInfinity(cut)) return;
            from = cut;
            basis = b;
        }

        void Add(FootnotePart part)
        {
            call.Parts.Add(part);
            planned.Add(part);
        }
    }

    /// <summary>The farthest line boundary of footnote <paramref name="k"/> after <paramref name="from"/> within <paramref name="room"/>, or 0.</summary>
    private float BestCut(int k, int basis, float from, float room)
    {
        float best = 0;
        foreach (float c in Measure(k, basis).Cuts)
            if (c > from + Eps && c - from <= room + Eps) best = c;
        return best;
    }

    /// <summary>A copy of the parts in a page's footnote area, in order: those carried from earlier pages first.</summary>
    private List<FootnotePart> PartsOn(int page)
        => _footnoteParts.TryGetValue(page, out var parts) ? new List<FootnotePart>(parts) : new List<FootnotePart>();

    private readonly Dictionary<int, List<FootnotePart>> _footnoteParts = new();

    private void UpdateFootnoteReserves()
    {
        _footnoteReserve.Clear();
        _footnoteParts.Clear();
        foreach (bool carried in new[] { true, false })
            foreach (var c in _footnoteCalls)
                foreach (var p in c.Parts)
                {
                    if (p.Carried != carried) continue;
                    if (!_footnoteParts.TryGetValue(p.Page, out var list)) _footnoteParts[p.Page] = list = new List<FootnotePart>();
                    list.Add(p);
                }
        foreach (var (page, parts) in _footnoteParts) _footnoteReserve[page] = AreaFrameHeight(page) + AreaContent(parts);
    }

    /// <summary>Takes back the footnotes whose calls were in ops that a rewind removed (or keeps them when the ops were only wrapped).</summary>
    private void TruncateFootnotes(int count, bool wrapped)
    {
        bool changed = false;
        // Calls are registered in the order of their ops, so the ones at or after count are at the end.
        for (int i = _footnoteCalls.Count - 1; i >= 0; i--)
        {
            var c = _footnoteCalls[i];
            if (!ReferenceEquals(c.Owner, _ops) || c.OpsIndex < count) break;
            if (wrapped) c.OpsIndex = count;
            else
            {
                _footnoteCalls.RemoveAt(i);
                changed = true;
            }
        }
        if (changed) UpdateFootnoteReserves();
    }

    // ------------------------------------------------------------------ measuring

    /// <summary>
    /// The width of the footnote area on a page: the page's content width. The layout's width is the first page's, and
    /// with @page :first, :left or :right rules other pages can be wider or narrower.
    /// </summary>
    private float AreaWidth(int page)
    {
        if (!_paginate || !_grid.Varies) return _rootWidth;
        return _rootWidth + _grid.SetupOf(page).ContentWidth - _grid.SetupOf(0).ContentWidth;
    }

    private float AreaFrameHeight(int page)
    {
        var s = Footnotes!.Area;
        float w = AreaWidth(page);
        return (s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(w)) + s.BorderTop + s.PaddingTop.Resolve(w)
               + s.PaddingBottom.Resolve(w) + s.BorderBottom + (s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(w));
    }

    private (float Left, float Right) AreaFrameX(int page)
    {
        var s = Footnotes!.Area;
        float w = AreaWidth(page);
        return ((s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(w)) + s.BorderLeft + s.PaddingLeft.Resolve(w),
            (s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(w)) + s.BorderRight + s.PaddingRight.Resolve(w));
    }

    /// <summary>The width of the footnote area's content box on a page.</summary>
    private float AreaContentWidth(int page)
    {
        var (l, r) = AreaFrameX(page);
        return Math.Max(1, AreaWidth(page) - l - r);
    }

    /// <summary>The most the area's content may take on a page: its max-height, and never more than the page.</summary>
    private float MaxAreaContent(int page)
    {
        var s = Footnotes!.Area;
        float pageH = _grid.Height(page), frame = AreaFrameHeight(page), w = AreaWidth(page);
        float max = pageH - frame;
        if (!s.MaxHeight.IsAuto)
        {
            float mh = s.MaxHeight.Resolve(pageH);
            if (s.BorderBox) mh -= s.BorderTop + s.BorderBottom + s.PaddingTop.Resolve(w) + s.PaddingBottom.Resolve(w);
            max = Math.Min(max, mh);
        }
        return Math.Max(0, max);
    }

    /// <summary>The height of a footnote area's content: its parts one below the other, inline footnotes sharing lines.</summary>
    private float AreaContent(List<FootnotePart> parts)
    {
        float h = 0;
        for (int i = 0; i < parts.Count;)
        {
            int end = InlineRunEnd(parts, i);
            if (end > i)
            {
                h += InlineGroupHeight(parts, i, end);
                i = end;
                continue;
            }
            var p = parts[i++];
            h += (float.IsPositiveInfinity(p.To) ? Measure(p.Index, p.Basis).Height : p.To) - p.From;
        }
        return h;
    }

    /// <summary>The end of a run of whole inline footnotes starting at <paramref name="i"/>; <paramref name="i"/> when there is none.</summary>
    private int InlineRunEnd(List<FootnotePart> parts, int i)
    {
        int end = i;
        while (end < parts.Count && parts[end].Whole && Footnotes!.Bodies[parts[end].Index].Style.Display == Display.Inline) end++;
        return end;
    }

    private float InlineGroupHeight(List<FootnotePart> parts, int start, int end)
    {
        var key = new StringBuilder();
        for (int i = start; i < end; i++) key.Append(parts[i].Index).Append(',');
        key.Append(AreaContentWidth(parts[start].Page).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var k = key.ToString();
        if (!_footnoteGroups.TryGetValue(k, out float h))
            _footnoteGroups[k] = h = LayoutInlineFootnotes(parts, start, end).Height;
        return h;
    }

    /// <summary>Lays out inline footnotes together, as one anonymous block of lines in the footnote area.</summary>
    private Captured LayoutInlineFootnotes(List<FootnotePart> parts, int start, int end)
    {
        var run = new List<Node>(end - start);
        for (int i = start; i < end; i++) run.Add(Footnotes!.Bodies[parts[i].Index]);
        _footnoteArea ??= new Element("::footnote-area") { Generated = true, Style = Footnotes!.Area };
        // The footnotes are out of the flow they came from; here they are the area's content.
        foreach (Element e in run) e.Detached = false;
        // The footnote area's height is not definite: percentage heights in footnotes behave as auto.
        var outerCb = _cbHeight;
        _cbHeight = null;
        try
        {
            return CaptureRun(run, _footnoteArea, AreaContentWidth(parts[start].Page));
        }
        finally
        {
            _cbHeight = outerCb;
            foreach (Element e in run) e.Detached = true;
        }
    }

    /// <summary>Footnote <paramref name="k"/> laid out in the area width of page <paramref name="basis"/>.</summary>
    private FootnoteMeasure Measure(int k, int basis)
    {
        float width = AreaContentWidth(basis);
        if (_footnoteMeasures.TryGetValue((k, width), out var m)) return m;
        // Laid out as part of the footnote area, whose height is not definite, wherever its call is.
        var outerCb = _cbHeight;
        _cbHeight = null;
        var c = CaptureBox(Footnotes!.Bodies[k], width, shrinkToFit: false);
        _cbHeight = outerCb;
        var tops = new SortedSet<float>();
        CollectLineTops(c.Ops, tops);
        var cuts = tops.Where(t => t > 0.5f && t < c.Height - 0.5f).ToArray();
        return _footnoteMeasures[(k, width)] = new FootnoteMeasure(c.Height, cuts);
    }

    private static void CollectLineTops(List<DrawOp> ops, SortedSet<float> tops)
    {
        foreach (var op in ops)
        {
            if (op is TextOp t) tops.Add(t.LineTop);
            else if (op is GroupOp g) CollectLineTops(g.Ops, tops);
        }
    }

    // ------------------------------------------------------------------ painting the areas

    /// <summary>Lays out the footnote area of every page with footnotes into <see cref="FootnoteOps"/>.</summary>
    private void FinishFootnotes()
    {
        if (Footnotes == null || _footnoteCalls.Count == 0) return;
        int start = _ops.Count;
        var area = Footnotes.Area;
        foreach (var (page, height) in _footnoteReserve.OrderBy(kv => kv.Key))
        {
            float w = AreaWidth(page);
            float ml = area.MarginLeft.IsAuto ? 0 : area.MarginLeft.Resolve(w), mr = area.MarginRight.IsAuto ? 0 : area.MarginRight.Resolve(w);
            float mt = area.MarginTop.IsAuto ? 0 : area.MarginTop.Resolve(w), mb = area.MarginBottom.IsAuto ? 0 : area.MarginBottom.Resolve(w);
            var (frameL, _) = AreaFrameX(page);
            float top = _grid.Start(page + 1) - height;
            var group = new GroupOp();
            _ops.Add(group);
            BoxPainter.Paint(group, area, ml, top + mt, Math.Max(0, w - ml - mr), Math.Max(0, height - mt - mb), _images, w);
            float cx = frameL, cy = top + mt + area.BorderTop + area.PaddingTop.Resolve(w);
            var parts = PartsOn(page);
            for (int i = 0; i < parts.Count;)
            {
                int end = InlineRunEnd(parts, i);
                if (end > i)
                {
                    var together = LayoutInlineFootnotes(parts, i, end);
                    for (int j = i; j < end; j++) SetAnchor(FootnoteSetup.BodyKey(parts[j].Index), cy);
                    Place(together, cx, cy);
                    cy += together.Height;
                    i = end;
                    continue;
                }
                var p = parts[i++];
                var c = CaptureBox(Footnotes.Bodies[p.Index], AreaContentWidth(p.Basis), shrinkToFit: false);
                if (p.From == 0) SetAnchor(FootnoteSetup.BodyKey(p.Index), cy);
                if (p.Whole)
                {
                    Place(c, cx, cy);
                    cy += c.Height;
                    continue;
                }
                float to = float.IsPositiveInfinity(p.To) ? c.Height : p.To;
                var slice = new List<DrawOp>();
                Slice(c.Ops, p.From, to, slice);
                foreach (var op in slice)
                {
                    op.Offset(cx, cy - p.From);
                    _ops.Add(op);
                }
                if (c.AnchorIds != null)
                    foreach (var id in c.AnchorIds)
                        if (Anchors.TryGetValue(id, out float ay) && ay >= p.From - Eps && ay < to - Eps) SetAnchor(id, ay + cy - p.From);
                cy += to - p.From;
            }
        }
        for (int i = start; i < _ops.Count; i++) FootnoteOps.Add(_ops[i]);
        _ops.RemoveRange(start, _ops.Count - start);
    }

    /// <summary>The ops of a footnote's own layout that start between <paramref name="from"/> and <paramref name="to"/>.</summary>
    private static void Slice(List<DrawOp> ops, float from, float to, List<DrawOp> into)
    {
        foreach (var op in ops)
        {
            if (op.GetType() == typeof(GroupOp))
            {
                var g = new GroupOp();
                Slice(((GroupOp)op).Ops, from, to, g.Ops);
                if (g.Ops.Count > 0) into.Add(g);
                continue;
            }
            float top = OpTop(op);
            if (top < from - 0.5f || top >= to - 0.5f) continue;
            // A box's background that runs past the split ends at it; the rest of the box goes on in the next area.
            if (op is RectOp r && r.Y + r.H > to) r.H = to - r.Y;
            into.Add(op);
        }
    }

    private static float OpTop(DrawOp op) => op switch
    {
        TextOp t => t.LineTop,
        RectOp r => r.Y,
        BorderRectOp b => b.Y,
        RoundRectOp rr => rr.Y,
        BackgroundOp bg => bg.Painted().Y0,
        LineOp l => Math.Min(l.Y1, l.Y2),
        ImageOp im => im.Y,
        SvgOp sv => sv.Y,
        LinkOp lk => lk.Y,
        CircleOp c => c.Cy - c.R,
        FieldOp f => f.Y,
        ClipOp clip when clip.H < ClipOp.Far => clip.Y,
        _ => FirstContentY(op) ?? 0,
    };
}
