namespace PdfPecker.Images;

internal sealed partial class Vp8Decoder
{
    // libwebp's conversion of BT.601 limited-range YUV to RGB: each term is a sample times a 16-bit coefficient,
    // keeping the top bits, which leaves six fraction bits in the sum.
    private const int YuvFractionBits = 6;
    private const int YuvMask = (256 << YuvFractionBits) - 1;
    private const int YScale = 19077;
    private const int VToRed = 26149, RedOffset = 14234;
    private const int UToGreen = 6419, VToGreen = 13320, GreenOffset = 8708;
    private const int UToBlue = 33050, BlueOffset = 17685;

    private static int MultHi(int v, int coefficient) => (v * coefficient) >> 8;

    private static int YuvClip(int v) => (v & ~YuvMask) == 0 ? v >> YuvFractionBits : v < 0 ? 0 : 255;

    private static void YuvToRgb(int y, int u, int v, Span<byte> rgb, int x)
    {
        var pixel = rgb.Slice(x * 3, 3);
        int luma = MultHi(y, YScale);
        pixel[0] = (byte)YuvClip(luma + MultHi(v, VToRed) - RedOffset);
        pixel[1] = (byte)YuvClip(luma - MultHi(u, UToGreen) - MultHi(v, VToGreen) + GreenOffset);
        pixel[2] = (byte)YuvClip(luma + MultHi(u, UToBlue) - BlueOffset);
    }

    /// <summary>Converts the picture to RGB, interpolating the half-resolution chroma as libwebp's "fancy" upsampler does.</summary>
    private byte[] ToRgb(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        int lastChromaRow = (height + 1) / 2 - 1;
        // The first row lies above every chroma row, so it uses the first one alone; then each pair of rows lies
        // between two chroma rows, and the last row of an even height again has one.
        UpsampleRows(0, -1, 0, 0, width, rgb);
        int y = 1;
        for (; y + 1 < height; y += 2) UpsampleRows(y, y + 1, (y - 1) / 2, (y + 1) / 2, width, rgb);
        if (y < height) UpsampleRows(y, -1, Math.Min(lastChromaRow, y / 2), Math.Min(lastChromaRow, y / 2), width, rgb);
        return rgb;
    }

    /// <summary>
    /// Converts rows <paramref name="topRow"/> and <paramref name="bottomRow"/> (or only the first, when the second
    /// is -1), which lie between chroma rows <paramref name="topChroma"/> and <paramref name="bottomChroma"/>: the
    /// top row nearer the first, the bottom row nearer the second. Each output sample weighs the four chroma samples
    /// around it 9:3:3:1 by distance.
    /// </summary>
    private void UpsampleRows(int topRow, int bottomRow, int topChroma, int bottomChroma, int width, byte[] rgb)
    {
        bool hasBottom = bottomRow >= 0;
        ReadOnlySpan<byte> topY = _y.Row(topRow), topU = _u.Row(topChroma), topV = _v.Row(topChroma);
        ReadOnlySpan<byte> bottomU = _u.Row(bottomChroma), bottomV = _v.Row(bottomChroma);
        ReadOnlySpan<byte> bottomY = hasBottom ? _y.Row(bottomRow) : default;
        var topRgb = rgb.AsSpan(topRow * width * 3, width * 3);
        var bottomRgb = hasBottom ? rgb.AsSpan(bottomRow * width * 3, width * 3) : default;

        // The first sample has chroma to its right only; it takes three parts of the nearer row and one of the other.
        int leftTopU = topU[0], leftTopV = topV[0], leftBottomU = bottomU[0], leftBottomV = bottomV[0];
        YuvToRgb(topY[0], (3 * leftTopU + leftBottomU + 2) >> 2, (3 * leftTopV + leftBottomV + 2) >> 2, topRgb, 0);
        if (hasBottom) YuvToRgb(bottomY[0], (3 * leftBottomU + leftTopU + 2) >> 2, (3 * leftBottomV + leftTopV + 2) >> 2, bottomRgb, 0);
        int lastPair = (width - 1) >> 1;
        for (int x = 1; x <= lastPair; x++)
        {
            int topU1 = topU[x], topV1 = topV[x], bottomU1 = bottomU[x], bottomV1 = bottomV[x];
            // The averages along the two diagonals of the four chroma samples, shared by the four output samples.
            int sumU = leftTopU + topU1 + leftBottomU + bottomU1 + 8, sumV = leftTopV + topV1 + leftBottomV + bottomV1 + 8;
            int diagonal12U = (sumU + 2 * (topU1 + leftBottomU)) >> 3, diagonal12V = (sumV + 2 * (topV1 + leftBottomV)) >> 3;
            int diagonal03U = (sumU + 2 * (leftTopU + bottomU1)) >> 3, diagonal03V = (sumV + 2 * (leftTopV + bottomV1)) >> 3;
            YuvToRgb(topY[2 * x - 1], (diagonal12U + leftTopU) >> 1, (diagonal12V + leftTopV) >> 1, topRgb, 2 * x - 1);
            YuvToRgb(topY[2 * x], (diagonal03U + topU1) >> 1, (diagonal03V + topV1) >> 1, topRgb, 2 * x);
            if (hasBottom)
            {
                YuvToRgb(bottomY[2 * x - 1], (diagonal03U + leftBottomU) >> 1, (diagonal03V + leftBottomV) >> 1, bottomRgb, 2 * x - 1);
                YuvToRgb(bottomY[2 * x], (diagonal12U + bottomU1) >> 1, (diagonal12V + bottomV1) >> 1, bottomRgb, 2 * x);
            }
            leftTopU = topU1;
            leftTopV = topV1;
            leftBottomU = bottomU1;
            leftBottomV = bottomV1;
        }
        // An even width ends with a sample that has chroma to its left only.
        if ((width & 1) == 0)
        {
            YuvToRgb(topY[width - 1], (3 * leftTopU + leftBottomU + 2) >> 2, (3 * leftTopV + leftBottomV + 2) >> 2, topRgb, width - 1);
            if (hasBottom) YuvToRgb(bottomY[width - 1], (3 * leftBottomU + leftTopU + 2) >> 2, (3 * leftBottomV + leftTopV + 2) >> 2, bottomRgb, width - 1);
        }
    }
}
