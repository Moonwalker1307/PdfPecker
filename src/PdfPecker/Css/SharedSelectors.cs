using System.Collections.Concurrent;

namespace PdfPecker.Css;

/// <summary>
/// The parsed selectors of style rules, by their text. Applications convert the same templates again and again, so a
/// document's selectors are nearly always ones that were parsed before, and a selector never changes once parsed:
/// matching only reads it. Documents can therefore share them, as they share the declarations of
/// <see cref="SharedDeclarations"/>.
///
/// Selectors of rules in an @scope block are never taken from here. What a list such as :is() or :has() matched is
/// remembered per selector and element during a style pass, and a list that names :scope matches differently in each
/// block, so those selectors stay each rule's own.
/// </summary>
internal static class SharedSelectors
{
    // Enough for the style sheets of many templates; a stream of documents whose selectors are all different starts
    // the cache again whenever it is full, so it never holds more than this.
    private const int MaxSelectors = 8192;
    private const int MaxChars = 1 << 19;
    // A longer selector (a generated one, say) is parsed every time rather than kept.
    private const int MaxSelectorChars = 512;

    private static readonly ConcurrentDictionary<string, Selector?> Selectors = new(StringComparer.Ordinal);
    private static int _chars;

    /// <summary>The selector that <see cref="Selector.Parse(string)"/> gives for the text; null when it is not supported.</summary>
    public static Selector? Get(string text)
    {
        if (Selectors.TryGetValue(text, out var shared)) return shared;
        var parsed = Selector.Parse(text);
        if (text.Length > MaxSelectorChars) return parsed;
        if (Selectors.Count >= MaxSelectors || Interlocked.Add(ref _chars, text.Length) > MaxChars)
        {
            lock (Selectors)
            {
                if (Selectors.Count >= MaxSelectors || Volatile.Read(ref _chars) > MaxChars)
                {
                    Selectors.Clear();
                    Volatile.Write(ref _chars, text.Length);
                }
            }
        }
        return Selectors.GetOrAdd(text, parsed);
    }
}
