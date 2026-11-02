namespace PdfPecker.Layout;

/// <summary>
/// continue: discard (CSS Overflow 4). The box is a fragmentation container of its own: its content fills its first
/// fragment and what does not fit is left out, rather than continuing on the next page. The fragment ends at the box's
/// height (or max-height) when it has one, and at the end of the page the box starts on, whichever comes first.
/// </summary>
/// <remarks>
/// On pages the end becomes a break the content honours, kept as room at the end of the page as the room for a box's
/// bottom edges is: lines, images and table rows that would cross it go past it whole, as they would go to the next
/// page, and everything that went past it is then dropped. In columns and in running headers and footers, which do not
/// break that way, the content is cut off at the end instead, and only what starts past it is dropped.
/// </remarks>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// Where the content of a box with continue: discard whose content box starts at <paramref name="cy"/> ends: after
    /// <paramref name="height"/> (infinity when the box has none), and on pages no lower than leaves room for its
    /// bottom padding and border, <paramref name="bottomEdges"/>, on the page it starts on. On pages it keeps room at
    /// the end of that page so that the content breaks there, and gives the page and the room kept before, which the
    /// caller restores once the content is laid out; <paramref name="page"/> is -1 when no room was kept.
    /// </summary>
    private float DiscardEdge(float cy, float height, float bottomEdges, out int page, out float reserveBefore)
    {
        page = -1;
        reserveBefore = 0;
        float end = cy + Math.Max(0, height);
        if (!_paginate || _grid is ColumnGrid) return end;
        int p = PageOf(cy);
        float flowEnd = FlowEnd(p);
        end = Math.Min(end, Math.Max(cy, flowEnd - bottomEdges));
        page = p;
        _edgeReserve.TryGetValue(p, out reserveBefore);
        if (flowEnd - end > 0) _edgeReserve[p] = reserveBefore + (flowEnd - end);
        return end;
    }

    /// <summary>Whether any content laid out from <paramref name="start"/> starts above <paramref name="end"/>.</summary>
    private bool KeepsContentAbove(int start, float end)
    {
        for (int i = start; i < _ops.Count; i++)
            if (FirstContentY(_ops[i]) is { } fy && fy < end - Eps) return true;
        return false;
    }

    /// <summary>
    /// Leaves out the content of a box from op <paramref name="start"/> on that is past <paramref name="end"/>: what
    /// starts there is dropped, and what reaches past it, such as the background of a box inside, is cut off there.
    /// </summary>
    private void Discard(int start, float top, float end)
    {
        if (_ops.Count <= start) return;
        // The clip reaches well above the box, whose content can stick out at the top, but not so far that its bottom
        // loses precision.
        float clipTop = Math.Min(top, end) - 10000;
        var clip = new ClipOp(-ClipOp.Far, clipTop, 2 * ClipOp.Far, end - clipTop, null);
        for (int i = start; i < _ops.Count; i++)
            if (StartsAbove(_ops[i], end)) clip.Ops.Add(_ops[i]);
        Truncate(start, keepFloats: true);
        _ops.Add(clip);
        _floats.Fold(start);
    }

    /// <summary>
    /// Whether <paramref name="op"/> starts above <paramref name="end"/> and so stays. A group keeps the ops inside it
    /// that do, and goes when none does. A link is cut to end there too, since the clip does not reach annotations, and
    /// a form field, which is an annotation too, stays only when all of it does.
    /// </summary>
    private static bool StartsAbove(DrawOp op, float end)
    {
        end -= Eps;
        switch (op)
        {
            case TextOp t: return t.LineTop < end && t.Baseline < end;
            case ImageOp im: return im.Y < end;
            case SvgOp sv: return sv.Y < end;
            case RectOp r: return r.Y < end;
            case BorderRectOp b: return b.Y < end;
            case RoundRectOp rr: return rr.Y < end;
            case BackgroundOp bg: return bg.ClipY < end;
            case LineOp ln: return Math.Min(ln.Y1, ln.Y2) < end;
            case CircleOp c: return c.Cy - c.R < end;
            case LinkOp link:
                if (link.Y >= end) return false;
                link.H = Math.Min(link.H, end + Eps - link.Y);
                return true;
            case FieldOp f: return f.Y + f.H <= end + 2 * Eps;
            case LayerOp layer:
                // A layer may be transformed, so what is inside it stays or goes with it.
                foreach (var inner in layer.Ops)
                    if (FirstContentY(inner) is { } fy) return fy < end;
                return layer.Ops.Count > 0;
            case GroupOp g:
                if (g.Ops.Count == 0) return true;
                g.Ops.RemoveAll(inner => !StartsAbove(inner, end + Eps));
                return g.Ops.Count > 0;
            default: return true;
        }
    }
}
