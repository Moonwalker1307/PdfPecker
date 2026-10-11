using PdfPecker.Css;

namespace PdfPecker.Fonts;

/// <summary>One face of a font family: a registered font, or a CSS @font-face rule.</summary>
internal sealed class FontFace
{
    public readonly string Family;
    public readonly int WeightMin, WeightMax;
    public readonly bool Italic;
    public readonly UnicodeRange? Range;
    public readonly string? FullName, PostScriptName; // for CSS local()
    private readonly Lazy<FontProgram?> _program;

    public FontFace(string family, int weightMin, int weightMax, bool italic, UnicodeRange? range, Lazy<FontProgram?> program,
        string? fullName = null, string? postScriptName = null)
    {
        Family = family;
        WeightMin = weightMin;
        WeightMax = weightMax;
        Italic = italic;
        Range = range;
        _program = program;
        FullName = fullName;
        PostScriptName = postScriptName;
    }

    /// <summary>The font program, loaded on first use; null when it cannot be loaded.</summary>
    public FontProgram? Program => _program.Value;
}

/// <summary>
/// Picks the fonts for an element's font-family, font-weight and font-style from the document's @font-face rules
/// and the registered <see cref="FontCollection"/>, following the CSS font matching rules. One instance serves one
/// document.
/// </summary>
internal sealed class FontResolver
{
    private readonly Dictionary<string, List<FontFace>> _docFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _docOrder = new();
    private readonly FontCollection.Snapshot? _collection;
    private readonly Dictionary<(string, int, bool, FontFamily), TextFont> _resolved = new();
    private readonly Dictionary<(string, int, bool), List<EmbeddedFont>> _matched = new();
    private readonly Dictionary<(FontProgram, bool, bool, UnicodeRange?), EmbeddedFont> _fonts = new();

    private FontResolver(FontCollection.Snapshot? collection) => _collection = collection;

    /// <summary>Creates a resolver, or returns null when the document has no fonts beyond the standard ones.</summary>
    public static FontResolver? Create(List<List<Declaration>> fontFaceRules, FontCollection? collection, Func<string, byte[]?> fetch)
    {
        var snapshot = collection?.GetSnapshot();
        if (snapshot is { All.Count: 0 }) snapshot = null;
        var r = new FontResolver(snapshot);
        foreach (var rule in fontFaceRules)
        {
            var face = FromFontFaceRule(rule, fetch, snapshot);
            if (face == null) continue;
            if (!r._docFamilies.TryGetValue(face.Family, out var list))
            {
                r._docFamilies[face.Family] = list = new List<FontFace>();
                r._docOrder.Add(face.Family);
            }
            list.Add(face);
        }
        return r._docFamilies.Count == 0 && snapshot == null ? null : r;
    }

    // ------------------------------------------------------------------ @font-face

    private static FontFace? FromFontFaceRule(List<Declaration> decls, Func<string, byte[]?> fetch, FontCollection.Snapshot? collection)
    {
        string? family = null, src = null;
        int wMin = 400, wMax = 400;
        bool italic = false;
        UnicodeRange? range = null;
        foreach (var d in decls)
        {
            var v = d.Value.Trim();
            switch (d.Property)
            {
                case "font-family": family = Unquote(v); break;
                case "src": src = v; break;
                case "font-style": italic = v.StartsWith("italic", StringComparison.OrdinalIgnoreCase) || v.StartsWith("oblique", StringComparison.OrdinalIgnoreCase); break;
                case "font-weight":
                {
                    var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && ParseWeight(parts[0]) is int a)
                    {
                        wMin = wMax = a;
                        if (parts.Length >= 2 && ParseWeight(parts[1]) is int b) { wMin = Math.Min(a, b); wMax = Math.Max(a, b); }
                    }
                    break;
                }
                case "unicode-range": range = UnicodeRange.Parse(v); break;
            }
        }
        if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(src)) return null;

        var sources = CssParser.SplitTopLevel(src!, ',');
        var program = new Lazy<FontProgram?>(() =>
        {
            foreach (var item in sources)
            {
                try
                {
                    var p = LoadSource(item.Trim(), fetch, collection);
                    if (p != null) return p;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || FontProgram.IsFormatError(ex))
                {
                    // try the next source
                }
            }
            return null;
        });
        return new FontFace(family!, wMin, wMax, italic, range, program);
    }

    private static FontProgram? LoadSource(string item, Func<string, byte[]?> fetch, FontCollection.Snapshot? collection)
    {
        string? url = null, format = null, local = null;
        foreach (var tok in CssParser.Tokens(item))
        {
            var lower = tok.ToLowerInvariant();
            if (lower.StartsWith("url(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal)) url = Unquote(tok.Substring(4, tok.Length - 5));
            else if (lower.StartsWith("format(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal)) format = Unquote(tok.Substring(7, tok.Length - 8)).ToLowerInvariant();
            else if (lower.StartsWith("local(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal)) local = Unquote(tok.Substring(6, tok.Length - 7));
        }
        if (local != null)
        {
            if (collection == null) return null;
            foreach (var f in collection.All)
                if (string.Equals(f.FullName, local, StringComparison.OrdinalIgnoreCase) || string.Equals(f.PostScriptName, local, StringComparison.OrdinalIgnoreCase))
                    return f.Program;
            return null;
        }
        if (url == null) return null;
        // Skip formats we cannot read without downloading them (fetching may be expensive for the caller).
        if (format != null && !format.StartsWith("truetype", StringComparison.Ordinal) && !format.StartsWith("opentype", StringComparison.Ordinal)
            && format != "woff" && format != "collection" && !format.StartsWith("woff-variations", StringComparison.Ordinal))
            return null;
        var bytes = fetch(url);
        if (bytes == null) return null;
        var programs = FontProgram.Load(bytes);
        if (programs.Count == 0) return null;
        int hash = url.IndexOf('#');
        if (hash >= 0 && programs.Count > 1)
        {
            // url(fonts.ttc#PostScriptName) picks one font of a collection.
            var name = url.Substring(hash + 1);
            foreach (var p in programs)
                if (string.Equals(p.PostScriptName, name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return programs[0];
    }

    private static int? ParseWeight(string v)
    {
        v = v.ToLowerInvariant();
        if (v == "normal") return 400;
        if (v == "bold") return 700;
        return float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w) && w >= 1 && w <= 1000
            ? (int)w : null;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) s = s.Substring(1, s.Length - 2);
        return s.Trim();
    }

    // ------------------------------------------------------------------ matching

    /// <summary>The font for a computed style: a chain over the font-family list with per-character fallback.</summary>
    public TextFont Resolve(string? familyList, FontFamily generic, int weight, bool italic)
    {
        var key = (familyList ?? "", weight, italic, generic);
        if (_resolved.TryGetValue(key, out var cached)) return cached;

        bool bold = weight >= 600;
        var candidates = new List<TextFont>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (familyList != null)
        {
            foreach (var part in familyList.Split(','))
            {
                var name = Unquote(part);
                if (name.Length == 0 || !listed.Add(name)) continue;
                var fonts = MatchFamily(name, weight, italic);
                if (fonts.Count > 0) candidates.AddRange(fonts);
                else if (StandardFamily(name) is FontFamily sf) candidates.Add(StandardFont.Get(sf, bold, italic));
            }
        }
        var standard = StandardFont.Get(generic, bold, italic);
        if (candidates.Count == 0) candidates.Add(standard);

        var primary = candidates[0];
        var result = new FontChain(primary, cp =>
        {
            for (int i = 1; i < candidates.Count; i++)
                if (Covers(candidates[i], cp)) return candidates[i];
            if (!candidates.Contains(standard) && Covers(standard, cp)) return standard;
            foreach (var family in AllFamilies())
            {
                if (listed.Contains(family)) continue;
                foreach (var f in MatchFamily(family, weight, italic))
                    if (f.Covers(cp)) return f;
            }
            return null;
        });
        _resolved[key] = result;
        return result;
    }

    private static bool Covers(TextFont f, int cp)
        => f is EmbeddedFont e ? e.Covers(cp) : cp < 0x10000 && StandardFont.CanEncode((char)cp);

    private IEnumerable<string> AllFamilies()
    {
        foreach (var f in _docOrder) yield return f;
        if (_collection == null) yield break;
        foreach (var f in _collection.ByFamily.Keys) yield return f;
    }

    /// <summary>The best faces of a family for a weight and style (several when @font-face splits it by unicode-range).</summary>
    private List<EmbeddedFont> MatchFamily(string family, int weight, bool italic)
    {
        var key = (family.ToLowerInvariant(), weight, italic);
        if (_matched.TryGetValue(key, out var result)) return result;
        result = new List<EmbeddedFont>();
        if (!_docFamilies.TryGetValue(family, out var faces) && (_collection == null || !_collection.ByFamily.TryGetValue(family, out faces)))
        {
            _matched[key] = result;
            return result;
        }

        var ordered = faces.Select(f => (Face: f, Score: Score(f, weight, italic))).OrderBy(x => x.Score).ToList();
        long bestScore = -1;
        foreach (var (face, score) in ordered)
        {
            if (bestScore >= 0 && score != bestScore) break;
            var program = face.Program;
            if (program == null) continue;
            bestScore = score;
            bool synthBold = weight >= 600 && face.WeightMax < 600;
            bool synthItalic = italic && !face.Italic;
            var fk = (program, synthBold, synthItalic, face.Range);
            if (!_fonts.TryGetValue(fk, out var font)) _fonts[fk] = font = new EmbeddedFont(program, synthBold, synthItalic, face.Range);
            result.Add(font);
        }
        _matched[key] = result;
        return result;
    }

    /// <summary>Lower is better: style first, then weight in the order CSS Fonts level 4 prescribes.</summary>
    private static long Score(FontFace f, int desired, bool italic)
    {
        long style = f.Italic == italic ? 0 : 100000;
        if (desired >= f.WeightMin && desired <= f.WeightMax) return style;
        int w = desired < f.WeightMin ? f.WeightMin : f.WeightMax;
        long weight;
        if (desired > 500) weight = w >= desired ? w - desired : 1000 + (desired - w);
        else if (desired < 400) weight = w <= desired ? desired - w : 1000 + (w - desired);
        else weight = w >= desired && w <= 500 ? w - desired : w < desired ? 1000 + (desired - w) : 2000 + (w - desired);
        return style + weight;
    }

    /// <summary>The first standard family named in a CSS font-family list, if any.</summary>
    public static FontFamily? FirstStandardFamily(string familyList)
    {
        foreach (var part in familyList.Split(','))
            if (StandardFamily(Unquote(part)) is FontFamily f) return f;
        return null;
    }

    /// <summary>Family names that always mean a standard PDF font.</summary>
    private static FontFamily? StandardFamily(string name) => name.ToLowerInvariant() switch
    {
        "serif" or "times" or "times new roman" or "times-roman" or "ui-serif" => FontFamily.Times,
        "monospace" or "courier" or "courier new" or "ui-monospace" => FontFamily.Courier,
        "sans-serif" or "helvetica" or "arial" or "system-ui" or "ui-sans-serif" or "-apple-system" or "blinkmacsystemfont"
            or "cursive" or "fantasy" => FontFamily.Helvetica,
        _ => null,
    };
}
