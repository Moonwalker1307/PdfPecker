namespace PdfPecker.Fonts;

/// <summary>
/// A font's Unicode variation sequences (cmap format 14): which glyph a character followed by a variation selector
/// takes, such as one of the forms of a CJK ideograph that the ideographic variation selectors pick, or a standardized
/// variant. A sequence is either non-default, with a glyph of its own, or default, drawn with the character's usual
/// glyph; any other sequence is not in the font.
/// </summary>
internal sealed class VariationSequences
{
    // The non-default glyphs by selector and character, and the default ranges of each selector, sorted by their start
    // and merged where they overlap once the table is read.
    private readonly Dictionary<long, ushort> _glyphs = new();
    private readonly Dictionary<int, List<(int Start, int End)>> _defaults = new();

    /// <summary>Whether the font has no sequences at all.</summary>
    public bool IsEmpty => _glyphs.Count == 0 && _defaults.Count == 0;

    /// <summary>
    /// The glyph of a character followed by a selector: a glyph id for a non-default sequence, 0 for a default one,
    /// which takes the character's usual glyph, and -1 for a sequence the font does not have.
    /// </summary>
    public int Glyph(int codePoint, int selector)
    {
        if (_glyphs.TryGetValue(Key(codePoint, selector), out var glyph)) return glyph;
        if (!_defaults.TryGetValue(selector, out var ranges)) return -1;
        int lo = 0, hi = ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var range = ranges[mid];
            if (codePoint < range.Start) hi = mid - 1;
            else if (codePoint > range.End) lo = mid + 1;
            else return 0;
        }
        return -1;
    }

    internal void AddGlyph(int selector, int codePoint, int glyph) => _glyphs[Key(codePoint, selector)] = (ushort)glyph;

    internal void AddDefault(int selector, int start, int end)
    {
        if (!_defaults.TryGetValue(selector, out var ranges)) _defaults[selector] = ranges = new();
        ranges.Add((start, end));
    }

    /// <summary>Sorts each selector's default ranges and merges those that overlap, so that they can be searched.</summary>
    internal void Seal()
    {
        foreach (var ranges in _defaults.Values)
        {
            ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            int kept = 0;
            for (int i = 0; i < ranges.Count; i++)
            {
                if (kept > 0 && ranges[i].Start <= ranges[kept - 1].End + 1)
                    ranges[kept - 1] = (ranges[kept - 1].Start, Math.Max(ranges[kept - 1].End, ranges[i].End));
                else ranges[kept++] = ranges[i];
            }
            ranges.RemoveRange(kept, ranges.Count - kept);
        }
    }

    // The 21 bits of a code point and the selector above them; a damaged font's 24-bit values stay apart too.
    private static long Key(int codePoint, int selector) => (long)selector << 24 | (uint)codePoint;

    /// <summary>
    /// Whether the text at <paramref name="i"/> is a variation selector, and its code point and how many UTF-16 units
    /// it takes: U+FE00..U+FE0F, or the supplement U+E0100..U+E01EF, which are surrogate pairs.
    /// </summary>
    public static bool IsSelector(string s, int i, int end, out int selector, out int units)
    {
        char c = s[i];
        if (c is >= '︀' and <= '️')
        {
            (selector, units) = (c, 1);
            return true;
        }
        if (c == '\uDB40' && i + 1 < end && s[i + 1] is >= '\uDD00' and <= '\uDDEF')
        {
            (selector, units) = (char.ConvertToUtf32(c, s[i + 1]), 2);
            return true;
        }
        (selector, units) = (0, 0);
        return false;
    }
}
