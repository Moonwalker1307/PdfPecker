using System.Globalization;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// What generated content needs from pagination (CSS Generated Content for Paged Media): the pages elements land on,
/// for <c>target-counter()</c>, and the running strings of <c>string-set</c>, for <c>string()</c> in page-margin
/// boxes. Page numbers are only known after a layout, so a document that uses them is laid out again with the
/// numbers the previous layout gave.
/// </summary>
internal sealed class PagedContent
{
    /// <summary>Some content used <c>target-counter()</c> or <c>target-text()</c>.</summary>
    public bool UsesTargets;

    /// <summary>The page number (1-based) of the element with an id, from the last layout; null before one.</summary>
    public Func<string, int?>? PageOfId;

    /// <summary>The number of pages of the last layout, for <c>counter(pages)</c> outside the page-margin boxes; null before one.</summary>
    public int? PageCount;

    /// <summary>
    /// The anchor of where the element with an id ends, which the layout records next to where it starts, for the last
    /// page of a section in <c>-pdfpecker-target-pages()</c>.
    /// </summary>
    public static string EndKey(string id) => id + EndSuffix;

    /// <summary>What <see cref="EndKey"/> adds to an id.</summary>
    public const string EndSuffix = "\u0001end";

    /// <summary>
    /// The layout keys of the elements that show their own page within a section, which the layout records where their
    /// content starts. They need no id, and an element whose first line moves to the next page counts as being there.
    /// </summary>
    public HashSet<string>? SelfKeys;

    private Dictionary<Element, string>? _selfKeyOf;

    /// <summary>
    /// The layout key of an element that shows its own page: the one it already has, or one of its own that stays the
    /// same each time the styles are resolved again, so the pages of the last layout can be found under it.
    /// </summary>
    public string SelfKey(Element e)
    {
        _selfKeyOf ??= new Dictionary<Element, string>(ReferenceEqualityComparer.Instance);
        if (e.LayoutKey == null)
        {
            if (!_selfKeyOf.TryGetValue(e, out var key))
                _selfKeyOf[e] = key = "\u0001p" + _selfKeyOf.Count.ToString(CultureInfo.InvariantCulture);
            e.LayoutKey = key;
        }
        (SelfKeys ??= new HashSet<string>(StringComparer.Ordinal)).Add(e.LayoutKey);
        return e.LayoutKey;
    }

    /// <summary>Counters other than page that <c>target-counter()</c> asks for, and their values at each element with an id.</summary>
    public readonly HashSet<string> WantedCounters = new(StringComparer.Ordinal);
    public readonly Dictionary<string, Dictionary<string, int>> CounterSnapshots = new(StringComparer.Ordinal);

    /// <summary>The running strings, once the pages of their elements are known; for margin boxes.</summary>
    public RunningValues<string>? Running;

    /// <summary>The running elements, once the pages they were taken from are known; for element() in margin boxes.</summary>
    public RunningValues<Element>? RunningElements;

    /// <summary>The page (0-based) whose margin boxes are being generated.</summary>
    public int CurrentPage;

    /// <summary>
    /// The value <c>@page { counter-reset: footnote }</c> gives the footnote counter at the top of every page, so
    /// footnotes are numbered per page; null when no @page rule resets it.
    /// </summary>
    public int? FootnotePageReset;

    /// <summary>The page (0-based, in the whole document) of each footnote's call, by footnote index, from the last layout.</summary>
    public Func<int, int?>? FootnotePageOf;
}

/// <summary>A <c>string-set</c> assignment: the element's name and value, and the anchor key that finds its page.</summary>
internal sealed record StringAssignment(Element Element, string Name, string Value, string Key);

/// <summary>A running element: the element with position: running(name), and the anchor key that finds its page.</summary>
internal sealed record RunningAssignment(Element Element, string Name, string Key);

/// <summary>
/// The values of named strings (string-set) or running elements (position: running()) per page, for
/// <c>string(name, policy)</c> and <c>element(name, policy)</c>.
/// </summary>
internal sealed class RunningValues<T> where T : class
{
    // Per name, the assignments in document order with their page and position on it.
    private readonly Dictionary<string, List<(int Page, float Y, T Value)>> _byName = new(StringComparer.Ordinal);

    public RunningValues(IEnumerable<(string Name, int Page, float Y, T Value)> assignments)
    {
        foreach (var (name, page, y, value) in assignments)
        {
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<(int, float, T)>();
            list.Add((page, y, value));
        }
    }

    /// <summary>
    /// The value on a page, null for none. first: the first assignment on the page, else the value carried in from
    /// earlier pages; start: the carried-in value unless an assignment starts the page; last: the last assignment on the
    /// page, else the carried-in value; first-except: none on a page with an assignment, else the carried-in value.
    /// </summary>
    public T? Get(string name, int page, string policy)
    {
        if (!_byName.TryGetValue(name, out var list)) return null;
        T? entry = null, first = null, last = null;
        bool startsPage = false;
        foreach (var (p, y, value) in list)
        {
            if (p < page) { entry = value; continue; }
            if (p > page) break;
            if (first == null) { first = value; startsPage = y <= 0.5f; }
            last = value;
        }
        return policy switch
        {
            "last" => last ?? entry,
            "start" => startsPage ? first : entry,
            "first-except" => first != null ? null : entry,
            _ => first ?? entry,
        };
    }
}
