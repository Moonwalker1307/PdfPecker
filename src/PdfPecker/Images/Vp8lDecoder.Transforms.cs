namespace PdfPecker.Images;

internal ref partial struct Vp8lDecoder
{
    private const uint OpaqueBlack = 0xFF000000;

    private enum TransformType
    {
        Predictor = 0,
        CrossColor = 1,
        SubtractGreen = 2,
        ColorIndexing = 3,
    }

    /// <summary>
    /// The number of bits that select how many pixels share one coded pixel under colour indexing: eight with up to
    /// two colours, four with up to four, two with up to sixteen, and one pixel each with more.
    /// </summary>
    private static int PackingBits(int colors) => colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;

    /// <summary>
    /// Undoes the delta coding of a palette. The palette has an entry for every index a packed pixel can hold;
    /// indexes past its colours are transparent black.
    /// </summary>
    private static uint[] ExpandPalette(uint[] coded, int colors, int packingBits)
    {
        var palette = new uint[1 << (8 >> packingBits)];
        palette[0] = coded[0];
        for (int i = 1; i < colors && i < palette.Length; i++) palette[i] = AddPixels(coded[i], palette[i - 1]);
        return palette;
    }

    /// <summary>
    /// One transform of the stream: its type, the size of its tiles (or the pixel packing of colour indexing) as a
    /// power of two, the width of the image it applies to, and its data, the tile image or the palette.
    /// </summary>
    private readonly record struct Transform(TransformType Type, int Bits, int Width, uint[]? Data)
    {
        /// <summary>Undoes the transform, in place except when colour indexing unpacks pixels into a wider image.</summary>
        public uint[] Undo(uint[] pixels, int height)
        {
            switch (Type)
            {
                case TransformType.SubtractGreen:
                    AddGreenToBlueAndRed(pixels);
                    return pixels;
                case TransformType.CrossColor:
                    UndoCrossColor(pixels, height);
                    return pixels;
                case TransformType.Predictor:
                    UndoPredictor(pixels, height);
                    return pixels;
                default:
                    return Bits == 0 ? MapColorIndexes(pixels) : UnpackColorIndexes(pixels, height);
            }
        }

        private static void AddGreenToBlueAndRed(Span<uint> pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                uint argb = pixels[i], green = (argb >> 8) & 0xFF;
                uint redBlue = (argb & 0x00FF00FF) + (green << 16 | green);
                pixels[i] = (argb & 0xFF00FF00) | (redBlue & 0x00FF00FF);
            }
        }

        private void UndoCrossColor(uint[] pixels, int height)
        {
            int tilesPerRow = SubSampleSize(Width, Bits), tileSize = 1 << Bits;
            for (int y = 0; y < height; y++)
            {
                var row = pixels.AsSpan(y * Width, Width);
                var tiles = Data.AsSpan((y >> Bits) * tilesPerRow, tilesPerRow);
                for (int tile = 0; tile < tiles.Length; tile++)
                {
                    var element = new ColorTransformElement(tiles[tile]);
                    int x = tile << Bits;
                    var span = row.Slice(x, Math.Min(tileSize, row.Length - x));
                    for (int i = 0; i < span.Length; i++) span[i] = element.Undo(span[i]);
                }
            }
        }

        private void UndoPredictor(uint[] pixels, int height)
        {
            // The first row predicts from the left, its first pixel from opaque black, and the first column from above.
            var first = pixels.AsSpan(0, Width);
            first[0] = AddPixels(first[0], OpaqueBlack);
            for (int x = 1; x < first.Length; x++) first[x] = AddPixels(first[x], first[x - 1]);

            int tilesPerRow = SubSampleSize(Width, Bits);
            for (int y = 1; y < height; y++)
            {
                var row = pixels.AsSpan(y * Width, Width);
                // The row above and the first pixel of this row, which is the top-right of the row's last pixel.
                ReadOnlySpan<uint> above = pixels.AsSpan((y - 1) * Width, Width + 1);
                var modes = Data.AsSpan((y >> Bits) * tilesPerRow, tilesPerRow);
                row[0] = AddPixels(row[0], above[0]);
                for (int x = 1; x < row.Length; x++)
                {
                    int mode = (int)(modes[x >> Bits] >> 8) & 0xF;
                    row[x] = AddPixels(row[x], Predict(mode, row[x - 1], above[x], above[x - 1], above[x + 1]));
                }
            }
        }

        /// <summary>Replaces each pixel by the palette colour its green channel indexes.</summary>
        private uint[] MapColorIndexes(uint[] pixels)
        {
            var palette = Data!;
            for (int i = 0; i < pixels.Length; i++)
            {
                int index = (int)(pixels[i] >> 8) & 0xFF;
                pixels[i] = index < palette.Length ? palette[index] : 0;
            }
            return pixels;
        }

        /// <summary>Unpacks the indexes that share a coded pixel, lowest bits first, into a picture of the full width.</summary>
        private uint[] UnpackColorIndexes(uint[] packed, int height)
        {
            var palette = Data!;
            int packedWidth = SubSampleSize(Width, Bits);
            int bitsPerPixel = 8 >> Bits, pixelsPerByte = 1 << Bits;
            uint mask = (1u << bitsPerPixel) - 1;
            var pixels = new uint[(long)Width * height];
            for (int y = 0; y < height; y++)
            {
                var source = packed.AsSpan(y * packedWidth, packedWidth);
                var row = pixels.AsSpan(y * Width, Width);
                uint indexes = 0;
                for (int x = 0; x < row.Length; x++)
                {
                    if ((x & (pixelsPerByte - 1)) == 0) indexes = (source[x >> Bits] >> 8) & 0xFF;
                    row[x] = palette[indexes & mask];
                    indexes >>= bitsPerPixel;
                }
            }
            return pixels;
        }
    }

    /// <summary>The multipliers of one cross-colour tile, signed fixed-point numbers with five fraction bits.</summary>
    private readonly struct ColorTransformElement(uint packed)
    {
        private readonly sbyte greenToRed = (sbyte)packed;
        private readonly sbyte greenToBlue = (sbyte)(packed >> 8);
        private readonly sbyte redToBlue = (sbyte)(packed >> 16);

        public uint Undo(uint argb)
        {
            sbyte green = (sbyte)(argb >> 8);
            int red = (int)(argb >> 16) & 0xFF, blue = (int)argb & 0xFF;
            red = (red + (greenToRed * green >> 5)) & 0xFF;
            // Blue is predicted from the red value already restored.
            blue += greenToBlue * green >> 5;
            blue += redToBlue * (sbyte)red >> 5;
            return (argb & 0xFF00FF00) | (uint)red << 16 | (uint)(blue & 0xFF);
        }
    }

    // ------------------------------------------------------------------ predictors

    private static uint Predict(int mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(top, left, topLeft),
        12 => ClampedAddSubtractFull(left, top, topLeft),
        13 => ClampedAddSubtractHalf(Average2(left, top), topLeft),
        _ => OpaqueBlack, // mode 0, and the unused 14 and 15
    };

    /// <summary>Adds two pixels channel by channel, modulo 256.</summary>
    private static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xFF00FF00) + (b & 0xFF00FF00);
        uint redBlue = (a & 0x00FF00FF) + (b & 0x00FF00FF);
        return (alphaGreen & 0xFF00FF00) | (redBlue & 0x00FF00FF);
    }

    /// <summary>The average of each channel, rounded down.</summary>
    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    /// <summary>Whichever of top and left is nearer, summed over the channels, to the gradient top + left - topLeft.</summary>
    private static uint Select(uint top, uint left, uint topLeft)
    {
        int topNearer = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int t = Channel(top, shift), l = Channel(left, shift), tl = Channel(topLeft, shift);
            topNearer += Math.Abs(l - tl) - Math.Abs(t - tl);
        }
        return topNearer <= 0 ? top : left;
    }

    private static uint ClampedAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
            result |= (uint)Math.Clamp(Channel(a, shift) + Channel(b, shift) - Channel(c, shift), 0, 255) << shift;
        return result;
    }

    private static uint ClampedAddSubtractHalf(uint average, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int a = Channel(average, shift), b = Channel(c, shift);
            result |= (uint)Math.Clamp(a + (a - b) / 2, 0, 255) << shift;
        }
        return result;
    }

    private static int Channel(uint argb, int shift) => (int)(argb >> shift) & 0xFF;
}
