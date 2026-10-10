using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;

namespace PdfPecker.Layout;

/// <summary>
/// Lays out the styled tree into a flat list of <see cref="DrawOp"/>s. Pagination happens during layout:
/// atomic pieces (lines, images, table rows) that would straddle a page boundary are pushed to the next page.
/// </summary>
internal sealed partial class LayoutEngine
{
    private readonly float _pageH;
    private readonly ImageLoader _images;
    private List<DrawOp> _ops = new(256);
    private bool _paginate = true;
    private ListMarker? _marker;
    private int _currentListIndex;
    private float _lastBaseline = float.NaN;
    private float _lastBoxRight; // right border edge (plus nothing else) of the last block box laid out
    private readonly Dictionary<Element, (float Min, float Max)> _intrinsicCache = new();

    public readonly Dictionary<string, float> Anchors = new(StringComparer.Ordinal);

    public LayoutEngine(float pageContentHeight, ImageLoader images)
    {
        _pageH = pageContentHeight;
        _images = images;
    }

    public List<DrawOp> Ops => _ops;

    private sealed class ListMarker
    {
        public string? Text;
        public ListStyleType Type;
        public ComputedStyle Style = null!;
        public float ContentX;
    }

    public float Layout(Element document, float width)
        => LayoutFlow(document, 0, 0, width);

    // ------------------------------------------------------------------ pagination helpers

    private const float Eps = 0.01f;

    private int PageOf(float y) => (int)Math.Floor((y + Eps) / _pageH);

    private bool AtPageTop(float y) => y - PageOf(y) * _pageH < 0.5f;

    /// <summary>Returns the y at which a box of height h should start so that it does not straddle a page boundary.</summary>
    private float FitOnPage(float y, float h)
    {
        if (!_paginate || h > _pageH) return y;
        int p = PageOf(y);
        float pageEnd = (p + 1) * _pageH;
        return y + h > pageEnd + Eps ? pageEnd : y;
    }

    private float NextPage(float y)
    {
        if (!_paginate || AtPageTop(y)) return y;
        return (PageOf(y) + 1) * _pageH;
    }

    private bool CrossesPage(float top, float bottom) => _paginate && bottom - top > Eps && PageOf(top) != PageOf(bottom - 0.5f);

    private void Truncate(int count)
    {
        if (_ops.Count > count) _ops.RemoveRange(count, _ops.Count - count);
    }

    // ------------------------------------------------------------------ block formatting

    private static bool IsInlineLevel(Element e)
    {
        var d = e.Style.Display;
        if (d == Display.InlineBlock) return true;
        if (d != Display.Inline) return false;
        return !ContainsBlock(e);
    }

    private static bool ContainsBlock(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce || ce.Style == null) continue;
            var d = ce.Style.Display;
            if (d == Display.None || d == Display.InlineBlock) continue;
            if (d != Display.Inline) return true;
            if (ContainsBlock(ce)) return true;
        }
        return false;
    }

    private static float CollapseMargins(float a, float b)
    {
        if (a >= 0 && b >= 0) return Math.Max(a, b);
        if (a < 0 && b < 0) return Math.Min(a, b);
        return a + b;
    }

    /// <summary>Lays out the children of a block container. Returns the bottom of the content (including the last child's bottom margin).</summary>
    private float LayoutFlow(Element parent, float x, float y, float w)
    {
        float pendingMargin = 0;
        bool anyContent = false;
        List<Node>? run = null;
        var children = parent.Children;

        int listIndex = 0;
        if (parent.Name == "ol" && int.TryParse(parent.GetAttribute("start"), out int start)) listIndex = start - 1;

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child is TextNode)
            {
                (run ??= new List<Node>()).Add(child);
                continue;
            }
            var e = (Element)child;
            var s = e.Style;
            if (s == null || s.Display == Display.None) continue;
            if (IsInlineLevel(e) && !(s.Floated && e.Name != "img"))
            {
                (run ??= new List<Node>()).Add(child);
                continue;
            }

            if (run is { Count: > 0 })
            {
                float before = y;
                y = FlushInline(run, parent, x, y + pendingMargin, w, !anyContent, out bool produced);
                if (produced) { pendingMargin = 0; anyContent = true; }
                else y = before;
                run.Clear();
            }

            if (s.Display == Display.ListItem)
            {
                listIndex++;
                if (int.TryParse(e.GetAttribute("value"), out int v)) listIndex = v;
                _currentListIndex = listIndex;
            }

            float mt = s.MarginTop.Resolve(w);
            float top = y + CollapseMargins(pendingMargin, mt);
            if (s.PageBreakBefore) top = NextPage(top);

            float bottom = LayoutBlockBox(e, x, top, w, shrinkToFit: false);
            anyContent = true;
            pendingMargin = s.MarginBottom.Resolve(w);
            y = bottom;
            if (s.PageBreakAfter)
            {
                y = NextPage(y + pendingMargin);
                pendingMargin = 0;
            }
        }

        if (run is { Count: > 0 })
        {
            float yy = FlushInline(run, parent, x, y + pendingMargin, w, !anyContent, out bool produced);
            if (produced) { y = yy; pendingMargin = 0; }
        }
        return y + pendingMargin;
    }

    /// <summary>Lays out a block-level box whose top border edge is at <paramref name="y"/>. Returns its bottom border edge.</summary>
    private float LayoutBlockBox(Element e, float x, float y, float availW, bool shrinkToFit)
    {
        var s = e.Style;
        if (s.Display == Display.Table) return LayoutTable(e, x, y, availW);

        int opsStart = _ops.Count;
        var savedMarker = _marker;
        float bottom = LayoutBlockBoxCore(e, x, y, availW, shrinkToFit);
        if (s.AvoidBreakInside && CrossesPage(y, bottom) && bottom - y <= _pageH && !AtPageTop(y))
        {
            Truncate(opsStart);
            _marker = savedMarker;
            float ny = NextPage(y);
            bottom = LayoutBlockBoxCore(e, x, ny, availW, shrinkToFit);
        }
        return bottom;
    }

    private float LayoutBlockBoxCore(Element e, float x, float y, float availW, bool shrinkToFit)
    {
        var s = e.Style;
        if (e.Id != null) Anchors[e.Id] = y;
        if (e.Name == "a" && e.GetAttribute("name") is { } aname) Anchors[aname] = y;

        float ml = s.MarginLeft.Resolve(availW), mr = s.MarginRight.Resolve(availW);
        float bl = s.BorderLeft, br = s.BorderRight, bt = s.BorderTop, bb = s.BorderBottom;
        float pl = s.PaddingLeft.Resolve(availW), pr = s.PaddingRight.Resolve(availW);
        float pt = s.PaddingTop.Resolve(availW), pb = s.PaddingBottom.Resolve(availW);
        float frame = bl + br + pl + pr;

        bool isImage = e.Name == "img";
        float imgW = 0, imgH = 0;
        PdfImage? image = null;
        float contentW;
        if (isImage)
        {
            image = ImageSize(e, availW - ml - mr - frame, out imgW, out imgH);
            contentW = imgW;
        }
        else if (!s.Width.IsAuto)
        {
            contentW = s.Width.Resolve(availW);
            if (s.BorderBox) contentW -= frame;
        }
        else
        {
            contentW = availW - ml - mr - frame;
            if (shrinkToFit)
            {
                var (_, max) = Intrinsic(e);
                contentW = Math.Min(contentW, max - frame);
            }
        }
        if (!s.MaxWidth.IsAuto && !isImage)
        {
            float mw = s.MaxWidth.Resolve(availW) - (s.BorderBox ? frame : 0);
            if (contentW > mw) contentW = mw;
        }
        if (!s.MinWidth.IsAuto && !isImage)
        {
            float mnw = s.MinWidth.Resolve(availW) - (s.BorderBox ? frame : 0);
            if (contentW < mnw) contentW = mnw;
        }
        if (contentW < 0) contentW = 0;

        float used = contentW + frame;
        if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto) ml = Math.Max(0, (availW - used) / 2);
        else if (s.MarginLeft.IsAuto) ml = Math.Max(0, availW - used - mr);
        float bx = x + ml;

        var group = new GroupOp();
        _ops.Add(group);

        float cx = bx + bl + pl;
        float cy = y + bt + pt;

        if (s.Display == Display.ListItem && s.ListStyleType != ListStyleType.None)
            _marker = new ListMarker { Text = MarkerText(s.ListStyleType, _currentListIndex), Type = s.ListStyleType, Style = s, ContentX = cx };

        float contentBottom;
        if (isImage)
        {
            if (_paginate)
            {
                float fitted = FitOnPage(y, imgH + bt + bb + pt + pb);
                if (fitted != y)
                {
                    y = fitted;
                    cy = y + bt + pt;
                }
            }
            if (image != null && s.Visible) _ops.Add(new ImageOp(cx, cy, imgW, imgH, image));
            contentBottom = cy + imgH;
        }
        else if (e.Name == "hr")
        {
            contentBottom = cy;
        }
        else
        {
            contentBottom = LayoutFlow(e, cx, cy, contentW);
        }

        if (_marker != null && _marker.Style == s)
        {
            // List item without any line of text (e.g. it only holds a nested list): put the marker at the top.
            EmitMarker(cy + s.Font.Ascent * s.FontSize, cy);
        }

        float contentH = contentBottom - cy;
        if (!s.Height.IsAuto && !s.Height.IsPercent)
        {
            float h = s.Height.Value - (s.BorderBox ? bt + bb + pt + pb : 0);
            contentH = Math.Max(0, h);
        }
        if (!s.MinHeight.IsAuto && !s.MinHeight.IsPercent)
        {
            float mh = s.MinHeight.Value - (s.BorderBox ? bt + bb + pt + pb : 0);
            if (contentH < mh) contentH = mh;
        }

        float bottom = cy + contentH + pb + bb;
        BoxPainter.Paint(group, s, bx, y, used, bottom - y);
        _lastBoxRight = bx + used;
        return bottom;
    }

    // ------------------------------------------------------------------ list markers

    private static string? MarkerText(ListStyleType type, int index)
        => type switch
        {
            ListStyleType.Decimal => index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
            ListStyleType.DecimalLeadingZero => index.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".",
            ListStyleType.LowerAlpha => Alpha(index, 'a') + ".",
            ListStyleType.UpperAlpha => Alpha(index, 'A') + ".",
            ListStyleType.LowerRoman => Roman(index).ToLowerInvariant() + ".",
            ListStyleType.UpperRoman => Roman(index) + ".",
            _ => null,
        };

    private static string Alpha(int n, char a)
    {
        if (n <= 0) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var s = "";
        while (n > 0)
        {
            n--;
            s = (char)(a + n % 26) + s;
            n /= 26;
        }
        return s;
    }

    private static string Roman(int n)
    {
        if (n <= 0 || n >= 4000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] numerals = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
            while (n >= values[i]) { sb.Append(numerals[i]); n -= values[i]; }
        return sb.ToString();
    }

    private void EmitMarker(float baseline, float lineTop)
    {
        var m = _marker!;
        _marker = null;
        var s = m.Style;
        if (!s.Visible) return;
        float size = s.FontSize;
        if (m.Text != null)
        {
            var font = s.Font;
            float tw = font.Measure(m.Text, size);
            float gap = size * 0.4f;
            _ops.Add(new TextOp(m.ContentX - gap - tw, baseline, lineTop, font, size, s.Color, m.Text, 0, 0));
            return;
        }
        float r = size * 0.17f;
        float cxm = m.ContentX - size * 0.75f;
        float cym = baseline - size * 0.27f;
        switch (m.Type)
        {
            case ListStyleType.Disc:
                _ops.Add(new CircleOp(cxm, cym, r, s.Color, filled: true));
                break;
            case ListStyleType.Circle:
                _ops.Add(new CircleOp(cxm, cym, r, s.Color, filled: false));
                break;
            case ListStyleType.Square:
                _ops.Add(new RectOp(cxm - r, cym - r, 2 * r, 2 * r, s.Color));
                break;
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>Computes the used size of an image in points. Returns the decoded image, or null when it could not be loaded.</summary>
    private PdfImage? ImageSize(Element e, float availW, out float w, out float h)
    {
        var s = e.Style;
        var img = _images.Load(e.GetAttribute("src"));
        float iw = img != null ? img.Width * 0.75f : 0, ih = img != null ? img.Height * 0.75f : 0;
        bool hasW = !s.Width.IsAuto && !(_measuring && s.Width.IsPercent), hasH = !s.Height.IsAuto && !s.Height.IsPercent;
        w = hasW ? s.Width.Resolve(Math.Max(availW, 0)) : 0;
        h = hasH ? s.Height.Value : 0;
        if (s.BorderBox)
        {
            if (hasW) w -= s.PaddingLeft.Resolve(availW) + s.PaddingRight.Resolve(availW) + s.BorderLeft + s.BorderRight;
            if (hasH) h -= s.PaddingTop.Resolve(availW) + s.PaddingBottom.Resolve(availW) + s.BorderTop + s.BorderBottom;
        }
        if (hasW && !hasH) h = iw > 0 ? w * ih / iw : 0;
        else if (!hasW && hasH) w = ih > 0 ? h * iw / ih : 0;
        else if (!hasW && !hasH) { w = iw; h = ih; }

        if (!s.MaxWidth.IsAuto)
        {
            float mw = s.MaxWidth.Resolve(availW);
            if (w > mw && w > 0) { h *= mw / w; w = mw; }
        }
        if (!s.MaxHeight.IsAuto && !s.MaxHeight.IsPercent && h > s.MaxHeight.Value && h > 0)
        {
            w *= s.MaxHeight.Value / h;
            h = s.MaxHeight.Value;
        }
        // Never let an image run off the page.
        if (availW > 0 && w > availW) { h *= availW / w; w = availW; }
        if (_paginate && h > _pageH) { w *= _pageH / h; h = _pageH; }
        if (w < 0) w = 0;
        if (h < 0) h = 0;
        return img;
    }
}
