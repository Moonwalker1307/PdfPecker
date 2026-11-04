namespace PdfPecker.Fonts;

/// <summary>
/// The shaped glyphs of one font with one set of features turned off, by the characters shaped and their direction.
/// Layout measures each word, the line breaker the words around each break, and the writer draws each line after its
/// alignment measured it, so a font whose text all goes through the shaper (contextual alternates) shapes the same
/// text many times; the shaper depends on nothing but the characters, so the result is reused as it is.
/// </summary>
/// <remarks>
/// The cache holds at most <see cref="MaxGlyphs"/> glyphs and starts again when that is reached, so a long document
/// cannot make it grow without bound. It is shared by the threads that use the font.
/// </remarks>
internal sealed class ShapeCache
{
    private const int MaxGlyphs = 1 << 18;

    private sealed class Entry(ShapedGlyph[] glyphs, int width, bool inert)
    {
        public readonly ShapedGlyph[] Glyphs = glyphs; // with Start counted from the start of the text
        public readonly int Width = width;
        public readonly bool Inert = inert; // see EmbeddedFont.InertInContext
    }

    private readonly Dictionary<string, Entry> _ltr = new(StringComparer.Ordinal), _rtl = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _ltrSpan, _rtlSpan;
#endif
    private int _glyphs;

    public ShapeCache()
    {
#if NET9_0_OR_GREATER
        _ltrSpan = _ltr.GetAlternateLookup<ReadOnlySpan<char>>();
        _rtlSpan = _rtl.GetAlternateLookup<ReadOnlySpan<char>>();
#endif
    }

    /// <summary>
    /// The glyphs of the text into <paramref name="output"/>, with their Start in <paramref name="s"/>, and their width
    /// as <see cref="Width"/> rounds it, and whether it is inert; false when the text is not in the cache.
    /// </summary>
    public bool TryGet(string s, int start, int length, bool rtl, List<ShapedGlyph>? output, out int width, out bool inert)
    {
        Entry? entry;
        lock (_ltr)
        {
#if NET9_0_OR_GREATER
            var found = (rtl ? _rtlSpan : _ltrSpan).TryGetValue(s.AsSpan(start, length), out entry);
#else
            var found = (rtl ? _rtl : _ltr).TryGetValue(s.Substring(start, length), out entry);
#endif
            if (!found)
            {
                width = 0;
                inert = false;
                return false;
            }
        }
        width = entry!.Width;
        inert = entry.Inert;
        if (output != null)
        {
            output.Clear();
            foreach (var g in entry.Glyphs) output.Add(new ShapedGlyph(g.Gid, g.Start + start, g.Length, g.Advance, g.Dx, g.Dy));
        }
        return true;
    }

    /// <summary>Keeps the glyphs the shaper gave for the text, whose Start are in <paramref name="s"/>.</summary>
    public void Add(string s, int start, int length, bool rtl, List<ShapedGlyph> glyphs, bool inert)
    {
        var copy = new ShapedGlyph[glyphs.Count];
        for (int k = 0; k < copy.Length; k++)
        {
            var g = glyphs[k];
            copy[k] = new ShapedGlyph(g.Gid, g.Start - start, g.Length, g.Advance, g.Dx, g.Dy);
        }
        var entry = new Entry(copy, Width(glyphs), inert);
        var key = s.Substring(start, length);
        lock (_ltr)
        {
            if (_glyphs + copy.Length > MaxGlyphs)
            {
                _ltr.Clear();
                _rtl.Clear();
                _glyphs = 0;
            }
            if ((rtl ? _rtl : _ltr).TryAdd(key, entry)) _glyphs += copy.Length;
        }
    }

    /// <summary>The width of shaped glyphs in 1/1000 em: the sum of their advances, rounded.</summary>
    public static int Width(List<ShapedGlyph> glyphs)
    {
        float total = 0;
        foreach (var g in glyphs) total += g.Advance;
        return (int)MathF.Round(total);
    }
}
