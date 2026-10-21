using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Html;

/// <summary>A link on a page: its area in points on the displayed page, and a web address or a page it goes to.</summary>
internal readonly record struct PageLink(double X, double Y, double Width, double Height, string? Uri, int Page);

/// <summary>Reads the link annotations of a document's pages for the HTML output.</summary>
internal sealed class PageLinks
{
    /// <summary>
    /// The most links read from one page. Each becomes an element of the output, and a damaged or hostile PDF can list
    /// far more annotations than any real page has.
    /// </summary>
    public const int MaxPerPage = 10_000;

    private static readonly string[] Schemes = { "http", "https", "mailto", "tel" };

    private readonly RenderDocument _doc;
    private readonly Dictionary<PdfDict, int> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly Lazy<NamedDestinations> _named;

    public PageLinks(RenderDocument doc)
    {
        _doc = doc;
        for (int i = 0; i < doc.Pages.Count; i++) _pages.TryAdd(doc.Pages[i].Dict, i);
        _named = new Lazy<NamedDestinations>(() => new NamedDestinations(doc.File));
    }

    /// <summary>The links of the page at <paramref name="index"/>, in annotation order.</summary>
    public List<PageLink> Read(int index)
    {
        var file = _doc.File;
        var links = new List<PageLink>();
        if (file.GetArray(_doc.Pages[index].Dict, "Annots") is not { } annots) return links;
        var m = _doc.DisplayMatrix(index);
        foreach (var item in annots.Items)
        {
            if (links.Count >= MaxPerPage) break;
            if (file.Resolve(item) is not PdfDict annot || file.GetName(annot, "Subtype") != "Link") continue;
            if ((file.GetInt(annot, "F", 0) & 2) != 0) continue; // hidden
            if (file.GetNumbers(annot, "Rect") is not { Length: >= 4 } r) continue;
            string? uri = null;
            int page = -1;
            var action = file.GetDict(annot, "A");
            if (action != null)
            {
                switch (file.GetName(action, "S"))
                {
                    case "URI": uri = WebAddress(file.GetBytes(action, "URI")); break;
                    case "GoTo": page = Target(file.Resolve(action["D"])); break;
                }
            }
            else page = Target(file.Resolve(annot["Dest"]));
            if (uri == null && page < 0) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var (px, py) in new[] { (r[0], r[1]), (r[2], r[1]), (r[0], r[3]), (r[2], r[3]) })
            {
                var (x, y) = m.Apply(px, py);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            if (!(x1 - x0 > 0 && y1 - y0 > 0) || !double.IsFinite(x1 - x0) || !double.IsFinite(y1 - y0)) continue;
            links.Add(new PageLink(x0, y0, x1 - x0, y1 - y0, uri, page));
        }
        return links;
    }

    /// <summary>The page index an explicit or named destination goes to, or -1.</summary>
    private int Target(PdfObject? dest)
    {
        if (dest is PdfString or PdfName) dest = _named.Value.Find(dest);
        if (dest is not PdfArray { Count: > 0 } a) return -1;
        return _doc.File.Resolve(a[0]) is PdfDict p && _pages.TryGetValue(p, out int i) ? i : -1;
    }

    /// <summary>
    /// The address of a URI action when it is an absolute web, mail or phone address. Anything else, such as
    /// <c>javascript:</c> or <c>file:</c>, is dropped: a PDF from anyone must not put script or local paths into a page.
    /// </summary>
    internal static string? WebAddress(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 8192) return null;
        // URIs in PDFs are 7-bit ASCII; some writers put UTF-8 there, which Uri then escapes.
        string text = Encoding.UTF8.GetString(bytes).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        return Array.IndexOf(Schemes, uri.Scheme) >= 0 ? uri.AbsoluteUri : null;
    }
}
