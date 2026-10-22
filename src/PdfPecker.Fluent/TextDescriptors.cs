using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>A span of text, styled with the <see cref="TextSpanExtensions"/> methods.</summary>
public class TextSpanDescriptor
{
    internal TextSpanDescriptor(TextRun run) => Run = run;

    internal TextRun Run { get; }
}

/// <summary>A paragraph with one span: style it like a span, and align it like a paragraph.</summary>
public sealed class TextBlockDescriptor : TextSpanDescriptor
{
    private readonly TextElement _text;

    internal TextBlockDescriptor(TextElement text, TextRun run) : base(run) => _text = text;

    /// <summary>Aligns the lines to the left.</summary>
    public TextBlockDescriptor AlignLeft() => Align(TextAlignment.Left);

    /// <summary>Centres the lines.</summary>
    public TextBlockDescriptor AlignCenter() => Align(TextAlignment.Center);

    /// <summary>Aligns the lines to the right.</summary>
    public TextBlockDescriptor AlignRight() => Align(TextAlignment.Right);

    /// <summary>Aligns the lines to the start: left, or right in right-to-left text.</summary>
    public TextBlockDescriptor AlignStart() => Align(TextAlignment.Start);

    /// <summary>Aligns the lines to the end: right, or left in right-to-left text.</summary>
    public TextBlockDescriptor AlignEnd() => Align(TextAlignment.End);

    /// <summary>Justifies the lines, except the last.</summary>
    public TextBlockDescriptor Justify() => Align(TextAlignment.Justify);

    /// <summary>Shows at most <paramref name="maxLines"/> lines, ending the last with an ellipsis (…) when text is cut.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLines"/> is not 1 to 10,000.</exception>
    public TextBlockDescriptor ClampLines(int maxLines)
    {
        _text.MaxLines = TextLimits.Lines(maxLines);
        return this;
    }

    /// <summary>Indents the first line of the paragraph.</summary>
    public TextBlockDescriptor ParagraphFirstLineIndentation(float value, Unit unit = Unit.Point)
    {
        _text.FirstLineIndent = Length.Points(value, unit, nameof(value));
        return this;
    }

    /// <summary>Extra space between the paragraphs of the text, which line breaks (<c>\n</c>) separate.</summary>
    public TextBlockDescriptor ParagraphSpacing(float value, Unit unit = Unit.Point)
    {
        _text.ParagraphSpacing = Length.Points(value, unit, nameof(value));
        return this;
    }

    private TextBlockDescriptor Align(TextAlignment alignment)
    {
        _text.Alignment = alignment;
        return this;
    }
}

/// <summary>Builds a paragraph from spans, links and page numbers.</summary>
public sealed class TextDescriptor
{
    private readonly TextElement _text;

    internal TextDescriptor(TextElement text) => _text = text;

    /// <summary>Aligns the lines to the left.</summary>
    public void AlignLeft() => _text.Alignment = TextAlignment.Left;

    /// <summary>Centres the lines.</summary>
    public void AlignCenter() => _text.Alignment = TextAlignment.Center;

    /// <summary>Aligns the lines to the right.</summary>
    public void AlignRight() => _text.Alignment = TextAlignment.Right;

    /// <summary>Aligns the lines to the start: left, or right in right-to-left text.</summary>
    public void AlignStart() => _text.Alignment = TextAlignment.Start;

    /// <summary>Aligns the lines to the end: right, or left in right-to-left text.</summary>
    public void AlignEnd() => _text.Alignment = TextAlignment.End;

    /// <summary>Justifies the lines, except the last.</summary>
    public void Justify() => _text.Alignment = TextAlignment.Justify;

    /// <summary>Shows at most <paramref name="maxLines"/> lines, ending the last with an ellipsis (…) when text is cut.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLines"/> is not 1 to 10,000.</exception>
    public void ClampLines(int maxLines) => _text.MaxLines = TextLimits.Lines(maxLines);

    /// <summary>Indents the first line of the paragraph.</summary>
    public void ParagraphFirstLineIndentation(float value, Unit unit = Unit.Point)
        => _text.FirstLineIndent = Length.Points(value, unit, nameof(value));

    /// <summary>
    /// Extra space between the paragraphs of the text, which line breaks separate (<c>Line</c>, <c>EmptyLine</c> or
    /// <c>\n</c> in a span).
    /// </summary>
    public void ParagraphSpacing(float value, Unit unit = Unit.Point) => _text.ParagraphSpacing = Length.Points(value, unit, nameof(value));

    /// <summary>The style every span of this paragraph starts from.</summary>
    public void DefaultTextStyle(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _text.Style = style;
    }

    /// <summary>The style every span of this paragraph starts from, built from <see cref="TextStyle.Default"/>.</summary>
    public void DefaultTextStyle(Func<TextStyle, TextStyle> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        DefaultTextStyle(handler(TextStyle.Default));
    }

    /// <summary>A span of text.</summary>
    public TextSpanDescriptor Span(string? text) => Add(new TextRun { Text = text ?? "" });

    /// <summary>A span of text followed by a line break.</summary>
    public TextSpanDescriptor Line(string? text) => Add(new TextRun { Text = (text ?? "") + "\n" });

    /// <summary>An empty line.</summary>
    public TextSpanDescriptor EmptyLine() => Add(new TextRun { Text = "\n" });

    /// <summary>The current page number. It works in a page header or footer only.</summary>
    public TextSpanDescriptor CurrentPageNumber() => Add(new TextRun { Kind = RunKind.PageNumber });

    /// <summary>The number of pages. It works in a page header or footer only.</summary>
    public TextSpanDescriptor TotalPages() => Add(new TextRun { Kind = RunKind.TotalPages });

    /// <summary>A link to a web address (http, https or mailto).</summary>
    public TextSpanDescriptor Hyperlink(string? text, string url)
        => Add(new TextRun { Kind = RunKind.Hyperlink, Text = text ?? "", Target = Links.Check(url, nameof(url)) });

    /// <summary>A link to a <see cref="ContainerExtensions.Section"/> of the document.</summary>
    public TextSpanDescriptor SectionLink(string? text, string sectionName)
        => Add(new TextRun { Kind = RunKind.SectionLink, Text = text ?? "", Target = Links.Section(sectionName, nameof(sectionName)) });

    /// <summary>
    /// The page number of a <see cref="ContainerExtensions.Section"/>'s first page, for a table of contents. Unlike
    /// <see cref="CurrentPageNumber"/>, it works anywhere in the document.
    /// </summary>
    public TextSpanDescriptor BeginPageNumberOfSection(string sectionName)
        => Add(new TextRun { Kind = RunKind.SectionPageNumber, Target = Links.Section(sectionName, nameof(sectionName)) });

    /// <summary>
    /// A slot inside the text, placed like a word and wrapped with the lines, for an icon, a badge or a small image.
    /// </summary>
    public IContainer Element(TextInjectedElementAlignment alignment = TextInjectedElementAlignment.AboveBaseline)
    {
        string verticalAlign = alignment switch
        {
            TextInjectedElementAlignment.AboveBaseline => "baseline",
            TextInjectedElementAlignment.BelowBaseline or TextInjectedElementAlignment.Top => "top",
            TextInjectedElementAlignment.Middle => "middle",
            TextInjectedElementAlignment.Bottom => "bottom",
            _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Not an alignment."),
        };
        var slot = new Container();
        _text.Runs.Add(new TextRun { Kind = RunKind.Element, Slot = slot, VerticalAlign = verticalAlign });
        return slot;
    }

    /// <summary>Fills a slot inside the text with a method; see <see cref="Element(TextInjectedElementAlignment)"/>.</summary>
    public void Element(Action<IContainer> handler, TextInjectedElementAlignment alignment = TextInjectedElementAlignment.AboveBaseline)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(Element(alignment));
    }

    private TextSpanDescriptor Add(TextRun run)
    {
        _text.Runs.Add(run);
        return new TextSpanDescriptor(run);
    }
}

/// <summary>Where a slot inside text (<see cref="TextDescriptor.Element(TextInjectedElementAlignment)"/>) sits on its line.</summary>
public enum TextInjectedElementAlignment
{
    /// <summary>On the baseline, like a word: the bottom of an image, or the baseline of text, on the line's baseline.</summary>
    AboveBaseline,

    /// <summary>
    /// At the top of the line. CSS cannot hang a box from the baseline as QuestPDF does, so this is the same as
    /// <see cref="Top"/>.
    /// </summary>
    BelowBaseline,

    /// <summary>At the top of the line.</summary>
    Top,

    /// <summary>Centred on the middle of the lowercase letters.</summary>
    Middle,

    /// <summary>At the bottom of the line.</summary>
    Bottom,
}

internal static class TextLimits
{
    public static int Lines(int maxLines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLines, 10_000);
        return maxLines;
    }
}

/// <summary>Styles a span; each method returns the same descriptor, so calls chain.</summary>
public static class TextSpanExtensions
{
    private static T Apply<T>(T descriptor, Func<TextStyle, TextStyle> change) where T : TextSpanDescriptor
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Run.Style = change(descriptor.Run.Style);
        return descriptor;
    }

    /// <summary>Lays a whole style over the span's own.</summary>
    public static T Style<T>(this T descriptor, TextStyle style) where T : TextSpanDescriptor
    {
        ArgumentNullException.ThrowIfNull(style);
        return Apply(descriptor, s => s.Override(style));
    }

    /// <summary>The font size in points.</summary>
    public static T FontSize<T>(this T descriptor, float size) where T : TextSpanDescriptor => Apply(descriptor, s => s.FontSize(size));

    /// <summary>The text colour.</summary>
    public static T FontColor<T>(this T descriptor, Color color) where T : TextSpanDescriptor => Apply(descriptor, s => s.FontColor(color));

    /// <summary>The colour behind the text.</summary>
    public static T BackgroundColor<T>(this T descriptor, Color color) where T : TextSpanDescriptor => Apply(descriptor, s => s.BackgroundColor(color));

    /// <summary>The font family, with fallbacks.</summary>
    public static T FontFamily<T>(this T descriptor, params string[] families) where T : TextSpanDescriptor => Apply(descriptor, s => s.FontFamily(families));

    /// <summary>The font weight.</summary>
    public static T Weight<T>(this T descriptor, FontWeight weight) where T : TextSpanDescriptor => Apply(descriptor, s => s.FontWeight(weight));

    /// <summary>Weight 100.</summary>
    public static T Thin<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Thin());

    /// <summary>Weight 200.</summary>
    public static T ExtraLight<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.ExtraLight());

    /// <summary>Weight 300.</summary>
    public static T Light<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Light());

    /// <summary>Weight 400.</summary>
    public static T NormalWeight<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.NormalWeight());

    /// <summary>Weight 500.</summary>
    public static T Medium<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Medium());

    /// <summary>Weight 600.</summary>
    public static T SemiBold<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.SemiBold());

    /// <summary>Weight 700.</summary>
    public static T Bold<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Bold());

    /// <summary>Weight 800.</summary>
    public static T ExtraBold<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.ExtraBold());

    /// <summary>Weight 900.</summary>
    public static T Black<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Black());

    /// <summary>Weight 1000.</summary>
    public static T ExtraBlack<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.ExtraBlack());

    /// <summary>Italic, or upright with <c>false</c>.</summary>
    public static T Italic<T>(this T descriptor, bool value = true) where T : TextSpanDescriptor => Apply(descriptor, s => s.Italic(value));

    /// <summary>Underlined, or not with <c>false</c>.</summary>
    public static T Underline<T>(this T descriptor, bool value = true) where T : TextSpanDescriptor => Apply(descriptor, s => s.Underline(value));

    /// <summary>Struck through, or not with <c>false</c>.</summary>
    public static T Strikethrough<T>(this T descriptor, bool value = true) where T : TextSpanDescriptor => Apply(descriptor, s => s.Strikethrough(value));

    /// <summary>The line height as a multiple of the font size.</summary>
    public static T LineHeight<T>(this T descriptor, float? factor) where T : TextSpanDescriptor => Apply(descriptor, s => s.LineHeight(factor));

    /// <summary>Extra space between letters, as a fraction of the font size.</summary>
    public static T LetterSpacing<T>(this T descriptor, float factor) where T : TextSpanDescriptor => Apply(descriptor, s => s.LetterSpacing(factor));

    /// <summary>Underlines and strike-throughs drawn as a solid line.</summary>
    public static T DecorationSolid<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationSolid());

    /// <summary>Underlines and strike-throughs drawn as a double line.</summary>
    public static T DecorationDouble<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationDouble());

    /// <summary>Underlines and strike-throughs drawn as a wavy line.</summary>
    public static T DecorationWavy<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationWavy());

    /// <summary>Underlines and strike-throughs drawn as a dotted line.</summary>
    public static T DecorationDotted<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationDotted());

    /// <summary>Underlines and strike-throughs drawn as a dashed line.</summary>
    public static T DecorationDashed<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationDashed());

    /// <summary>The colour of underlines and strike-throughs.</summary>
    public static T DecorationColor<T>(this T descriptor, Color color) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationColor(color));

    /// <summary>The thickness of underlines and strike-throughs, as a multiple of the usual thickness.</summary>
    public static T DecorationThickness<T>(this T descriptor, float factor) where T : TextSpanDescriptor => Apply(descriptor, s => s.DecorationThickness(factor));

    /// <summary>Extra space between words, as a fraction of the font size.</summary>
    public static T WordSpacing<T>(this T descriptor, float factor) where T : TextSpanDescriptor => Apply(descriptor, s => s.WordSpacing(factor));

    /// <summary>Raised and smaller.</summary>
    public static T Superscript<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Superscript());

    /// <summary>Lowered and smaller.</summary>
    public static T Subscript<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.Subscript());

    /// <summary>Turns an OpenType feature on, such as <see cref="FontFeatures.SmallCapitals"/>.</summary>
    public static T EnableFontFeature<T>(this T descriptor, string featureName) where T : TextSpanDescriptor
        => Apply(descriptor, s => s.EnableFontFeature(featureName));

    /// <summary>Turns an OpenType feature off, such as <see cref="FontFeatures.StandardLigatures"/>.</summary>
    public static T DisableFontFeature<T>(this T descriptor, string featureName) where T : TextSpanDescriptor
        => Apply(descriptor, s => s.DisableFontFeature(featureName));

    /// <summary>On the baseline.</summary>
    public static T NormalPosition<T>(this T descriptor) where T : TextSpanDescriptor => Apply(descriptor, s => s.NormalPosition());
}
