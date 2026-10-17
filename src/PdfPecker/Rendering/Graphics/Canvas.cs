using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering.Graphics;

/// <summary>A device bitmap of premultiplied RGBA pixels (memory order R, G, B, A).</summary>
internal sealed class Canvas
{
    private const int GreenShift = 8;
    private const int BlueShift = 16;
    private const int AlphaShift = 24;
    private const int Opaque = 255;

    // Two 8-bit channels at a time sit in the low byte of each 16-bit half, so one multiply scales both.
    private const uint RedBlueMask = 0x00FF00FF;
    private const uint AlphaGreenMask = 0xFF00FF00;
    private const uint RoundingBias = 0x00800080;

    public Canvas(int width, int height, uint background)
    {
        Width = width;
        Height = height;
        Pixels = new uint[checked(width * height)];
        if (background != 0) Array.Fill(Pixels, background);
    }

    public int Width { get; }

    public int Height { get; }

    public uint[] Pixels { get; }

    /// <summary>Packs a straight (non-premultiplied) color into a premultiplied pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int r, int g, int b, int a = Opaque)
    {
        if (a < Opaque)
        {
            r = (r * a + 127) / 255;
            g = (g * a + 127) / 255;
            b = (b * a + 127) / 255;
        }
        return (uint)(r | g << GreenShift | b << BlueShift | a << AlphaShift);
    }

    /// <summary>Scales all four channels of a premultiplied pixel by <paramref name="a"/>/256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Scale(uint p, uint a)
    {
        uint redBlue = ((p & RedBlueMask) * a >> 8) & RedBlueMask;
        uint alphaGreen = ((p >> 8) & RedBlueMask) * a & AlphaGreenMask;
        return redBlue | alphaGreen;
    }

    /// <summary>Scales all four channels by <paramref name="k"/>/255 with exact rounding (k in 0..255).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mul255(uint p, uint k)
    {
        // x / 255 rounded to nearest is (x + 128 + ((x + 128) >> 8)) >> 8 for every product x of two bytes (x up to 255 * 255).
        uint redBlue = (p & RedBlueMask) * k + RoundingBias;
        redBlue = ((redBlue + ((redBlue >> 8) & RedBlueMask)) >> 8) & RedBlueMask;
        uint alphaGreen = ((p >> 8) & RedBlueMask) * k + RoundingBias;
        alphaGreen = (alphaGreen + ((alphaGreen >> 8) & RedBlueMask)) & AlphaGreenMask;
        return redBlue | alphaGreen;
    }

    /// <summary>Source-over of premultiplied <paramref name="s"/> onto <paramref name="d"/> (opaque stays exactly opaque).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Over(uint s, uint d)
    {
        uint sourceAlpha = s >> AlphaShift;
        if (sourceAlpha == Opaque) return s;
        if (sourceAlpha == 0) return d;
        return s + Mul255(d, Opaque - sourceAlpha);
    }
}
