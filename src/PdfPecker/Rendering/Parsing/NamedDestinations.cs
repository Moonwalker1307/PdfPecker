using System.Text;

namespace PdfPecker.Rendering.Parsing;

/// <summary>The named destinations of a PDF, from the PDF 1.1 /Dests dictionary and the /Names /Dests name tree.</summary>
internal sealed class NamedDestinations
{
    private readonly PdfFile _file;
    private readonly Dictionary<string, PdfObject> _dests = new(StringComparer.Ordinal);

    public NamedDestinations(PdfFile file)
    {
        _file = file;
        var catalog = file.Catalog;
        // PDF 1.1: a dictionary of names.
        if (file.GetDict(catalog, "Dests") is { } dests)
            foreach (var (k, v) in dests.Entries) _dests.TryAdd("/" + k, v);
        // PDF 1.2 and later: a name tree of strings.
        if (file.GetDict(file.GetDict(catalog, "Names"), "Dests") is { } tree)
            WalkNameTree(tree, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
    }

    /// <summary>The explicit destination array a destination name stands for, or null.</summary>
    /// <remarks>Typed as any object, as callers write the result back into PDF objects where a null stands for none.</remarks>
    public PdfObject? Find(PdfObject name)
    {
        string key = name switch
        {
            PdfName n => "/" + n.Value,
            PdfString s => Encoding.Latin1.GetString(s.Bytes),
            _ => "",
        };
        if (!_dests.TryGetValue(key, out var dest) && name is PdfName nn)
            _dests.TryGetValue(nn.Value, out dest); // some writers use names for name-tree keys
        dest = _file.Resolve(dest);
        if (dest is PdfDict dd) dest = _file.Resolve(dd["D"]);
        return dest is PdfArray ? dest : null;
    }

    private void WalkNameTree(PdfDict node, HashSet<PdfDict> seen, int depth)
    {
        if (depth > 32 || !seen.Add(node) || _dests.Count > 100_000) return;
        if (_file.GetArray(node, "Names") is { } names)
        {
            for (int i = 0; i + 1 < names.Count; i += 2)
                if (_file.Resolve(names[i]) is PdfString k) _dests.TryAdd(Encoding.Latin1.GetString(k.Bytes), names[i + 1]);
        }
        if (_file.GetArray(node, "Kids") is { } kids)
            foreach (var kid in kids.Items)
                if (_file.Resolve(kid) is PdfDict kd) WalkNameTree(kd, seen, depth + 1);
    }
}
