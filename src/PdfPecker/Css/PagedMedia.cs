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

    /// <summary>
    /// The anchor of where the element with an id ends, which the layout records next to where it starts, for the last
    /// page of a section in <c>-pdfpecker-target-pages()</c>.
    /// </summary>
    public static string EndKey(string id) => id + "\u0001end";

    /// <summary>Counters other than page that <c>target-counter()</c> asks for, and their values at each element with an id.</summary>
    public readonly HashSet<string> WantedCounters = new(StringComparer.Ordinal);
    public readonly Dictionary<string, Dictionary<string, int>> CounterSnapshots = new(StringComparer.Ordinal);

    /// <summary>The running strings, once the pages of their elements are known; for margin boxes.</summary>
    public RunningStrings? Running;

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

/// <summary>The values of named strings per page, for <c>string(name, first | start | last | first-except)</c>.</summary>
internal sealed class RunningStrings
{
    // Per name, the assignments in document order with their page and position on it.
    private readonly Dictionary<string, List<(int Page, float Y, string Value)>> _byName = new(StringComparer.Ordinal);

    public RunningStrings(IEnumerable<(string Name, int Page, float Y, string Value)> assignments)
    {
        foreach (var (name, page, y, value) in assignments)
        {
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<(int, float, string)>();
            list.Add((page, y, value));
        }
    }

    /// <summary>
    /// The string's value on a page. first: the first assignment on the page, else the value carried in from earlier
    /// pages; start: the carried-in value unless an assignment starts the page; last: the last assignment on the page,
    /// else the carried-in value; first-except: empty on a page with an assignment, else the carried-in value.
    /// </summary>
    public string Get(string name, int page, string policy)
    {
        if (!_byName.TryGetValue(name, out var list)) return "";
        string entry = "";
        string? first = null, last = null;
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
            "start" => startsPage ? first! : entry,
            "first-except" => first != null ? "" : entry,
            _ => first ?? entry,
        };
    }
}
