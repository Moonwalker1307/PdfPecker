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

    public ShapedGlyph(int gid, int start, int length)
    {
        Gid = gid;
        Start = start;
        Length = length;
    }

    public ShapedGlyph(int gid, int start, int length, float advance, float dx, float dy)
    {
        Gid = gid;
        Start = start;
        Length = length;
        Advance = advance;
        Dx = dx;
        Dy = dy;
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
    // GSUB lookup types.
    private const int LigatureLookup = 4;
    private const int ExtensionLookup = 7;

    // Ligature substitution subtable, format 1: a coverage of the first glyphs, and a set of ligatures for each.
    private const int SupportedSubtableFormat = 1;
    private const int FormatField = 0, CoverageField = 2, SetCountField = 4, SetOffsetsField = 6;

    // A ligature set holds the offsets of its ligatures. A ligature is its glyph, the number of components
    // (including the first glyph, which the coverage gives) and the components after the first.
    private const int LigatureOffsetsField = 2;
    private const int LigatureGlyphField = 0, ComponentCountField = 2, SecondComponentField = 4;
    private const int FirstTwoComponents = 2;

    private const int MaxGlyph = 0xFFFF;

    private static readonly uint[] LigatureFeatures = [FontProgram.Tag("liga"), FontProgram.Tag("clig")];

    private readonly struct Ligature(int glyph, int[] rest)
    {
        public int Glyph { get; } = glyph;

        /// <summary>The components after the first two, which the lookup key already holds.</summary>
        public int[] Rest { get; } = rest;
    }

    private readonly TableReader _reader;
    private readonly List<Dictionary<int, Ligature[]>> _lookups = new(); // first << 16 | second glyph -> ligatures, in the font's order
    private readonly ulong[]?[] _seconds; // first glyph -> bit set of the second glyphs of its ligatures (null: none)

    private Ligatures(FontProgram font)
    {
        _reader = new TableReader(font.Data);
        if (font.Table("GSUB") is { } gsub) ReadGsub(gsub.Offset, gsub.Length);
        _seconds = IndexSecondGlyphs();
    }

    /// <summary>The font's standard ligatures, or null when it has none.</summary>
    public static Ligatures? Load(FontProgram font)
    {
        try
        {
            var ligatures = new Ligatures(font);
            return ligatures._lookups.Count > 0 ? ligatures : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
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
                if (!StartsLigature(first, second) || !lookup.TryGetValue(first << 16 | second, out var candidates)) continue;
                foreach (var ligature in candidates)
                    if (TryReplace(glyphs, i, ligature)) break;
            }
        }
    }

    // The ligature replaces the glyphs from the first to its last component, which then count as one glyph that
    // stands for all their characters.
    private static bool TryReplace(List<ShapedGlyph> glyphs, int first, Ligature ligature)
    {
        var rest = ligature.Rest;
        if (first + 1 + rest.Length >= glyphs.Count) return false;
        for (int k = 0; k < rest.Length; k++)
            if (glyphs[first + 2 + k].Gid != rest[k]) return false;
        var last = glyphs[first + 1 + rest.Length];
        glyphs[first] = new ShapedGlyph(ligature.Glyph, glyphs[first].Start, last.Start + last.Length - glyphs[first].Start);
        glyphs.RemoveRange(first + 1, 1 + rest.Length);
        return true;
    }

    // Sized by the glyph ids that occur, which are small for Latin ligatures even in large fonts.
    private ulong[]?[] IndexSecondGlyphs()
    {
        int maxFirst = -1, maxSecond = 0;
        foreach (var lookup in _lookups)
            foreach (int key in lookup.Keys)
            {
                maxFirst = Math.Max(maxFirst, key >>> 16);
                maxSecond = Math.Max(maxSecond, key & 0xFFFF);
            }
        var seconds = new ulong[]?[maxFirst + 1];
        foreach (var lookup in _lookups)
            foreach (int key in lookup.Keys)
            {
                int second = key & 0xFFFF;
                (seconds[key >>> 16] ??= new ulong[maxSecond / 64 + 1])[second >> 6] |= 1UL << second;
            }
        return seconds;
    }

    private void ReadGsub(int gsub, int length)
    {
        if (!LayoutLists.TryOpen(_reader, gsub, length, out var lists)) return;
        foreach (int index in lists.FindLookupIndexes(LigatureFeatures))
        {
            if (lists.Lookup(index) is not { } lookup) continue;
            Dictionary<int, Ligature[]>? map = null;
            for (int i = 0; i < lookup.SubtableCount; i++)
            {
                var (type, subtable) = lookup.Subtable(i, ExtensionLookup);
                if (type == LigatureLookup && _reader.U16(subtable + FormatField) == SupportedSubtableFormat)
                    ReadLigatureSubstitution(subtable, map ??= new Dictionary<int, Ligature[]>());
            }
            if (map is { Count: > 0 }) _lookups.Add(map);
        }
    }

    // A ligature substitution subtable: the first glyph's coverage index picks a set of ligatures, tried in order.
    // An earlier subtable of the same lookup is tried first. Ligatures are indexed by their first two glyphs.
    private void ReadLigatureSubstitution(int subtable, Dictionary<int, Ligature[]> map)
    {
        int setCount = _reader.U16(subtable + SetCountField);
        var coveredFirstGlyphs = new HashSet<int>();
        var byPair = new Dictionary<int, List<Ligature>>();
        var coverage = new CoverageTable(_reader, subtable + _reader.U16(subtable + CoverageField));
        foreach (var (glyph, index) in coverage.Entries())
        {
            if (index >= setCount || glyph > MaxGlyph || !coveredFirstGlyphs.Add(glyph)) continue;
            int set = subtable + _reader.U16(subtable + SetOffsetsField + index * 2);
            for (int i = 0, n = _reader.U16(set); i < n; i++)
            {
                int ligature = set + _reader.U16(set + LigatureOffsetsField + i * 2);
                int componentCount = _reader.U16(ligature + ComponentCountField);
                if (componentCount < 2) continue; // a single glyph is not a ligature
                var rest = new int[componentCount - FirstTwoComponents];
                for (int k = 0; k < rest.Length; k++) rest[k] = _reader.U16(ligature + SecondComponentField + 2 + k * 2);
                int key = glyph << 16 | _reader.U16(ligature + SecondComponentField);
                if (!byPair.TryGetValue(key, out var list)) byPair[key] = list = new List<Ligature>();
                list.Add(new Ligature(_reader.U16(ligature + LigatureGlyphField), rest));
            }
        }
        foreach (var (key, list) in byPair)
            map[key] = map.TryGetValue(key, out var earlier) ? [.. earlier, .. list] : list.ToArray();
    }
}
