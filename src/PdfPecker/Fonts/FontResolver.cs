using PdfPecker.Css;

namespace PdfPecker.Fonts;

/// <summary>One face of a font family: a registered font, or a CSS @font-face rule.</summary>
internal sealed class FontFace
{
    public readonly string Family;
    private readonly int _weightMin, _weightMax;
    private readonly bool _weightAuto; // no font-weight descriptor: a variable font covers its whole weight axis
    public readonly bool Italic;
    public readonly UnicodeRange? Range;
    public readonly string? FullName, PostScriptName; // for CSS local()
    private readonly Lazy<FontProgram?> _program;

    public FontFace(string family, int weightMin, int weightMax, bool italic, UnicodeRange? range, Lazy<FontProgram?> program,
        string? fullName = null, string? postScriptName = null, bool weightAuto = false)
    {
        Family = family;
        _weightMin = weightMin;
        _weightMax = weightMax;
        _weightAuto = weightAuto;
        Italic = italic;
        Range = range;
        _program = program;
        FullName = fullName;
        PostScriptName = postScriptName;
    }

    /// <summary>The font program, loaded on first use; null when it cannot be loaded.</summary>
    public FontProgram? Program => _program.Value;

    /// <summary>The weights the face covers: as declared, or for a variable font without a declaration, its weight axis.</summary>
    public int WeightMin => _weightAuto && Program?.Axis(FontVariations.Wght) is { } a ? (int)Math.Ceiling(a.Min) : _weightMin;

    public int WeightMax => _weightAuto && Program?.Axis(FontVariations.Wght) is { } a ? (int)Math.Floor(a.Max) : _weightMax;
}

/// <summary>
/// What picks the instance of a variable font besides weight and style: font-stretch (a percentage), the font size in
/// CSS pixels for the optical size axis (0 with font-optical-sizing: none), and font-variation-settings as written.
/// </summary>
internal readonly record struct FontVariation(float Stretch, float OpticalSize, string? Settings)
{
    public static readonly FontVariation Normal = new(100, 0, null);
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
    private readonly Dictionary<(string, int, bool, FontFamily, FontVariation), TextFont> _resolved = new();
    private readonly Dictionary<(string, int, bool, FontVariation), List<EmbeddedFont>> _matched = new();
    private readonly Dictionary<(FontProgram, bool, bool, UnicodeRange?), EmbeddedFont> _fonts = new();

    private readonly bool _embedStandard;
    private readonly Lazy<FontCollection.Snapshot>? _system; // the system font folders, scanned on first use
    private readonly MissingFontReport? _report;

    private FontResolver(FontCollection.Snapshot? collection, bool embedStandard, Lazy<FontCollection.Snapshot>? system, MissingFontReport? report)
    {
        _collection = collection;
        _embedStandard = embedStandard;
        _system = system;
        _report = report;
    }

    /// <summary>
    /// Creates a resolver, or returns null when the document has no fonts beyond the standard ones and they need not be
    /// embedded. With <paramref name="embedStandard"/>, the standard fonts are replaced by the bundled Liberation fonts.
    /// With <paramref name="systemFonts"/>, families found nowhere else are looked up in the system font folders.
    /// </summary>
    public static FontResolver? Create(List<List<Declaration>> fontFaceRules, FontCollection? collection, Func<string, byte[]?> fetch,
        bool embedStandard = false, bool systemFonts = false, MissingFontReport? report = null)
    {
        var snapshot = collection?.GetSnapshot();
        if (snapshot is { All.Count: 0 }) snapshot = null;
        var system = systemFonts ? new Lazy<FontCollection.Snapshot>(() => FontCollection.Installed.GetSnapshot()) : null;
        var r = new FontResolver(snapshot, embedStandard, system, report);
        foreach (var rule in fontFaceRules)
        {
            var face = FromFontFaceRule(rule, fetch, snapshot, system);
            if (face == null) continue;
            if (!r._docFamilies.TryGetValue(face.Family, out var list))
            {
                r._docFamilies[face.Family] = list = new List<FontFace>();
                r._docOrder.Add(face.Family);
            }
            list.Add(face);
        }
        return r._docFamilies.Count == 0 && snapshot == null && !embedStandard && !systemFonts ? null : r;
    }

    // ------------------------------------------------------------------ @font-face

    private static FontFace? FromFontFaceRule(List<Declaration> decls, Func<string, byte[]?> fetch, FontCollection.Snapshot? collection,
        Lazy<FontCollection.Snapshot>? system)
    {
        string? family = null, src = null;
        int wMin = 400, wMax = 400;
        bool weightGiven = false;
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
                        weightGiven = true;
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
                    var p = LoadSource(item.Trim(), fetch, collection, system);
                    if (p != null) return p;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || FontProgram.IsFormatError(ex))
                {
                    // try the next source
                }
            }
            return null;
        });
        return new FontFace(family!, wMin, wMax, italic, range, program, weightAuto: !weightGiven);
    }

    private static FontProgram? LoadSource(string item, Func<string, byte[]?> fetch, FontCollection.Snapshot? collection,
        Lazy<FontCollection.Snapshot>? system)
    {
        string? url = null, format = null, local = null;
        foreach (var tok in CssParser.Tokens(item))
        {
            var lower = tok.ToLowerInvariant();
            if (lower.StartsWith("url(", StringComparison.Ordinal) && tok.EndsWith(')')) url = Unquote(tok.Substring(4, tok.Length - 5));
            else if (lower.StartsWith("format(", StringComparison.Ordinal) && tok.EndsWith(')')) format = Unquote(tok.Substring(7, tok.Length - 8)).ToLowerInvariant();
            else if (lower.StartsWith("local(", StringComparison.Ordinal) && tok.EndsWith(')')) local = Unquote(tok.Substring(6, tok.Length - 7));
        }
        if (local != null)
        {
            // local() names a font by its full or PostScript name: a registered font, else an installed one when allowed.
            foreach (var faces in new[] { collection?.All, system?.Value.All })
            {
                if (faces == null) continue;
                foreach (var f in faces)
                    if (string.Equals(f.FullName, local, StringComparison.OrdinalIgnoreCase) || string.Equals(f.PostScriptName, local, StringComparison.OrdinalIgnoreCase))
                        return f.Program;
            }
            return null;
        }
        if (url == null) return null;
        // Skip formats we cannot read without downloading them (fetching may be expensive for the caller).
        if (format != null && !format.StartsWith("truetype", StringComparison.Ordinal) && !format.StartsWith("opentype", StringComparison.Ordinal)
            && format != "woff" && format != "woff2" && format != "collection" && !format.StartsWith("woff-variations", StringComparison.Ordinal)
            && !format.StartsWith("woff2-variations", StringComparison.Ordinal))
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

    internal static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) s = s.Substring(1, s.Length - 2);
        return s.Trim();
    }

    // ------------------------------------------------------------------ matching

    /// <summary>The font for a computed style: a chain over the font-family list with per-character fallback.</summary>
    public TextFont Resolve(string? familyList, FontFamily generic, int weight, bool italic) => Resolve(familyList, generic, weight, italic, FontVariation.Normal);

    /// <summary>The font for a computed style, with the instance of variable fonts its variation values pick.</summary>
    public TextFont Resolve(string? familyList, FontFamily generic, int weight, bool italic, FontVariation variation)
    {
        var key = (familyList ?? "", weight, italic, generic, variation);
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
                var fonts = MatchFamily(name, weight, italic, variation);
                if (fonts.Count > 0) candidates.AddRange(fonts);
                else if (StandardFamily(name) is FontFamily sf) candidates.Add(Standard(sf, bold, italic));
                else if (candidates.Count == 0) _report?.Missing(name); // the text falls back to a later family
            }
        }
        var standard = Standard(generic, bold, italic);
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
                foreach (var f in MatchFamily(family, weight, italic, variation))
                    if (f.Covers(cp)) return f;
            }
            return null;
        });
        _resolved[key] = result;
        return result;
    }

    /// <summary>A standard font, or its bundled embeddable replacement when every font must be embedded.</summary>
    private TextFont Standard(FontFamily family, bool bold, bool italic)
    {
        var sf = StandardFont.Get(family, bold, italic);
        return _embedStandard ? (TextFont?)BundledFonts.Get(sf) ?? sf : sf;
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
    private List<EmbeddedFont> MatchFamily(string family, int weight, bool italic, FontVariation variation)
    {
        var key = (family.ToLowerInvariant(), weight, italic, variation);
        if (_matched.TryGetValue(key, out var result)) return result;
        result = new List<EmbeddedFont>();
        // Installed fonts are only looked up by name, and never for the names that mean a standard font.
        if (!_docFamilies.TryGetValue(family, out var faces) && (_collection == null || !_collection.ByFamily.TryGetValue(family, out faces))
            && (_system == null || StandardFamily(family) != null || !_system.Value.ByFamily.TryGetValue(family, out faces)))
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
            if (program.Axes.Length > 0) program = Instance(program, face, weight, italic, variation, ref synthItalic);
            var fk = (program, synthBold, synthItalic, face.Range);
            if (!_fonts.TryGetValue(fk, out var font)) _fonts[fk] = font = new EmbeddedFont(program, synthBold, synthItalic, face.Range);
            result.Add(font);
        }
        _matched[key] = result;
        return result;
    }

    /// <summary>
    /// The instance of a variable font for the style, as CSS Fonts level 4 picks it: the weight within the face's
    /// declared range, the width from font-stretch, italic from an ital or slnt axis when the face is not italic itself,
    /// the optical size from the font size, and then font-variation-settings over all of them.
    /// </summary>
    private static FontProgram Instance(FontProgram program, FontFace face, int weight, bool italic, FontVariation variation, ref bool synthItalic)
    {
        var values = new Dictionary<uint, float>();
        if (program.Axis(FontVariations.Wght) != null) values[FontVariations.Wght] = Math.Clamp(weight, face.WeightMin, Math.Max(face.WeightMin, face.WeightMax));
        if (program.Axis(FontVariations.Wdth) != null) values[FontVariations.Wdth] = variation.Stretch;
        if (italic && !face.Italic)
        {
            if (program.Axis(FontVariations.Ital) != null) { values[FontVariations.Ital] = 1; synthItalic = false; }
            else if (program.Axis(FontVariations.Slnt) is { } slnt && slnt.Min < 0) { values[FontVariations.Slnt] = Math.Max(slnt.Min, -14); synthItalic = false; }
        }
        if (variation.OpticalSize > 0 && program.Axis(FontVariations.Opsz) != null) values[FontVariations.Opsz] = variation.OpticalSize;
        if (variation.Settings != null)
            foreach (var (tag, value) in ParseVariationSettings(variation.Settings)) values[tag] = value;
        return program.Instance(values);
    }

    /// <summary>font-variation-settings: "wght" 650, "wdth" 80. Invalid entries are skipped.</summary>
    internal static IEnumerable<(uint Tag, float Value)> ParseVariationSettings(string settings)
    {
        foreach (var part in CssParser.SplitTopLevel(settings, ','))
        {
            var tokens = CssParser.Tokens(part.Trim());
            if (tokens.Count != 2) continue;
            var tag = Unquote(tokens[0]);
            if (tag.Length != 4 || tag.Any(c => c < 0x20 || c > 0x7E) || tokens[0].Trim()[0] is not ('"' or '\'')) continue;
            if (!float.TryParse(tokens[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)) continue;
            yield return (FontProgram.Tag(tag), value);
        }
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
    internal static FontFamily? StandardFamily(string name) => name.ToLowerInvariant() switch
    {
        "serif" or "times" or "times new roman" or "times-roman" or "ui-serif" => FontFamily.Times,
        "monospace" or "courier" or "courier new" or "ui-monospace" => FontFamily.Courier,
        "sans-serif" or "helvetica" or "arial" or "system-ui" or "ui-sans-serif" or "-apple-system" or "blinkmacsystemfont"
            or "cursive" or "fantasy" => FontFamily.Helvetica,
        _ => null,
    };
}
