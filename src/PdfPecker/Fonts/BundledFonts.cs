using System.IO.Compression;

namespace PdfPecker.Fonts;

/// <summary>
/// The Liberation fonts (SIL Open Font License) built into the library, metric-compatible with Helvetica, Times and
/// Courier. They replace the standard fonts when every font must be embedded (PDF/A, PdfOptions.EmbedStandardFonts),
/// with the standard fonts' line metrics so that the layout does not change. Each face is decompressed on first use.
/// </summary>
internal static class BundledFonts
{
    private static readonly string[] Files =
    {
        "LiberationSans-Regular", "LiberationSans-Bold", "LiberationSans-Italic", "LiberationSans-BoldItalic",
        "LiberationSerif-Regular", "LiberationSerif-Bold", "LiberationSerif-Italic", "LiberationSerif-BoldItalic",
        "LiberationMono-Regular", "LiberationMono-Bold", "LiberationMono-Italic", "LiberationMono-BoldItalic",
    };

    // Indexed like StandardFont.All: family * 4 + bold + 2 * italic.
    private static readonly Lazy<EmbeddedFont?>[] Fonts = StandardFont.All.Select(sf => new Lazy<EmbeddedFont?>(() => Load(sf))).ToArray();

    /// <summary>The embeddable replacement for a standard font, or null if it cannot be loaded.</summary>
    public static EmbeddedFont? Get(StandardFont font) => Fonts[font.Index].Value;

    private static EmbeddedFont? Load(StandardFont sf)
    {
        // StandardFont.All orders styles regular, bold, oblique, bold-oblique, like Files.
        string name = "PdfPecker.Fonts.Bundled." + Files[sf.Index] + ".ttf.br";
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream(512 * 1024);
        brotli.CopyTo(ms);
        var program = FontProgram.Load(ms.ToArray())[0];
        return new EmbeddedFont(program, false, false, metrics: sf);
    }
}
