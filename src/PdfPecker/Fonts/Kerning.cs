using System.Collections.Concurrent;

namespace PdfPecker.Fonts;

/// <summary>
/// Pair kerning of a font, as browsers apply it by default (font-kerning: auto): the GPOS 'kern' feature's pair
/// adjustments, or the legacy 'kern' table when the font has no GPOS kerning. Values are in font units and only
/// adjust the advance of the first glyph of a pair, which is all horizontal Latin, Greek and Cyrillic text needs.
/// Malformed tables give no kerning rather than an error.
/// </summary>
internal sealed class Kerning
{
    private readonly byte[] _d;
    private readonly List<int> _pairPos = new();       // offsets of PairPos subtables, in lookup order
    private readonly int _kern = -1, _kernPairs;       // legacy kern table: offset of the first pair, and count
    private readonly ConcurrentDictionary<int, short> _cache = new();

    private Kerning(FontProgram font)
    {
        _d = font.Data;
        if (font.Table("GPOS") is { } gpos) ReadGpos(gpos.Offset, gpos.Length);
        if (_pairPos.Count == 0 && font.Table("kern") is { } kern) (_kern, _kernPairs) = ReadKern(kern.Offset, kern.Length);
    }

    /// <summary>The font's kerning, or null when it has none.</summary>
    public static Kerning? Load(FontProgram font)
    {
        try
        {
            var k = new Kerning(font);
            return k._pairPos.Count > 0 || k._kernPairs > 0 ? k : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The adjustment to the first glyph's advance, in font units (usually negative).</summary>
    public int Pair(int left, int right)
    {
        if ((left | right) > 0xFFFF || left == 0 || right == 0) return 0;
        int key = left << 16 | right;
        if (_cache.TryGetValue(key, out short v)) return v;
        int value;
        try { value = _pairPos.Count > 0 ? LookupGpos(left, right) : LookupKern(left, right); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { value = 0; }
        v = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        _cache.TryAdd(key, v);
        return v;
    }

    private int U16(int o) => _d[o] << 8 | _d[o + 1];
    private short S16(int o) => (short)(_d[o] << 8 | _d[o + 1]);
    private uint U32(int o) => (uint)(_d[o] << 24 | _d[o + 1] << 16 | _d[o + 2] << 8 | _d[o + 3]);

    private void ReadGpos(int gpos, int length)
    {
        if (length < 10 || U16(gpos) != 1) return;
        int scripts = gpos + U16(gpos + 4), features = gpos + U16(gpos + 6), lookups = gpos + U16(gpos + 8);

        // The 'kern' features of the Latin script (or the default script) in its default language system.
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

        var lookupIndices = new SortedSet<int>();
        int featureCount = U16(features);
        for (int i = 0, n = U16(langSys + 4); i < n; i++)
        {
            int fi = U16(langSys + 6 + i * 2);
            if (fi >= featureCount || U32(features + 2 + fi * 6) != FontProgram.Tag("kern")) continue;
            int feature = features + U16(features + 2 + fi * 6 + 4);
            for (int j = 0, m = U16(feature + 2); j < m; j++) lookupIndices.Add(U16(feature + 4 + j * 2));
        }

        int lookupCount = U16(lookups);
        foreach (int li in lookupIndices)
        {
            if (li >= lookupCount) continue;
            int lookup = lookups + U16(lookups + 2 + li * 2);
            int type = U16(lookup);
            for (int s = 0, n = U16(lookup + 4); s < n; s++)
            {
                int sub = lookup + U16(lookup + 6 + s * 2);
                int subType = type;
                if (type == 9) // extension: the real subtable is behind a 32-bit offset
                {
                    subType = U16(sub + 2);
                    sub += (int)U32(sub + 4);
                }
                if (subType == 2 && (U16(sub) == 1 || U16(sub) == 2)) _pairPos.Add(sub);
            }
        }
    }

    private int LookupGpos(int left, int right)
    {
        // Each subtable that covers the pair adjusts it; real fonts list a pair once.
        foreach (int sub in _pairPos)
        {
            int coverage = CoverageIndex(sub + U16(sub + 2), left);
            if (coverage < 0) continue;
            int vf1 = U16(sub + 4), vf2 = U16(sub + 6);
            int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
            if (U16(sub) == 1)
            {
                if (coverage >= U16(sub + 8)) continue;
                int set = sub + U16(sub + 10 + coverage * 2);
                int lo = 0, hi = U16(set) - 1, rec = 2 + size1 + size2;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    int r = set + 2 + mid * rec;
                    int g = U16(r);
                    if (g == right) return XAdvance(r + 2, vf1);
                    if (g < right) lo = mid + 1; else hi = mid - 1;
                }
            }
            else
            {
                int c1 = ClassOf(sub + U16(sub + 8), left), c2 = ClassOf(sub + U16(sub + 10), right);
                int class1Count = U16(sub + 12), class2Count = U16(sub + 14);
                if (c1 >= class1Count || c2 >= class2Count) continue;
                int r = sub + 16 + (c1 * class2Count + c2) * (size1 + size2);
                return XAdvance(r, vf1);
            }
        }
        return 0;
    }

    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    // The XAdvance field of a ValueRecord: XPlacement and YPlacement come first when present.
    private int XAdvance(int record, int format)
    {
        if ((format & 4) == 0) return 0;
        int skip = ((format & 1) != 0 ? 2 : 0) + ((format & 2) != 0 ? 2 : 0);
        return S16(record + skip);
    }

    private int CoverageIndex(int cov, int glyph)
    {
        int format = U16(cov), n = U16(cov + 2);
        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (format == 1)
            {
                int g = U16(cov + 4 + mid * 2);
                if (g == glyph) return mid;
                if (g < glyph) lo = mid + 1; else hi = mid - 1;
            }
            else if (format == 2)
            {
                int r = cov + 4 + mid * 6;
                int start = U16(r), end = U16(r + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return U16(r + 4) + glyph - start;
            }
            else return -1;
        }
        return -1;
    }

    private int ClassOf(int cd, int glyph)
    {
        int format = U16(cd);
        if (format == 1)
        {
            int start = U16(cd + 2), n = U16(cd + 4);
            return glyph >= start && glyph < start + n ? U16(cd + 6 + (glyph - start) * 2) : 0;
        }
        if (format == 2)
        {
            int lo = 0, hi = U16(cd + 2) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = cd + 4 + mid * 6;
                if (glyph < U16(r)) hi = mid - 1;
                else if (glyph > U16(r + 2)) lo = mid + 1;
                else return U16(r + 4);
            }
        }
        return 0;
    }

    // The first horizontal format 0 subtable of a version 0 (Windows) kern table.
    private (int, int) ReadKern(int kern, int length)
    {
        if (length < 4 || U16(kern) != 0) return (-1, 0);
        int end = kern + length, p = kern + 4;
        for (int i = 0, n = U16(kern + 2); i < n && p + 14 <= end; i++)
        {
            int subLength = U16(p + 2), coverage = U16(p + 4);
            // Horizontal, format 0, not minimum values and not cross-stream.
            if ((coverage & 0xFF07) == 1)
            {
                int pairs = Math.Min(U16(p + 6), (end - (p + 14)) / 6);
                return (p + 14, pairs);
            }
            if (subLength < 6) break;
            p += subLength;
        }
        return (-1, 0);
    }

    private int LookupKern(int left, int right)
    {
        uint key = (uint)(left << 16 | right);
        int lo = 0, hi = _kernPairs - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int r = _kern + mid * 6;
            uint k = U32(r);
            if (k == key) return S16(r + 4);
            if (k < key) lo = mid + 1; else hi = mid - 1;
        }
        return 0;
    }
}
