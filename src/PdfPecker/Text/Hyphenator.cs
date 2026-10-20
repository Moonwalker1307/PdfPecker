using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PdfPecker.Text;

/// <summary>
/// Liang's hyphenation algorithm (the one TeX uses) over a language's patterns: the break points of a word, as
/// <c>hyphens: auto</c> uses them. Patterns are kept in a trie keyed by (node, character).
/// </summary>
internal sealed class Hyphenator
{
    // Chrome hyphenates words of at least five letters.
    private const int MinWordLength = 5;

    private readonly Dictionary<long, int> _children = new();
    private readonly List<int> _valueStart = new() { -1 }; // per node: start of its pattern's digits in _values, or -1
    private readonly List<byte> _values = new();
    private readonly Dictionary<string, byte[]> _exceptions = new(StringComparer.Ordinal);
    private readonly int _left, _right;
    // French and Italian patterns cover elisions such as l'ordinateur, so words with an apostrophe are hyphenated only
    // for languages whose patterns mention it; elsewhere the patterns would put breaks next to it.
    private bool _apostrophes;

    private Hyphenator(int left, int right)
    {
        _left = Math.Max(1, left);
        _right = Math.Max(1, right);
    }

    // ---------------------------------------------------------------- the bundled patterns

    private static readonly ConcurrentDictionary<string, Hyphenator?> Bundled = new(StringComparer.Ordinal);

    /// <summary>
    /// The hyphenator for a language tag (such as "en-GB" or "da"): the user's patterns for it if any, else the
    /// bundled ones, else null.
    /// </summary>
    public static Hyphenator? For(string? lang, IReadOnlyDictionary<string, HyphenationPatterns>? custom)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        string tag = lang!.Trim().Replace('_', '-').ToLowerInvariant();
        int dash = tag.IndexOf('-');
        string primary = dash < 0 ? tag : tag.Substring(0, dash);
        if (custom != null)
        {
            if (custom.TryGetValue(tag, out var p) || custom.TryGetValue(primary, out p)) return p.Hyphenator;
        }
        string? key = primary switch
        {
            "en" => "en-us",
            "da" or "de" or "es" or "fr" or "it" or "nl" or "no" or "nb" or "nn" or "pl" or "pt" or "sv" => primary,
            _ => null,
        };
        return key == null ? null : Bundled.GetOrAdd(key, Load);
    }

    private static Hyphenator? Load(string key)
    {
        // The Swedish patterns are under the LPPL, which allows an unmodified copy, so their TeX file is bundled as it is.
        if (key == "sv") return ReadResource("sv.tex.br", reader => FromTeX(reader.ReadToEnd(), null, 2, 2));
        // Bokmål and nynorsk share the Norwegian patterns and differ in two exceptions (from hyph-nb.tex and hyph-nn.tex).
        string resource = key is "nb" or "nn" ? "no" : key;
        return ReadResource(resource + ".hyph.br", reader => Parse(reader, key));
    }

    private static Hyphenator? ReadResource(string name, Func<StreamReader, Hyphenator> read)
    {
        using var stream = typeof(Hyphenator).Assembly.GetManifestResourceStream("PdfPecker.Text.Hyphenation." + name);
        if (stream == null) return null;
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return read(reader);
    }

    private static Hyphenator Parse(StreamReader reader, string key)
    {
        var head = reader.ReadLine()!.Split(' ');
        var h = new Hyphenator(int.Parse(head[0], CultureInfo.InvariantCulture), int.Parse(head[1], CultureInfo.InvariantCulture));
        bool exceptions = false;
        while (reader.ReadLine() is { } line)
        {
            if (line == "--") { exceptions = true; continue; }
            if (exceptions) h.AddException(line);
            else h.AddPattern(line);
        }
        if (key == "nb") { h.AddException("at-ten-de"); h.AddException("be-tre"); }
        else if (key == "nn") { h.AddException("att-en-de"); h.AddException("bet-re"); }
        return h;
    }

    /// <summary>Reads patterns from a TeX file or a plain list of patterns.</summary>
    public static Hyphenator FromTeX(string patterns, string? exceptions, int left, int right)
    {
        var h = new Hyphenator(left, right);
        string text = StripComments(patterns);
        string? patternBlock = Block(text, "\\patterns{");
        string? exceptionBlock = Block(text, "\\hyphenation{");
        foreach (var p in Tokens(patternBlock ?? (exceptionBlock == null ? text : ""))) h.AddPattern(p);
        if (exceptionBlock != null) foreach (var e in Tokens(exceptionBlock)) h.AddException(e);
        if (exceptions != null) foreach (var e in Tokens(StripComments(exceptions))) h.AddException(e);
        return h;
    }

    private static string StripComments(string text)
    {
        if (text.IndexOf('%') < 0) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            int pct = line.IndexOf('%');
            sb.Append(pct >= 0 ? line.AsSpan(0, pct) : line.AsSpan()).Append('\n');
        }
        return sb.ToString();
    }

    private static string? Block(string text, string start)
    {
        int i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        int end = text.IndexOf('}', i);
        return end < 0 ? text.Substring(i) : text.Substring(i, end - i);
    }

    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // ---------------------------------------------------------------- building

    private void AddPattern(string pattern)
    {
        if (pattern.Length == 0 || pattern[0] == '\\') return;
        var letters = new StringBuilder(pattern.Length);
        var digits = new List<byte>(pattern.Length + 1) { 0 };
        foreach (char c in pattern)
        {
            if (c >= '0' && c <= '9') digits[^1] = (byte)(c - '0');
            else { letters.Append(char.ToLowerInvariant(c)); digits.Add(0); }
        }
        if (letters.Length == 0) return;
        if (pattern.Contains('\'')) _apostrophes = true;
        int node = 0;
        for (int k = 0; k < letters.Length; k++)
        {
            long key = (long)node << 16 | letters[k];
            if (!_children.TryGetValue(key, out int child))
            {
                child = _valueStart.Count;
                _valueStart.Add(-1);
                _children[key] = child;
            }
            node = child;
        }
        _valueStart[node] = _values.Count;
        _values.AddRange(digits);
    }

    private void AddException(string word)
    {
        var letters = new StringBuilder(word.Length);
        var breaks = new List<byte>(word.Length + 1) { 0 };
        foreach (char c in word)
        {
            if (c == '-') breaks[^1] = 1;
            else { letters.Append(char.ToLowerInvariant(c)); breaks.Add(0); }
        }
        if (letters.Length > 0) _exceptions[letters.ToString()] = breaks.ToArray();
    }

    // ---------------------------------------------------------------- hyphenating

    private static bool IsApostrophe(char c) => c is '\'' or '\u2019';

    /// <summary>
    /// The break points of a word that hyphens: auto may use: offsets k (0 &lt; k &lt; length) at which the word may
    /// break with a hyphen, in increasing order, written into <paramref name="breaks"/>. Leading and trailing
    /// punctuation is ignored; words with digits or other non-letters inside, short words and, with
    /// <paramref name="skipCapitalized"/> (Chrome's rule for English), capitalized words get none.
    /// <paramref name="minWord"/>, <paramref name="minBefore"/> and <paramref name="minAfter"/> are the values of
    /// hyphenate-limit-chars, 0 for auto.
    /// </summary>
    public int Hyphenate(ReadOnlySpan<char> word, List<int> breaks, bool skipCapitalized, int minWord = 0, int minBefore = 0, int minAfter = 0)
    {
        breaks.Clear();
        foreach (char c in word)
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                return HyphenateComposed(word, breaks, skipCapitalized, minWord, minBefore, minAfter);
        int start = 0, end = word.Length;
        while (start < end && !char.IsLetter(word[start])) start++;
        while (end > start && !char.IsLetter(word[end - 1])) end--;
        int n = end - start;
        // hyphenate-limit-chars can ask for more letters around a hyphen than the patterns do, but not fewer: Chrome keeps
        // its own minimums of two letters before and three after a hyphen whatever the property says, and patterns are
        // not made for breaks nearer the ends of a word than their \lefthyphenmin and \righthyphenmin. As in Chrome, a
        // word with fewer letters than both sides need is never hyphenated.
        int left = Math.Max(_left, minBefore), right = Math.Max(_right, minAfter);
        if (n < (minWord > 0 ? minWord : MinWordLength) || n < left + right) return 0;
        for (int k = start; k < end; k++)
            if (!char.IsLetter(word[k]) && !(_apostrophes && IsApostrophe(word[k])))
                return 0; // digits, URLs, apostrophes, surrogate pairs...
        if (skipCapitalized && char.IsUpper(word[start])) return 0;

        Span<char> lower = n <= 128 ? stackalloc char[n + 2] : new char[n + 2];
        lower[0] = '.';
        for (int k = 0; k < n; k++)
        {
            char c = word[start + k];
            lower[k + 1] = IsApostrophe(c) ? '\'' : char.ToLowerInvariant(c); // the patterns use the ASCII apostrophe
        }
        lower[n + 1] = '.';

        if (_exceptions.Count > 0 && _exceptions.TryGetValue(lower.Slice(1, n).ToString(), out var exc))
        {
            for (int k = left; k <= n - right; k++)
                if (exc[k] != 0) breaks.Add(start + k);
            return breaks.Count;
        }

        Span<byte> points = n <= 128 ? stackalloc byte[n + 3] : new byte[n + 3];
        points.Clear();
        for (int i = 0; i < lower.Length; i++)
        {
            int node = 0;
            for (int j = i; j < lower.Length; j++)
            {
                if (!_children.TryGetValue((long)node << 16 | lower[j], out node)) break;
                int v = _valueStart[node];
                if (v < 0) continue;
                int len = j - i + 2;
                for (int k = 0; k < len; k++)
                    if (_values[v + k] > points[i + k]) points[i + k] = _values[v + k];
            }
        }
        // points[p + 1] is the value between letters p - 1 and p of the word (lower[0] is the leading dot).
        for (int k = left; k <= n - right; k++)
            if ((points[k + 1] & 1) != 0) breaks.Add(start + k);
        return breaks.Count;
    }

    /// <summary>
    /// Hyphenates a word written with combining marks after its letters (decomposed, as text pasted from macOS often is),
    /// which the patterns do not know: each letter is composed with its marks for the patterns, and the breaks are mapped
    /// back to the word. A letter that has no composed form gives no breaks, as before.
    /// </summary>
    private int HyphenateComposed(ReadOnlySpan<char> word, List<int> breaks, bool skipCapitalized, int minWord, int minBefore, int minAfter)
    {
        var composed = new StringBuilder(word.Length);
        var starts = new List<int>(word.Length);
        for (int i = 0; i < word.Length;)
        {
            int j = i + 1;
            while (j < word.Length && CharUnicodeInfo.GetUnicodeCategory(word[j]) == UnicodeCategory.NonSpacingMark) j++;
            string letter;
            try
            {
                letter = word.Slice(i, j - i).ToString().Normalize(NormalizationForm.FormC);
            }
            catch (PlatformNotSupportedException)
            {
                return 0; // invariant globalization mode without normalization data
            }
            // A mark with no letter before it (at the start of the word) stays a mark when composed, and hyphenating the
            // composed word would come back here for ever.
            if (letter.Length != 1 || CharUnicodeInfo.GetUnicodeCategory(letter[0]) == UnicodeCategory.NonSpacingMark) return 0;
            starts.Add(i);
            composed.Append(letter);
            i = j;
        }
        int count = Hyphenate(composed.ToString(), breaks, skipCapitalized, minWord, minBefore, minAfter);
        for (int b = 0; b < count; b++) breaks[b] = starts[breaks[b]];
        return count;
    }
}
