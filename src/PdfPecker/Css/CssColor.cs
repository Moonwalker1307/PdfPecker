using System.Globalization;

namespace PdfPecker.Css;

/// <summary>Colors are stored as 0xAARRGGBB. Alpha 0 means transparent.</summary>
internal static partial class CssColor
{
    public const uint Black = 0xFF000000;
    public const uint Transparent = 0;

    private const int AlphaShift = 24;
    private const int RedShift = 16;
    private const int GreenShift = 8;
    private const uint ChannelMask = 0xFF;
    private const uint RgbMask = 0x00FFFFFF;
    private const uint OpaqueAlpha = 0xFF000000;
    private const uint FullAlpha = 0xFF;

    public static bool IsVisible(uint color) => color >> AlphaShift != 0;

    /// <summary>The colour with its alpha multiplied by <paramref name="opacity"/>.</summary>
    public static uint Fade(uint color, float opacity)
    {
        if (opacity >= 1) return color;
        uint alpha = (uint)MathF.Round((color >> AlphaShift) * Math.Clamp(opacity, 0, 1));
        return (alpha << AlphaShift) | (color & RgbMask);
    }

    /// <summary>Blends a translucent color over white (box shadows, whose blur bands are painted over each other).</summary>
    public static uint FlattenOnWhite(uint color)
    {
        uint alpha = color >> AlphaShift;
        if (alpha == FullAlpha || alpha == 0) return color;
        uint red = OverWhite((color >> RedShift) & ChannelMask, alpha);
        uint green = OverWhite((color >> GreenShift) & ChannelMask, alpha);
        uint blue = OverWhite(color & ChannelMask, alpha);
        return OpaqueAlpha | red << RedShift | green << GreenShift | blue;
    }

    private static uint OverWhite(uint channel, uint alpha) => (channel * alpha + 255 * (255 - alpha)) / 255;

    /// <summary>Parses a CSS color. <c>currentColor</c> resolves to <paramref name="current"/>.</summary>
    public static bool TryParse(string value, uint current, out uint color)
    {
        color = 0;
        var text = value.AsSpan().Trim();
        if (text.Length == 0) return false;
        if (text[0] == '#') return TryParseHex(text.Slice(1), out color);

        int open = text.IndexOf('(');
        if (open > 0) return TryParseFunction(text, open, current, out color);
        return TryParseKeyword(text.ToString(), current, out color);
    }

    private static bool TryParseFunction(ReadOnlySpan<char> text, int open, uint current, out uint color)
    {
        color = 0;
        if (text[^1] != ')') return false;
        var name = text.Slice(0, open).Trim();
        var arguments = text.Slice(open + 1, text.Length - open - 2);
        return ColorFunctions.TryParse(name.ToString(), arguments.ToString(), current, out color);
    }

    private static bool TryParseKeyword(string keyword, uint current, out uint color)
    {
        if (keyword.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = Transparent; return true; }
        if (keyword.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) { color = current; return true; }
        return NamedColors.TryGetValue(keyword, out color) || SystemColors.TryGetValue(keyword, out color);
    }

    /// <summary>
    /// Parses the digits after the "#": three (#rgb), four (#rgba), six (#rrggbb) or eight (#rrggbbaa). The digits are
    /// read with the framework's hex rule, which also accepts white space around them; the length decides the form.
    /// </summary>
    private static bool TryParseHex(ReadOnlySpan<char> digits, out uint color)
    {
        color = 0;
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return false;
        switch (digits.Length)
        {
            case 3:
                color = OpaqueAlpha | Doubled(value, 8) << RedShift | Doubled(value, 4) << GreenShift | Doubled(value, 0);
                return true;
            case 4:
                color = Doubled(value, 0) << AlphaShift | Doubled(value, 12) << RedShift | Doubled(value, 8) << GreenShift | Doubled(value, 4);
                return true;
            case 6:
                color = OpaqueAlpha | value;
                return true;
            case 8:
                color = (value & ChannelMask) << AlphaShift | value >> 8;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The hex digit at <paramref name="shift"/> written twice, so that #f becomes #ff.</summary>
    private static uint Doubled(uint value, int shift)
    {
        uint digit = (value >> shift) & 0xF;
        return digit << 4 | digit;
    }
}
