using System.Collections.Concurrent;

namespace PdfPecker.Css;

/// <summary>
/// The declaration blocks of documents' style sheets and style attributes, parsed once and shared by every conversion
/// that has the same block: documents made from one template have the same style sheets and style attributes, and
/// parsing them again, shorthands and all, was about a twentieth of the time of a small document. A declaration never
/// changes once parsed (what it remembers about its value is the same for everyone who asks), so documents can share
/// them, as they share the user agent's.
///
/// A document's style cache compares declarations by reference, so elements whose rules have blocks of the same text
/// now share a computed style too. That gives the same style, because a computed style depends only on the text and
/// order of the declarations, with one exception: all: revert keeps the user agent's declarations, so those are never
/// taken from here, and an author block never shares a declaration with the user agent's sheet.
/// </summary>
internal static class SharedDeclarations
{
    // Enough for the style sheets and style attributes of many templates; a stream of documents whose blocks are all
    // different starts the cache again whenever it is full, so it never holds more than this.
    private const int MaxBlocks = 4096;
    private const int MaxChars = 1 << 20;
    // A larger block (a generated one, say) is parsed every time rather than kept.
    private const int MaxBlockChars = 4096;

    private static readonly ConcurrentDictionary<string, Declaration[]> Blocks = new(StringComparer.Ordinal);
    private static int _chars;

    /// <summary>The declarations of a block, as <see cref="CssParser.ParseDeclarations"/> parses them.</summary>
    public static Declaration[] Get(string block)
    {
        if (Blocks.TryGetValue(block, out var shared)) return shared;
        var list = new List<Declaration>();
        CssParser.ParseDeclarations(block, list);
        var parsed = list.ToArray();
        if (block.Length > MaxBlockChars) return parsed;
        if (Blocks.Count >= MaxBlocks || Interlocked.Add(ref _chars, block.Length) > MaxChars)
        {
            lock (Blocks)
            {
                if (Blocks.Count >= MaxBlocks || Volatile.Read(ref _chars) > MaxChars)
                {
                    Blocks.Clear();
                    Volatile.Write(ref _chars, block.Length);
                }
            }
        }
        return Blocks.GetOrAdd(block, parsed);
    }
}
