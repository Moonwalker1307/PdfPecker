using System.Globalization;
using System.Text;

namespace PdfPecker;

/// <summary>
/// A choice of pages from a PDF, in the order they should appear: for <see cref="PdfPages"/> and for merging selected
/// pages with <see cref="PdfMergeInput"/>. Page numbers start at 1. A page can be chosen more than once.
/// </summary>
/// <remarks>
/// The text form is a comma-separated list of page numbers and ranges: <c>"1-3,5,8-"</c> is pages 1, 2, 3, 5 and 8 to
/// the end; <c>"3,1,2"</c> reorders; <c>"1,1"</c> repeats; <c>"5-3"</c> runs backwards (5, 4, 3); <c>"-4"</c> is pages 1
/// to 4; <c>"last"</c> is the last page, so <c>"last-1"</c> is every page in reverse. Spaces are ignored. Numbers are
/// checked against the PDF only when the range is used: a page past the end throws
/// <see cref="ArgumentOutOfRangeException"/>.
/// </remarks>
public sealed class PdfPageRange
{
    private const int Last = -1;
    private readonly (int From, int To)[] _parts; // 1-based, inclusive; Last = the last page

    private PdfPageRange((int, int)[] parts) => _parts = parts;

    /// <summary>Every page, in order.</summary>
    public static PdfPageRange All { get; } = new(new[] { (1, Last) });

    /// <summary>Reads a range such as <c>"1-3,5,8-"</c> (see the remarks on <see cref="PdfPageRange"/>).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pages"/> is null.</exception>
    /// <exception cref="FormatException"><paramref name="pages"/> is empty or not a valid range.</exception>
    public static PdfPageRange Parse(string pages)
    {
        if (pages == null) throw new ArgumentNullException(nameof(pages));
        var parts = new List<(int, int)>();
        foreach (var raw in pages.Split(','))
        {
            var item = raw.Trim();
            if (item.Length == 0) throw new FormatException($"\"{pages}\" is not a page range: it has an empty item. Write ranges like \"1-3,5,8-\".");
            int dash = item.IndexOf('-');
            if (dash < 0)
            {
                int n = Number(item, pages);
                parts.Add((n, n));
            }
            else
            {
                var left = item[..dash].Trim();
                var right = item[(dash + 1)..].Trim();
                if (left.Length == 0 && right.Length == 0) throw new FormatException($"\"{pages}\" is not a page range: \"-\" needs a page number on at least one side.");
                parts.Add((left.Length == 0 ? 1 : Number(left, pages), right.Length == 0 ? Last : Number(right, pages)));
            }
        }
        return new PdfPageRange(parts.ToArray());
    }

    /// <summary>Reads a range such as <c>"1-3,5,8-"</c>, or returns false when it is not one.</summary>
    public static bool TryParse(string? pages, out PdfPageRange? range)
    {
        range = null;
        if (pages == null) return false;
        try
        {
            range = Parse(pages);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The given pages (numbered from 1), in the given order.</summary>
    /// <exception cref="ArgumentException"><paramref name="pages"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page number is below 1.</exception>
    public static PdfPageRange FromPages(IEnumerable<int> pages)
    {
        if (pages == null) throw new ArgumentNullException(nameof(pages));
        var parts = new List<(int, int)>();
        foreach (var n in pages)
        {
            if (n < 1) throw new ArgumentOutOfRangeException(nameof(pages), n, "Page numbers start at 1.");
            parts.Add((n, n));
        }
        if (parts.Count == 0) throw new ArgumentException("Give at least one page.", nameof(pages));
        return new PdfPageRange(parts.ToArray());
    }

    /// <summary>Pages <paramref name="first"/> to <paramref name="last"/> (numbered from 1), backwards if <paramref name="last"/> is smaller.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A page number is below 1.</exception>
    public static PdfPageRange Span(int first, int last)
    {
        if (first < 1) throw new ArgumentOutOfRangeException(nameof(first), first, "Page numbers start at 1.");
        if (last < 1) throw new ArgumentOutOfRangeException(nameof(last), last, "Page numbers start at 1.");
        return new PdfPageRange(new[] { (first, last) });
    }

    /// <summary>The chosen page numbers (from 1) for a PDF with <paramref name="pageCount"/> pages.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A chosen page is past the last page.</exception>
    public IReadOnlyList<int> ToPageNumbers(int pageCount) => Resolve(pageCount, "the PDF", int.MaxValue).Select(i => i + 1).ToList();

    /// <summary>
    /// The chosen 0-based page indices, stopping once there are more than <paramref name="max"/>; <paramref name="what"/>
    /// names the PDF in the message for a page out of range.
    /// </summary>
    internal List<int> Resolve(int pageCount, string what, int max)
    {
        var result = new List<int>();
        foreach (var (a, b) in Bounds(pageCount, what))
        {
            int step = b >= a ? 1 : -1;
            for (int n = a; ; n += step)
            {
                result.Add(n - 1);
                if (result.Count > max) return result; // the caller reports the limit
                if (n == b) break;
            }
        }
        return result;
    }

    /// <summary>Which pages (by 0-based index) the range names at least once; <paramref name="what"/> is as for <see cref="Resolve"/>.</summary>
    internal bool[] Mark(int pageCount, string what)
    {
        var diff = new int[pageCount + 1];
        foreach (var (a, b) in Bounds(pageCount, what))
        {
            diff[Math.Min(a, b) - 1]++;
            diff[Math.Max(a, b)]--;
        }
        var marked = new bool[pageCount];
        for (int i = 0, run = 0; i < pageCount; i++) marked[i] = (run += diff[i]) > 0;
        return marked;
    }

    /// <summary>The parts as 1-based page numbers, all checked against <paramref name="pageCount"/> first.</summary>
    private (int From, int To)[] Bounds(int pageCount, string what)
    {
        if (pageCount < 1)
            throw new ArgumentOutOfRangeException("pages", char.ToUpperInvariant(what[0]) + what[1..] + " has no pages.");
        var bounds = new (int, int)[_parts.Length];
        for (int i = 0; i < _parts.Length; i++)
        {
            var (from, to) = _parts[i];
            int a = from == Last ? pageCount : from, b = to == Last ? pageCount : to;
            int bad = Math.Max(a, b);
            if (bad > pageCount)
                throw new ArgumentOutOfRangeException("pages",
                    $"Page {bad.ToString(CultureInfo.InvariantCulture)} is out of range: {what} has {pageCount} page{(pageCount == 1 ? "" : "s")}.");
            bounds[i] = (a, b);
        }
        return bounds;
    }

    /// <summary>The range in its text form, for example <c>"1-3,5,8-last"</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (from, to) in _parts)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(Text(from));
            if (to != from) sb.Append('-').Append(Text(to));
        }
        return sb.ToString();

        static string Text(int n) => n == Last ? "last" : n.ToString(CultureInfo.InvariantCulture);
    }

    private static int Number(string s, string whole)
    {
        if (s.Equals("last", StringComparison.OrdinalIgnoreCase)) return Last;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            throw new FormatException($"\"{whole}\" is not a page range: \"{s}\" is not a page number. Write ranges like \"1-3,5,8-\".");
        if (n < 1) throw new FormatException($"\"{whole}\" is not a page range: page numbers start at 1.");
        return n;
    }
}

/// <summary>A PDF to merge, and which of its pages to take (see <see cref="PdfMerger.Merge(IEnumerable{PdfMergeInput}, PdfMergeOptions?, CancellationToken)"/>).</summary>
public sealed class PdfMergeInput
{
    /// <param name="pdf">The PDF.</param>
    /// <param name="pages">The pages to take, in order; null for every page.</param>
    public PdfMergeInput(byte[] pdf, PdfPageRange? pages = null)
    {
        Pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
        Pages = pages;
    }

    /// <param name="pdf">The PDF.</param>
    /// <param name="pages">The pages to take, in order, such as <c>"1-3,5"</c> (see <see cref="PdfPageRange.Parse"/>).</param>
    public PdfMergeInput(byte[] pdf, string pages) : this(pdf, PdfPageRange.Parse(pages)) { }

    /// <summary>The PDF.</summary>
    public byte[] Pdf { get; }

    /// <summary>The pages to take, in order; null for every page.</summary>
    public PdfPageRange? Pages { get; }
}
