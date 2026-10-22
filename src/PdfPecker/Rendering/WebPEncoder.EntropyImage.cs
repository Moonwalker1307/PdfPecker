using System.Numerics;

namespace PdfPecker.Rendering;

internal static partial class WebPEncoder
{
    // The alphabets of a prefix code group: the first code holds the green values, then the back-reference lengths,
    // then the colour cache indexes; the last code holds the back-reference distances.
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int MaxCacheBits = 10;
    private const int CacheBitsBits = 4;

    /// <summary>
    /// Writes one entropy-coded image: colour cache size, (for the main image) no meta prefix codes, five prefix codes
    /// and the coded pixels.
    /// </summary>
    private static void EncodeImage(BitWriter writer, uint[] pixels, int width, bool isMainImage, Settings settings)
    {
        var references = BackwardReferences(pixels, width, settings);
        var histograms = new Histograms();
        var cache = new ColorCache();
        int cacheBits = ChooseCacheBits(pixels, references, settings, histograms, cache);
        if (cacheBits > 0)
        {
            writer.Write(1, 1);
            writer.Write((uint)cacheBits, CacheBitsBits);
        }
        else writer.Write(0, 1);
        if (isMainImage) writer.Write(0, 1); // a single group of prefix codes

        // The cache key of each literal that is found in the colour cache, else -1.
        var cacheHits = new int[references.Count];
        histograms.Count(pixels, references, cacheBits, cache, cacheHits);
        var green = WriteCode(writer, histograms.Green.AsSpan(0, histograms.GreenAlphabet));
        var red = WriteCode(writer, histograms.Red);
        var blue = WriteCode(writer, histograms.Blue);
        var alpha = WriteCode(writer, histograms.Alpha);
        var distance = WriteCode(writer, histograms.Distance);

        int position = 0;
        for (int t = 0; t < references.Count; t++)
        {
            var reference = references[t];
            if (reference.IsLiteral)
            {
                uint p = pixels[position++];
                if (cacheHits[t] >= 0)
                {
                    green.Put(writer, NumLiteralCodes + NumLengthCodes + cacheHits[t]);
                    continue;
                }
                green.Put(writer, (int)(p >> 8) & 255);
                red.Put(writer, (int)(p >> 16) & 255);
                blue.Put(writer, (int)p & 255);
                alpha.Put(writer, (int)(p >> 24));
            }
            else
            {
                var length = PrefixEncode(reference.Length);
                green.Put(writer, NumLiteralCodes + length.Symbol);
                writer.Write((uint)length.ExtraValue, length.ExtraBits);
                var distanceCode = PrefixEncode(reference.DistanceCode);
                distance.Put(writer, distanceCode.Symbol);
                writer.Write((uint)distanceCode.ExtraValue, distanceCode.ExtraBits);
                position += reference.Length;
            }
        }
    }

    /// <summary>A length or distance as a prefix symbol and the extra bits that follow it.</summary>
    private readonly record struct PrefixCoded(int Symbol, int ExtraBits, int ExtraValue);

    /// <summary>The prefix code of a length or distance value (1 or more) with its extra bits.</summary>
    private static PrefixCoded PrefixEncode(int value)
    {
        int d = value - 1;
        if (d < 4) return new PrefixCoded(d, 0, 0);
        int high = 31 - BitOperations.LeadingZeroCount((uint)d);
        int second = (d >> (high - 1)) & 1;
        int extraBits = high - 1;
        return new PrefixCoded(2 * high + second, extraBits, d & ((1 << extraBits) - 1));
    }

    /// <summary>
    /// The colour cache of the decoder: recent colours by a hash of their value, so that a literal seen before can be
    /// sent as its key.
    /// </summary>
    private sealed class ColorCache
    {
        private readonly uint[] colors = new uint[1 << MaxCacheBits];
        private readonly bool[] valid = new bool[1 << MaxCacheBits];
        private int shift;

        /// <summary>Empties the cache and sets its size to 2^<paramref name="bits"/> entries.</summary>
        public void Reset(int bits)
        {
            shift = 32 - bits;
            Array.Clear(valid);
        }

        public int Key(uint argb) => (int)((ColorHashMultiplier * argb) >> shift);

        public bool Contains(int key, uint argb) => valid[key] && colors[key] == argb;

        public void Insert(int key, uint argb)
        {
            colors[key] = argb;
            valid[key] = true;
        }
    }

    /// <summary>The symbol counts of the five codes of an image for one colour cache size.</summary>
    private sealed class Histograms
    {
        public readonly int[] Green = new int[NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits)];
        public readonly int[] Red = new int[256];
        public readonly int[] Blue = new int[256];
        public readonly int[] Alpha = new int[256];
        public readonly int[] Distance = new int[NumDistanceCodes];

        /// <summary>The symbols of the green code for the cache size counted last.</summary>
        public int GreenAlphabet { get; private set; }

        /// <summary>
        /// Counts the symbols the references code with a cache of 2^<paramref name="cacheBits"/> entries (none for 0),
        /// recording which literals hit the cache when <paramref name="cacheHits"/> is given.
        /// </summary>
        public void Count(uint[] pixels, References references, int cacheBits, ColorCache cache, int[]? cacheHits = null)
        {
            Array.Clear(Green);
            Array.Clear(Red);
            Array.Clear(Blue);
            Array.Clear(Alpha);
            Array.Clear(Distance);
            bool useCache = cacheBits > 0;
            GreenAlphabet = NumLiteralCodes + NumLengthCodes + (useCache ? 1 << cacheBits : 0);
            if (useCache) cache.Reset(cacheBits);
            int position = 0;
            for (int t = 0; t < references.Count; t++)
            {
                var reference = references[t];
                if (reference.IsLiteral)
                {
                    uint p = pixels[position++];
                    if (cacheHits != null) cacheHits[t] = -1;
                    if (useCache)
                    {
                        int key = cache.Key(p);
                        if (cache.Contains(key, p))
                        {
                            if (cacheHits != null) cacheHits[t] = key;
                            Green[NumLiteralCodes + NumLengthCodes + key]++;
                            continue;
                        }
                        cache.Insert(key, p);
                    }
                    Green[(p >> 8) & 255]++;
                    Red[(p >> 16) & 255]++;
                    Blue[p & 255]++;
                    Alpha[p >> 24]++;
                }
                else
                {
                    Green[NumLiteralCodes + PrefixEncode(reference.Length).Symbol]++;
                    Distance[PrefixEncode(reference.DistanceCode).Symbol]++;
                    // The decoder adds every copied pixel to the cache. A pixel equal to the one before it is already
                    // there, which skips most of a run.
                    if (useCache)
                    {
                        var copied = pixels.AsSpan(position, reference.Length);
                        uint last = copied[0];
                        cache.Insert(cache.Key(last), last);
                        foreach (uint c in copied)
                            if (c != last)
                            {
                                last = c;
                                cache.Insert(cache.Key(c), c);
                            }
                    }
                    position += reference.Length;
                }
            }
        }

        /// <summary>Estimated bits of the literals and lengths, plus a rough charge for each used symbol's code length.</summary>
        public double LiteralCost()
            => Entropy(Green.AsSpan(0, GreenAlphabet)) + Entropy(Red) + Entropy(Blue) + Entropy(Alpha);

        private static double Entropy(ReadOnlySpan<int> histogram)
        {
            long total = 0;
            int used = 0;
            foreach (int count in histogram)
                if (count > 0)
                {
                    total += count;
                    used++;
                }
            if (total == 0) return 0;
            double bits = 0;
            foreach (int count in histogram)
                if (count > 0) bits += count * Math.Log2((double)total / count);
            return bits + used * 4;
        }
    }

    /// <summary>Picks the colour cache size with the smallest estimated literal cost.</summary>
    private static int ChooseCacheBits(uint[] pixels, References references, Settings settings, Histograms histograms, ColorCache cache)
    {
        int best = 0;
        double bestCost = double.MaxValue;
        foreach (int bits in settings.CacheBits)
        {
            histograms.Count(pixels, references, bits, cache);
            double cost = histograms.LiteralCost();
            if (cost < bestCost)
            {
                bestCost = cost;
                best = bits;
            }
        }
        return best;
    }
}
