using PdfPecker.Fluent.Html;
using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>How <see cref="ContainerExtensions.AspectRatio"/> sizes the slot.</summary>
public enum AspectRatioOption
{
    /// <summary>As wide as the slot allows; the height follows from the ratio.</summary>
    FitWidth,

    /// <summary>As tall as the slot allows (which a row or a <c>Height</c> gives it); the width follows from the ratio.</summary>
    FitHeight,

    /// <summary>As large as fits both the slot's width and height.</summary>
    FitArea,
}

/// <summary>A shadow behind a slot, for <see cref="ContainerExtensions.Shadow"/>.</summary>
public sealed class BoxShadowStyle
{
    /// <summary>The shadow colour; a translucent colour such as <c>#00000040</c> looks most natural.</summary>
    public Color Color { get; init; } = Color.FromArgb(0x40, 0, 0, 0);

    /// <summary>How far the shadow is blurred, in points.</summary>
    public float Blur { get; init; }

    /// <summary>How far the shadow grows beyond the slot on every side, in points.</summary>
    public float Spread { get; init; }

    /// <summary>The shadow's offset to the right, in points (negative to the left).</summary>
    public float OffsetX { get; init; }

    /// <summary>The shadow's offset downwards, in points (negative upwards).</summary>
    public float OffsetY { get; init; }
}

public static partial class ContainerExtensions
{
    // Transforms. They change how the content is drawn, not the space it takes, as in QuestPDF.

    /// <summary>Rotates the content clockwise by <paramref name="angle"/> degrees around its top-left corner.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="angle"/> is not finite.</exception>
    public static IContainer Rotate(this IContainer container, float angle)
        => Transform(container, "rotate(" + CssValue.Number(Finite(angle, nameof(angle))) + "deg)", topLeft: true);

    /// <summary>
    /// Turns the content a quarter clockwise. Unlike <see cref="Rotate"/>, the layout turns too: the content's lines run
    /// down the page. Give the slot a height when the content is more than a short label.
    /// </summary>
    public static IContainer RotateRight(this IContainer container) => Style(container, Rank.Size, ("writing-mode", "vertical-rl"));

    /// <summary>Turns the content a quarter anticlockwise, its lines running up the page; see <see cref="RotateRight"/>.</summary>
    public static IContainer RotateLeft(this IContainer container)
        => Style(container, Rank.Size, ("writing-mode", "vertical-rl"), ("transform", "rotate(180deg)"));

    /// <summary>Scales the content by <paramref name="factor"/> from its top-left corner; a negative factor also mirrors it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is zero, not finite, or beyond ±1,000.</exception>
    public static IContainer Scale(this IContainer container, float factor)
    {
        string f = CssValue.Number(Factor(factor, nameof(factor)));
        return Transform(container, "scale(" + f + "," + f + ")", topLeft: true);
    }

    /// <summary>Scales the content horizontally only.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is zero, not finite, or beyond ±1,000.</exception>
    public static IContainer ScaleHorizontal(this IContainer container, float factor)
        => Transform(container, "scale(" + CssValue.Number(Factor(factor, nameof(factor))) + ",1)", topLeft: true);

    /// <summary>Scales the content vertically only.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is zero, not finite, or beyond ±1,000.</exception>
    public static IContainer ScaleVertical(this IContainer container, float factor)
        => Transform(container, "scale(1," + CssValue.Number(Factor(factor, nameof(factor))) + ")", topLeft: true);

    /// <summary>Mirrors the content left to right, in place.</summary>
    public static IContainer FlipHorizontal(this IContainer container) => Transform(container, "scale(-1,1)", topLeft: false);

    /// <summary>Mirrors the content top to bottom, in place.</summary>
    public static IContainer FlipVertical(this IContainer container) => Transform(container, "scale(1,-1)", topLeft: false);

    /// <summary>Mirrors the content both ways, which turns it upside down in place.</summary>
    public static IContainer FlipOver(this IContainer container) => Transform(container, "scale(-1,-1)", topLeft: false);

    /// <summary>Moves the content to the right (or left, when negative) without changing the space it takes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite or beyond ±1,000,000 points.</exception>
    public static IContainer TranslateX(this IContainer container, float value, Unit unit = Unit.Point)
        => Transform(container, "translate(" + CssValue.Pt(Offset(value, unit, nameof(value))) + ",0)", topLeft: false);

    /// <summary>Moves the content down (or up, when negative) without changing the space it takes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite or beyond ±1,000,000 points.</exception>
    public static IContainer TranslateY(this IContainer container, float value, Unit unit = Unit.Point)
        => Transform(container, "translate(0," + CssValue.Pt(Offset(value, unit, nameof(value))) + ")", topLeft: false);

    private static Container Transform(IContainer container, string transform, bool topLeft)
        => topLeft
            ? Style(container, Rank.Size, ("transform", transform), ("transform-origin", "0 0"))
            : Style(container, Rank.Size, ("transform", transform));

    private static float Finite(float value, string paramName)
    {
        if (!float.IsFinite(value) || Math.Abs(value) > Length.MaxPoints)
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be a finite number from -1,000,000 to 1,000,000.");
        return value;
    }

    private static float Factor(float value, string paramName)
    {
        if (!float.IsFinite(value) || value == 0 || Math.Abs(value) > 1000)
            throw new ArgumentOutOfRangeException(paramName, value, "The factor must be a finite number other than zero, from -1,000 to 1,000.");
        return value;
    }

    private static float Offset(float value, Unit unit, string paramName)
    {
        float points = Length.ToPoints(value, unit);
        if (!float.IsFinite(points) || Math.Abs(points) > Length.MaxPoints)
            throw new ArgumentOutOfRangeException(paramName, value, "An offset must be a finite number from -1,000,000 to 1,000,000 points.");
        return points;
    }

    // Placement

    /// <summary>
    /// Takes the content out of the layout: it is drawn where it would start, takes no space, and is as wide as its
    /// content needs rather than the slot. Content after it is drawn over it unless it has a higher
    /// <see cref="ZIndex"/>. Combine it with <see cref="TranslateX"/> and <see cref="TranslateY"/> to
    /// place a stamp or a badge.
    /// </summary>
    public static IContainer Unconstrained(this IContainer container)
        => Style(container, Rank.Size, ("width", "0"), ("height", "0"), ("min-height", "0"))
            .Element(inner => Style(inner, Rank.Size, ("width", "max-content"), HtmlWriter.ContentSized));

    /// <summary>Draws the content above (higher values) or below (lower values) overlapping content around it.</summary>
    public static IContainer ZIndex(this IContainer container, int index)
        => Style(container, Rank.Any, ("position", "relative"), ("z-index", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Gives the slot a width-to-height ratio, such as 16/9f.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratio"/> is not a finite number greater than zero.</exception>
    public static IContainer AspectRatio(this IContainer container, float ratio, AspectRatioOption option = AspectRatioOption.FitWidth)
    {
        string r = CssValue.Number(Length.Weight(ratio, nameof(ratio)));
        return option switch
        {
            AspectRatioOption.FitHeight => Style(container, Rank.Size, ("height", "100%"), ("width", "auto"), ("aspect-ratio", r)),
            AspectRatioOption.FitArea => Style(container, Rank.Size, ("width", "100%"), ("max-height", "100%"), ("aspect-ratio", r)),
            AspectRatioOption.FitWidth => Style(container, Rank.Size, ("width", "100%"), ("aspect-ratio", r)),
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown aspect ratio option."),
        };
    }

    /// <summary>
    /// Starts the content on a new page unless at least <paramref name="minHeight"/> is left on this one, so a heading
    /// does not end up alone at the bottom of a page.
    /// </summary>
    public static IContainer EnsureSpace(this IContainer container, float minHeight = 150, Unit unit = Unit.Point)
        => Decorate(container, new Decorator(DecoratorKind.EnsureSpace) { Value = Length.Points(minHeight, unit, nameof(minHeight)) });

    // Paint

    /// <summary>A shadow behind the slot.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A length in the style is negative where it may not be, or not finite.</exception>
    public static IContainer Shadow(this IContainer container, BoxShadowStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        string value = CssValue.Pt(Offset(style.OffsetX, Unit.Point, nameof(style))) + " " + CssValue.Pt(Offset(style.OffsetY, Unit.Point, nameof(style)))
            + " " + CssValue.Pt(Length.Points(style.Blur, Unit.Point, nameof(style))) + " " + CssValue.Pt(Offset(style.Spread, Unit.Point, nameof(style)))
            + " " + CssValue.Color(style.Color);
        return Style(container, Rank.Paint, ("box-shadow", value));
    }

    /// <summary>Rounds the top-left corner.</summary>
    public static IContainer CornerRadiusTopLeft(this IContainer container, float value, Unit unit = Unit.Point) => Corner(container, "top-left", value, unit);

    /// <summary>Rounds the top-right corner.</summary>
    public static IContainer CornerRadiusTopRight(this IContainer container, float value, Unit unit = Unit.Point) => Corner(container, "top-right", value, unit);

    /// <summary>Rounds the bottom-left corner.</summary>
    public static IContainer CornerRadiusBottomLeft(this IContainer container, float value, Unit unit = Unit.Point) => Corner(container, "bottom-left", value, unit);

    /// <summary>Rounds the bottom-right corner.</summary>
    public static IContainer CornerRadiusBottomRight(this IContainer container, float value, Unit unit = Unit.Point) => Corner(container, "bottom-right", value, unit);

    private static Container Corner(IContainer container, string corner, float value, Unit unit)
        => Style(container, Rank.Paint, ("border-" + corner + "-radius", CssValue.Pt(Length.Points(value, unit, nameof(value)))), ("overflow", "hidden"));

    // Development and composition

    /// <summary>
    /// Outlines the slot and labels it, to see where content goes while building a layout. Remove it before
    /// production.
    /// </summary>
    public static IContainer DebugArea(this IContainer container, string? text = null, Color? color = null)
    {
        var c = color ?? Colors.Red.Medium;
        IContainer primary = null!;
        container.Layers(layers =>
        {
            primary = layers.PrimaryLayer();
            layers.Layer().Border(1).BorderColor(c);
            if (!string.IsNullOrEmpty(text))
                layers.Layer().AlignRight().AlignTop().Background(c).PaddingHorizontal(2).Text(text).FontSize(7).FontColor(Colors.White);
        });
        return primary;
    }

    /// <summary>
    /// Fills the slot with a method, as QuestPDF's <c>Lazy</c> does. PdfPecker lays the document out in one pass, so
    /// the content is built straight away.
    /// </summary>
    public static void Lazy(this IContainer container, Action<IContainer> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        content(container);
    }

    /// <summary>The same as <see cref="Lazy"/>.</summary>
    public static void LazyWithCache(this IContainer container, Action<IContainer> content) => Lazy(container, content);
}
