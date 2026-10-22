namespace PdfPecker.Fluent;

/// <summary>
/// Fonts registered once for every document. They are used when the <see cref="PdfOptions"/> passed to a
/// <c>Generate</c> method has no fonts of its own; otherwise those options' <see cref="PdfOptions.Fonts"/> are used.
/// </summary>
public static class FontManager
{
    internal static FontCollection Fonts { get; } = new();

    /// <summary>Registers a TrueType, OpenType or WOFF font under the family name inside it.</summary>
    /// <exception cref="ArgumentException">The data is not a font PdfPecker can read.</exception>
    public static void RegisterFont(byte[] fontData) => Register(fontData, null);

    /// <summary>Registers a font read from a stream under the family name inside it.</summary>
    public static void RegisterFont(Stream stream) => Register(Read(stream), null);

    /// <summary>Registers a font under the family name you give, which text styles then use.</summary>
    public static void RegisterFontWithCustomName(string fontName, Stream stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontName);
        Register(Read(stream), fontName);
    }

    /// <summary>Registers a font under the family name you give, which text styles then use.</summary>
    public static void RegisterFontWithCustomName(string fontName, byte[] fontData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontName);
        Register(fontData, fontName);
    }

    private static void Register(byte[] fontData, string? familyName)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        Fonts.Add(fontData, familyName);
    }

    private static byte[] Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
