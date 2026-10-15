namespace PdfPecker.Fonts;

/// <summary>
/// A glyph of shaped text and the characters it stands for (several for a ligature, none for the extra glyphs of a
/// decomposition). Complex shaping also gives its advance and offset in 1/1000 em, which replace the font's advance
/// and kerning.
/// </summary>
internal readonly struct ShapedGlyph
{
    public readonly int Gid;
    public readonly int Start, Length; // the characters in the source string
    public readonly float Advance, Dx, Dy;
    public ShapedGlyph(int gid, int start, int length) { Gid = gid; Start = start; Length = length; }

    public ShapedGlyph(int gid, int start, int length, float advance, float dx, float dy)
    {
        Gid = gid; Start = start; Length = length; Advance = advance; Dx = dx; Dy = dy;
    }
}

/// <summary>
/// The standard ligatures of a font, as browsers apply them by default (font-variant-ligatures: normal): the ligature
/// substitutions of the GSUB 'liga' and 'clig' features, for the Latin script or the default one. Lookups apply in
/// order, each over the whole run, as HarfBuzz does. Contextual and other substitutions are not applied, and marks
/// are not skipped, which Latin, Greek and Cyrillic text without combining marks does not need.
/// Malformed tables give no ligatures rather than an error.
/// </summary>
internal sealed class Ligatures
{
    private readonly struct Ligature
    {
        public readonly int Glyph;
        public readonly int[] Rest; // the components after the first two
        public Ligature(int glyph, int[] rest) { Glyph = glyph; Rest = rest; }
    }

    private readonly byte[] _d;
    private readonly List<Dictionary<int, Ligature[]>> _lookups = new(); // first << 16 | second glyph -> ligatures, in the font's order
    private readonly ulong[]?[] _seconds; // first glyph -> bit set of the second glyphs of its ligatures (null: none)

    private Ligatures(FontProgram font)
    {
        _d = font.Data;
        if (font.Table("GSUB") is { } gsub) ReadGsub(gsub.Offset, gsub.Length);
        // Sized by the glyph ids that occur, which are small for Latin ligatures even in large fonts.
        int maxFirst = -1, maxSecond = 0;
        foreach (var lookup in _lookups)
            foreach (int key in lookup.Keys)
            {
                maxFirst = Math.Max(maxFirst, key >>> 16);
                maxSecond = Math.Max(maxSecond, key & 0xFFFF);
            }
        _seconds = new ulong[]?[maxFirst + 1];
        foreach (var lookup in _lookups)
            foreach (int key in lookup.Keys)
                (_seconds[key >>> 16] ??= new ulong[maxSecond / 64 + 1])[(key & 0xFFFF) >> 6] |= 1UL << (key & 0xFFFF);
    }

    /// <summary>The font's standard ligatures, or null when it has none.</summary>
    public static Ligatures? Load(FontProgram font)
    {
        try
        {
            var l = new Ligatures(font);
            return l._lookups.Count > 0 ? l : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>True when a ligature can start with the glyph.</summary>
    public bool StartsLigature(int gid) => (uint)gid < (uint)_seconds.Length && _seconds[gid] != null;

    /// <summary>True when a ligature can start with the two glyphs; text without such a pair needs no shaping.</summary>
    public bool StartsLigature(int first, int second)
        => (uint)first < (uint)_seconds.Length && _seconds[first] is { } bits && (uint)second < (uint)bits.Length * 64
           && (bits[second >> 6] & 1UL << second) != 0;

    /// <summary>Replaces the glyphs that form ligatures with the ligature glyphs.</summary>
    public void Apply(List<ShapedGlyph> glyphs)
    {
        foreach (var lookup in _lookups)
        {
            for (int i = 0; i < glyphs.Count - 1; i++)
            {
                int first = glyphs[i].Gid;
                int second = glyphs[i + 1].Gid;
                if (!StartsLigature(first, second) || !lookup.TryGetValue(first << 16 | second, out var set)) continue;
                foreach (var lig in set)
                {
                    var rest = lig.Rest;
                    if (i + 1 + rest.Length >= glyphs.Count) continue;
                    int k = 0;
                    while (k < rest.Length && glyphs[i + 2 + k].Gid == rest[k]) k++;
                    if (k < rest.Length) continue;
                    var last = glyphs[i + 1 + rest.Length];
                    glyphs[i] = new ShapedGlyph(lig.Glyph, glyphs[i].Start, last.Start + last.Length - glyphs[i].Start);
                    glyphs.RemoveRange(i + 1, 1 + rest.Length);
                    break;
                }
            }
        }
    }

    private int U16(int o) => _d[o] << 8 | _d[o + 1];
    private uint U32(int o) => (uint)(_d[o] << 24 | _d[o + 1] << 16 | _d[o + 2] << 8 | _d[o + 3]);

    private void ReadGsub(int gsub, int length)
    {
        if (length < 10 || U16(gsub) != 1) return;
        int scripts = gsub + U16(gsub + 4), features = gsub + U16(gsub + 6), lookups = gsub + U16(gsub + 8);

        // The features of the Latin script (or the default script) in its default language system.
        int langSys = -1, fallback = -1;
        for (int i = 0, n = U16(scripts); i < n; i++)
        {
            uint tag = U32(scripts + 2 + i * 6);
            int script = scripts + U16(scripts + 2 + i * 6 + 4);
            int def = U16(script);
            if (def == 0) continue;
            if (tag == FontProgram.Tag("latn")) langSys = script + def;
            else if (tag == FontProgram.Tag("DFLT") || fallback < 0) fallback = script + def;
        }
        if (langSys < 0) langSys = fallback;
        if (langSys < 0) return;

        uint liga = FontProgram.Tag("liga"), clig = FontProgram.Tag("clig");
        var lookupIndices = new SortedSet<int>();
        int featureCount = U16(features);
        for (int i = 0, n = U16(langSys + 4); i < n; i++)
        {
            int fi = U16(langSys + 6 + i * 2);
            if (fi >= featureCount) continue;
            uint tag = U32(features + 2 + fi * 6);
            if (tag != liga && tag != clig) continue;
            int feature = features + U16(features + 2 + fi * 6 + 4);
            for (int j = 0, m = U16(feature + 2); j < m; j++) lookupIndices.Add(U16(feature + 4 + j * 2));
        }

        int lookupCount = U16(lookups);
        foreach (int li in lookupIndices)
        {
            if (li >= lookupCount) continue;
            int lookup = lookups + U16(lookups + 2 + li * 2);
            int type = U16(lookup);
            Dictionary<int, Ligature[]>? map = null;
            for (int s = 0, n = U16(lookup + 4); s < n; s++)
            {
                int sub = lookup + U16(lookup + 6 + s * 2);
                int subType = type;
                if (type == 7) // extension: the real subtable is behind a 32-bit offset
                {
                    subType = U16(sub + 2);
                    sub += (int)U32(sub + 4);
                }
                if (subType == 4 && U16(sub) == 1) ReadLigatureSubst(sub, map ??= new Dictionary<int, Ligature[]>());
            }
            if (map is { Count: > 0 }) _lookups.Add(map);
        }
    }

    // A ligature substitution subtable: the first glyph's coverage index picks a set of ligatures, tried in order.
    // An earlier subtable of the same lookup is tried first. Ligatures are indexed by their first two glyphs.
    private void ReadLigatureSubst(int sub, Dictionary<int, Ligature[]> map)
    {
        int setCount = U16(sub + 4);
        var seen = new HashSet<int>(); // first glyphs this subtable covers
        var byPair = new Dictionary<int, List<Ligature>>();
        foreach (var (glyph, index) in Coverage(sub + U16(sub + 2)))
        {
            if (index >= setCount || glyph > 0xFFFF || !seen.Add(glyph)) continue;
            int set = sub + U16(sub + 6 + index * 2);
            for (int i = 0, n = U16(set); i < n; i++)
            {
                int lig = set + U16(set + 2 + i * 2);
                int count = U16(lig + 2);
                if (count < 2) continue; // a single glyph is not a ligature
                var rest = new int[count - 2];
                for (int k = 0; k < rest.Length; k++) rest[k] = U16(lig + 6 + k * 2);
                int key = glyph << 16 | U16(lig + 4);
                if (!byPair.TryGetValue(key, out var list)) byPair[key] = list = new List<Ligature>();
                list.Add(new Ligature(U16(lig), rest));
            }
        }
        foreach (var (key, list) in byPair)
            map[key] = map.TryGetValue(key, out var earlier) ? [.. earlier, .. list] : list.ToArray();
    }

    private IEnumerable<(int Glyph, int Index)> Coverage(int cov)
    {
        int format = U16(cov), n = U16(cov + 2);
        if (format == 1)
            for (int i = 0; i < n; i++) yield return (U16(cov + 4 + i * 2), i);
        else if (format == 2)
            for (int i = 0; i < n; i++)
            {
                int r = cov + 4 + i * 6;
                int start = U16(r), end = U16(r + 2), index = U16(r + 4);
                for (int g = start; g <= end; g++) yield return (g, index + g - start);
            }
    }
}
