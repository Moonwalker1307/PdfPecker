using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>Relative, absolute and fixed positioning (CSS 2.1 §9.3, §10.3.7, §10.6.4), z-index and transforms.</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// A containing block for absolutely positioned descendants: a positioned or transformed box, the initial containing
    /// block, or the boundary of content laid out on its own (whose pending boxes move with it when it is placed).
    /// </summary>
    private sealed class CbFrame
    {
        private static readonly List<(Element E, float X, float Y)> None = new();
        private List<(Element E, float X, float Y)>? _pending; // made on the first box: most frames get none
        public List<(Element E, float X, float Y)> Pending => _pending ?? None;
        public bool Transformed; // also the containing block of fixed descendants
        public bool Boundary;

        /// <summary>The pending boxes, leaving the frame empty.</summary>
        public List<(Element E, float X, float Y)>? TakePending()
        {
            var list = _pending;
            _pending = null;
            _index = null;
            return list;
        }

        public void Add(Element e, float x, float y)
        {
            _pending ??= new List<(Element E, float X, float Y)>();
            // A box queued again after layout rewound past it moves to its new static position.
            if (_index != null ? _index.TryGetValue(e, out int at) : (at = _pending.FindIndex(p => p.E == e)) >= 0)
            {
                _pending[at] = (e, x, y);
                return;
            }
            _pending.Add((e, x, y));
            if (_index != null) _index[e] = _pending.Count - 1;
            else if (_pending.Count > 16)
            {
                _index = new Dictionary<Element, int>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < _pending.Count; i++) _index[_pending[i].E] = i;
            }
        }

        private Dictionary<Element, int>? _index;
    }

    private readonly List<CbFrame> _frames = new() { new CbFrame() };
    private readonly CbFrame _fixedFrame = new();
    private readonly Stack<CbFrame> _boundaryPool = new();
    private bool _hasLayers;
    private Dictionary<Element, int>? _treeOrder;
    // The subtree that TreeOrder numbers, when it is not the whole document: a header or footer laid out on its own for
    // every page would otherwise number the document's whole tree once per page.
    private Element? _treeRoot;
    // The content boxes of the flex containers whose absolutely positioned children have a static position, as width,
    // height and where in them a box goes on each axis (0 start, 0.5 centre, 1 end), from that static position.
    private Dictionary<Element, (float W, float H, float Fx, float Fy)>? _staticAreas;
    // The grid areas absolutely positioned children of a grid container are placed in, as left, right, top and bottom
    // edges, NaN where the grid container's padding edge is the edge.
    private Dictionary<Element, (float X0, float X1, float Y0, float Y1)>? _gridAreas;

    /// <summary>Ops of position: fixed boxes, in coordinates from the top-left of the page area, drawn on every page.</summary>
    public List<DrawOp> FixedOps { get; } = new();

    /// <summary>The height of the page area that fixed boxes are placed in (the page's content height).</summary>
    public float FixedAreaHeight { get; init; } = float.NaN;

    /// <summary>
    /// Notes an absolutely positioned box met during layout, with its static position (where its top-left margin corner
    /// would be in the flow). It is laid out when its containing block is complete.
    /// </summary>
    private void QueueAbsolute(Element e, float staticX, float staticY)
    {
        bool isFixed = e.Style.Position == Position.Fixed;
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (!isFixed || f.Transformed || f.Boundary) { f.Add(e, staticX, staticY); return; }
        }
        _fixedFrame.Add(e, staticX, staticY);
    }

    /// <summary>
    /// A positioned inline element that is the containing block of absolutely positioned boxes: its pending boxes, and
    /// the border boxes of its fragments on its first and last line, each the union of the fragments on that line.
    /// </summary>
    private sealed class InlineCb
    {
        public required Element Element;
        public readonly CbFrame Frame = new();
        public bool Placed;
        public float FirstBaseline, LastBaseline;
        public float FirstLeft, FirstRight, Top, LastLeft, LastRight, Bottom;
    }

    // The containing inline elements of the lines being placed, in the order they were met; null until one is.
    private List<InlineCb>? _inlineCbs;
    private Dictionary<Element, InlineCb>? _inlineCbIndex;

    private InlineCb InlineCbOf(Element e)
    {
        var list = _inlineCbs ??= new List<InlineCb>();
        var index = _inlineCbIndex ??= new Dictionary<Element, InlineCb>(ReferenceEqualityComparer.Instance);
        if (index.TryGetValue(e, out var cb)) return cb;
        cb = new InlineCb { Element = e };
        list.Add(cb);
        index[e] = cb;
        return cb;
    }

    /// <summary>Notes a line fragment of an inline containing block, in its border box.</summary>
    private void NoteInlineCb(Element e, float baseline, float x, float y, float w, float h)
    {
        if (_measuring) return;
        var cb = InlineCbOf(e);
        if (!cb.Placed)
        {
            cb.Placed = true;
            cb.FirstBaseline = cb.LastBaseline = baseline;
            cb.FirstLeft = cb.LastLeft = x;
            cb.FirstRight = cb.LastRight = x + w;
            cb.Top = y;
            cb.Bottom = y + h;
            return;
        }
        if (baseline != cb.LastBaseline)
        {
            cb.LastBaseline = baseline;
            cb.LastLeft = x;
            cb.LastRight = x + w;
            cb.Bottom = y + h;
        }
        else
        {
            cb.LastLeft = Math.Min(cb.LastLeft, x);
            cb.LastRight = Math.Max(cb.LastRight, x + w);
            cb.Bottom = Math.Max(cb.Bottom, y + h);
        }
        if (baseline == cb.FirstBaseline)
        {
            cb.FirstLeft = cb.LastLeft;
            cb.FirstRight = cb.LastRight;
            cb.Top = Math.Min(cb.Top, y);
        }
    }

    /// <summary>
    /// Notes an absolutely positioned box of a line: inside a positioned inline element it waits for the element's
    /// fragments, otherwise for the containing block around the lines.
    /// </summary>
    private void QueueLineAbsolute(Item it, float x, float y)
    {
        if (it.Deco is { } d && !_measuring) InlineCbOf(d.Element).Frame.Add(it.Element!, x, y);
        else QueueAbsolute(it.Element!, x, y);
    }

    /// <summary>
    /// Lays out the boxes waiting for the inline containing blocks of the lines just placed. As in Chrome, the containing
    /// block runs from the start of the element's first fragment to the end of its last, as padding boxes, and has no
    /// width when the end comes before the start; an element with no fragment leaves its boxes to the block around it.
    /// </summary>
    private void LayoutInlineAbsolutes(List<InlineCb> cbs)
    {
        foreach (var cb in cbs)
        {
            if (cb.Frame.Pending.Count == 0) continue;
            if (!cb.Placed)
            {
                foreach (var (e, sx, sy) in cb.Frame.TakePending()!) QueueAbsolute(e, sx, sy);
                continue;
            }
            var s = cb.Element.Style;
            float top = cb.Top + s.BorderTop, h = Math.Max(0, cb.Bottom - s.BorderBottom - top);
            float x, w;
            if (s.Rtl)
            {
                float right = cb.FirstRight - s.BorderRight;
                w = Math.Max(0, right - (cb.LastLeft + s.BorderLeft));
                x = right - w;
            }
            else
            {
                x = cb.FirstLeft + s.BorderLeft;
                w = Math.Max(0, cb.LastRight - s.BorderRight - x);
            }
            LayoutAbsolutes(cb.Frame, x, top, w, h);
        }
    }

    /// <summary>Lays out the absolutely positioned boxes of a frame against the padding box given.</summary>
    private void LayoutAbsolutes(CbFrame frame, float cbX, float cbY, float cbW, float cbH)
    {
        // Positioned boxes are not the last content of the flow they are laid out after.
        float lastContent = _lastContentBottom;
        // Laying one out can queue more (nested ones without a positioned box in between): take them in rounds.
        for (int round = 0; round < 64 && frame.Pending.Count > 0; round++)
        {
            var list = frame.TakePending()!;
            foreach (var (e, sx, sy) in list) LayoutAbsolute(e, sx, sy, cbX, cbY, cbW, cbH);
        }
        _lastContentBottom = lastContent;
    }

    private void LayoutAbsolute(Element e, float staticX, float staticY, float cbX, float cbY, float cbW, float cbH)
    {
        Cancellation.ThrowIfCancellationRequested();
        // A box placed in the grid of the grid container that is its containing block is positioned in its grid area.
        if (_gridAreas != null && _gridAreas.TryGetValue(e, out var gridArea) && e.Parent?.Style is { } ps
            && (ps.Positioned || ps.Transform != null || ps.Perspective != null || ps.Filters != null))
        {
            float x1 = float.IsNaN(gridArea.X1) ? cbX + cbW : gridArea.X1, y1 = float.IsNaN(gridArea.Y1) ? cbY + cbH : gridArea.Y1;
            if (!float.IsNaN(gridArea.X0)) cbX = gridArea.X0;
            if (!float.IsNaN(gridArea.Y0)) cbY = gridArea.Y0;
            cbW = Math.Max(0, x1 - cbX);
            cbH = Math.Max(0, y1 - cbY);
        }
        var s = e.Style;
        float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(cbW), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(cbW);
        float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(cbW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(cbW);
        bool hasL = !s.Left.IsAuto, hasR = !s.Right.IsAuto, hasT = !s.Top.IsAuto, hasB = !s.Bottom.IsAuto;
        float l = s.Left.Resolve(cbW), r = s.Right.Resolve(cbW), t = s.Top.Resolve(cbH), b = s.Bottom.Resolve(cbH);
        float frameH = s.PaddingLeft.Resolve(cbW) + s.PaddingRight.Resolve(cbW) + s.BorderLeft + s.BorderRight;
        float frameV = s.PaddingTop.Resolve(cbW) + s.PaddingBottom.Resolve(cbW) + s.BorderTop + s.BorderBottom;
        bool replaced = e.IsReplaced;

        // Width: given, stretched between left and right, or shrink-to-fit.
        float? borderW = null;
        if (!s.Width.IsAuto && !replaced)
        {
            float w = s.Width.Resolve(cbW);
            borderW = s.BorderBox ? w : w + frameH;
        }
        else if (s.Width.IsAuto && hasL && hasR && !replaced)
            borderW = Math.Max(0, cbW - l - r - ml - mr);
        if (borderW is { } bw0)
        {
            if (!s.MaxWidth.IsAuto) bw0 = Math.Min(bw0, s.MaxWidth.Resolve(cbW) + (s.BorderBox ? 0 : frameH));
            if (!s.MinWidth.IsAuto) bw0 = Math.Max(bw0, s.MinWidth.Resolve(cbW) + (s.BorderBox ? 0 : frameH));
            borderW = Math.Max(bw0, frameH);
        }
        // Room for shrink-to-fit: the containing block less the offsets that are set.
        float room = cbW - (hasL ? l : 0) - (hasR ? r : 0);
        if (!hasL && !hasR) room = Math.Max(0, cbX + cbW - staticX);

        // Height: given, or stretched between top and bottom.
        float? borderH = null;
        if (!s.Height.IsAuto && !replaced)
        {
            float h = s.Height.IsPercent ? s.Height.Resolve(cbH) : s.Height.Value;
            borderH = s.BorderBox ? h : h + frameV;
        }
        else if (s.Height.IsAuto && hasT && hasB && !replaced)
            borderH = Math.Max(frameV, cbH - t - b - mt - mb);

        // Its min-height and max-height percentages, and its content's when its height is definite, resolve against
        // the containing block's padding box too.
        var outerCb = _cbHeight;
        _cbHeight = new CbHeights(cbH);
        var c = CaptureBox(e, borderW.HasValue ? borderW.Value + ml + mr : Math.Max(0, room), shrinkToFit: !borderW.HasValue, borderW, borderH);
        _cbHeight = outerCb;
        float boxW = c.BoxW, boxH = c.BoxH;

        // Auto margins centre the box between left and right (or top and bottom) when all three are set.
        if (hasL && hasR && !s.Width.IsAuto && (s.MarginLeft.IsAuto || s.MarginRight.IsAuto))
        {
            float free = cbW - l - r - boxW - ml - mr;
            if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto) ml += free / 2;
            else if (s.MarginLeft.IsAuto) ml += free;
        }
        if (hasT && hasB && !s.Height.IsAuto && (s.MarginTop.IsAuto || s.MarginBottom.IsAuto))
        {
            float free = cbH - t - b - boxH - mt - mb;
            if (s.MarginTop.IsAuto && s.MarginBottom.IsAuto) mt += free / 2;
            else if (s.MarginTop.IsAuto) mt += free;
        }

        float x = hasL ? cbX + l + ml : hasR ? cbX + cbW - r - mr - boxW : staticX + ml;
        float y = hasT ? cbY + t + mt : hasB ? cbY + cbH - b - mb - boxH : staticY + mt;
        // In a flex container the static position is where the box would go as its only item, aligned by
        // justify-content and align-self (css-flexbox §4.1).
        if (_staticAreas != null && _staticAreas.TryGetValue(e, out var area))
        {
            if (!hasL && !hasR) x += area.Fx * (area.W - boxW - ml - mr);
            if (!hasT && !hasB) y += area.Fy * (area.H - boxH - mt - mb);
        }
        int start = _ops.Count;
        Place(c, x - c.BoxX, y - c.BoxY);
        if (_ops.Count > start && !(_ops.Count == start + 1 && _ops[start] is LayerOp)) WrapLayer(start, LayerOp.PositionedOrder, stackingContext: false);
        if (_ops.Count > start && _ops[^1] is LayerOp { TreeOrder: < 0 } own) own.TreeOrder = TreeOrder(e);
    }

    /// <summary>
    /// The element's position in the document, counted in tree order. Absolutely positioned boxes are laid out when
    /// their containing block ends, after the boxes that follow them, so positioned boxes that paint at the same level
    /// are put back in document order by this number rather than by when their ops were made.
    /// </summary>
    private int TreeOrder(Element e)
    {
        if (_treeOrder == null)
        {
            var root = _treeRoot ?? e;
            if (_treeRoot == null) while (root.Parent != null) root = root.Parent;
            _treeOrder = new Dictionary<Element, int>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<Element>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                _treeOrder[next] = _treeOrder.Count;
                for (int i = next.Children.Count - 1; i >= 0; i--)
                    if (next.Children[i] is Element child) stack.Push(child);
            }
        }
        // An element outside the tree that was numbered (a box made during layout) goes after it.
        if (!_treeOrder.TryGetValue(e, out int order)) _treeOrder[e] = order = _treeOrder.Count;
        return order;
    }

    /// <summary>
    /// Notes the content box of a flex container, <paramref name="w"/> by <paramref name="h"/>, for the static positions
    /// of its absolutely positioned children, which were queued at its top-left before its height was known.
    /// </summary>
    private void NoteFlexStaticArea(Element container, float w, float h)
    {
        var s = container.Style;
        foreach (var c in container.Children)
        {
            if (c is not Element { Style: { OutOfFlow: true, Display: not Display.None } } e || e.Detached) continue;
            var align = e.Style.AlignSelf == BoxAlign.Auto ? s.AlignItems : e.Style.AlignSelf;
            float main = AlignFactor(s.JustifyContent), cross = AlignFactor(align);
            if (s.FlexDirection is FlexDirection.RowReverse or FlexDirection.ColumnReverse) main = 1 - main;
            if (s.FlexWrap == FlexWrap.WrapReverse) cross = 1 - cross;
            float fx = s.IsRow ? main : cross, fy = s.IsRow ? cross : main;
            // Right to left, the start of a row (and the cross start of a column) is on the right.
            if (s.Rtl) fx = 1 - fx;
            if (fx == 0 && fy == 0)
            {
                _staticAreas?.Remove(e);
                continue;
            }
            _staticAreas ??= new Dictionary<Element, (float, float, float, float)>(ReferenceEqualityComparer.Instance);
            _staticAreas[e] = (w, h, fx, fy);
        }
    }

    /// <summary>Where on an axis a box alone in its container goes for an alignment value: 0 start, 0.5 centre, 1 end.</summary>
    private static float AlignFactor(BoxAlign a) => a switch
    {
        BoxAlign.Center or BoxAlign.SpaceAround or BoxAlign.SpaceEvenly => 0.5f,
        BoxAlign.End => 1,
        _ => 0,
    };

    /// <summary>Places relative offsets, z-index layering and transforms on a laid-out box's ops.</summary>
    private void FinishLayered(Element e, int opsStart, float cbW)
    {
        var s = e.Style;
        // A box with a backdrop filter changes what is behind it even when it paints nothing itself.
        if (_ops.Count == opsStart && s.BackdropFilters != null && _lastBoxW > 0 && _lastBoxH > 0) _ops.Add(new GroupOp());
        if (_ops.Count == opsStart) return;
        if (s.Position == Position.Relative)
        {
            float dx = !s.Left.IsAuto ? s.Left.Resolve(cbW) : !s.Right.IsAuto ? -s.Right.Resolve(cbW) : 0;
            float dy = !s.Top.IsAuto ? (s.Top.IsPercent ? 0 : s.Top.Value) : !s.Bottom.IsAuto ? -(s.Bottom.IsPercent ? 0 : s.Bottom.Value) : 0;
            if (dx != 0 || dy != 0)
                for (int i = opsStart; i < _ops.Count; i++) _ops[i].Offset(dx, dy);
            _lastBoxX += dx;
            _lastBoxTop += dy;
        }
        float order = s.ZIndex is { } z && z != 0 ? z : LayerOp.PositionedOrder;
        var layer = WrapLayer(opsStart, order, stackingContext: s.ZIndex != null || s.Transform != null || s.Perspective != null || s.Opacity < 1 || s.HasEffects);
        layer.TreeOrder = TreeOrder(e);
        layer.Opacity = s.Opacity;
        if (s.HasEffects || s.Clipped) ApplyEffects(e, layer, cbW);
        if (s.Transform is { } fns)
        {
            float w = _lastBoxW, h = _lastBoxH;
            float ox = s.TransformOriginX.Resolve(w), oy = s.TransformOriginY.Resolve(h);
            layer.Transform = CssTransform.ToMatrix(fns, w, h, ox, oy, null) ?? Matrix.Scale(0, 0);
            layer.OriginX = _lastBoxX + ox;
            layer.OriginY = _lastBoxTop + oy;
            if (CssTransform.Has3D(fns)) layer.Transform3D = new Transform3D(e, fns, w, h, ox, oy);
        }
        if (s.Perspective is { } distance)
        {
            // The viewer looks at the children from the perspective origin; their 3D transforms are drawn as seen from there.
            float px = _lastBoxX + s.PerspectiveOriginX.Resolve(_lastBoxW), py = _lastBoxTop + s.PerspectiveOriginY.Resolve(_lastBoxH);
            ApplyPerspective(e, layer.Ops, distance, px, py);
        }
    }

    private static void ApplyPerspective(Element parent, List<DrawOp> ops, float distance, float px, float py)
    {
        foreach (var op in ops)
        {
            if (op is not GroupOp g) continue;
            if (g is LayerOp { Transform3D: { } t } layer && t.Owner.Parent == parent)
                layer.Transform = CssTransform.ToMatrix(t.Functions, t.W, t.H, t.OriginX, t.OriginY, (distance, px - layer.OriginX, py - layer.OriginY)) ?? Matrix.Scale(0, 0);
            // The child's layer sits inside the parent's ops, maybe within the plain groups of boxes between them.
            ApplyPerspective(parent, g.Ops, distance, px, py);
        }
    }

    private static float WholePx(float pt) => MathF.Floor(pt / 0.75f + 0.001f) * 0.75f;

    /// <summary>Lays out the boxes waiting for the initial containing block and the page area, and sorts the layers.</summary>
    private void FinishRoot(float width)
    {
        // Chrome sizes the initial containing block (its viewport) in whole CSS pixels.
        width = WholePx(width);
        LayoutAbsolutes(_frames[0], 0, 0, width, WholePx(_grid.Height(0)));
        if (_fixedFrame.Pending.Count > 0)
        {
            float areaH = WholePx(float.IsNaN(FixedAreaHeight) ? _grid.Height(0) : FixedAreaHeight);
            var list = _fixedFrame.TakePending()!;
            var c = Capture(() =>
            {
                _frames.Add(new CbFrame { Transformed = true });
                foreach (var (e, sx, sy) in list)
                    LayoutAbsolute(e, sx, sy - _grid.Start(PageOf(sy)), 0, 0, width, areaH);
                LayoutAbsolutes(_frames[^1], 0, 0, width, areaH);
                _frames.RemoveAt(_frames.Count - 1);
                return 0;
            });
            FixedOps.AddRange(c.Ops);
            LayerOp.SortLayers(FixedOps);
        }
        if (_hasLayers) LayerOp.SortLayers(_ops);
    }
}
