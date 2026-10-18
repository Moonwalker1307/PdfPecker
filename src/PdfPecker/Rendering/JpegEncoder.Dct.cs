using System.Buffers;
using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering;

internal static partial class JpegEncoder
{
    // The AAN transform leaves each output scaled by these factors of its row and column, cos(k * pi / 16) * sqrt(2)
    // for k > 0; the quantisation divisors take them out again.
    private static readonly double[] AanScale = { 1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379 };

    // Below this many block rows the transform runs on the calling thread, which is faster for small images.
    private const int ParallelBlockRows = 32;

    private const float LevelShift = 128f; // samples are centred on zero before the transform

    /// <summary>Forward DCT (the AAN float algorithm) and quantisation of every 8x8 block, stored in zigzag order.</summary>
    private static short[] Transform(byte[] plane, int width, int height, byte[] quantization)
    {
        var divisors = Divisors(quantization);
        int blocksX = width / BlockSize, blocksY = height / BlockSize;
        // Rented, so not cleared: every block writes all of its coefficients.
        var coefficients = ArrayPool<short>.Shared.Rent(blocksX * blocksY * BlockLength);
        var options = new ParallelOptions { MaxDegreeOfParallelism = blocksY > ParallelBlockRows ? Environment.ProcessorCount : 1 };
        Parallel.For(0, blocksY, options, blockRow =>
        {
            Span<float> block = stackalloc float[BlockLength];
            for (int blockColumn = 0; blockColumn < blocksX; blockColumn++)
            {
                int origin = blockRow * BlockSize * width + blockColumn * BlockSize;
                var output = coefficients.AsSpan((blockRow * blocksX + blockColumn) * BlockLength, BlockLength);
                if (LoadBlock(plane, origin, width, block)) TransformBlock(block, divisors, output);
                else QuantizeFlatBlock(plane[origin], divisors, output);
            }
        });
        return coefficients;
    }

    /// <summary>The reciprocal of each quantisation step, with the transform's scale and its factor of 8 folded in.</summary>
    private static float[] Divisors(byte[] quantization)
    {
        var divisors = new float[BlockLength];
        for (int row = 0, i = 0; row < BlockSize; row++)
            for (int column = 0; column < BlockSize; column++, i++)
                divisors[i] = (float)(1.0 / (quantization[i] * AanScale[row] * AanScale[column] * 8.0));
        return divisors;
    }

    /// <summary>Copies a block of samples centred on zero; returns false when all 64 samples are equal.</summary>
    private static bool LoadBlock(byte[] plane, int origin, int width, Span<float> block)
    {
        byte first = plane[origin];
        bool varies = false;
        for (int row = 0; row < BlockSize; row++)
        {
            var samples = plane.AsSpan(origin + row * width, BlockSize);
            var destination = block.Slice(row * BlockSize, BlockSize);
            for (int column = 0; column < BlockSize; column++)
            {
                byte v = samples[column];
                if (v != first) varies = true;
                destination[column] = v - LevelShift;
            }
        }
        return varies;
    }

    private static void TransformBlock(Span<float> block, float[] divisors, Span<short> output)
    {
        for (int row = 0; row < BlockSize; row++) Fdct8(block, row * BlockSize, 1);
        for (int column = 0; column < BlockSize; column++) Fdct8(block, column, BlockSize);
        for (int k = 0; k < BlockLength; k++)
        {
            int n = ZigZag[k];
            output[k] = (short)MathF.Round(block[n] * divisors[n]);
        }
    }

    /// <summary>A uniform block has only a DC term: 8 * (value - 128), scaled like the transform's output.</summary>
    private static void QuantizeFlatBlock(byte value, float[] divisors, Span<short> output)
    {
        output[1..].Clear();
        output[0] = (short)MathF.Round((value - LevelShift) * 64f * divisors[0]);
    }

    /// <summary>The AAN forward DCT of the eight values at <paramref name="origin"/>, <paramref name="step"/> apart, in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fdct8(Span<float> d, int origin, int step)
    {
        int o = origin;
        float d0 = d[o], d1 = d[o + step], d2 = d[o + 2 * step], d3 = d[o + 3 * step];
        float d4 = d[o + 4 * step], d5 = d[o + 5 * step], d6 = d[o + 6 * step], d7 = d[o + 7 * step];
        float tmp0 = d0 + d7, tmp7 = d0 - d7, tmp1 = d1 + d6, tmp6 = d1 - d6;
        float tmp2 = d2 + d5, tmp5 = d2 - d5, tmp3 = d3 + d4, tmp4 = d3 - d4;

        // Even part.
        float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
        d[o] = tmp10 + tmp11;
        d[o + 4 * step] = tmp10 - tmp11;
        float z1 = (tmp12 + tmp13) * 0.707106781f;
        d[o + 2 * step] = tmp13 + z1;
        d[o + 6 * step] = tmp13 - z1;

        // Odd part.
        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;
        float z5 = (tmp10 - tmp12) * 0.382683433f;
        float z2 = 0.541196100f * tmp10 + z5;
        float z4 = 1.306562965f * tmp12 + z5;
        float z3 = tmp11 * 0.707106781f;
        float z11 = tmp7 + z3, z13 = tmp7 - z3;
        d[o + 5 * step] = z13 + z2;
        d[o + 3 * step] = z13 - z2;
        d[o + step] = z11 + z4;
        d[o + 7 * step] = z11 - z4;
    }
}
