namespace PdfPecker.Css;

/// <summary>
/// The SVG geometry and gradient stop properties that came from the cascade (cx, cy, r, rx, ry, x, y, d, stop-color
/// and stop-opacity). A property that no rule sets stays null, so the SVG renderer reads the attribute as before; one
/// that a rule sets wins over the attribute, as a presentation attribute has the lowest author priority. Instances
/// are never changed after they are stored on a style.
/// </summary>
internal sealed class SvgGeometry
{
    /// <summary>
    /// Lengths in points; a percentage or calc() is resolved against the viewport by the SVG renderer, which knows
    /// it. rx and ry may be auto, which takes the value of the other radius.
    /// </summary>
    public Length? Cx, Cy, R, Rx, Ry, X, Y;

    /// <summary>The path data of <c>d: path("...")</c>; null when the property is not set or is none.</summary>
    public string? PathData;

    /// <summary>Whether <c>d</c> is set, so that the attribute is ignored: to <see cref="PathData"/>, or to none.</summary>
    public bool HasPath;

    /// <summary>The text of <c>stop-color</c>, a colour that was valid when it was read; null when no rule sets it.</summary>
    public string? StopColor;

    public float? StopOpacity;

    public SvgGeometry Copy() => (SvgGeometry)MemberwiseClone();
}
