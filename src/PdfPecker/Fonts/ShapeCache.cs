namespace PdfPecker.Fonts;

/// <summary>
/// The shaped glyphs of one font with one set of features turned off: of whole texts, by their characters and
/// direction, of the words that EmbeddedFont.ShapeWords puts texts together from, by their characters and script, and
/// whether words are plain (see EmbeddedFont.PlainWords).
/// Layout measures each word, the line breaker the words around each break, and the writer draws each line after its
/// alignment measured it, so a font whose text all goes through the shaper (contextual alternates) shapes the same
/// text many times; the shaper depends on nothing but the characters (and the script it is told), so the result is
/// reused as it is.
/// </summary>
/// <remarks>
/// The cache holds at most <see cref="MaxGlyphs"/> glyphs, an entry without glyphs counting as one, and starts again
/// when that is reached, so a long document cannot make it grow without bound. Only the fonts of one conversion keep a
/// cache (see EmbeddedFont.Shared). It is shared by the threads that use the font.
/// </remarks>
internal sealed class ShapeCache
{
    internal const int MaxGlyphs = 1 << 18;

    // A word's key is its characters after a character for its script, which no text holds.
    private const char ScriptKeyBase = '\uE000';
#if NET9_0_OR_GREATER
    private const int MaxStackKey = 128; // the longest word whose key is put together on the stack to look it up
#endif

    private sealed class Entry(ShapedGlyph[] glyphs, int width, bool free)
    {
        public readonly ShapedGlyph[] Glyphs = glyphs; // with Start counted from the start of the text
        public readonly int Width = width;
        public readonly bool Free = free; // for a word: see EmbeddedFont.ShapeWords
    }

    private readonly Dictionary<string, Entry> _ltr = new(StringComparer.Ordinal), _rtl = new(StringComparer.Ordinal),
        _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _plain = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _ltrSpan, _rtlSpan, _wordsSpan;
    private readonly Dictionary<string, bool>.AlternateLookup<ReadOnlySpan<char>> _plainSpan;
#endif
    private int _glyphs;

    public ShapeCache()
    {
#if NET9_0_OR_GREATER
        _ltrSpan = _ltr.GetAlternateLookup<ReadOnlySpan<char>>();
        _rtlSpan = _rtl.GetAlternateLookup<ReadOnlySpan<char>>();
        _wordsSpan = _words.GetAlternateLookup<ReadOnlySpan<char>>();
        _plainSpan = _plain.GetAlternateLookup<ReadOnlySpan<char>>();
#endif
    }

    /// <summary>The glyphs held, as the bound counts them.</summary>
    public int Count
    {
        get
        {
            lock (_ltr) return _glyphs;
        }
    }

    /// <summary>
    /// The glyphs of the text into <paramref name="output"/> (when it is not null), with their Start in
    /// <paramref name="s"/>, and their width as <see cref="Width"/> rounds it; false when the text is not in the cache.
    /// </summary>
    public bool TryGet(string s, int start, int length, bool rtl, List<ShapedGlyph>? output, out int width)
    {
        Entry? entry;
        lock (_ltr)
        {
#if NET9_0_OR_GREATER
            bool found = (rtl ? _rtlSpan : _ltrSpan).TryGetValue(s.AsSpan(start, length), out entry);
#else
            bool found = (rtl ? _rtl : _ltr).TryGetValue(s.Substring(start, length), out entry);
#endif
            if (!found)
            {
                width = 0;
                return false;
            }
        }
        width = entry!.Width;
        if (output != null)
        {
            output.Clear();
            Append(entry, start, output);
        }
        return true;
    }

    /// <summary>Keeps the glyphs the shaper gave for the text, whose Start are in <paramref name="s"/>.</summary>
    public void Add(string s, int start, int length, bool rtl, List<ShapedGlyph> glyphs)
        => Store(rtl ? _rtl : _ltr, s.Substring(start, length), glyphs, 0, glyphs.Count, start, free: false);

    /// <summary>
    /// Appends the glyphs of a word shaped as the given script to <paramref name="output"/>, with their Start in
    /// <paramref name="s"/>; false when the word is not in the cache.
    /// </summary>
    public bool TryAppendWord(int script, string s, int start, int length, List<ShapedGlyph> output, out bool free, out int glyphs)
    {
        Entry? entry;
        lock (_ltr)
        {
#if NET9_0_OR_GREATER
            bool found;
            if (length < MaxStackKey)
            {
                Span<char> key = stackalloc char[length + 1];
                key[0] = (char)(ScriptKeyBase + script);
                s.AsSpan(start, length).CopyTo(key[1..]);
                found = _wordsSpan.TryGetValue(key, out entry);
            }
            else found = _words.TryGetValue(WordKey(script, s, start, length), out entry);
#else
            bool found = _words.TryGetValue(WordKey(script, s, start, length), out entry);
#endif
            if (!found)
            {
                free = false;
                glyphs = 0;
                return false;
            }
        }
        free = entry!.Free;
        glyphs = entry.Glyphs.Length;
        Append(entry, start, output);
        return true;
    }

    /// <summary>Keeps the glyphs of a word, those of <paramref name="glyphs"/> from <paramref name="from"/> on.</summary>
    public void AddWord(int script, string s, int start, int length, List<ShapedGlyph> glyphs, int from, bool free)
        => Store(_words, WordKey(script, s, start, length), glyphs, from, glyphs.Count - from, start, free);

    /// <summary>Whether a word is plain (see EmbeddedFont.PlainWords); false when that is not in the cache.</summary>
    public bool TryGetPlain(string s, int start, int length, out bool plain)
    {
        lock (_ltr)
        {
#if NET9_0_OR_GREATER
            return _plainSpan.TryGetValue(s.AsSpan(start, length), out plain);
#else
            return _plain.TryGetValue(s.Substring(start, length), out plain);
#endif
        }
    }

    /// <summary>Keeps whether a word is plain.</summary>
    public void AddPlain(string s, int start, int length, bool plain)
    {
        var key = s.Substring(start, length);
        lock (_ltr)
        {
            // Each entry counts as a glyph towards the bound.
            if (_glyphs + 1 > MaxGlyphs) Clear();
            if (_plain.TryAdd(key, plain)) _glyphs++;
        }
    }

    private void Clear()
    {
        _ltr.Clear();
        _rtl.Clear();
        _words.Clear();
        _plain.Clear();
        _glyphs = 0;
    }

    private static string WordKey(int script, string s, int start, int length)
        => string.Create(length + 1, (script, s, start), static (key, state) =>
        {
            key[0] = (char)(ScriptKeyBase + state.script);
            state.s.AsSpan(state.start, key.Length - 1).CopyTo(key[1..]);
        });

    private void Store(Dictionary<string, Entry> table, string key, List<ShapedGlyph> glyphs, int from, int count, int start, bool free)
    {
        var copy = new ShapedGlyph[count];
        float total = 0;
        for (int k = 0; k < count; k++)
        {
            var g = glyphs[from + k];
            copy[k] = new ShapedGlyph(g.Gid, g.Start - start, g.Length, g.Advance, g.Dx, g.Dy);
            total += g.Advance;
        }
        var entry = new Entry(copy, (int)MathF.Round(total), free);
        lock (_ltr)
        {
            int size = Math.Max(1, copy.Length);
            if (_glyphs + size > MaxGlyphs) Clear();
            if (table.TryAdd(key, entry)) _glyphs += size;
        }
    }

    private static void Append(Entry entry, int start, List<ShapedGlyph> output)
    {
        foreach (var g in entry.Glyphs) output.Add(new ShapedGlyph(g.Gid, g.Start + start, g.Length, g.Advance, g.Dx, g.Dy));
    }

    /// <summary>The width of shaped glyphs in 1/1000 em: the sum of their advances, rounded.</summary>
    public static int Width(List<ShapedGlyph> glyphs)
    {
        float total = 0;
        foreach (var g in glyphs) total += g.Advance;
        return (int)MathF.Round(total);
    }
}
