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
