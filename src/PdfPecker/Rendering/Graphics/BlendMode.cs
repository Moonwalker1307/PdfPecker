namespace PdfPecker.Rendering.Graphics;

/// <summary>The PDF blend modes. The separable ones come first, the four non-separable ones (from <see cref="Hue"/> on) last.</summary>
internal enum BlendMode : byte
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion,
    Hue, Saturation, Color, Luminosity,
}
