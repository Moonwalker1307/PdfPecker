using System.Globalization;
using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;

namespace PdfPecker.Layout;

/// <summary>
/// The page-margin boxes of CSS Paged Media (<c>@page { @top-center { content: "Page " counter(page) } }</c>): up to
/// sixteen boxes around the page area, chosen per page by the @page rules that apply to it (<c>:first</c>,
/// <c>:left</c>, <c>:right</c>), sized as the spec distributes the space and laid out like any block.
/// </summary>
internal sealed class PageMarginBoxes
{
    private readonly List<PageRule> _rules;
    private readonly Func<ComputedStyle> _initial;
    private PageSetup _page; // the setup of the page being laid out
    private readonly ImageLoader _images;
    private readonly CancellationToken _cancellation;
    private readonly PagedContent? _paged;
    private readonly CounterStyles _counterStyles;

    // initial makes a style with initial values: margin boxes inherit from the page, not from the document.
    // paged gives string() the running strings.
    public PageMarginBoxes(List<PageRule> rules, Func<ComputedStyle> initial, PageSetup page, ImageLoader images, CancellationToken cancellation,
        PagedContent? paged = null, CounterStyles? counterStyles = null)
    {
        _paged = paged;
        _counterStyles = counterStyles ?? CounterStyles.Predefined;
        // Rules of lower specificity first, in source order otherwise: later declarations win.
        _rules = rules.Where(r => r.MarginBoxes.Count > 0 || r.Declarations.Count > 0)
            .Select((r, i) => (r, i)).OrderBy(x => x.r.Specificity).ThenBy(x => x.i).Select(x => x.r).ToList();
        _initial = initial;
        _page = page;
        _images = images;
        _cancellation = cancellation;
    }

    /// <summary>
    /// The first page is a left page: the document's root element is right to left, so its pages progress from right to
    /// left (CSS Paged Media 3, 4.2).
    /// </summary>
    public bool FirstPageLeft { get; init; }

    /// <summary>True when some @page rule has a margin box.</summary>
    public static bool AnyIn(List<PageRule> rules) => rules.Any(r => r.MarginBoxes.Count > 0);

    private enum Edge : byte { Top, Bottom, Left, Right }

    private static readonly (string Name, Edge Edge, int Slot)[] Boxes =
    {
        ("top-left", Edge.Top, 0), ("top-center", Edge.Top, 1), ("top-right", Edge.Top, 2),
        ("bottom-left", Edge.Bottom, 0), ("bottom-center", Edge.Bottom, 1), ("bottom-right", Edge.Bottom, 2),
        ("left-top", Edge.Left, 0), ("left-middle", Edge.Left, 1), ("left-bottom", Edge.Left, 2),
        ("right-top", Edge.Right, 0), ("right-middle", Edge.Right, 1), ("right-bottom", Edge.Right, 2),
    };

    /// <summary>The margin boxes of one page, as draw ops with y from the top of the page area and x from its left edge.</summary>
    public List<DrawOp> Layout(int pageIndex, int pageCount) => Layout(pageIndex, pageCount, _page, null);

    /// <summary>The margin boxes of a page with its own setup: a named page's (<paramref name="pageName"/>).</summary>
    public List<DrawOp> Layout(int pageIndex, int pageCount, PageSetup page, string? pageName)
    {
        _page = page;
        if (_paged != null) _paged.CurrentPage = pageIndex;
        var ops = new List<DrawOp>();
        // The page context: declarations of the @page rules that apply, and each margin box's declarations.
        var context = new List<Declaration>();
        var boxes = new Dictionary<string, List<Declaration>>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            if (!Applies(rule, pageIndex, pageName, FirstPageLeft)) continue;
            context.AddRange(rule.Declarations);
            foreach (var (name, decls) in rule.MarginBoxes)
            {
                if (!boxes.TryGetValue(name, out var list)) boxes[name] = list = new List<Declaration>();
                list.AddRange(decls);
            }
        }
        if (boxes.Count == 0) return ops;

        float W = _page.Width, H = _page.Height;
        float mt = _page.MarginTop, mr = _page.MarginRight, mb = _page.MarginBottom, ml = _page.MarginLeft;

        // Corners: the size of the margins they sit between.
        Corner("top-left-corner", 0, 0, ml, mt);
        Corner("top-right-corner", W - mr, 0, mr, mt);
        Corner("bottom-left-corner", 0, H - mb, ml, mb);
        Corner("bottom-right-corner", W - mr, H - mb, mr, mb);

        foreach (var edge in new[] { Edge.Top, Edge.Bottom, Edge.Left, Edge.Right })
        {
            var names = Boxes.Where(b => b.Edge == edge).Select(b => b.Name).ToArray();
            var generated = new Built?[3];
            bool horizontal = edge is Edge.Top or Edge.Bottom;
            float thickness = edge switch { Edge.Top => mt, Edge.Bottom => mb, Edge.Left => ml, _ => mr };
            float available = horizontal ? W - ml - mr : H - mt - mb;
            if (thickness <= 0 || available <= 0) continue;
            var sizes = new float[3];
            for (int k = 0; k < 3; k++)
            {
                if (!boxes.TryGetValue(names[k], out var decls)) continue;
                generated[k] = Build(names[k], decls, context, pageIndex, pageCount, horizontal ? null : thickness, horizontal ? thickness : null);
                if (generated[k] is { } g) sizes[k] = horizontal ? g.MaxContentWidth : g.MeasuredHeight;
            }
            if (generated.All(g => g == null)) continue;
            var (a, b, c) = Distribute(sizes, generated[0] != null, generated[1] != null, generated[2] != null, available);
            float[] extent = { a, b, c };
            float[] offset = { 0, (available - b) / 2, available - c };
            for (int k = 0; k < 3; k++)
            {
                if (generated[k] == null || extent[k] <= 0) continue;
                float x, y, w, h;
                if (horizontal)
                {
                    x = ml + offset[k]; w = extent[k];
                    y = edge == Edge.Top ? 0 : H - mb; h = thickness;
                }
                else
                {
                    y = mt + offset[k]; h = extent[k];
                    x = edge == Edge.Left ? 0 : W - mr; w = thickness;
                }
                Place(names[k], boxes[names[k]], context, pageIndex, pageCount, x, y, w, h);
            }
        }
        return ops;

        void Corner(string name, float x, float y, float w, float h)
        {
            if (w > 0 && h > 0 && boxes.TryGetValue(name, out var decls)) Place(name, decls, context, pageIndex, pageCount, x, y, w, h);
        }

        void Place(string name, List<Declaration> decls, List<Declaration> ctx, int page, int count, float x, float y, float w, float h)
        {
            var box = Build(name, decls, ctx, page, count, w, h);
            if (box == null) return;
            var engine = new LayoutEngine(Math.Max(h, 1), _images) { Cancellation = _cancellation };
            engine.LayoutBand(box.Element, w);
            foreach (var op in engine.Ops)
            {
                op.Offset(x - _page.MarginLeft, y - _page.MarginTop);
                ops.Add(op);
            }
        }
    }

    /// <summary>
    /// Whether an @page rule's selector matches the page: its page name, if any, and :first (the document's first
    /// page, as in Chrome), :left or :right (the first page is a right page, or a left page in a right-to-left document).
    /// </summary>
    private static bool Applies(PageRule rule, int pageIndex, string? pageName, bool firstPageLeft)
        => (rule.Name.Length == 0 || rule.Name == pageName) && rule.Selector switch
    {
        "" => true,
        ":first" => pageIndex == 0,
        ":right" => pageIndex % 2 == (firstPageLeft ? 1 : 0),
        ":left" => pageIndex % 2 == (firstPageLeft ? 0 : 1),
        _ => false, // :blank: pages are never left blank by forced breaks here
    };

    /// <summary>
    /// The used sizes of the three boxes along an edge (CSS Paged Media 3, 6.3.2), from their max-content sizes: the
    /// centre box stays centred, and the space is shared in proportion to the content.
    /// </summary>
    internal static (float A, float B, float C) Distribute(float[] max, bool hasA, bool hasB, bool hasC, float available)
    {
        float ma = hasA ? max[0] : 0, mb = hasB ? max[1] : 0, mc = hasC ? max[2] : 0;
        if (!hasB)
        {
            var (a, c) = Share(ma, mc, available, hasA, hasC);
            return (a, 0, c);
        }
        // The centre box against an imaginary box twice as wide as the wider side box.
        float side = 2 * Math.Max(ma, mc);
        var (center, _) = Share(mb, side, available, true, hasA || hasC);
        float rest = (available - center) / 2;
        return (hasA ? rest : 0, center, hasC ? rest : 0);
    }

    private static (float, float) Share(float a, float b, float available, bool hasA, bool hasB)
    {
        if (!hasA) return (0, hasB ? available : 0);
        if (!hasB) return (available, 0);
        float sum = a + b;
        if (sum <= 0) return (available / 2, available / 2);
        // Free space (or the shortfall) is shared in proportion to the max-content sizes.
        return (available * a / sum, available * b / sum);
    }

    private sealed record Built(Element Element, float MaxContentWidth, float MeasuredHeight);

    /// <summary>
    /// Builds a margin box: its element (in a page-context element), styled from the declarations, with its content.
    /// With <paramref name="width"/> and <paramref name="height"/> it is sized to fill its area; without, it is
    /// measured. Null when the box's content is none or normal, which generates no box.
    /// </summary>
    private Built? Build(string name, List<Declaration> decls, List<Declaration> context, int pageIndex, int pageCount, float? width, float? height)
    {
        var resolver = new StyleResolver(new Stylesheet(), _page.ContentWidth, _page.ContentHeight) { Paged = _paged ?? new PagedContent(), CounterStyles = _counterStyles };
        resolver.SetCounter("page", pageIndex + 1);
        resolver.SetCounter("pages", pageCount);

        string? content = null;
        foreach (var d in decls)
            if (d.Property == "content") content = d.Value;
        if (content == null) return null;
        var c = content.Trim().ToLowerInvariant();
        if (c is "none" or "normal" or "") return null;

        var doc = new Element("#document");
        var page = new Element("div");
        page.AddAttribute("style", Serialize(context.Where(d => IsContextProperty(d.Property)), null));
        doc.AppendChild(page);
        var box = new Element("div");
        var defaults = new StringBuilder();
        var (align, valign) = Defaults(name);
        defaults.Append("display:flex;flex-direction:column;box-sizing:border-box;text-align:").Append(align).Append(';');
        defaults.Append("justify-content:").Append(valign).Append(';');
        if (width is { } w) defaults.Append("width:").Append(Pt(w)).Append(';');
        if (height is { } h) defaults.Append("height:").Append(Pt(h)).Append(';');
        box.AddAttribute("style", defaults + Serialize(decls.Where(d => d.Property is not ("content" or "width" or "height")), MapVerticalAlign));
        page.AppendChild(box);
        var inner = new Element("div");
        box.AppendChild(inner);

        resolver.ResolveTree(doc, _initial());
        var text = GeneratedContent.Evaluate(content, page, resolver);
        if (text == null) return null;
        if (text.Length > 0) inner.AppendChild(new TextNode(text));

        float maxContent = 0, measured = 0;
        if (width == null)
        {
            var engine = new LayoutEngine(100000, _images) { Cancellation = _cancellation };
            maxContent = engine.MeasureIntrinsic(box).Max;
        }
        else if (height == null)
        {
            var engine = new LayoutEngine(100000, _images) { Cancellation = _cancellation };
            measured = engine.LayoutBand(box, width.Value);
        }
        return new Built(box, maxContent, measured);
    }

    /// <summary>The default text-align and vertical alignment of each box (CSS Paged Media 3, 6.3.1).</summary>
    private static (string Align, string VAlign) Defaults(string name) => name switch
    {
        "top-left-corner" or "bottom-left-corner" => ("right", "center"),
        "top-right-corner" or "bottom-right-corner" => ("left", "center"),
        "top-left" or "bottom-left" => ("left", "center"),
        "top-right" or "bottom-right" => ("right", "center"),
        "left-top" or "right-top" => ("center", "flex-start"),
        "left-bottom" or "right-bottom" => ("center", "flex-end"),
        _ => ("center", "center"),
    };

    /// <summary>vertical-align on a margin box places its content: top, middle or bottom.</summary>
    private static Declaration? MapVerticalAlign(Declaration d)
    {
        if (d.Property != "vertical-align") return d;
        var v = d.Value.Trim().ToLowerInvariant() switch { "top" => "flex-start", "bottom" => "flex-end", "middle" => "center", _ => null };
        return v == null ? null : new Declaration("justify-content", v, d.Important);
    }

    /// <summary>Properties of an @page rule that its margin boxes inherit (not the page's own size, margins or background).</summary>
    private static bool IsContextProperty(string p)
        => p is not ("size" or "marks" or "bleed" or "page-orientation") && !p.StartsWith("margin", StringComparison.Ordinal)
           && !p.StartsWith("background", StringComparison.Ordinal) && !p.StartsWith("border", StringComparison.Ordinal)
           && !p.StartsWith("padding", StringComparison.Ordinal) && p is not ("width" or "height");

    private static string Serialize(IEnumerable<Declaration> decls, Func<Declaration, Declaration?>? map)
    {
        var sb = new StringBuilder();
        foreach (var d0 in decls)
        {
            var d = map == null ? d0 : map(d0);
            if (d == null) continue;
            sb.Append(d.Property).Append(':').Append(d.Value);
            if (d.Important) sb.Append(" !important");
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static string Pt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
}
