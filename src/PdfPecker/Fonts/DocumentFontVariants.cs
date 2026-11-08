namespace PdfPecker.Fonts;

/// <summary>
/// The variable-font instances and feature variants one document uses. Fonts keep the ones they make for later
/// documents too, but drop the least recently used past a cap, and the fonts the process keeps are shared by every
/// conversion; so a document keeps its own here, and each location and feature setting gets one font throughout the
/// document, and one subset in the PDF, whatever other documents ask of the same fonts meanwhile. It also bounds what
/// one document may make of a font. One conversion uses it, on one thread, like the <see cref="FontResolver"/> that
/// holds it.
/// </summary>
internal sealed class DocumentFontVariants
{
    // What one document may make of one font: instances at this many locations (an optical size axis makes one for each
    // font size) and this many feature variants that change the font. Past them the document uses the font as it is,
    // the same way every time, so that a hostile stylesheet cannot grow memory without bound.
    internal const int MaxInstances = 256, MaxFeatureVariants = 64;

    private sealed class Made
    {
        public readonly Dictionary<string, FontProgram> Instances = new(StringComparer.Ordinal);
        public readonly Dictionary<string, FontProgram> Variants = new(StringComparer.Ordinal);
        public int Changed; // the variants that are not the font itself
    }

    private readonly Dictionary<FontProgram, Made> _programs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextFont, Dictionary<string, TextFont>> _fonts = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The document's instance of a variable font at a location (normalized coordinates as a key), which
    /// <paramref name="make"/> makes on the first request; the font itself once the document has its fill of instances.
    /// </summary>
    public FontProgram Instance(FontProgram font, string location, Func<FontProgram> make)
    {
        var made = Of(font);
        if (made.Instances.TryGetValue(location, out var instance)) return instance;
        if (made.Instances.Count >= MaxInstances) return font;
        return made.Instances[location] = make();
    }

    /// <summary>The document's variant of a font program with OpenType features (see <see cref="FontProgram.WithFeatures"/>).</summary>
    public FontProgram WithFeatures(FontProgram font, string features)
    {
        var made = Of(font);
        if (made.Variants.TryGetValue(features, out var variant)) return variant;
        // Settings that change nothing do not count, since keeping them costs the document nothing.
        variant = made.Changed < MaxFeatureVariants ? font.WithFeatures(features) : font;
        if (!ReferenceEquals(variant, font)) made.Changed++;
        return made.Variants[features] = variant;
    }

    /// <summary>The document's variant of a font with OpenType features, whose program is the document's variant.</summary>
    public TextFont WithFeatures(TextFont font, string features)
    {
        if (!_fonts.TryGetValue(font, out var variants)) _fonts[font] = variants = new Dictionary<string, TextFont>(StringComparer.Ordinal);
        if (!variants.TryGetValue(features, out var variant))
            variants[features] = variant = font is EmbeddedFont embedded ? embedded.WithFeatures(features, this) : font.WithFeatures(features);
        return variant;
    }

    private Made Of(FontProgram font)
    {
        if (!_programs.TryGetValue(font, out var made)) _programs[font] = made = new Made();
        return made;
    }
}
