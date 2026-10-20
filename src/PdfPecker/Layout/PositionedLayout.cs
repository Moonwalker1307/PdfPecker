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
        var s = e.Style;
        float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(cbW), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(cbW);
        float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(cbW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(cbW);
        bool hasL = !s.Left.IsAuto, hasR = !s.Right.IsAuto, hasT = !s.Top.IsAuto, hasB = !s.Bottom.IsAuto;
        float l = s.Left.Resolve(cbW), r = s.Right.Resolve(cbW), t = s.Top.Resolve(cbH), b = s.Bottom.Resolve(cbH);
        float frameH = s.PaddingLeft.Resolve(cbW) + s.PaddingRight.Resolve(cbW) + s.BorderLeft + s.BorderRight;
        float frameV = s.PaddingTop.Resolve(cbW) + s.PaddingBottom.Resolve(cbW) + s.BorderTop + s.BorderBottom;
        bool replaced = e.Name is "img" or "svg";

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
        int start = _ops.Count;
        Place(c, x - c.BoxX, y - c.BoxY);
        if (_ops.Count > start && !(_ops.Count == start + 1 && _ops[start] is LayerOp)) WrapLayer(start, LayerOp.PositionedOrder, stackingContext: false);
    }

    /// <summary>Places relative offsets, z-index layering and transforms on a laid-out box's ops.</summary>
    private void FinishLayered(Element e, int opsStart, float cbW)
    {
        var s = e.Style;
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
        var layer = WrapLayer(opsStart, order, stackingContext: s.ZIndex != null || s.Transform != null || s.Opacity < 1 || s.HasEffects);
        layer.Opacity = s.Opacity;
        if (s.HasEffects) ApplyEffects(e, layer, cbW);
        if (s.Transform is { } fns)
        {
            float w = _lastBoxW, h = _lastBoxH;
            layer.Transform = CssTransform.ToMatrix(fns, w, h);
            layer.OriginX = _lastBoxX + s.TransformOriginX.Resolve(w);
            layer.OriginY = _lastBoxTop + s.TransformOriginY.Resolve(h);
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
