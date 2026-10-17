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
    private static uint[]? FindPalette(uint[] argb)
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

    private static byte[] EncodeWithPalette(uint[] argb, int width, int height, bool hasAlpha, uint[] palette, Settings settings)
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
        var packed = PackIndexes(argb, width, height, palette, widthBits, packedWidth);
        EndTransforms(writer);
        EncodeImage(writer, packed, packedWidth, isMainImage: true, settings);
        return writer.ToArray();
    }

    /// <summary>
    /// How many pixels share one coded pixel, as a power of two: 8 for up to two colours, 4 for up to four, 2 for up
    /// to sixteen, else 1.
    /// </summary>
    private static int PackingBits(int colors) => colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;

    /// <summary>The palette indexes in the green channel, several to a pixel when the palette is small, low bits first.</summary>
    private static uint[] PackIndexes(uint[] argb, int width, int height, uint[] palette, int widthBits, int packedWidth)
    {
        int bitsPerIndex = 8 >> widthBits, perPixel = 1 << widthBits;
        var index = new Dictionary<uint, uint>(palette.Length);
        for (int i = 0; i < palette.Length; i++) index[palette[i]] = (uint)i;
        var packed = new uint[packedWidth * height];
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
        return packed;
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

    private static byte[] EncodeWithPredictor(uint[] argb, int width, int height, bool hasAlpha, Settings settings)
    {
        var writer = StartBitstream(width, height, hasAlpha, argb.Length + 1024);
        var pixels = SubtractGreen(argb);
        WriteTransform(writer, TransformType.SubtractGreen);

        int tilesX = (width + PredictorTileSize - 1) >> PredictorBits;
        int tilesY = (height + PredictorTileSize - 1) >> PredictorBits;
        var modes = new uint[tilesX * tilesY];
        var residuals = new uint[pixels.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = tilesY > ParallelTileRows ? Environment.ProcessorCount : 1 };
        Parallel.For(0, tilesY, options, tileY =>
        {
            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                var tile = new Tile(tileX, tileY, width, height);
                var mode = BestMode(pixels, width, tile, settings.Modes);
                modes[tileY * tilesX + tileX] = OpaqueBlack | (uint)mode << 8; // the mode goes in the green channel
                Residuals(pixels, width, tile, mode, residuals);
            }
        });
        WriteTransform(writer, TransformType.Predictor);
        writer.Write(PredictorBits - MinTileBits, TileSizeBits);
        EncodeImage(writer, modes, tilesX, isMainImage: false, settings);
        EndTransforms(writer);
        EncodeImage(writer, residuals, width, isMainImage: true, settings);
        return writer.ToArray();
    }

    /// <summary>Red and blue minus green, each modulo 256.</summary>
    private static uint[] SubtractGreen(uint[] argb)
    {
        var pixels = (uint[])argb.Clone();
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i], green = (p >> 8) & 0xFF;
            uint redBlue = 0xFF00FF00 + (p & 0x00FF00FF) - (green << 16 | green); // the high bytes absorb borrows
            pixels[i] = (p & 0xFF00FF00) | (redBlue & 0x00FF00FF);
        }
        return pixels;
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

    private static void Residuals(uint[] pixels, int width, Tile tile, PredictorMode mode, uint[] residuals)
    {
        for (int y = tile.Y0; y < tile.Y1; y++)
            for (int x = tile.X0; x < tile.X1; x++)
            {
                int i = y * width + x;
                residuals[i] = SubtractPixels(pixels[i], PredictAt(pixels, i, x, y, width, mode));
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

    /// <summary>The mode, of those to try, that leaves the cheapest residuals in a tile.</summary>
    private static PredictorMode BestMode(uint[] pixels, int width, Tile tile, PredictorMode[] modes)
    {
        // The first row and column use fixed predictors; score the rest of the tile.
        int startX = Math.Max(tile.X0, 1), startY = Math.Max(tile.Y0, 1);
        if (startX >= tile.X1 || startY >= tile.Y1) return PredictorMode.Left;
        var cost = ResidualCost;
        var best = modes[0];
        long bestCost = long.MaxValue;
        foreach (var mode in modes)
        {
            long total = 0;
            // A mode is dropped as soon as it costs more than the best so far.
            for (int y = startY; y < tile.Y1 && total < bestCost; y++)
            {
                int row = y * width;
                for (int x = startX; x < tile.X1; x++)
                {
                    int i = row + x;
                    uint r = SubtractPixels(pixels[i], Predict(mode, pixels[i - 1], pixels[i - width], pixels[i - width - 1], pixels[i - width + 1]));
                    if (r != 0) total += cost[r & 255] + cost[(r >> 8) & 255] + cost[(r >> 16) & 255] + cost[r >> 24];
                }
            }
            if (total < bestCost)
            {
                bestCost = total;
                best = mode;
                if (total == 0) break;
            }
        }
        return best;
    }

    private static uint PredictAt(uint[] pixels, int i, int x, int y, int width, PredictorMode mode)
    {
        if (y == 0) return x == 0 ? OpaqueBlack : pixels[i - 1];
        if (x == 0) return pixels[i - width];
        // For the last pixel of a row, "top right" is the first pixel of the current row, as in the decoder.
        return Predict(mode, pixels[i - 1], pixels[i - width], pixels[i - width - 1], pixels[i - width + 1]);
    }

    private static uint Predict(PredictorMode mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        PredictorMode.Left => left,
        PredictorMode.Top => top,
        PredictorMode.TopRight => topRight,
        PredictorMode.TopLeft => topLeft,
        PredictorMode.AverageLeftTopRightTop => Average2(Average2(left, topRight), top),
        PredictorMode.AverageLeftTopLeft => Average2(left, topLeft),
        PredictorMode.AverageLeftTop => Average2(left, top),
        PredictorMode.AverageTopLeftTop => Average2(topLeft, top),
        PredictorMode.AverageTopTopRight => Average2(top, topRight),
        PredictorMode.AverageOfAverages => Average2(Average2(left, topLeft), Average2(top, topRight)),
        PredictorMode.Select => Select(top, left, topLeft),
        PredictorMode.ClampedAddSubtractFull => ClampedAddSubtractFull(left, top, topLeft),
        PredictorMode.ClampedAddSubtractHalf => ClampedAddSubtractHalf(Average2(left, top), topLeft),
        _ => OpaqueBlack,
    };

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
