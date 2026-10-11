namespace PdfPecker.Fonts;

/// <summary>
/// Collects the font families a document asks for but that no font provides, and reports each one once per document
/// through <see cref="PdfOptions.MissingFont"/>.
/// </summary>
internal sealed class MissingFontReport
{
    private readonly Action<string> _callback;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedLists = new(StringComparer.Ordinal);

    public MissingFontReport(Action<string> callback) => _callback = callback;

    /// <summary>Reports a family that is missing, unless it is a CSS generic family or was reported already.</summary>
    public void Missing(string family)
    {
        if (family.Length == 0 || IsGeneric(family)) return;
        bool first;
        lock (_reported) first = _reported.Add(family);
        if (first) _callback(family);
    }

    /// <summary>
    /// Checks a font-family list when only the standard fonts exist: the families before the first one that maps to a
    /// standard font are missing.
    /// </summary>
    public void CheckStandardOnly(string? familyList)
    {
        if (string.IsNullOrEmpty(familyList)) return;
        lock (_reported)
            if (!_checkedLists.Add(familyList!)) return;
        foreach (var part in familyList!.Split(','))
        {
            var name = FontResolver.Unquote(part);
            if (name.Length == 0) continue;
            if (FontResolver.StandardFamily(name) != null) return;
            Missing(name);
        }
    }

    /// <summary>CSS generic families and keywords that never name an installed font.</summary>
    private static bool IsGeneric(string name) => name.ToLowerInvariant() is "serif" or "sans-serif" or "monospace" or "cursive"
        or "fantasy" or "system-ui" or "ui-serif" or "ui-sans-serif" or "ui-monospace" or "ui-rounded" or "emoji" or "math"
        or "fangsong" or "inherit" or "initial" or "unset" or "revert" or "revert-layer";
}
