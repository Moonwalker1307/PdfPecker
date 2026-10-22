using System.Globalization;

namespace PdfPecker.Fluent;

/// <summary>
/// A colour with alpha. A string converts to it implicitly in CSS hex notation: <c>"#rgb"</c>, <c>"#rgba"</c>,
/// <c>"#rrggbb"</c> or <c>"#rrggbbaa"</c>, with the alpha last as in CSS. <see cref="Colors"/> has a ready palette.
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    private readonly uint _rgba;

    private Color(uint rgba) => _rgba = rgba;

    /// <summary>The red component, 0 to 255.</summary>
    public byte Red => (byte)(_rgba >> 24);

    /// <summary>The green component, 0 to 255.</summary>
    public byte Green => (byte)(_rgba >> 16);

    /// <summary>The blue component, 0 to 255.</summary>
    public byte Blue => (byte)(_rgba >> 8);

    /// <summary>The alpha component, 0 (transparent) to 255 (opaque).</summary>
    public byte Alpha => (byte)_rgba;

    /// <summary>An opaque colour from its red, green and blue components.</summary>
    public static Color FromRgb(byte red, byte green, byte blue) => FromArgb(255, red, green, blue);

    /// <summary>A colour from its alpha, red, green and blue components.</summary>
    public static Color FromArgb(byte alpha, byte red, byte green, byte blue)
        => new((uint)red << 24 | (uint)green << 16 | (uint)blue << 8 | alpha);

    /// <summary>Parses <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>; the leading # is optional.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="hex"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="hex"/> is not a hex colour.</exception>
    public static Color FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (!TryParse(hex, out var color))
            throw new ArgumentException("A colour must be written #rgb, #rgba, #rrggbb or #rrggbbaa.", nameof(hex));
        return color;
    }

    /// <summary>Parses a hex colour as <see cref="FromHex"/> does, without throwing.</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (hex == null) return false;
        var s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length is not (3 or 4 or 6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
        color = s.Length switch
        {
            3 => new(Expand(v >> 8) << 24 | Expand(v >> 4) << 16 | Expand(v) << 8 | 0xFF),
            4 => new(Expand(v >> 12) << 24 | Expand(v >> 8) << 16 | Expand(v >> 4) << 8 | Expand(v)),
            6 => new(v << 8 | 0xFF),
            _ => new(v),
        };
        return true;
    }

    private static uint Expand(uint nibble) => (nibble & 0xF) * 0x11;

    /// <summary>Parses a hex colour; see <see cref="FromHex"/>.</summary>
    public static implicit operator Color(string hex) => FromHex(hex);

    /// <summary>The colour as <c>#rrggbb</c>, or <c>#rrggbbaa</c> when it is not opaque.</summary>
    public override string ToString() => Alpha == 255
        ? "#" + (_rgba >> 8).ToString("x6", CultureInfo.InvariantCulture)
        : "#" + _rgba.ToString("x8", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public bool Equals(Color other) => _rgba == other._rgba;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Color other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)_rgba;

    /// <summary>Whether two colours are the same.</summary>
    public static bool operator ==(Color left, Color right) => left.Equals(right);

    /// <summary>Whether two colours differ.</summary>
    public static bool operator !=(Color left, Color right) => !left.Equals(right);
}
