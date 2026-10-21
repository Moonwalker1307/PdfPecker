using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>Floats (CSS 2.1 §9.5): placing them, flowing lines around them, and clearing them.</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>A float's margin box in flow coordinates.</summary>
    private struct FloatBox
    {
        public float X0, Y0, X1, Y1;
        public bool Right;
        public bool InitialLetter;
        public int OpsIndex; // where its ops start in the op list, to forget it when layout rewinds past it
        public FloatShape? Shape; // shape-outside: what lines beside it wrap around; null for the margin box
    }

    /// <summary>The floats of one block formatting context.</summary>
    private sealed class FloatContext
    {
        private const float Tiny = 0.001f; // a float touching a band at an edge only is not beside it

        private static readonly List<FloatBox> None = new();
        private List<FloatBox>? _boxes; // made on the first float: most formatting contexts have none
        public List<FloatBox> Boxes => _boxes ?? None;
        public float MinTop = float.NegativeInfinity; // a float's top may not be above an earlier float's top
        public float Bottom = float.NegativeInfinity; // lowest float bottom
        private int _lastOps = -1; // no float's OpsIndex is above this, so that folding usually has nothing to look at

        public bool Any => _boxes is { Count: > 0 };

        public void Clear()
        {
            _boxes?.Clear();
            MinTop = Bottom = float.NegativeInfinity;
            _lastOps = -1;
        }

        public void Add(FloatBox box)
        {
            (_boxes ??= new List<FloatBox>()).Add(box);
            if (box.Y0 > MinTop) MinTop = box.Y0;
            if (box.Y1 > Bottom) Bottom = box.Y1;
            if (box.OpsIndex > _lastOps) _lastOps = box.OpsIndex;
        }

        /// <summary>Forgets the floats whose ops were removed.</summary>
        public void Truncate(int opsCount)
        {
            if (_boxes == null) return;
            int n = Boxes.Count;
            while (n > 0 && Boxes[n - 1].OpsIndex >= opsCount) n--;
            if (n == Boxes.Count) return;
            Boxes.RemoveRange(n, Boxes.Count - n);
            MinTop = Bottom = float.NegativeInfinity;
            _lastOps = -1;
            foreach (var b in Boxes)
            {
                if (b.Y0 > MinTop) MinTop = b.Y0;
                if (b.Y1 > Bottom) Bottom = b.Y1;
                if (b.OpsIndex > _lastOps) _lastOps = b.OpsIndex;
            }
        }

        /// <summary>The ops of floats at or after <paramref name="start"/> were folded into one op at that index.</summary>
        public void Fold(int start)
        {
            // Every float placed wraps its own ops in a layer and folds, so looking at every float each time would make
            // a formatting context with n floats cost n squared; usually no float is past start and there is nothing to do.
            if (_lastOps <= start) return;
            _lastOps = start;
            for (int i = 0; i < Boxes.Count; i++)
                if (Boxes[i].OpsIndex > start) { var b = Boxes[i]; b.OpsIndex = start; Boxes[i] = b; }
        }

        /// <summary>
        /// The room left between <paramref name="left"/> and <paramref name="right"/> by the floats beside the band from
        /// <paramref name="y"/> to <paramref name="y"/> + <paramref name="h"/>.
        /// </summary>
        public (float Left, float Right) Band(float y, float h, float left, float right)
        {
            float bottom = y + Math.Max(h, Eps);
            foreach (var b in Boxes)
            {
                if (b.Y0 >= bottom - Tiny || b.Y1 <= y + Tiny || b.X1 <= b.X0) continue;
                if (b.Right) { if (b.X0 < right) right = b.X0; }
                else if (b.X1 > left) left = b.X1;
            }
            return (left, Math.Max(left, right));
        }

        /// <summary>
        /// The room a line from <paramref name="y"/> to <paramref name="y"/> + <paramref name="h"/> has between
        /// <paramref name="left"/> and <paramref name="right"/>: as <see cref="Band"/>, but a float with shape-outside
        /// takes only what its shape covers in the band, kept within its margin box, or nothing when the shape is not
        /// beside the line. Floats themselves are placed beside margin boxes, as in Chrome.
        /// </summary>
        public (float Left, float Right) LineBand(float y, float h, float left, float right)
        {
            float bottom = y + Math.Max(h, Eps);
            foreach (var b in Boxes)
            {
                if (b.Y0 >= bottom - Tiny || b.Y1 <= y + Tiny || b.X1 <= b.X0) continue;
                float x0 = b.X0, x1 = b.X1;
                if (b.Shape is { } shape)
                {
                    if (!shape.Excluded(y, y + h, out float sl, out float sr)) continue;
                    x0 = Math.Clamp(sl, b.X0, b.X1);
                    x1 = Math.Clamp(sr, b.X0, b.X1);
                }
                if (b.Right) { if (x0 < right) right = x0; }
                else if (x1 > left) left = x1;
            }
            return (left, Math.Max(left, right));
        }

        /// <summary>Whether a float with shape-outside is beside the band, so that the room changes as a line moves down.</summary>
        public bool ShapeBeside(float y, float h)
        {
            float bottom = y + Math.Max(h, Eps);
            foreach (var b in Boxes)
                if (b.Shape != null && b.Y0 < bottom - Tiny && b.Y1 > y + Tiny) return true;
            return false;
        }

        /// <summary>Whether a float with shape-outside reaches into the band from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
        public bool ShapeIn(float top, float bottom)
        {
            if (_boxes == null) return false;
            foreach (var b in _boxes)
                if (b.Shape != null && b.Y0 < bottom && b.Y1 > top) return true;
            return false;
        }

        /// <summary>The nearest float bottom below <paramref name="y"/> among the floats beside the band, or NaN.</summary>
        public float NextBottom(float y, float h)
        {
            float bottom = y + Math.Max(h, Eps), next = float.NaN;
            foreach (var b in Boxes)
            {
                if (b.Y0 >= bottom - Tiny || b.Y1 <= y + Tiny) continue;
                if (float.IsNaN(next) || b.Y1 < next) next = b.Y1;
            }
            return next;
        }

        /// <summary>The bottom of the lowest initial letter, or negative infinity: a paragraph with one of its own starts below it.</summary>
        public float InitialLetterBottom()
        {
            float y = float.NegativeInfinity;
            foreach (var b in Boxes)
                if (b.InitialLetter && b.Y1 > y) y = b.Y1;
            return y;
        }

        /// <summary>The bottom of the lowest left and/or right float, or negative infinity.</summary>
        public float ClearY(bool left, bool right)
        {
            float y = float.NegativeInfinity;
            foreach (var b in Boxes)
                if ((b.Right ? right : left) && b.Y1 > y) y = b.Y1;
            return y;
        }
    }

    private FloatContext _floats = new();
    private readonly Stack<FloatContext> _floatPool = new(); // formatting contexts come and go with every table cell

    private FloatContext RentFloats()
    {
        if (_floatPool.TryPop(out var f)) return f;
        return new FloatContext();
    }

    private void ReturnFloats(FloatContext f)
    {
        f.Clear();
        _floatPool.Push(f);
    }

    /// <summary>Whether floats reach below <paramref name="y"/>, so that content laid out there must flow around them.</summary>
    private bool FloatsBelow(float y) => _floats.Any && _floats.Bottom > y + Eps;

    /// <summary>Starts a new block formatting context: floats inside it stay inside it. Returns the outer one.</summary>
    private FloatContext EnterFormattingContext()
    {
        var saved = _floats;
        _floats = RentFloats();
        return saved;
    }

    /// <summary>Ends a block formatting context; its content's bottom grows to contain its floats.</summary>
    private float LeaveFormattingContext(FloatContext outer, float bottom)
    {
        if (_floats.Any && _floats.Bottom > bottom) bottom = _floats.Bottom;
        ReturnFloats(_floats);
        _floats = outer;
        return bottom;
    }

    /// <summary>The top of a block that clears floats.</summary>
    private float Cleared(ComputedStyle s, float top)
    {
        if (!(s.ClearLeft || s.ClearRight) || !_floats.Any) return top;
        float y = _floats.ClearY(s.ClearLeft, s.ClearRight);
        return y > top ? y : top;
    }

    /// <summary>The style of the br that ends <paramref name="line"/> when it clears floats (HTML's br clear), else null.</summary>
    private static ComputedStyle? ClearingBreak(Line line)
    {
        foreach (var it in line.Items)
            if (it.Kind == ItemKind.Break && (it.Style.ClearLeft || it.Style.ClearRight)) return it.Style;
        return null;
    }

    /// <summary>
    /// Block-level boxes whose border box may not overlap floats (CSS 2.1 §9.5): tables, replaced elements and boxes that
    /// start a block formatting context. They are placed beside the floats, or below them when too wide.
    /// </summary>
    private static bool AvoidsFloats(Element e)
    {
        var s = e.Style;
        return s.FormattingRoot || s.Display == Display.Table || s.IsFlex || s.IsGrid || s.IsMultiColumn || e.IsReplaced || e.Name == "hr";
    }

    private float LayoutBesideFloats(Element e, float x, ref float top, float w)
    {
        var s = e.Style;
        bool fixedWidth = !s.Width.IsAuto || e.IsReplaced;
        float l = x, r = x + w;
        for (int guard = 0; guard < 10_000; guard++)
        {
            (l, r) = _floats.Band(top, Eps, x, x + w);
            if (l <= x + Eps && r >= x + w - Eps) break;
            if (fixedWidth)
            {
                float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(w), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(w);
                float need = ml + mr + (e.IsReplaced ? Intrinsic(e).Max
                    : s.Width.Resolve(w) + (s.BorderBox ? 0 : s.PaddingLeft.Resolve(w) + s.PaddingRight.Resolve(w) + s.BorderLeft + s.BorderRight));
                if (r - l >= need - Eps) break;
            }
            else if (r - l > 0) break;
            float next = _floats.NextBottom(top, Eps);
            if (float.IsNaN(next) || next <= top) break;
            top = next;
        }
        return LayoutBlockBox(e, l, top, r - l, shrinkToFit: false);
    }

    /// <summary>A float laid out on its own, waiting to be placed.</summary>
    private sealed class PendingFloat
    {
        public Element Element = null!;
        public Captured Box = null!;
        public float CbW;
        public bool SizeOnly; // Box holds the size of an earlier measurement but no ops: it is laid out when placed
        public InitialLetterPlace? Letter; // an initial letter, placed against the first line instead
        public float Width => Box.Width;
    }

    /// <summary>The size of each float measured so far, by element and containing block width.</summary>
    private readonly Dictionary<(Element, float), (float Width, float Height, bool ForcedBreaks)> _floatSizes = new();

    private PendingFloat MeasureFloat(Element e, float cbW)
    {
        if (IsInitialLetter(e) && MeasureInitialLetter(e, cbW) is { } letter) return letter;
        // A float that is laid out again on the page, because it crosses a page boundary or forces a break, measures
        // the floats inside it again. Measuring them in full each time would lay out a float nested n deep n times,
        // once for each float around it, so a size measured before is reused and the float is only laid out when it
        // is placed. Measurement is unpaginated, so the same element in the same width always gets the same size.
        if (_paginate && _floatSizes.TryGetValue((e, cbW), out var size))
        {
            return new()
            {
                Element = e, CbW = cbW, SizeOnly = true,
                Box = new Captured { Ops = [], Width = size.Width, Height = size.Height, ForcedBreaks = size.ForcedBreaks },
            };
        }
        var c = CaptureBox(e, cbW, shrinkToFit: true);
        _floatSizes[(e, cbW)] = (c.Width, c.Height, c.ForcedBreaks);
        return new() { Element = e, CbW = cbW, Box = c };
    }

    /// <summary>
    /// Places a float as high as it can go, but not above <paramref name="y"/> nor above earlier floats, beside the floats
    /// already there within the containing block from <paramref name="cbX"/> to <paramref name="cbX"/> + <paramref name="cbW"/>.
    /// </summary>
    private void PlaceFloat(PendingFloat f, float y, float cbX, float cbW)
    {
        if (f.Letter != null)
        {
            PlaceInitialLetter(f, y, cbX, cbW, 0);
            return;
        }
        var e = f.Element;
        var s = e.Style;
        var c = f.Box;
        float fw = c.Width, fh = c.Height;
        bool right = s.Float == FloatSide.Right;
        float top = Math.Max(Cleared(s, y), _floats.MinTop);
        float l = cbX, r = cbX + cbW;
        for (int guard = 0; guard < 10_000; guard++)
        {
            (l, r) = _floats.Band(top, fh, cbX, cbX + cbW);
            if (r - l >= fw - Eps || l <= cbX + Eps && r >= cbX + cbW - Eps) break;
            float next = _floats.NextBottom(top, fh);
            if (float.IsNaN(next) || next <= top) break;
            top = next;
        }
        float x = right ? r - fw : l;

        int start = _ops.Count;
        float bottom;
        if (Reusable(c, top))
        {
            if (f.SizeOnly) c = CaptureBox(e, f.CbW, shrinkToFit: true);
            Place(c, x, top);
            bottom = top + fh;
        }
        else
        {
            // It crosses a page boundary: lay it out again in place so that its content paginates.
            float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(cbW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(cbW);
            float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(cbW), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(cbW);
            _widthOverride[e] = Math.Max(0, fw - ml - mr);
            try
            {
                bottom = LayoutBlockBox(e, x, top + mt, fw, shrinkToFit: false) + mb;
            }
            finally
            {
                _widthOverride.Remove(e);
            }
        }
        WrapLayer(start, LayerOp.FloatOrder, stackingContext: false);
        bottom = Math.Max(top, bottom);
        var shape = s.ShapeOutside != null ? FloatShapeOf(e, x, top, fw, bottom - top, f.CbW) : null;
        _floats.Add(new FloatBox { X0 = x, X1 = x + fw, Y0 = top, Y1 = bottom, Right = right, OpsIndex = start, Shape = shape });
    }

    /// <summary>
    /// Paints the lines laid out from op <paramref name="start"/> above the floats, as CSS paints inline content: lines
    /// beside a float with shape-outside may overlap the float's box where its shape leaves room. Floats and positioned
    /// boxes among them keep their own order. Only such paragraphs are lifted, so other pages paint as they did.
    /// </summary>
    private void LiftInlineContent(int start)
    {
        var lifted = new LayerOp { Order = LayerOp.InlineOverFloatsOrder };
        int keep = start;
        for (int i = start; i < _ops.Count; i++)
        {
            var op = _ops[i];
            if (op is LayerOp { Order: > 0 }) _ops[keep++] = op;
            else lifted.Ops.Add(op);
        }
        if (lifted.Ops.Count == 0) return;
        _ops.RemoveRange(keep, _ops.Count - keep);
        _ops.Add(lifted);
        _floats.Fold(start);
        _hasLayers = true;
    }

    /// <summary>Moves the ops from <paramref name="start"/> into one layer op.</summary>
    private LayerOp WrapLayer(int start, float order, bool stackingContext)
    {
        var layer = new LayerOp { Order = order, StackingContext = stackingContext };
        for (int i = start; i < _ops.Count; i++) layer.Ops.Add(_ops[i]);
        Truncate(start, keepFloats: true);
        _ops.Add(layer);
        _floats.Fold(start);
        _hasLayers = true;
        return layer;
    }

    // ------------------------------------------------------------------ lines beside floats

    /// <summary>
    /// Tracks where each line of a paragraph goes while the paragraph is broken into lines, so that each line gets the
    /// room the floats beside it leave, and floats met in the text are placed as the lines reach them.
    /// </summary>
    private sealed class FloatLines
    {
        public LayoutEngine Engine = null!;
        public ComputedStyle Block = null!;
        public float X, W;
        public float Y;         // top of the current line
        public float Left, Avail; // the current line's room, relative to X
        public float StrutH;
        public float LetterIndent; // the text-indent an initial letter takes from the first line
        public readonly List<PendingFloat> Waiting = new(); // floats that did not fit beside the current line

        // Pages of different widths: the block's content box on each kind of page, and the page the current line was
        // measured for. A line that ends up on a page with other geometry is reported in Moved (its index) and
        // MovedHeight, so that the paragraph can be broken again with HeightHints saying which lines are taller than
        // the line height. ForcedBefore holds the lines that start a new page to keep orphans and widows.
        public PageBox? Pages;
        public Dictionary<int, float>? HeightHints;
        public bool[]? ForcedBefore;
        public int Moved = -1;
        public float MovedHeight;
        private int _index, _measuredPage;

        /// <summary>The block's content box on the page the current line goes on.</summary>
        private (float X, float W) Box => Pages == null ? (X, W) : (Pages.X[PageBox.Kind(_measuredPage)], Pages.W[PageBox.Kind(_measuredPage)]);

        private void Measure()
        {
            if (Pages != null)
            {
                float h = StrutH;
                if (HeightHints != null && HeightHints.TryGetValue(_index, out float hint) && hint > h) h = hint;
                _measuredPage = Engine.PageOf(Engine.FitOnPage(Y, h));
            }
            var (bx, bw) = Box;
            var (l, r) = Engine._floats.LineBand(Y, StrutH, bx, bx + bw);
            Left = l - X;
            Avail = r - l;
        }

        /// <summary>Starts a line after <paramref name="lines"/>: places waiting floats and returns its room.</summary>
        public void Begin(List<Line> lines)
        {
            if (lines.Count > 0)
            {
                var last = lines[^1];
                Y = last.Top + LayoutEngine.LineBoxHeight(last, Block);
            }
            _index = lines.Count;
            _shapeSteps = 0;
            if (ForcedBefore != null && _index < ForcedBefore.Length && ForcedBefore[_index]) Y = Engine.NextPage(Y);
            if (Waiting.Count > 0)
            {
                if (Pages != null) Measure();
                var (bx, bw) = Box;
                foreach (var f in Waiting) Engine.PlaceFloat(f, Y, bx, bw);
                Waiting.Clear();
            }
            // The line after a br that clears floats starts below them.
            if (lines.Count > 0 && ClearingBreak(lines[^1]) is { } br) Y = Engine.Cleared(br, Y);
            Measure();
        }

        /// <summary>A float met in the text: placed beside the current line when it fits there, else after the line.</summary>
        public void Float(Item it, float used, bool hasContent)
        {
            var (bx, bw) = Box;
            var f = Engine.MeasureFloat(it.Element!, bw);
            if (f.Letter != null)
            {
                // The first line starts below an earlier paragraph's initial letter, and a raised letter moves its text down.
                Y = Math.Max(Y, Engine._floats.InitialLetterBottom());
                Engine.PlaceInitialLetter(f, Y, bx, bw, LetterIndent);
                Y += f.Letter.Shift;
                Measure();
                return;
            }
            if (hasContent && (Waiting.Count > 0 || f.Width > Avail - used + Eps))
            {
                Waiting.Add(f);
                return;
            }
            Engine.PlaceFloat(f, Y, bx, bw);
            Measure();
        }

        /// <summary>
        /// Nothing fits beside the floats here: moves the line down past the next float, or beside a float with
        /// shape-outside, one CSS pixel at a time as Chrome does, as the shape may leave more room a little lower. False
        /// when no float is in the way.
        /// </summary>
        public bool Lower()
        {
            float next = Engine._floats.NextBottom(Y, StrutH);
            if (float.IsNaN(next) || next <= Y) return false;
            if (_shapeSteps < MaxShapeSteps && next > Y + ShapeStep && Engine._floats.ShapeBeside(Y, StrutH))
            {
                _shapeSteps++;
                next = Y + ShapeStep;
            }
            Y = next;
            Measure();
            return true;
        }

        // Moving down beside shapes a pixel at a time is bounded per line; past that the line goes below the floats.
        private const float ShapeStep = 0.75f;
        private const int MaxShapeSteps = 4000;
        private int _shapeSteps;

        /// <summary>Fixes the position of a finished line.</summary>
        public void End(Line line)
        {
            float h = LayoutEngine.LineBoxHeight(line, Block);
            line.Top = h > 0 ? Engine.FitOnPage(Y, h) : Y;
            line.Left = Left;
            line.Avail = Avail;
            if (Pages != null && Moved < 0 && h > 0 && !Pages.Same(Engine.PageOf(line.Top), _measuredPage))
            {
                Moved = _index;
                MovedHeight = h;
            }
        }

        /// <summary>Places the floats still waiting once the last line is done.</summary>
        public void Flush(List<Line> lines)
        {
            if (Waiting.Count == 0) return;
            if (lines.Count > 0) Y = lines[^1].Top + LayoutEngine.LineBoxHeight(lines[^1], Block);
            _index = lines.Count;
            if (Pages != null) Measure();
            var (bx, bw) = Box;
            foreach (var f in Waiting) Engine.PlaceFloat(f, Y, bx, bw);
            Waiting.Clear();
        }
    }

    /// <summary>The height of a line box: its tallest ascent plus its deepest descent, 0 for a line of anchors only.</summary>
    private static float LineBoxHeight(Line line, ComputedStyle block)
    {
        LineExtent(line, block, out float a, out float d);
        return float.IsNaN(a) ? 0 : a + d;
    }

    private static void LineExtent(Line line, ComputedStyle block, out float maxA, out float maxD)
    {
        StrutMetrics(block, out maxA, out maxD);
        bool visible = false, lineAligned = false;
        // The words of a line mostly share a style, whose metrics are found once.
        var measured = block;
        float measuredA = maxA, measuredD = maxD;
        foreach (var it in line.Items)
        {
            float a, d;
            switch (it.Kind)
            {
                case ItemKind.Anchor:
                case ItemKind.Float:
                case ItemKind.Absolute:
                case ItemKind.Bidi:
                    continue;
                case ItemKind.Image:
                case ItemKind.Box:
                    if (IsLineAligned(it))
                    {
                        // Placed against the line's top or bottom once the rest of the line is measured.
                        lineAligned = visible = true;
                        continue;
                    }
                    AtomicMetrics(it, block, out a, out d);
                    break;
                default:
                    if (IsLineAlignedText(it.Style))
                    {
                        lineAligned = visible = true;
                        continue;
                    }
                    if (!ReferenceEquals(it.Style, measured))
                    {
                        measured = it.Style;
                        TextMetrics(measured, out measuredA, out measuredD);
                    }
                    a = measuredA;
                    d = measuredD;
                    break;
            }
            visible = true;
            if (a > maxA) maxA = a;
            if (d > maxD) maxD = d;
        }
        if (lineAligned)
        {
            // A box aligned to the top hangs down from the line's top and one aligned to the bottom stands on its
            // bottom; a box taller than the line makes it deeper or higher, as in Chrome.
            foreach (var it in line.Items)
            {
                if (!IsLineAligned(it)) continue;
                float h = LineAlignedHeight(it);
                if (h <= maxA + maxD) continue;
                if (it.Style.VerticalAlign == VerticalAlign.Top) maxD = h - maxA;
                else maxA = h - maxD;
            }
        }
        if (!visible) maxA = float.NaN;
    }
}
