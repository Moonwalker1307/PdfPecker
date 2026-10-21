using System.IO.Compression;

namespace PdfPecker.Fonts;

/// <summary>
/// The Liberation fonts (SIL Open Font License) built into the library, metric-compatible with Helvetica, Times and
/// Courier. They replace the standard fonts when every font must be embedded (PDF/A, PdfOptions.EmbedStandardFonts),
/// with the standard fonts' line metrics so that the layout does not change. With the symbol font, a DejaVu Sans
/// subset, they are also the last fallback for characters that no other font has. Each face is decompressed on first
/// use.
/// </summary>
internal static class BundledFonts
{
    private static readonly string[] Names =
    {
        "LiberationSans-Regular", "LiberationSans-Bold", "LiberationSans-Italic", "LiberationSans-BoldItalic",
        "LiberationSerif-Regular", "LiberationSerif-Bold", "LiberationSerif-Italic", "LiberationSerif-BoldItalic",
        "LiberationMono-Regular", "LiberationMono-Bold", "LiberationMono-Italic", "LiberationMono-BoldItalic",
    };

    // Indexed like StandardFont.All: family * 4 + bold + 2 * italic.
    private static readonly Lazy<byte[]?>[] Files = StandardFont.All.Select(sf => new Lazy<byte[]?>(() => Decompress(sf))).ToArray();
    private static readonly Lazy<EmbeddedFont?>[] Fonts = StandardFont.All.Select(sf => new Lazy<EmbeddedFont?>(() => Load(sf))).ToArray();

    /// <summary>The embeddable replacement for a standard font, or null if it cannot be loaded.</summary>
    public static EmbeddedFont? Get(StandardFont font) => Fonts[font.Index].Value;

    /// <summary>The font file of a standard font's replacement, or null if it cannot be loaded.</summary>
    public static byte[]? Data(StandardFont font) => Files[font.Index].Value;

    /// <summary>
    /// The characters of the symbol fallback font, as Unicode ranges: punctuation, currency, letterlike symbols,
    /// arrows, math operators, technical symbols, enclosed numbers, box drawing, geometric shapes, dingbats and
    /// supplemental arrows. tools/FontPackBuilder --symbols builds the font from the DejaVu Sans glyphs in them.
    /// </summary>
    internal static readonly (int First, int Last)[] SymbolRanges =
    {
        (0x2010, 0x2027), (0x2030, 0x205E), (0x2070, 0x20CF), (0x2100, 0x23FF), (0x2460, 0x27BF), (0x27F0, 0x27FF),
        (0x2900, 0x297F), (0x2B00, 0x2BFF),
    };

    private static readonly Lazy<FontProgram?> SymbolProgram = new(LoadSymbols);
    private static readonly Lazy<EmbeddedFont?>[] SymbolFonts =
        Enumerable.Range(0, 4).Select(i => new Lazy<EmbeddedFont?>(() => SymbolProgram.Value is { } p ? new EmbeddedFont(p, (i & 1) != 0, (i & 2) != 0) : null)).ToArray();

    /// <summary>
    /// Whether text has a character that the standard fonts cannot encode and a bundled font may have: a letter of
    /// the Liberation fonts beyond WinAnsi (Central European, Greek, Cyrillic) or a symbol. Spaces and invisible
    /// formatting characters do not count, since the standard fonts stand in for them well enough.
    /// </summary>
    public static bool BeyondStandard(ReadOnlySpan<char> text)
    {
        for (int i = text.IndexOfAnyExceptInRange(' ', '~'); i >= 0; )
        {
            if (BeyondStandard(text[i])) return true;
            text = text[(i + 1)..];
            i = text.IndexOfAnyExceptInRange(' ', '~');
        }
        return false;
    }

    /// <summary>Whether one character is beyond the standard fonts, as <see cref="BeyondStandard(ReadOnlySpan{char})"/> counts.</summary>
    public static bool BeyondStandard(char c)
        => c >= 0x80 && !WinAnsi.IsExact(c) && c is not ((>= '\u2000' and <= '\u200F') or (>= '\u2028' and <= '\u202F') or (>= '\u205F' and <= '\u206F') or '\uFEFF');

    /// <summary>
    /// The font drawn when no other font has a character: symbols of DejaVu Sans, made bold and slanted as the text
    /// is, or null if it cannot be loaded.
    /// </summary>
    public static EmbeddedFont? Symbols(bool bold, bool italic) => SymbolFonts[(bold ? 1 : 0) + (italic ? 2 : 0)].Value;

    private static FontProgram? LoadSymbols()
        => Decompress("PdfPeckerSymbols") is { } data ? FontProgram.Load(data)[0] : null;

    private static EmbeddedFont? Load(StandardFont sf)
        => Data(sf) is { } data ? new EmbeddedFont(FontProgram.Load(data)[0], false, false, metrics: sf) : null;

    // StandardFont.All orders styles regular, bold, oblique, bold-oblique, like Names.
    private static byte[]? Decompress(StandardFont sf) => Decompress(Names[sf.Index]);

    private static byte[]? Decompress(string font)
    {
        string name = "PdfPecker.Fonts.Bundled." + font + ".ttf.br";
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream(512 * 1024);
        brotli.CopyTo(ms);
        return ms.ToArray();
    }
}
