using System.Buffers.Binary;

namespace PdfPecker.Rendering;

/// <summary>
/// Writes lossless WebP files (a RIFF container holding one VP8L bitstream). Images with up to 256 colours use the
/// colour-indexing transform (packing 2, 4 or 8 pixels into one when the palette is small); others use the
/// subtract-green and predictor transforms. The coded pixels go through LZ77 back-references, a colour cache and
/// length-limited canonical prefix codes. Alpha is kept exactly.
/// </summary>
internal static partial class WebPEncoder
{
    /// <summary>The largest width or height a VP8L bitstream can describe.</summary>
    public const int MaxDimension = 16384;

    // The RIFF container: "RIFF", the size of the rest, "WEBP", then the "VP8L" chunk with its size, padded to an even
    // length. The RIFF size counts "WEBP" and the chunk with its header.
    private const int ContainerHeaderLength = 20;
    private const int RiffSizeOffset = 4;
    private const int FormOffset = 8;
    private const int ChunkSizeOffset = 16;
    private const int RiffSizeBase = 12;

    // The bitstream starts with a signature byte, then the width and height minus one in 14 bits each, a hint that
    // alpha is used, and a version number of zero in 3 bits.
    private const byte Signature = 0x2F;
    private const int SizeBits = 14;
    private const int VersionBits = 3;

    // A transform is announced by a 1 bit and a 2-bit type; a 0 bit ends the list.
    private enum TransformType : uint
    {
        Predictor = 0,
        SubtractGreen = 2,
        ColorIndexing = 3,
    }

    private const int TransformTypeBits = 2;

    /// <summary>Encodes premultiplied 0xAABBGGRR pixels; <paramref name="effort"/> (1..100) trades speed for size.</summary>
    public static byte[] Encode(uint[] pixels, int width, int height, int effort)
    {
        if (effort is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(effort), "Quality must be between 1 and 100.");
        if (width > MaxDimension || height > MaxDimension)
            throw new NotSupportedException($"WebP images are limited to {MaxDimension} pixels in each dimension; lower the Dpi or MaxDimension.");
        var argb = ToStraightArgb(pixels, out bool hasAlpha);
        var settings = Settings.For(effort);

        var palette = FindPalette(argb);
        byte[] best = palette != null
            ? EncodeWithPalette(argb, width, height, hasAlpha, palette, settings)
            : EncodeWithPredictor(argb, width, height, hasAlpha, settings);
        if (palette != null && settings.TryBoth)
        {
            var other = EncodeWithPredictor(argb, width, height, hasAlpha, settings);
            if (other.Length < best.Length) best = other;
        }
        return WrapInContainer(best);
    }

    private static byte[] WrapInContainer(byte[] bitstream)
    {
        int padding = bitstream.Length & 1;
        var file = new byte[ContainerHeaderLength + bitstream.Length + padding];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(RiffSizeOffset), RiffSizeBase + bitstream.Length + padding);
        "WEBPVP8L"u8.CopyTo(file.AsSpan(FormOffset));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(ChunkSizeOffset), bitstream.Length);
        bitstream.CopyTo(file, ContainerHeaderLength);
        return file;
    }

    /// <summary>Straight-alpha 0xAARRGGBB, rounding like the PNG encoder so both formats carry the same pixels.</summary>
    private static uint[] ToStraightArgb(uint[] pixels, out bool hasAlpha)
    {
        var argb = new uint[pixels.Length];
        hasAlpha = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint c = pixels[i], a = c >> 24;
            uint r = c & 255, g = (c >> 8) & 255, b = (c >> 16) & 255;
            if (a != 255)
            {
                hasAlpha = true;
                if (a == 0) continue; // fully transparent pixels are all zero, whatever their colour
                r = Unpremultiply(r, a);
                g = Unpremultiply(g, a);
                b = Unpremultiply(b, a);
            }
            argb[i] = a << 24 | r << 16 | g << 8 | b;
        }
        return argb;
    }

    // A channel above its alpha is not valid premultiplied colour; it is clamped rather than wrapped.
    private static uint Unpremultiply(uint channel, uint alpha) => Math.Min(255, (channel * 255 + alpha / 2) / alpha);

    /// <summary>A bit writer holding the bitstream header.</summary>
    private static BitWriter StartBitstream(int width, int height, bool hasAlpha, int capacity)
    {
        var writer = new BitWriter(capacity);
        writer.Write(Signature, 8);
        writer.Write((uint)width - 1, SizeBits);
        writer.Write((uint)height - 1, SizeBits);
        writer.Write(hasAlpha ? 1u : 0u, 1);
        writer.Write(0, VersionBits);
        return writer;
    }

    private static void WriteTransform(BitWriter writer, TransformType type)
    {
        writer.Write(1, 1);
        writer.Write((uint)type, TransformTypeBits);
    }

    private static void EndTransforms(BitWriter writer) => writer.Write(0, 1);

    /// <summary>How hard the encoder searches, from the 1..100 effort.</summary>
    private sealed class Settings
    {
        // Predictor modes in the order tried; the first ones win most tiles of rendered pages.
        private static readonly PredictorMode[] FewModes =
        {
            PredictorMode.Left, PredictorMode.Top, PredictorMode.Select, PredictorMode.ClampedAddSubtractFull,
        };

        private static readonly PredictorMode[] SomeModes =
        {
            PredictorMode.Left, PredictorMode.Top, PredictorMode.Select, PredictorMode.ClampedAddSubtractFull,
            PredictorMode.ClampedAddSubtractHalf, PredictorMode.AverageLeftTop, PredictorMode.AverageLeftTopRightTop,
            PredictorMode.AverageOfAverages,
        };

        private static readonly PredictorMode[] AllModes =
        {
            PredictorMode.Left, PredictorMode.Top, PredictorMode.Select, PredictorMode.ClampedAddSubtractFull,
            PredictorMode.ClampedAddSubtractHalf, PredictorMode.AverageLeftTop, PredictorMode.AverageLeftTopRightTop,
            PredictorMode.AverageOfAverages, PredictorMode.AverageLeftTopLeft, PredictorMode.AverageTopLeftTop,
            PredictorMode.AverageTopTopRight, PredictorMode.TopRight, PredictorMode.TopLeft, PredictorMode.Black,
        };

        // Colour cache sizes in bits, 0 for none, from which the cheapest is picked for each image.
        private static readonly int[] FewCacheBits = { 0, 10 };
        private static readonly int[] SomeCacheBits = { 0, 4, 7, 10 };
        private static readonly int[] AllCacheBits = { 0, 2, 4, 6, 8, 10 };

        private Settings(int chainLength, int niceLength, PredictorMode[] modes, int[] cacheBits, bool tryBoth)
        {
            ChainLength = chainLength;
            NiceLength = niceLength;
            Modes = modes;
            CacheBits = cacheBits;
            TryBoth = tryBoth;
        }

        /// <summary>How many earlier positions with the same hash the LZ77 search tries.</summary>
        public int ChainLength { get; }

        /// <summary>A match at least this long ends the search.</summary>
        public int NiceLength { get; }

        public PredictorMode[] Modes { get; }

        public int[] CacheBits { get; }

        /// <summary>Whether an image with a palette is also tried with the predictor, keeping the smaller.</summary>
        public bool TryBoth { get; }

        public static Settings For(int effort)
        {
            var (modes, cacheBits) = effort < 30 ? (FewModes, FewCacheBits) : effort < 90 ? (SomeModes, SomeCacheBits) : (AllModes, AllCacheBits);
            return new Settings(1 + effort / 3, effort < 50 ? 64 : 256, modes, cacheBits, tryBoth: effort >= 90);
        }
    }
}
