using System.Globalization;
using System.Text;

namespace PdfPecker.Fonts;

/// <summary>
/// OpenType features turned on with font-variant-numeric or font-feature-settings, such as tabular figures (tnum),
/// old-style figures (onum), a slashed zero (zero) or a stylistic set (ss01). The features that replace one glyph with
/// another (GSUB single and alternate substitutions, which is what these features are) are applied to the font's
/// character map, so measuring, kerning, ligatures and the PDF all see the substituted glyphs.
/// </summary>
internal static class FontFeatures
{
    /// <summary>
    /// The features to apply, as a canonical key such as "onum,tnum,ss01=2": the features of font-variant-numeric,
    /// then font-feature-settings, which wins for a tag both name. Features turned off and ligature features (handled
    /// by <see cref="Ligatures"/>) are left out. Null when there are none.
    /// </summary>
    public static string? Key(string? numeric, string? settings)
    {
        var features = new Dictionary<string, int>(StringComparer.Ordinal);
        if (numeric != null)
            foreach (var word in numeric.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string? tag = word switch
                {
                    "lining-nums" => "lnum",
                    "oldstyle-nums" => "onum",
                    "proportional-nums" => "pnum",
                    "tabular-nums" => "tnum",
                    "diagonal-fractions" => "frac",
                    "stacked-fractions" => "afrc",
                    "ordinal" => "ordn",
                    "slashed-zero" => "zero",
                    _ => null,
                };
                if (tag != null) features[tag] = 1;
            }
        if (settings != null)
            foreach (var (tag, value) in ParseSettings(settings)) features[tag] = value;
        var key = new StringBuilder();
        foreach (var (tag, value) in features.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (value == 0 || tag is "liga" or "clig" or "kern") continue;
            if (key.Length > 0) key.Append(',');
            key.Append(tag);
            if (value != 1) key.Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
        }
        return key.Length == 0 ? null : key.ToString();
    }

    /// <summary>The (tag, value) pairs of a font-feature-settings value: "tnum", "ss01" 2, "liga" off.</summary>
    public static IEnumerable<(string Tag, int Value)> ParseSettings(string value)
    {
        foreach (var part in value.Split(','))
        {
            var p = part.Trim();
            if (p.Length < 6 || (p[0] != '"' && p[0] != '\'') || p[5] != p[0]) continue;
            var tag = p.Substring(1, 4);
            if (tag.Any(c => c < 0x20 || c > 0x7E)) continue;
            var arg = p.Substring(6).Trim().ToLowerInvariant();
            int v = arg switch
            {
                "" or "on" => 1,
                "off" => 0,
                _ => int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 ? n : -1,
            };
            if (v >= 0) yield return (tag, v);
        }
    }

    /// <summary>
    /// The glyph substitutions the features make, as a glyph map (null when they change nothing). Lookups apply in the
    /// font's order, each to the result of the ones before, as in a shaper; a value above 1 picks that alternate.
    /// </summary>
    public static int[]? Substitutions(FontProgram font, string key)
    {
        if (font.Table("GSUB") is not { Length: >= 10 } gsubTable) return null;
        var d = font.Data;
        int gsub = gsubTable.Offset;
        int U16(int o) => d[o] << 8 | d[o + 1];
        uint U32(int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        if (U16(gsub) != 1) return null;

        var wanted = new Dictionary<uint, int>();
        foreach (var part in key.Split(','))
        {
            int eq = part.IndexOf('=');
            wanted[FontProgram.Tag(eq < 0 ? part : part[..eq])] = eq < 0 ? 1 : int.Parse(part[(eq + 1)..], CultureInfo.InvariantCulture);
        }

        int scripts = gsub + U16(gsub + 4), features = gsub + U16(gsub + 6), lookups = gsub + U16(gsub + 8);
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
        if (langSys < 0) return null;

        // A feature whose lookups are all single or alternate substitutions applies everywhere. One with contextual
        // lookups (fractions and ordinals usually are) only substitutes in context, which this does not do, so it is
        // left out rather than applied to every glyph.
        int lookupCount = U16(lookups);
        int TypeOf(int li)
        {
            int lookup = lookups + U16(lookups + 2 + li * 2);
            int type = U16(lookup);
            if (type == 7 && U16(lookup + 4) > 0) type = U16(lookup + U16(lookup + 6) + 2);
            return type;
        }
        var lookupValues = new SortedDictionary<int, int>(); // lookup index -> the value of its feature
        int featureCount = U16(features);
        for (int i = 0, n = U16(langSys + 4); i < n; i++)
        {
            int fi = U16(langSys + 6 + i * 2);
            if (fi >= featureCount || !wanted.TryGetValue(U32(features + 2 + fi * 6), out int value)) continue;
            int feature = features + U16(features + 2 + fi * 6 + 4);
            var indices = new List<int>();
            for (int j = 0, m = U16(feature + 2); j < m; j++) indices.Add(U16(feature + 4 + j * 2));
            if (indices.Exists(li => li >= lookupCount || TypeOf(li) is not (1 or 3))) continue;
            foreach (int li in indices) lookupValues[li] = value;
        }
        if (lookupValues.Count == 0) return null;

        int glyphs = font.NumGlyphs;
        var map = new int[glyphs];
        for (int g = 0; g < glyphs; g++) map[g] = g;
        bool changed = false;
        foreach (var (li, value) in lookupValues)
        {
            int lookup = lookups + U16(lookups + 2 + li * 2);
            int type = U16(lookup);
            var step = new Dictionary<int, int>();
            for (int s = 0, n = U16(lookup + 4); s < n; s++)
            {
                int sub = lookup + U16(lookup + 6 + s * 2);
                int subType = type;
                if (type == 7)
                {
                    subType = U16(sub + 2);
                    sub += (int)U32(sub + 4);
                }
                int format = U16(sub);
                foreach (var (glyph, index) in Coverage(d, sub + U16(sub + 2)))
                {
                    if (step.ContainsKey(glyph)) continue; // an earlier subtable of the lookup wins
                    if (subType == 1 && format == 1) step[glyph] = (glyph + (short)U16(sub + 4)) & 0xFFFF;
                    else if (subType == 1 && format == 2 && index < U16(sub + 4)) step[glyph] = U16(sub + 6 + index * 2);
                    else if (subType == 3 && format == 1 && index < U16(sub + 4))
                    {
                        int set = sub + U16(sub + 6 + index * 2);
                        int count = U16(set);
                        if (count > 0) step[glyph] = U16(set + 2 + (Math.Min(value, count) - 1) * 2);
                    }
                }
            }
            if (step.Count == 0) continue;
            for (int g = 0; g < glyphs; g++)
                if (step.TryGetValue(map[g], out int to) && to < glyphs) { map[g] = to; changed = true; }
        }
        return changed ? map : null;
    }

    private static IEnumerable<(int Glyph, int Index)> Coverage(byte[] d, int cov)
    {
        int U16(int o) => d[o] << 8 | d[o + 1];
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
