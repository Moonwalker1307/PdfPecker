using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PdfPecker.Images;

/// <summary>
/// Turns rendered pages (premultiplied 0xAABBGGRR pixels) and decoded images (<see cref="ImagePlanes"/>) into the
/// byte layouts of <see cref="ImagePixelFormat"/>. Grey and RGB have no alpha, so transparency is flattened onto a
/// matte with the renderer's own arithmetic: a decoded image comes out as the renderer draws it on that background.
/// </summary>
internal static class PixelConversion
{
    /// <summary>The bytes one pixel of <paramref name="format"/> takes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined value.</exception>
    public static int BytesPerPixel(ImagePixelFormat format) => format switch
    {
        ImagePixelFormat.Gray => 1,
        ImagePixelFormat.Rgb => 3,
        ImagePixelFormat.Rgba => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };

    /// <summary>A 0xAARRGGBB background over white, as 0xRRGGBB: what JPEG, grey and RGB output flatten transparency onto.</summary>
    public static uint Matte(uint background)
    {
        uint a = background >> 24;
        uint Channel(int shift) => (((background >> shift) & 255) * a + 255 * (255 - a) + 127) / 255;
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    /// <summary>Rec. 601 luma, 0 to 255; a grey pixel keeps its level exactly.</summary>
    private static int Luma(int r, int g, int b) => Math.Min(255, (r * 299 + g * 587 + b * 114 + 500) / 1000);

    /// <summary>A premultiplied channel over an opaque matte channel, given the pixel's uncovered part 255 - alpha.</summary>
    private static int Over(uint channel, uint matte, uint uncovered) => (int)Math.Min(255, channel + (matte * uncovered + 127) / 255);

    /// <summary>
    /// Writes premultiplied pixels to <paramref name="destination"/>, <see cref="BytesPerPixel"/> bytes each: RGBA with
    /// straight alpha, or grey or RGB flattened onto <paramref name="matte"/> (0xRRGGBB).
    /// </summary>
    public static void FromPremultiplied(ReadOnlySpan<uint> pixels, Span<byte> destination, ImagePixelFormat format, uint matte)
    {
        uint mr = (matte >> 16) & 255, mg = (matte >> 8) & 255, mb = matte & 255;
        switch (format)
        {
            case ImagePixelFormat.Rgba:
                // 0xAABBGGRR is R, G, B, A in memory: opaque and fully transparent pixels are already right.
                MemoryMarshal.AsBytes(pixels).CopyTo(destination);
                Rendering.PngEncoder.UnpremultiplyTranslucent(pixels, destination);
                return;
            case ImagePixelFormat.Rgb:
                ToRgb(pixels, destination, mr, mg, mb);
                return;
            case ImagePixelFormat.Gray:
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint p = pixels[i];
                    uint r = p & 255, g = (p >> 8) & 255, b = (p >> 16) & 255, uncovered = 255 - (p >> 24);
                    if (uncovered == 0 && r == g && g == b)
                    {
                        destination[i] = (byte)r;
                        continue;
                    }
                    destination[i] = (byte)Luma(Over(r, mr, uncovered), Over(g, mg, uncovered), Over(b, mb, uncovered));
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format.");
        }
    }

    /// <summary>
    /// Premultiplied pixels to RGB on the matte. Rendered pages are mostly opaque, which needs only the colour bytes:
    /// four pixels at a time where vectors are fast, each run of four with a translucent pixel one by one.
    /// </summary>
    private static void ToRgb(ReadOnlySpan<uint> pixels, Span<byte> rgb, uint mr, uint mg, uint mb)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var opaque = Vector128.Create(0xFF000000u);
            var pack = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
            ref uint src = ref MemoryMarshal.GetReference(pixels);
            ref byte dst = ref MemoryMarshal.GetReference(rgb);
            // Each step stores 16 bytes but advances 12, so it stops while the store still fits.
            for (; i + 4 <= pixels.Length && i * 3 + 16 <= rgb.Length; i += 4)
            {
                var v = Vector128.LoadUnsafe(ref src, (nuint)i);
                if ((v & opaque) != opaque)
                {
                    for (int k = i; k < i + 4; k++) StoreRgb(pixels[k], rgb.Slice(k * 3, 3), mr, mg, mb);
                    continue;
                }
                Vector128.Shuffle(v.AsByte(), pack).StoreUnsafe(ref dst, (nuint)(i * 3));
            }
        }
        for (; i < pixels.Length; i++) StoreRgb(pixels[i], rgb.Slice(i * 3, 3), mr, mg, mb);
    }

    private static void StoreRgb(uint p, Span<byte> rgb, uint mr, uint mg, uint mb)
    {
        uint uncovered = 255 - (p >> 24);
        rgb[0] = (byte)Over(p & 255, mr, uncovered);
        rgb[1] = (byte)Over((p >> 8) & 255, mg, uncovered);
        rgb[2] = (byte)Over((p >> 16) & 255, mb, uncovered);
    }

    /// <summary>
    /// The pixels of decoded planes in <paramref name="format"/>, as a new array or, when nothing needs changing, the
    /// colour plane itself. Transparency goes onto <paramref name="matte"/> (0xRRGGBB) for grey and RGB, as the
    /// renderer composites the image there: the colour scaled by alpha/256 rounded down, plus the matte's share
    /// rounded to the nearest level.
    /// </summary>
    public static byte[] FromPlanes(ImagePlanes planes, ImagePixelFormat format, uint matte)
    {
        int count = planes.Width * planes.Height, colors = planes.Colors;
        var color = planes.Color;
        var alpha = planes.HasTransparency ? planes.Alpha : null;
        if (alpha == null && BytesPerPixel(format) == colors && color.Length == count * colors) return color;

        var result = new byte[count * BytesPerPixel(format)];
        uint mr = (matte >> 16) & 255, mg = (matte >> 8) & 255, mb = matte & 255;
        for (int i = 0; i < count; i++)
        {
            int r = color[i * colors], g = colors == 3 ? color[i * colors + 1] : r, b = colors == 3 ? color[i * colors + 2] : r;
            int a = alpha?[i] ?? 255;
            if (format == ImagePixelFormat.Rgba)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), (uint)(r | g << 8 | b << 16 | a << 24));
                continue;
            }
            if (a != 255)
            {
                uint scale = (uint)(a + (a >> 7)), uncovered = (uint)(255 - a);
                r = Over((uint)r * scale >> 8, mr, uncovered);
                g = Over((uint)g * scale >> 8, mg, uncovered);
                b = Over((uint)b * scale >> 8, mb, uncovered);
            }
            if (format == ImagePixelFormat.Gray) result[i] = (byte)Luma(r, g, b);
            else
            {
                result[i * 3] = (byte)r;
                result[i * 3 + 1] = (byte)g;
                result[i * 3 + 2] = (byte)b;
            }
        }
        return result;
    }
}
