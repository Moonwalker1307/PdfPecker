namespace PdfPecker.Fluent;

/// <summary>A font weight, from <see cref="Thin"/> (100) to <see cref="ExtraBlack"/> (1000).</summary>
public enum FontWeight
{
    /// <summary>100.</summary>
    Thin = 100,

    /// <summary>200.</summary>
    ExtraLight = 200,

    /// <summary>300.</summary>
    Light = 300,

    /// <summary>400, the default.</summary>
    Normal = 400,

    /// <summary>500.</summary>
    Medium = 500,

    /// <summary>600.</summary>
    SemiBold = 600,

    /// <summary>700.</summary>
    Bold = 700,

    /// <summary>800.</summary>
    ExtraBold = 800,

    /// <summary>900.</summary>
    Black = 900,

    /// <summary>1000.</summary>
    ExtraBlack = 1000,
}

internal enum TextDecorationStyle
{
    Solid,
    Double,
    Wavy,
    Dotted,
    Dashed,
}

internal enum TextPosition
{
    Normal,
    Superscript,
    Subscript,
}

/// <summary>
/// An immutable set of text properties. Each method returns a new style, so styles can be kept and combined:
/// <c>TextStyle.Default.FontSize(9).FontColor(Colors.Grey.Darken2)</c>. Properties left unset are inherited.
/// </summary>
public sealed class TextStyle
{
    internal float? Size { get; private set; }
    internal Color? Color { get; private set; }
    internal Color? Background { get; private set; }
    internal string[]? Families { get; private set; }
    internal FontWeight? Weight { get; private set; }
    internal bool? IsItalic { get; private set; }
    internal bool? IsUnderline { get; private set; }
    internal bool? IsStrikethrough { get; private set; }
    internal float? Leading { get; private set; }
    internal float? Tracking { get; private set; }
    internal TextPosition? Position { get; private set; }
    internal TextDecorationStyle? DecorationStyle { get; private set; }
    internal Color? DecorationTint { get; private set; }
    internal float? DecorationWidth { get; private set; }
    internal float? WordGap { get; private set; }
    internal (string Tag, bool On)[]? Features { get; private set; }

    /// <summary>The empty style: every property inherited.</summary>
    public static TextStyle Default { get; } = new();

    private TextStyle With(Action<TextStyle> change)
    {
        var copy = (TextStyle)MemberwiseClone();
        change(copy);
        return copy;
    }

    /// <summary>The font size in points.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not positive or not finite.</exception>
    public TextStyle FontSize(float size)
    {
        float points = Length.Positive(size, Unit.Point, nameof(size));
        return With(s => s.Size = points);
    }

    /// <summary>The text colour.</summary>
    public TextStyle FontColor(Color color) => With(s => s.Color = color);

    /// <summary>The colour behind the text.</summary>
    public TextStyle BackgroundColor(Color color) => With(s => s.Background = color);

    /// <summary>
    /// The font family, with fallbacks in order. Use a family registered with <see cref="FontManager"/> or
    /// <see cref="PdfOptions.Fonts"/>, or one of the generic families <c>sans-serif</c>, <c>serif</c> and
    /// <c>monospace</c>.
    /// </summary>
    /// <exception cref="ArgumentException">No family is given, or one is empty.</exception>
    public TextStyle FontFamily(params string[] families)
    {
        ArgumentNullException.ThrowIfNull(families);
        if (families.Length == 0) throw new ArgumentException("Give at least one font family.", nameof(families));
        foreach (var family in families)
            if (string.IsNullOrWhiteSpace(family)) throw new ArgumentException("A font family name cannot be empty.", nameof(families));
        var copy = (string[])families.Clone();
        return With(s => s.Families = copy);
    }

    /// <summary>The font weight.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is not a defined weight.</exception>
    public TextStyle FontWeight(FontWeight weight)
    {
        if ((int)weight is < 100 or > 1000 || (int)weight % 100 != 0)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "The weight must be 100 to 1000 in steps of 100.");
        return With(s => s.Weight = weight);
    }

    /// <summary>Weight 100.</summary>
    public TextStyle Thin() => FontWeight(Fluent.FontWeight.Thin);

    /// <summary>Weight 200.</summary>
    public TextStyle ExtraLight() => FontWeight(Fluent.FontWeight.ExtraLight);

    /// <summary>Weight 300.</summary>
    public TextStyle Light() => FontWeight(Fluent.FontWeight.Light);

    /// <summary>Weight 400.</summary>
    public TextStyle NormalWeight() => FontWeight(Fluent.FontWeight.Normal);

    /// <summary>Weight 500.</summary>
    public TextStyle Medium() => FontWeight(Fluent.FontWeight.Medium);

    /// <summary>Weight 600.</summary>
    public TextStyle SemiBold() => FontWeight(Fluent.FontWeight.SemiBold);

    /// <summary>Weight 700.</summary>
    public TextStyle Bold() => FontWeight(Fluent.FontWeight.Bold);

    /// <summary>Weight 800.</summary>
    public TextStyle ExtraBold() => FontWeight(Fluent.FontWeight.ExtraBold);

    /// <summary>Weight 900.</summary>
    public TextStyle Black() => FontWeight(Fluent.FontWeight.Black);

    /// <summary>Weight 1000.</summary>
    public TextStyle ExtraBlack() => FontWeight(Fluent.FontWeight.ExtraBlack);

    /// <summary>Italic, or upright with <c>false</c>.</summary>
    public TextStyle Italic(bool value = true) => With(s => s.IsItalic = value);

    /// <summary>Underlined, or not with <c>false</c>.</summary>
    public TextStyle Underline(bool value = true) => With(s => s.IsUnderline = value);

    /// <summary>Struck through, or not with <c>false</c>.</summary>
    public TextStyle Strikethrough(bool value = true) => With(s => s.IsStrikethrough = value);

    /// <summary>The line height as a multiple of the font size, such as 1.4; <c>null</c> for the font's own.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not positive or not finite.</exception>
    public TextStyle LineHeight(float? factor)
    {
        if (factor is { } f && (!float.IsFinite(f) || f <= 0 || f > 100))
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The line height must be a factor from 0 to 100.");
        return With(s => s.Leading = factor);
    }

    /// <summary>Extra space between letters, as a fraction of the font size (0.1 is a tenth of an em).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not finite or out of range.</exception>
    public TextStyle LetterSpacing(float factor)
    {
        if (!float.IsFinite(factor) || factor < -10 || factor > 10)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The letter spacing must be from -10 to 10 em.");
        return With(s => s.Tracking = factor);
    }

    /// <summary>Underlines and strike-throughs drawn as a solid line (the default).</summary>
    public TextStyle DecorationSolid() => With(s => s.DecorationStyle = TextDecorationStyle.Solid);

    /// <summary>Underlines and strike-throughs drawn as a double line.</summary>
    public TextStyle DecorationDouble() => With(s => s.DecorationStyle = TextDecorationStyle.Double);

    /// <summary>Underlines and strike-throughs drawn as a wavy line.</summary>
    public TextStyle DecorationWavy() => With(s => s.DecorationStyle = TextDecorationStyle.Wavy);

    /// <summary>Underlines and strike-throughs drawn as a dotted line.</summary>
    public TextStyle DecorationDotted() => With(s => s.DecorationStyle = TextDecorationStyle.Dotted);

    /// <summary>Underlines and strike-throughs drawn as a dashed line.</summary>
    public TextStyle DecorationDashed() => With(s => s.DecorationStyle = TextDecorationStyle.Dashed);

    /// <summary>The colour of underlines and strike-throughs, when it differs from the text's.</summary>
    public TextStyle DecorationColor(Color color) => With(s => s.DecorationTint = color);

    /// <summary>The thickness of underlines and strike-throughs, as a multiple of the usual thickness.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not from 0 to 100.</exception>
    public TextStyle DecorationThickness(float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0 || factor > 100)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The thickness must be a factor greater than 0, up to 100.");
        return With(s => s.DecorationWidth = factor);
    }

    /// <summary>Extra space between words, as a fraction of the font size (0.2 is a fifth of an em).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not finite or out of range.</exception>
    public TextStyle WordSpacing(float factor)
    {
        if (!float.IsFinite(factor) || factor < -10 || factor > 10)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The word spacing must be from -10 to 10 em.");
        return With(s => s.WordGap = factor);
    }

    /// <summary>Raised and smaller, as in x².</summary>
    public TextStyle Superscript() => With(s => s.Position = TextPosition.Superscript);

    /// <summary>Lowered and smaller, as in H₂O.</summary>
    public TextStyle Subscript() => With(s => s.Position = TextPosition.Subscript);

    /// <summary>On the baseline (undoes <see cref="Superscript"/> and <see cref="Subscript"/>).</summary>
    public TextStyle NormalPosition() => With(s => s.Position = TextPosition.Normal);

    /// <summary>
    /// Turns an OpenType feature on, by its four-letter tag; <see cref="FontFeatures"/> names the common ones. Fonts
    /// without the feature are drawn as usual.
    /// </summary>
    /// <exception cref="ArgumentException">The tag is not four letters, digits or spaces.</exception>
    public TextStyle EnableFontFeature(string featureName) => Feature(featureName, true);

    /// <summary>Turns an OpenType feature off, such as <see cref="FontFeatures.StandardLigatures"/>.</summary>
    /// <exception cref="ArgumentException">The tag is not four letters, digits or spaces.</exception>
    public TextStyle DisableFontFeature(string featureName) => Feature(featureName, false);

    private TextStyle Feature(string tag, bool on)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == ' '))
            throw new ArgumentException("An OpenType feature tag is four letters, digits or spaces, such as \"liga\".", nameof(tag));
        return With(s => s.Features = MergeFeatures(s.Features, [(tag, on)]));
    }

    /// <summary>The features of both lists, the later list's setting winning for a tag in both.</summary>
    private static (string, bool)[]? MergeFeatures((string Tag, bool On)[]? first, (string Tag, bool On)[]? second)
    {
        if (second == null) return first;
        if (first == null) return second;
        var merged = new List<(string Tag, bool On)>(first);
        foreach (var feature in second)
        {
            merged.RemoveAll(f => f.Tag == feature.Tag);
            merged.Add(feature);
        }
        return [.. merged];
    }

    /// <summary>This style with every property the other style sets laid over it.</summary>
    internal TextStyle Override(TextStyle? other)
    {
        if (other == null || ReferenceEquals(other, Default)) return this;
        var copy = (TextStyle)MemberwiseClone();
        copy.Size = other.Size ?? Size;
        copy.Color = other.Color ?? Color;
        copy.Background = other.Background ?? Background;
        copy.Families = other.Families ?? Families;
        copy.Weight = other.Weight ?? Weight;
        copy.IsItalic = other.IsItalic ?? IsItalic;
        copy.IsUnderline = other.IsUnderline ?? IsUnderline;
        copy.IsStrikethrough = other.IsStrikethrough ?? IsStrikethrough;
        copy.Leading = other.Leading ?? Leading;
        copy.Tracking = other.Tracking ?? Tracking;
        copy.Position = other.Position ?? Position;
        copy.DecorationStyle = other.DecorationStyle ?? DecorationStyle;
        copy.DecorationTint = other.DecorationTint ?? DecorationTint;
        copy.DecorationWidth = other.DecorationWidth ?? DecorationWidth;
        copy.WordGap = other.WordGap ?? WordGap;
        copy.Features = MergeFeatures(Features, other.Features);
        return copy;
    }
}

/// <summary>The tags of common OpenType features, for <see cref="TextStyle.EnableFontFeature"/> and <see cref="TextStyle.DisableFontFeature"/>.</summary>
public static class FontFeatures
{
    /// <summary>Kerning (kern).</summary>
    public const string Kerning = "kern";

    /// <summary>Standard ligatures such as fi and fl (liga).</summary>
    public const string StandardLigatures = "liga";

    /// <summary>Contextual ligatures (clig).</summary>
    public const string ContextualLigatures = "clig";

    /// <summary>Discretionary ligatures (dlig).</summary>
    public const string DiscretionaryLigatures = "dlig";

    /// <summary>Small capitals from lowercase letters (smcp).</summary>
    public const string SmallCapitals = "smcp";

    /// <summary>Small capitals from capital letters (c2sc).</summary>
    public const string CapitalsToSmallCapitals = "c2sc";

    /// <summary>Lining figures (lnum).</summary>
    public const string LiningFigures = "lnum";

    /// <summary>Old-style figures (onum).</summary>
    public const string OldstyleFigures = "onum";

    /// <summary>Proportional figures (pnum).</summary>
    public const string ProportionalFigures = "pnum";

    /// <summary>Tabular figures, all of one width, for columns of numbers (tnum).</summary>
    public const string TabularFigures = "tnum";

    /// <summary>Fractions such as 1/2 drawn as one glyph (frac).</summary>
    public const string Fractions = "frac";

    /// <summary>Slashed zero (zero).</summary>
    public const string SlashedZero = "zero";

    /// <summary>Ordinals such as 1st (ordn).</summary>
    public const string Ordinals = "ordn";

    /// <summary>Superscript glyphs (sups).</summary>
    public const string Superscript = "sups";

    /// <summary>Subscript glyphs (subs).</summary>
    public const string Subscript = "subs";

    /// <summary>Case-sensitive forms, for punctuation among capitals (case).</summary>
    public const string CaseSensitiveForms = "case";

    /// <summary>Stylistic alternates (salt).</summary>
    public const string StylisticAlternates = "salt";
}
