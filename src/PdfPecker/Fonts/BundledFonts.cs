using System.IO.Compression;

namespace PdfPecker.Fonts;

/// <summary>
/// The Liberation fonts (SIL Open Font License) built into the library, metric-compatible with Helvetica, Times and
/// Courier. They replace the standard fonts when every font must be embedded (PDF/A, PdfOptions.EmbedStandardFonts),
/// with the standard fonts' line metrics so that the layout does not change. Each face is decompressed on first use.
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

    private static EmbeddedFont? Load(StandardFont sf)
        => Data(sf) is { } data ? new EmbeddedFont(FontProgram.Load(data)[0], false, false, metrics: sf) : null;

    private static byte[]? Decompress(StandardFont sf)
    {
        // StandardFont.All orders styles regular, bold, oblique, bold-oblique, like Names.
        string name = "PdfPecker.Fonts.Bundled." + Names[sf.Index] + ".ttf.br";
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream(512 * 1024);
        brotli.CopyTo(ms);
        return ms.ToArray();
    }
}
