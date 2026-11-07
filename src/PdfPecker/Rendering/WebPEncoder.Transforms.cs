using System.Buffers;
using System.Numerics;

namespace PdfPecker.Rendering;

internal static partial class WebPEncoder
{
    // ------------------------------------------------------------------ colour indexing

    private const int MaxPaletteSize = 256;
    private const int PaletteSizeBits = 8;

    // The distinct colours are collected in an open-addressing hash table, which has room to spare for 256 of them.
    private const int PaletteHashBits = 10;
    private const int PaletteHashSize = 1 << PaletteHashBits;

    // Multiplicative hash used for the palette and the colour cache, as in libwebp.
    private const uint ColorHashMultiplier = 0x1E35A7BD;

    private const uint OpaqueBlack = 0xFF000000;

    /// <summary>The distinct colours, sorted, when there are at most 256 of them; otherwise null.</summary>
    private static uint[]? FindPalette(ReadOnlySpan<uint> argb)
    {
        var colors = new uint[PaletteHashSize];
        var used = new bool[PaletteHashSize];
        int count = 0;
        uint previous = 0;
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            if (i > 0 && p == previous) continue;
            previous = p;
            int slot = (int)((p * ColorHashMultiplier) >> (32 - PaletteHashBits));
            while (used[slot] && colors[slot] != p) slot = (slot + 1) & (PaletteHashSize - 1);
            if (used[slot]) continue;
            if (++count > MaxPaletteSize) return null;
            used[slot] = true;
            colors[slot] = p;
        }
        var palette = new uint[count];
        for (int slot = 0, n = 0; slot < PaletteHashSize; slot++)
            if (used[slot]) palette[n++] = colors[slot];
        Array.Sort(palette);
        return palette;
    }

    private static byte[] EncodeWithPalette(ReadOnlySpan<uint> argb, int width, int height, bool hasAlpha, uint[] palette, Settings settings)
    {
        int colors = palette.Length;
        var writer = StartBitstream(width, height, hasAlpha, argb.Length / 8 + 1024);
        WriteTransform(writer, TransformType.ColorIndexing);
        writer.Write((uint)colors - 1, PaletteSizeBits);
        // The palette is coded as the difference of each colour from the one before.
        var delta = new uint[colors];
        delta[0] = palette[0];
        for (int i = 1; i < colors; i++) delta[i] = SubtractPixels(palette[i], palette[i - 1]);
        EncodeImage(writer, delta, colors, isMainImage: false, settings);

        int widthBits = PackingBits(colors);
        int packedWidth = (width + (1 << widthBits) - 1) >> widthBits;
        var packed = ArrayPool<uint>.Shared.Rent(packedWidth * height);
        try
        {
            var indexes = packed.AsSpan(0, packedWidth * height);
            PackIndexes(argb, width, height, palette, widthBits, packedWidth, indexes);
            EndTransforms(writer);
            EncodeImage(writer, indexes, packedWidth, isMainImage: true, settings);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(packed);
        }
        return writer.ToArray();
    }

    /// <summary>
    /// How many pixels share one coded pixel, as a power of two: 8 for up to two colours, 4 for up to four, 2 for up
    /// to sixteen, else 1.
    /// </summary>
    private static int PackingBits(int colors) => colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;

    /// <summary>Writes the palette indexes in the green channel, several to a pixel when the palette is small, low bits first.</summary>
    private static void PackIndexes(ReadOnlySpan<uint> argb, int width, int height, uint[] palette, int widthBits, int packedWidth, Span<uint> packed)
    {
        int bitsPerIndex = 8 >> widthBits, perPixel = 1 << widthBits;
        var index = new Dictionary<uint, uint>(palette.Length);
        for (int i = 0; i < palette.Length; i++) index[palette[i]] = (uint)i;
        packed.Clear();
        // Rendered pages are mostly runs of one colour, so the index of the pixel before is reused.
        uint previousColor = argb[0], previousIndex = index[argb[0]];
        for (int y = 0; y < height; y++)
        {
            int row = y * width, packedRow = y * packedWidth;
            for (int x = 0; x < width; x++)
            {
                uint p = argb[row + x];
                if (p != previousColor)
                {
                    previousColor = p;
                    previousIndex = index[p];
                }
                packed[packedRow + (x >> widthBits)] |= previousIndex << (8 + (x & (perPixel - 1)) * bitsPerIndex);
            }
        }
        for (int i = 0; i < packed.Length; i++) packed[i] |= OpaqueBlack;
    }

    // ------------------------------------------------------------------ subtract green and prediction

    // The predictor image has one mode for each tile of 16x16 pixels; the tile size is sent as its bits minus 2.
    private const int PredictorBits = 4;
    private const int PredictorTileSize = 1 << PredictorBits;
    private const int TileSizeBits = 3;
    private const int MinTileBits = 2;

    // Below this many tile rows the prediction runs on the calling thread, which is faster for small images.
    private const int ParallelTileRows = 8;

    /// <summary>The fourteen predictors of VP8L, by their number in the predictor image.</summary>
    private enum PredictorMode
    {
        Black = 0,
        Left = 1,
        Top = 2,
        TopRight = 3,
        TopLeft = 4,
        AverageLeftTopRightTop = 5,
        AverageLeftTopLeft = 6,
        AverageLeftTop = 7,
        AverageTopLeftTop = 8,
        AverageTopTopRight = 9,
        AverageOfAverages = 10,
        Select = 11,
        ClampedAddSubtractFull = 12,
        ClampedAddSubtractHalf = 13,
    }

    /// <summary>
    /// Encodes the first <paramref name="n"/> pixels of <paramref name="argb"/>, which are changed: the predictor path
    /// is the last to read them.
    /// </summary>
    private static byte[] EncodeWithPredictor(uint[] argb, int n, int width, int height, bool hasAlpha, Settings settings)
    {
        var writer = StartBitstream(width, height, hasAlpha, n + 1024);
        SubtractGreen(argb.AsSpan(0, n));
        WriteTransform(writer, TransformType.SubtractGreen);

        int tilesX = (width + PredictorTileSize - 1) >> PredictorBits;
        int tilesY = (height + PredictorTileSize - 1) >> PredictorBits;
        var modes = new uint[tilesX * tilesY];
        // Every residual is written, so the rented array need not be cleared.
        var residuals = ArrayPool<uint>.Shared.Rent(n);
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = tilesY > ParallelTileRows ? Environment.ProcessorCount : 1 };
            Parallel.For(0, tilesY, options, tileY =>
            {
                // The residuals of the best mode so far and of the mode being tried, so the winner's need not be computed again.
                Span<uint> scratch = stackalloc uint[2 * PredictorTileSize * PredictorTileSize];
                for (int tileX = 0; tileX < tilesX; tileX++)
                {
                    var tile = new Tile(tileX, tileY, width, height);
                    var mode = PredictTile(argb, width, tile, settings.Modes, residuals, scratch);
                    modes[tileY * tilesX + tileX] = OpaqueBlack | (uint)mode << 8; // the mode goes in the green channel
                }
            });
            WriteTransform(writer, TransformType.Predictor);
            writer.Write(PredictorBits - MinTileBits, TileSizeBits);
            EncodeImage(writer, modes, tilesX, isMainImage: false, settings);
            EndTransforms(writer);
            EncodeImage(writer, residuals.AsSpan(0, n), width, isMainImage: true, settings);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(residuals);
        }
        return writer.ToArray();
    }

    /// <summary>Red and blue minus green, each modulo 256, in place.</summary>
    private static void SubtractGreen(Span<uint> pixels)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var alphaGreen = new Vector<uint>(0xFF00FF00);
            var redBlue = new Vector<uint>(0x00FF00FF);
            var low = new Vector<uint>(0xFF);
            for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                var p = new Vector<uint>(pixels.Slice(i));
                var green = Vector.ShiftRightLogical(p, 8) & low;
                var difference = alphaGreen + (p & redBlue) - (Vector.ShiftLeft(green, 16) | green);
                ((p & alphaGreen) | (difference & redBlue)).CopyTo(pixels.Slice(i));
            }
        }
        for (; i < pixels.Length; i++)
        {
            uint p = pixels[i], green = (p >> 8) & 0xFF;
            uint redBlue = 0xFF00FF00 + (p & 0x00FF00FF) - (green << 16 | green); // the high bytes absorb borrows
            pixels[i] = (p & 0xFF00FF00) | (redBlue & 0x00FF00FF);
        }
    }

    /// <summary>The pixel bounds of one predictor tile, clipped to the image.</summary>
    private readonly struct Tile
    {
        public Tile(int tileX, int tileY, int width, int height)
        {
            X0 = tileX << PredictorBits;
            Y0 = tileY << PredictorBits;
            X1 = Math.Min(width, X0 + PredictorTileSize);
            Y1 = Math.Min(height, Y0 + PredictorTileSize);
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }
    }

    /// <summary>
    /// The residuals of the tile's pixels in the image's first row (predicted from the left, the very first from opaque
    /// black) and first column (predicted from above), which do not depend on the tile's mode.
    /// </summary>
    private static void EdgeResiduals(uint[] pixels, int width, Tile tile, uint[] residuals)
    {
        if (tile.Y0 == 0)
            for (int x = tile.X0; x < tile.X1; x++) residuals[x] = SubtractPixels(pixels[x], x == 0 ? OpaqueBlack : pixels[x - 1]);
        if (tile.X0 == 0)
            for (int y = Math.Max(tile.Y0, 1); y < tile.Y1; y++)
            {
                int i = y * width;
                residuals[i] = SubtractPixels(pixels[i], pixels[i - width]);
            }
    }

    // The estimated cost in 1/16 bits of each residual byte value, from the magnitude of its signed value.
    private static readonly int[] ResidualCost = CreateResidualCost();

    private static int[] CreateResidualCost()
    {
        var cost = new int[256];
        for (int v = 1; v < 256; v++)
        {
            int magnitude = v < 128 ? v : 256 - v;
            cost[v] = (int)(16 * (1 + Math.Log2(magnitude)) + 0.5);
        }
        return cost;
    }

    /// <summary>
    /// Picks the mode, of those to try, that leaves the cheapest residuals in a tile, and writes the tile's residuals.
    /// <paramref name="scratch"/> holds two tiles' residuals.
    /// </summary>
    private static PredictorMode PredictTile(uint[] pixels, int width, Tile tile, PredictorMode[] modes, uint[] residuals, Span<uint> scratch)
    {
        EdgeResiduals(pixels, width, tile, residuals);
        // The first row and column use fixed predictors; score the rest of the tile.
        int startX = Math.Max(tile.X0, 1), startY = Math.Max(tile.Y0, 1);
        if (startX >= tile.X1 || startY >= tile.Y1) return PredictorMode.Left;
        int tileWidth = tile.X1 - startX, area = tileWidth * (tile.Y1 - startY);
        var bestResiduals = scratch[..area];
        var tried = scratch.Slice(scratch.Length / 2, area);
        var best = modes[0];
        long bestCost = long.MaxValue;
        foreach (var mode in modes)
        {
            long total = TileCost(mode, pixels, width, startX, startY, tile.X1, tile.Y1, bestCost, tried);
            if (total < bestCost)
            {
                // A mode only beats the best when it scored the whole tile, so its residuals are complete.
                bestCost = total;
                best = mode;
                var swap = bestResiduals;
                bestResiduals = tried;
                tried = swap;
                if (total == 0) break;
            }
        }
        for (int y = startY, k = 0; y < tile.Y1; y++, k += tileWidth)
            bestResiduals.Slice(k, tileWidth).CopyTo(residuals.AsSpan(y * width + startX, tileWidth));
        return best;
    }

    /// <summary>
    /// The estimated cost of a tile's residuals under one mode, which are written to <paramref name="residuals"/>. The
    /// mode is chosen once here rather than per pixel, so each loop is compiled with its predictor inlined.
    /// </summary>
    private static long TileCost(PredictorMode mode, uint[] pixels, int width, int startX, int startY, int endX, int endY, long limit, Span<uint> residuals) => mode switch
    {
        PredictorMode.Left => TileCost<LeftPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.Top => TileCost<TopPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.TopRight => TileCost<TopRightPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.TopLeft => TileCost<TopLeftPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.AverageLeftTopRightTop => TileCost<AverageLeftTopRightTopPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.AverageLeftTopLeft => TileCost<AverageLeftTopLeftPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.AverageLeftTop => TileCost<AverageLeftTopPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.AverageTopLeftTop => TileCost<AverageTopLeftTopPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.AverageTopTopRight => TileCost<AverageTopTopRightPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.AverageOfAverages => TileCost<AverageOfAveragesPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.Select => TileCost<SelectPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.ClampedAddSubtractFull => TileCost<ClampedAddSubtractFullPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        PredictorMode.ClampedAddSubtractHalf => TileCost<ClampedAddSubtractHalfPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
        _ => TileCost<BlackPredictor>(pixels, width, startX, startY, endX, endY, limit, residuals),
    };

    private static long TileCost<TPredictor>(uint[] pixels, int width, int startX, int startY, int endX, int endY, long limit, Span<uint> residuals)
        where TPredictor : struct, IPredictor
    {
        var cost = ResidualCost;
        long total = 0;
        int k = 0;
        // A mode is dropped as soon as it costs more than the best so far.
        for (int y = startY; y < endY && total < limit; y++)
        {
            int row = y * width;
            for (int x = startX; x < endX; x++)
            {
                int i = row + x;
                uint r = SubtractPixels(pixels[i], TPredictor.Predict(pixels[i - 1], pixels[i - width], pixels[i - width - 1], pixels[i - width + 1]));
                residuals[k++] = r;
                if (r != 0) total += cost[r & 255] + cost[(r >> 8) & 255] + cost[(r >> 16) & 255] + cost[r >> 24];
            }
        }
        return total;
    }

    /// <summary>One predictor of VP8L, from the left, top, top-left and top-right pixels.</summary>
    private interface IPredictor
    {
        static abstract uint Predict(uint left, uint top, uint topLeft, uint topRight);
    }

    private struct BlackPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => OpaqueBlack;
    }

    private struct LeftPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => left;
    }

    private struct TopPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => top;
    }

    private struct TopRightPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => topRight;
    }

    private struct TopLeftPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => topLeft;
    }

    private struct AverageLeftTopRightTopPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(Average2(left, topRight), top);
    }

    private struct AverageLeftTopLeftPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(left, topLeft);
    }

    private struct AverageLeftTopPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(left, top);
    }

    private struct AverageTopLeftTopPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(topLeft, top);
    }

    private struct AverageTopTopRightPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(top, topRight);
    }

    private struct AverageOfAveragesPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(Average2(left, topLeft), Average2(top, topRight));
    }

    private struct SelectPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Select(top, left, topLeft);
    }

    private struct ClampedAddSubtractFullPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => ClampedAddSubtractFull(left, top, topLeft);
    }

    private struct ClampedAddSubtractHalfPredictor : IPredictor
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => ClampedAddSubtractHalf(Average2(left, top), topLeft);
    }

    /// <summary>Each channel of <paramref name="a"/> minus that of <paramref name="b"/>, modulo 256.</summary>
    private static uint SubtractPixels(uint a, uint b)
    {
        uint alphaGreen = 0x00FF00FF + (a & 0xFF00FF00) - (b & 0xFF00FF00);
        uint redBlue = 0xFF00FF00 + (a & 0x00FF00FF) - (b & 0x00FF00FF);
        return (alphaGreen & 0xFF00FF00) | (redBlue & 0x00FF00FF);
    }

    /// <summary>The average of each channel, rounded down.</summary>
    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    /// <summary>Top or left, whichever is closer over all channels to the gradient estimate left + top - top left.</summary>
    private static uint Select(uint top, uint left, uint topLeft)
    {
        int towardsLeft = Distance(top, left, topLeft, 24) + Distance(top, left, topLeft, 16) + Distance(top, left, topLeft, 8) + Distance(top, left, topLeft, 0);
        return towardsLeft <= 0 ? top : left;
    }

    /// <summary>For one channel, |left - topLeft| - |top - topLeft|: the estimate's distance to top minus that to left.</summary>
    private static int Distance(uint top, uint left, uint topLeft, int shift)
    {
        int t = (int)(top >> shift) & 0xFF, l = (int)(left >> shift) & 0xFF, tl = (int)(topLeft >> shift) & 0xFF;
        return Math.Abs(l - tl) - Math.Abs(t - tl);
    }

    private static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static uint ClampedAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int v = (int)(a >> shift & 0xFF) + (int)(b >> shift & 0xFF) - (int)(c >> shift & 0xFF);
            result |= (uint)Clip255(v) << shift;
        }
        return result;
    }

    private static uint ClampedAddSubtractHalf(uint average, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int a = (int)(average >> shift & 0xFF), b = (int)(c >> shift & 0xFF);
            result |= (uint)Clip255(a + (a - b) / 2) << shift;
        }
        return result;
    }
}
