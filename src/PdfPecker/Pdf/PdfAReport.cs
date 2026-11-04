using System.Globalization;
using System.Text;

namespace PdfPecker.Pdf;

/// <summary>
/// Collects what a PDF/A conversion changed or found, as sentences: the same kind of finding is counted and named once
/// rather than listed once per object, so a document with a thousand annotations gets one line about them.
/// </summary>
internal sealed class PdfAReport
{
    /// <summary>The most names listed in one sentence; the rest are counted.</summary>
    private const int MaxNames = 5;

    private sealed class Entry
    {
        public int Count;
        public readonly List<string> Names = new();
        public int MoreNames;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    /// <summary>
    /// Counts one finding. In <paramref name="template"/>, {0} is the count, {s} an "s" when the count is not one and
    /// {names} the distinct <paramref name="name"/>s given, as ": a, b and c".
    /// </summary>
    public void Add(string template, string? name = null)
    {
        if (!_entries.TryGetValue(template, out var entry))
        {
            _entries[template] = entry = new Entry();
            _order.Add(template);
        }
        entry.Count++;
        if (string.IsNullOrEmpty(name) || entry.Names.Contains(name, StringComparer.Ordinal)) return;
        if (entry.Names.Count < MaxNames) entry.Names.Add(name);
        else entry.MoreNames++;
    }

    public bool IsEmpty => _order.Count == 0;

    public IReadOnlyList<string> Messages()
    {
        var list = new List<string>(_order.Count);
        foreach (var template in _order)
        {
            var entry = _entries[template];
            var names = new StringBuilder();
            if (entry.Names.Count > 0)
            {
                names.Append(" (");
                for (int i = 0; i < entry.Names.Count; i++)
                {
                    if (i > 0) names.Append(i == entry.Names.Count - 1 && entry.MoreNames == 0 ? " and " : ", ");
                    names.Append(entry.Names[i]);
                }
                if (entry.MoreNames > 0) names.Append(" and ").Append(entry.MoreNames.ToString(CultureInfo.InvariantCulture)).Append(" more");
                names.Append(')');
            }
            list.Add(template.Replace("{0}", entry.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{s}", entry.Count == 1 ? "" : "s", StringComparison.Ordinal)
                .Replace("{names}", names.ToString(), StringComparison.Ordinal));
        }
        return list;
    }
}
