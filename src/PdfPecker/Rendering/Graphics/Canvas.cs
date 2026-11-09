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
        : this(width, height, background, null)
    {
    }

    /// <summary>
    /// A canvas on <paramref name="reuse"/> when it has exactly the canvas's pixel count, which is then overwritten
    /// with <paramref name="background"/>; otherwise on a new array.
    /// </summary>
    public Canvas(int width, int height, uint background, uint[]? reuse)
    {
        Width = width;
        Height = height;
        int count = checked(width * height);
        if (reuse != null && reuse.Length == count)
        {
            Pixels = reuse;
            Array.Fill(Pixels, background);
        }
        else if (background == 0) Pixels = new uint[count];
        else
        {
            // Every pixel is set to the background, so the runtime need not clear the array first.
            Pixels = GC.AllocateUninitializedArray<uint>(count);
            Array.Fill(Pixels, background);
        }
    }

    public int Width { get; }

    public int Height { get; }

    public uint[] Pixels { get; }

    /// <summary>
    /// For the layer of a non-isolated transparency group, the canvas of the same size that the group is drawn over,
    /// which painting does not change until the group ends. The layer holds only what the group drew, but blend modes
    /// inside the group blend with what shows through it from below (ISO 32000-2, 11.4.8). Null for an isolated group
    /// and the page.
    /// </summary>
    public Canvas? Backdrop { get; set; }

    // The rectangle painting has touched since the canvas was made or cleaned, empty while X1 <= X0: a transparency
    // layer is composited and cleared only there, since the rest of it is still transparent.
    public int DirtyX0 { get; private set; } = int.MaxValue;

    public int DirtyY0 { get; private set; } = int.MaxValue;

    public int DirtyX1 { get; private set; } = int.MinValue;

    public int DirtyY1 { get; private set; } = int.MinValue;

    /// <summary>The pixels of the dirty rectangle.</summary>
    public long DirtyArea => DirtyX1 > DirtyX0 && DirtyY1 > DirtyY0 ? (long)(DirtyX1 - DirtyX0) * (DirtyY1 - DirtyY0) : 0;

    /// <summary>Adds the pixels from <paramref name="x0"/> to <paramref name="x1"/> of row <paramref name="y"/> to the dirty rectangle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkDirty(int y, int x0, int x1)
    {
        if (x0 < DirtyX0) DirtyX0 = x0;
        if (x1 > DirtyX1) DirtyX1 = x1;
        if (y < DirtyY0) DirtyY0 = y;
        if (y >= DirtyY1) DirtyY1 = y + 1;
    }

    /// <summary>Sets a rectangle of pixels to <paramref name="color"/> without marking it dirty.</summary>
    public void FillRect(int x0, int y0, int x1, int y1, uint color)
    {
        for (int y = y0; y < y1; y++) Pixels.AsSpan(y * Width + x0, x1 - x0).Fill(color);
    }

    /// <summary>Makes the dirty rectangle transparent again and forgets it, so the canvas can be used as a new layer.</summary>
    public void Clean()
    {
        if (DirtyArea > 0) FillRect(DirtyX0, DirtyY0, DirtyX1, DirtyY1, 0);
        DirtyX0 = DirtyY0 = int.MaxValue;
        DirtyX1 = DirtyY1 = int.MinValue;
    }

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
