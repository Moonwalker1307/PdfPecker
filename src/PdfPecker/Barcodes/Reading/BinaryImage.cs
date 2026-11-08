namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// An image as one byte per pixel, 1 for dark and 0 for light, which the detectors and samplers read. It is made from
/// grey levels with a threshold that follows the local brightness, so a code under uneven light or on a shaded scan is
/// still split into its modules.
/// </summary>
internal sealed class BinaryImage
{
    public readonly int Width, Height;
    public readonly byte[] Bits;

    private BinaryImage(int width, int height, byte[] bits)
    {
        Width = width;
        Height = height;
        Bits = bits;
    }

    public bool this[int x, int y] => Bits[y * Width + x] != 0;

    /// <summary>
    /// The modules the detectors may still sample on this image. Each try at a grid spends its modules, so an image
    /// full of patterns that look like finders, which a damaged or hostile file can be, ends in bounded time.
    /// </summary>
    private long _sampleBudget = MaxSamples;

    private const long MaxSamples = 40_000_000;

    /// <summary>Takes <paramref name="samples"/> from the budget; false once it is spent.</summary>
    public bool Spend(long samples) => (_sampleBudget -= samples) >= 0;

    public bool BudgetSpent => _sampleBudget <= 0;

    /// <summary>Whether the pixel is dark; points outside the image are light, as a quiet zone is.</summary>
    public bool Get(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Bits[y * Width + x] != 0;

    /// <summary>The same image with dark and light swapped, for light codes printed on a dark ground.</summary>
    public BinaryImage Inverted()
    {
        var bits = new byte[Bits.Length];
        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)(Bits[i] ^ 1);
        return new BinaryImage(Width, Height, bits);
    }

    private const int BlockSize = 8, MinDynamicRange = 24;

    /// <summary>
    /// Thresholds each 8 x 8 block against the average of the 5 x 5 blocks around it. A block with almost no contrast
    /// takes its neighbours' level, so the inside of a wide bar or a large module is not mistaken for background.
    /// Images too small for that use one threshold for all pixels.
    /// </summary>
    public static BinaryImage From(GrayImage gray)
    {
        int width = gray.Width, height = gray.Height;
        var bits = new byte[width * height];
        if (width < BlockSize * 5 || height < BlockSize * 5)
        {
            int threshold = OtsuThreshold(gray.Pixels);
            for (int i = 0; i < bits.Length; i++) bits[i] = gray.Pixels[i] <= threshold ? (byte)1 : (byte)0;
            return new BinaryImage(width, height, bits);
        }

        int blocksX = (width + BlockSize - 1) / BlockSize, blocksY = (height + BlockSize - 1) / BlockSize;
        var levels = BlackPoints(gray.Pixels, width, height, blocksX, blocksY);
        var pixels = gray.Pixels;
        for (int by = 0; by < blocksY; by++)
        {
            int top = Math.Min(by * BlockSize, height - BlockSize);
            int cy = Math.Clamp(by, 2, blocksY - 3);
            for (int bx = 0; bx < blocksX; bx++)
            {
                int left = Math.Min(bx * BlockSize, width - BlockSize);
                int cx = Math.Clamp(bx, 2, blocksX - 3);
                int sum = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int row = (cy + dy) * blocksX;
                    for (int dx = -2; dx <= 2; dx++) sum += levels[row + cx + dx];
                }
                int average = sum / 25;
                for (int y = 0; y < BlockSize; y++)
                {
                    int offset = (top + y) * width + left;
                    for (int x = 0; x < BlockSize; x++)
                        bits[offset + x] = pixels[offset + x] <= average ? (byte)1 : (byte)0;
                }
            }
        }
        return new BinaryImage(width, height, bits);
    }

    private static int[] BlackPoints(byte[] pixels, int width, int height, int blocksX, int blocksY)
    {
        var levels = new int[blocksX * blocksY];
        for (int by = 0; by < blocksY; by++)
        {
            int top = Math.Min(by * BlockSize, height - BlockSize);
            for (int bx = 0; bx < blocksX; bx++)
            {
                int left = Math.Min(bx * BlockSize, width - BlockSize);
                int sum = 0, min = 255, max = 0;
                for (int y = 0; y < BlockSize; y++)
                {
                    int offset = (top + y) * width + left;
                    for (int x = 0; x < BlockSize; x++)
                    {
                        int p = pixels[offset + x];
                        sum += p;
                        if (p < min) min = p;
                        if (p > max) max = p;
                    }
                }
                int average = sum >> 6;
                if (max - min <= MinDynamicRange)
                {
                    // A flat block is assumed to be background, slightly lighter than its darkest pixel, unless the
                    // blocks already done next to it say it lies inside something dark.
                    average = min / 2;
                    if (by > 0 && bx > 0)
                    {
                        int neighbours = (levels[(by - 1) * blocksX + bx] + 2 * levels[by * blocksX + bx - 1] + levels[(by - 1) * blocksX + bx - 1]) / 4;
                        if (min < neighbours) average = neighbours;
                    }
                }
                levels[by * blocksX + bx] = average;
            }
        }
        return levels;
    }

    /// <summary>The grey level that best splits the pixels into two groups (Otsu's method).</summary>
    internal static int OtsuThreshold(ReadOnlySpan<byte> pixels)
    {
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        foreach (var p in pixels) histogram[p]++;
        long total = pixels.Length, sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += (long)i * histogram[i];
        long sumBelow = 0, countBelow = 0;
        double best = -1;
        int threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            countBelow += histogram[t];
            if (countBelow == 0) continue;
            long countAbove = total - countBelow;
            if (countAbove == 0) break;
            sumBelow += (long)t * histogram[t];
            double meanBelow = (double)sumBelow / countBelow, meanAbove = (double)(sumAll - sumBelow) / countAbove;
            double between = (double)countBelow * countAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }
}

/// <summary>An image as grey levels, one byte per pixel, 0 for black.</summary>
internal sealed class GrayImage
{
    public readonly int Width, Height;
    public readonly byte[] Pixels;

    public GrayImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Grey levels of premultiplied RGBA pixels (red in the low byte) as drawn on white, so a code on a transparent
    /// background reads as it shows on a page.
    /// </summary>
    public static GrayImage FromPremultiplied(uint[] pixels, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0; i < gray.Length; i++)
        {
            uint p = pixels[i];
            int a = (int)(p >> 24), white = 255 - a;
            int r = (int)(p & 255) + white, g = (int)((p >> 8) & 255) + white, b = (int)((p >> 16) & 255) + white;
            gray[i] = (byte)Math.Min(255, (r * 306 + g * 601 + b * 117) >> 10);
        }
        return new GrayImage(width, height, gray);
    }

    /// <summary>Grey levels of 8-bit RGB, drawn on white through <paramref name="alpha"/> when there is one.</summary>
    public static GrayImage FromRgb(byte[] rgb, byte[]? alpha, int width, int height)
    {
        var gray = new byte[width * height];
        for (int i = 0, o = 0; i < gray.Length; i++, o += 3)
        {
            int y = (rgb[o] * 306 + rgb[o + 1] * 601 + rgb[o + 2] * 117) >> 10;
            if (alpha != null)
            {
                int a = alpha[i];
                y = (y * a + 255 * (255 - a) + 127) / 255;
            }
            gray[i] = (byte)y;
        }
        return new GrayImage(width, height, gray);
    }

    /// <summary>A copy shrunk by a whole factor, each pixel the average of the block it covers.</summary>
    public GrayImage Shrink(int factor)
    {
        int w = Width / factor, h = Height / factor;
        var result = new byte[w * h];
        int area = factor * factor;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int offset = (y * factor + dy) * Width + x * factor;
                    for (int dx = 0; dx < factor; dx++) sum += Pixels[offset + dx];
                }
                result[y * w + x] = (byte)(sum / area);
            }
        }
        return new GrayImage(w, h, result);
    }
}
