using System.Collections.Concurrent;

namespace PdfPecker.Fonts;

// Glyph digests: for each lookup, a small summary of the glyphs its subtables can start at, so that a lookup passes
// over the glyphs it cannot apply to without trying each of its subtables, as HarfBuzz's set digests do. A font such
// as Inter has a contextual alternates lookup of 61 subtables, which every glyph of every word otherwise tries.
internal sealed partial class OpenTypeLayout
{
    // Every subtable, coverage entry and coverage glyph read counts as one step, at most this many per lookup and in
    // all for the font, so a damaged font with huge or inverted coverages or thousands of subtables cannot make the
    // digests expensive; past either, a lookup gets the digest that admits every glyph, which only means its subtables
    // are tried as they always were, each of them spending the shaping call's operation budget.
    private const int MaxDigestReadsPerLookup = 1 << 16, MaxDigestReads = 1 << 22;
    private int _digestReads;
    private GlyphDigest?[]? _gsubDigests, _gposDigests;

    // The digests by lookup offset (and table, which decides how subtables are read), so that many lookup indices
    // pointing at one lookup build its digest only once.
    private readonly ConcurrentDictionary<long, GlyphDigest> _digestsByOffset = new();

    /// <summary>
    /// Three 64-bit masks of glyph id bits (bits 0-5, 4-9 and 9-14 of the id). A glyph whose three bits are not all set
    /// is in none of the lookup's coverages; one whose bits are set may be.
    /// </summary>
    private sealed class GlyphDigest(ulong low, ulong middle, ulong high)
    {
        public static readonly GlyphDigest All = new(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);

        public bool MayHave(int gid)
            => (low >> (gid & 63) & 1) != 0 && (middle >> ((gid >> 4) & 63) & 1) != 0 && (high >> ((gid >> 9) & 63) & 1) != 0;
    }

    /// <summary>The digest of the glyphs a lookup can start at, built the first time the lookup runs.</summary>
    private GlyphDigest DigestOf(bool gpos, int table, int lookupIndex, int lookup)
    {
        ref var digests = ref gpos ? ref _gposDigests : ref _gsubDigests;
        int count = _reader.U16(table + _reader.U16(table + LookupListField));
        var array = digests;
        if (array == null) Interlocked.CompareExchange(ref digests, array = new GlyphDigest?[count], null);
        array = digests!;
        if ((uint)lookupIndex >= (uint)array.Length) return GlyphDigest.All;
        // The digests are immutable, so two threads that build the same one at once both store an equal one.
        if (Volatile.Read(ref array[lookupIndex]) is { } digest) return digest;
        long key = (long)lookup << 1 | (gpos ? 1L : 0L);
        if (!_digestsByOffset.TryGetValue(key, out digest))
            digest = _digestsByOffset.GetOrAdd(key, BuildDigest(gpos, lookup));
        Volatile.Write(ref array[lookupIndex], digest);
        return digest;
    }

    private GlyphDigest BuildDigest(bool gpos, int lookup)
    {
        ulong low = 0, middle = 0, high = 0;
        int reads = 0;
        bool Spent() => ++reads > MaxDigestReadsPerLookup || Interlocked.Increment(ref _digestReads) > MaxDigestReads;
        try
        {
            var subtables = new LayoutLookup(_reader, lookup);
            int extension = gpos ? ExtensionPositioning : ExtensionSubstitution;
            for (int s = 0; s < subtables.SubtableCount; s++)
            {
                if (Spent()) return GlyphDigest.All;
                var (type, subtable) = subtables.Subtable(s, extension);
                int coverage = FirstCoverage(gpos, type, subtable);
                if (coverage < 0) continue;
                int format = _reader.U16(coverage), entries = _reader.U16(coverage + CoverageCountField);
                for (int e = 0; e < entries; e++)
                {
                    if (Spent()) return GlyphDigest.All;
                    int first, last;
                    if (format == CoverageListFormat) first = last = _reader.U16(coverage + CoverageEntriesField + e * 2);
                    else if (format == CoverageRangeFormat)
                    {
                        int range = coverage + CoverageEntriesField + e * CoverageRangeLength;
                        first = _reader.U16(range);
                        last = _reader.U16(range + CoverageRangeEndField);
                    }
                    else break;
                    for (int g = first; g <= last; g++)
                    {
                        if (Spent()) return GlyphDigest.All;
                        low |= 1UL << (g & 63);
                        middle |= 1UL << ((g >> 4) & 63);
                        high |= 1UL << ((g >> 9) & 63);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            // A damaged subtable fails when the lookup is applied, as it always has; the digest must not hide it.
            SwallowedErrors.Record(ex);
            return GlyphDigest.All;
        }
        return new GlyphDigest(low, middle, high);
    }

    // The features the shaper applies to text of the scripts that are not complex, and the scripts it looks them up
    // in (see ComplexShaper's plans and FontProgram.ContextualFeatures).
    private static readonly uint[] DefaultSubstitutions =
        [.. new[] { "rvrn", "ccmp", "locl", "rtlm", "rlig", "calt", "rclt", "liga", "clig" }.Select(FontProgram.Tag)];
    private static readonly uint[] DefaultPositionings =
        [.. new[] { "abvm", "blwm", "curs", "dist", "kern", "mark", "mkmk" }.Select(FontProgram.Tag)];
    private static readonly uint[] SimpleScripts = [.. new[] { "latn", "grek", "cyrl", "DFLT" }.Select(FontProgram.Tag)];

    /// <summary>
    /// The glyphs that can take part in a lookup of more than one glyph, one bit per glyph id, among the lookups the
    /// shaper applies to text of the scripts that are not complex: where GSUB contextual, reverse chaining and GPOS
    /// cursive, mark and contextual lookups can start, and the components of ligatures after their first. Text whose
    /// glyphs are none of them and that shaping leaves as they are is shaped next to any other such text as on its
    /// own, apart from the pair kerning between them (see EmbeddedFont.InertInContext). Null when the font's lookups
    /// are too many to read within the digests' budget, or damaged.
    /// </summary>
    public ulong[]? MultiGlyphStarts()
    {
        var bits = new ulong[(ushort.MaxValue + 1) / 64];
        int reads = 0;
        try
        {
            for (int t = 0; t < 2; t++)
            {
                bool gpos = t == 1;
                int table = gpos ? _gpos : _gsub;
                if (table < 0) continue;
                var lookups = new HashSet<int>();
                foreach (uint script in SimpleScripts)
                    foreach (uint feature in gpos ? DefaultPositionings : DefaultSubstitutions)
                        lookups.UnionWith(FeatureLookups(gpos, script, feature));
                int extension = gpos ? ExtensionPositioning : ExtensionSubstitution;
                foreach (int l in lookups)
                {
                    int lookup = LookupOffset(table, l);
                    if (lookup < 0) continue;
                    var subtables = new LayoutLookup(_reader, lookup);
                    for (int s = 0; s < subtables.SubtableCount; s++)
                    {
                        if (++reads > MaxDigestReads) return null;
                        var (type, subtable) = subtables.Subtable(s, extension);
                        if (!gpos && type == LigatureSubstitution)
                        {
                            if (!AddLigatureComponents(subtable, bits, ref reads)) return null;
                            continue;
                        }
                        bool several = gpos ? type is >= CursivePositioning and <= ChainedContextPositioning
                            : type is ContextSubstitution or ChainedContextSubstitution or ReverseChainingSubstitution;
                        if (!several) continue;
                        // A rule that needs a mark, such as a dotless i before a combining accent, cannot match text
                        // without marks, which inert text is.
                        bool context = gpos ? type is ContextPositioning or ChainedContextPositioning : type is ContextSubstitution or ChainedContextSubstitution;
                        if (context && NeedsMark(subtable, gpos ? type == ChainedContextPositioning : type == ChainedContextSubstitution, ref reads)) continue;
                        if (reads > MaxDigestReads) return null;
                        int coverage = FirstCoverage(gpos, type, subtable);
                        if (coverage >= 0 && !AddCoverage(coverage, bits, ref reads)) return null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
        return bits;
    }

    // Whether a contextual subtable of format 3 has a glyph of its backtrack, input or lookahead that only marks match.
    private bool NeedsMark(int sub, bool chained, ref int reads)
    {
        if (_reader.U16(sub + FormatField) != CoverageRulesFormat || _glyphClassDef < 0) return false;
        int arrays, count;
        if (chained)
        {
            arrays = sub + ChainArraysField;
            for (int a = 0; a < 3; a++)
            {
                count = _reader.U16(arrays);
                for (int k = 0; k < count; k++)
                    if (OnlyMarks(sub + _reader.U16(arrays + 2 + k * 2), ref reads)) return true;
                arrays += 2 + count * 2;
            }
            return false;
        }
        count = _reader.U16(sub + CoverageInputCountField);
        for (int k = 0; k < count; k++)
            if (OnlyMarks(sub + _reader.U16(sub + CoverageInputField + k * 2), ref reads)) return true;
        return false;
    }

    // Whether every glyph of a coverage is a mark; false for an empty or unknown coverage, or once the budget runs out.
    private bool OnlyMarks(int coverage, ref int reads)
    {
        int format = _reader.U16(coverage), entries = _reader.U16(coverage + CoverageCountField);
        if (entries == 0) return false;
        for (int e = 0; e < entries; e++)
        {
            int first, last;
            if (format == CoverageListFormat) first = last = _reader.U16(coverage + CoverageEntriesField + e * 2);
            else if (format == CoverageRangeFormat)
            {
                int range = coverage + CoverageEntriesField + e * CoverageRangeLength;
                first = _reader.U16(range);
                last = _reader.U16(range + CoverageRangeEndField);
            }
            else return false;
            for (int g = first; g <= last; g++)
                if (++reads > MaxDigestReads || (GlyphProps(g) & Mark) == 0) return false;
        }
        return true;
    }

    // Sets the bits of a coverage's glyphs; false when the budget runs out or the format is unknown.
    private bool AddCoverage(int coverage, ulong[] bits, ref int reads)
    {
        int format = _reader.U16(coverage), entries = _reader.U16(coverage + CoverageCountField);
        for (int e = 0; e < entries; e++)
        {
            if (++reads > MaxDigestReads) return false;
            int first, last;
            if (format == CoverageListFormat) first = last = _reader.U16(coverage + CoverageEntriesField + e * 2);
            else if (format == CoverageRangeFormat)
            {
                int range = coverage + CoverageEntriesField + e * CoverageRangeLength;
                first = _reader.U16(range);
                last = _reader.U16(range + CoverageRangeEndField);
            }
            else return false;
            for (int g = first; g <= last; g++)
            {
                if (++reads > MaxDigestReads) return false;
                bits[g >> 6] |= 1UL << (g & 63);
            }
        }
        return true;
    }

    // Sets the bits of every ligature component after the first in a ligature substitution; false when the budget
    // runs out or the format is unknown.
    private bool AddLigatureComponents(int sub, ulong[] bits, ref int reads)
    {
        if (_reader.U16(sub + FormatField) != OnlyFormat) return false;
        for (int set = 0, sets = _reader.U16(sub + SetCountField); set < sets; set++)
        {
            int ligatures = sub + _reader.U16(sub + SetOffsetsField + set * 2);
            for (int l = 0, count = _reader.U16(ligatures + ArrayCountField); l < count; l++)
            {
                int ligature = ligatures + _reader.U16(ligatures + ArrayEntriesField + l * 2);
                for (int k = 1, components = _reader.U16(ligature + ComponentCountField); k < components; k++)
                {
                    if (++reads > MaxDigestReads) return false;
                    int g = _reader.U16(ligature + ComponentsField + (k - 1) * 2);
                    bits[g >> 6] |= 1UL << (g & 63);
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The coverage that decides whether a subtable applies at a glyph: the one at the start of most subtables, and the
    /// first input glyph's for contextual subtables of format 3. -1 for a subtable that never applies.
    /// </summary>
    private int FirstCoverage(bool gpos, int type, int sub)
    {
        bool context = gpos ? type is ContextPositioning or ChainedContextPositioning : type is ContextSubstitution or ChainedContextSubstitution;
        if (context && _reader.U16(sub + FormatField) == CoverageRulesFormat)
        {
            bool chained = gpos ? type == ChainedContextPositioning : type == ChainedContextSubstitution;
            if (!chained)
                return _reader.U16(sub + CoverageInputCountField) == 0 ? -1 : sub + _reader.U16(sub + CoverageInputField);
            int input = sub + ChainArraysField + 2 + _reader.U16(sub + ChainArraysField) * 2;
            return _reader.U16(input) == 0 ? -1 : sub + _reader.U16(input + 2);
        }
        return sub + _reader.U16(sub + CoverageField);
    }
}
