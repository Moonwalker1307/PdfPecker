namespace PdfPecker.Layout;

/// <summary>
/// Puts the backgrounds and borders of block boxes in normal flow under the inline content before them, as CSS 2.1
/// Appendix E paints them: all block backgrounds of a stacking context first, then floats, then the lines. The layout
/// makes ops in tree order, so a block's background follows the text of the blocks before it and would cover any of
/// that text that reaches into it (a negative margin, a line taller than its box). A background moves back only past
/// ops it overlaps, which keeps the output of pages where nothing overlaps as it was.
/// </summary>
internal static class PaintOrder
{
    /// <summary>
    /// How many ops before a background are looked at. Overlapping content is nearly always close before it in tree
    /// order, and the bound keeps the pass linear in long documents.
    /// </summary>
    private const int Window = 256;

    /// <summary>Reorders <paramref name="ops"/> and the lists of the clips and layers in it, leaving the first <paramref name="pinned"/> in place.</summary>
    public static void BackgroundsFirst(List<DrawOp> ops, int pinned = 0)
    {
        bool any = false;
        foreach (var op in ops)
        {
            if (op is ClipOp clip) BackgroundsFirst(clip.Ops);
            else if (op is LayerOp layer) BackgroundsFirst(layer.Ops, layer.StackingContext ? 1 : 0);
            else if (op is GroupOp { BlockBackground: true }) any = true;
        }
        if (!any || ops.Count - pinned < 2) return;
        Rect?[]? boxes = null;
        for (int i = pinned + 1; i < ops.Count; i++)
        {
            if (ops[i] is not GroupOp { BlockBackground: true } group || group.Ops.Count == 0) continue;
            boxes ??= new Rect?[ops.Count];
            var b = BoxOf(ops, boxes, i);
            if (b.Empty) continue;
            int target = i;
            for (int j = i - 1; j >= pinned && i - j <= Window; j--)
            {
                var o = ops[j];
                var ob = BoxOf(ops, boxes, j);
                // Backgrounds keep their order among themselves where they overlap, and nothing passes what the pass
                // cannot see into, such as a turned layer or one painted at another level.
                if (ob.Unknown) break;
                bool overlaps = ob.Overlaps(b);
                if (o is GroupOp { BlockBackground: true })
                {
                    if (overlaps) break;
                    continue;
                }
                if (overlaps) target = j;
            }
            if (target == i) continue;
            var moved = ops[i];
            ops.RemoveAt(i);
            ops.Insert(target, moved);
            Array.Copy(boxes, target, boxes, target + 1, i - target);
            boxes[target] = b;
        }
    }

    private static Rect BoxOf(List<DrawOp> ops, Rect?[] boxes, int i)
    {
        if (boxes[i] is { } known) return known;
        var r = Rect.None;
        Add(ops[i], ref r, 0);
        boxes[i] = r;
        return r;
    }

    /// <summary>Grows <paramref name="r"/> to take in what <paramref name="op"/> paints, in flow coordinates.</summary>
    private static void Add(DrawOp op, ref Rect r, int depth)
    {
        if (r.Unknown) return;
        switch (op)
        {
            case RectOp o: r.Add(o.X, o.Y, o.W, o.H); break;
            case BorderRectOp o: r.Add(o.X, o.Y, o.W, o.H); break;
            case RoundRectOp o: r.Add(o.X - o.StrokeWidth / 2, o.Y - o.StrokeWidth / 2, o.W + o.StrokeWidth, o.H + o.StrokeWidth); break;
            case PathOp o: r.Add(o.X0 - o.StrokeWidth / 2, o.Y0 - o.StrokeWidth / 2, o.X1 - o.X0 + o.StrokeWidth, o.Y1 - o.Y0 + o.StrokeWidth); break;
            case BackgroundOp o:
            {
                var (x0, y0, x1, y1) = o.Painted();
                r.Add(x0, y0, x1 - x0, y1 - y0);
                break;
            }
            case LineOp o:
                r.Add(Math.Min(o.X1, o.X2) - o.Width, Math.Min(o.Y1, o.Y2) - o.Width, Math.Abs(o.X2 - o.X1) + 2 * o.Width,
                    Math.Abs(o.Y2 - o.Y1) + 2 * o.Width);
                break;
            case TextOp t:
            {
                // The glyphs' content area, which is what a background painted after them would cover.
                if (t.Font == null) { r.Unknown = true; break; }
                float width = t.Font.Measure(t.Text, t.Size) + Math.Abs(t.CharSpacing) * t.Text.Length + Math.Abs(t.WordSpacing) * 8;
                float above = t.Font.Ascent * t.Size, below = -t.Font.Descent * t.Size;
                if (t.UpsideDown) (above, below) = (below, above);
                r.Add(t.X, t.Baseline - above, width, above + below);
                break;
            }
            case ImageOp o: r.Add(o.X, o.Y, o.W, o.H); break;
            case SvgOp o: r.Add(o.X, o.Y, o.W, o.H); break;
            case CircleOp o: r.Add(o.Cx - o.R - 1, o.Cy - o.R - 1, 2 * o.R + 2, 2 * o.R + 2); break;
            case LinkOp: break;
            case ClipOp clip:
            {
                var inner = Rect.None;
                foreach (var o in clip.Ops) Add(o, ref inner, depth + 1);
                if (inner.Unknown) { r.Unknown = true; break; }
                inner.Clip(clip.X, clip.Y, clip.X + clip.W, clip.Y + clip.H);
                r.Union(inner);
                break;
            }
            case LayerOp layer:
                // Only a layer painted with the normal flow, untransformed and unfiltered, is looked into.
                if (layer.Order != 0 || layer.NeedsBlock || depth > 8) r.Unknown = true;
                else foreach (var o in layer.Ops) Add(o, ref r, depth + 1);
                break;
            case GroupOp g:
                if (depth > 8) r.Unknown = true;
                else foreach (var o in g.Ops) Add(o, ref r, depth + 1);
                break;
            default: r.Unknown = true; break;
        }
    }

    private struct Rect
    {
        public float X0, Y0, X1, Y1;
        public bool Unknown;

        public static Rect None => new() { X0 = float.MaxValue, Y0 = float.MaxValue, X1 = float.MinValue, Y1 = float.MinValue };

        public readonly bool Empty => !(X1 > X0 && Y1 > Y0);

        public void Add(float x, float y, float w, float h)
        {
            if (!(w > 0 && h > 0)) return;
            X0 = Math.Min(X0, x); Y0 = Math.Min(Y0, y);
            X1 = Math.Max(X1, x + w); Y1 = Math.Max(Y1, y + h);
        }

        public void Union(Rect o)
        {
            if (o.Empty) return;
            Add(o.X0, o.Y0, o.X1 - o.X0, o.Y1 - o.Y0);
        }

        public void Clip(float x0, float y0, float x1, float y1)
        {
            X0 = Math.Max(X0, x0); Y0 = Math.Max(Y0, y0);
            X1 = Math.Min(X1, x1); Y1 = Math.Min(Y1, y1);
        }

        /// <summary>Whether the two share an area; an unknown box overlaps everything.</summary>
        public readonly bool Overlaps(Rect o) => Unknown || o.Unknown || !Empty && !o.Empty && X0 < o.X1 && o.X0 < X1 && Y0 < o.Y1 && o.Y0 < Y1;
    }
}
