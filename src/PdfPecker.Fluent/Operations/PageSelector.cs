using System.Globalization;

namespace PdfPecker.Fluent.Operations;

/// <summary>
/// Page selections in qpdf's syntax, which QuestPDF's DocumentOperation passes on to qpdf: comma-separated numbers and
/// ranges, where <c>z</c> is the last page, <c>r2</c> the second from the end, <c>x</c> before an item takes its pages
/// out of the range before it, and <c>:odd</c> or <c>:even</c> at the end keeps the pages in odd or even positions.
/// </summary>
internal static class PageSelector
{
    /// <summary>The most pages a selection can name, PdfPecker's default limit for a merged document.</summary>
    internal const int MaxPages = 100_000;

    private readonly record struct Bound(bool FromEnd, int Value);

    private readonly record struct Item(bool Exclude, Bound From, Bound To);

    /// <summary>Checks the syntax, so a mistake shows at the call that makes it rather than when the PDF is saved.</summary>
    public static void Validate(string selector, string parameter) => Parse(selector, parameter);

    /// <summary>The page numbers (from 1) a selection names in a PDF of <paramref name="pageCount"/> pages; a blank one names none.</summary>
    public static List<int> Resolve(string selector, int pageCount, string parameter)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(selector)) return result;
        var (items, parity) = Parse(selector, parameter);
        for (int i = 0; i < items.Count;)
        {
            var (from, to) = (Page(items[i].From), Page(items[i].To));
            int low = Math.Min(from, to), high = Math.Max(from, to);
            if (result.Count + (high - low + 1) > MaxPages)
                throw new ArgumentException($"\"{selector}\" selects more than {MaxPages.ToString("N0", CultureInfo.InvariantCulture)} pages.", parameter);
            // The exclusions that follow a range take pages out of it alone, marked in a difference array over its span.
            int[]? excluded = null;
            for (i++; i < items.Count && items[i].Exclude; i++)
            {
                int a = Page(items[i].From), b = Page(items[i].To);
                int lo = Math.Max(Math.Min(a, b), low), hi = Math.Min(Math.Max(a, b), high);
                if (lo > hi) continue;
                excluded ??= new int[high - low + 2];
                excluded[lo - low]++;
                excluded[hi - low + 1]--;
            }
            if (excluded != null)
                for (int k = 1; k < excluded.Length; k++) excluded[k] += excluded[k - 1];
            int step = to >= from ? 1 : -1;
            for (int page = from; ; page += step)
            {
                if (excluded == null || excluded[page - low] == 0) result.Add(page);
                if (page == to) break;
            }
        }
        if (parity == 0) return result;
        var kept = new List<int>((result.Count + 1) / 2);
        for (int k = parity == 1 ? 0 : 1; k < result.Count; k += 2) kept.Add(result[k]);
        return kept;

        int Page(Bound bound)
        {
            int page = bound.FromEnd ? pageCount - bound.Value + 1 : bound.Value;
            if (page < 1 || page > pageCount)
                throw new ArgumentOutOfRangeException(parameter, $"\"{selector}\" names page {(bound.FromEnd ? "r" : "")}"
                    + $"{bound.Value.ToString(CultureInfo.InvariantCulture)}, but the PDF has {pageCount.ToString(CultureInfo.InvariantCulture)} page{(pageCount == 1 ? "" : "s")}.");
            return page;
        }
    }

    private static (List<Item> Items, int Parity) Parse(string selector, string parameter)
    {
        ArgumentNullException.ThrowIfNull(selector, parameter);
        var text = selector.AsSpan().Trim();
        int parity = 0;
        int colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            var suffix = text[(colon + 1)..].Trim();
            parity = suffix.SequenceEqual("odd") ? 1 : suffix.SequenceEqual("even") ? 2
                : throw Invalid(selector, parameter, "it may end only in \":odd\" or \":even\"");
            text = text[..colon].TrimEnd();
        }
        if (text.IsEmpty) throw Invalid(selector, parameter, "it names no pages");
        var items = new List<Item>();
        while (true)
        {
            int comma = text.IndexOf(',');
            var item = (comma < 0 ? text : text[..comma]).Trim();
            bool exclude = item.Length > 0 && item[0] == 'x';
            if (exclude)
            {
                if (items.Count == 0) throw Invalid(selector, parameter, "an exclusion with x needs a range before it");
                item = item[1..].TrimStart();
            }
            if (item.IsEmpty) throw Invalid(selector, parameter, "it has an empty item");
            int dash = item.IndexOf('-');
            var first = Number(dash < 0 ? item : item[..dash].TrimEnd(), selector, parameter);
            var last = dash < 0 ? first : Number(item[(dash + 1)..].TrimStart(), selector, parameter);
            items.Add(new Item(exclude, first, last));
            if (comma < 0) break;
            text = text[(comma + 1)..];
        }
        return (items, parity);
    }

    private static Bound Number(ReadOnlySpan<char> s, string selector, string parameter)
    {
        if (s.SequenceEqual("z")) return new Bound(true, 1);
        bool fromEnd = s.Length > 0 && s[0] == 'r';
        if (fromEnd) s = s[1..];
        // Nine digits keep the number within an int; no PDF has that many pages anyway.
        if (s.IsEmpty || s.Length > 9 || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            throw Invalid(selector, parameter, "it has an item that is not a page number, r and a number, or z");
        if (n < 1) throw Invalid(selector, parameter, "page numbers start at 1");
        return new Bound(fromEnd, n);
    }

    private static ArgumentException Invalid(string selector, string parameter, string reason)
        => new($"\"{selector}\" is not a page selection: {reason}. Write selections like \"1-3,5\", \"2-z\" or \"r3-r1\".", parameter);
}
