namespace PdfPecker.Images;

internal sealed partial class Vp8Decoder
{
    // Intra prediction modes, numbered as libwebp numbers them so that a 4x4 mode indexes BModesProba. A 16x16 or
    // chroma mode has the number of the 4x4 mode it resembles, which is the context it gives the 4x4 blocks next to it.
    private const int DcMode = 0, TrueMotionMode = 1, VerticalMode = 2, HorizontalMode = 3;
    private const int DownRightMode = 4, VerticalRightMode = 5, DownLeftMode = 6, VerticalLeftMode = 7;
    private const int HorizontalDownMode = 8, HorizontalUpMode = 9;
    private const int NumSubblockModes = 10;

    /// <summary>The prediction of a 16x16 luma or 8x8 chroma block, with the DC variants for the picture's edges.</summary>
    private enum BlockPredictor
    {
        Dc = DcMode,
        TrueMotion = TrueMotionMode,
        Vertical = VerticalMode,
        Horizontal = HorizontalMode,
        DcWithoutTop,
        DcWithoutLeft,
        DcWithoutEdges,
    }

    // The work buffer holds the macroblock being reconstructed with the samples around it, in rows of WorkStride: the
    // luma block at row 1, column 8, with the row above it in row 0 (from the top-left sample in column 7 to four
    // samples past its top-right corner) and the column to its left in column 7; below it the U and V blocks at
    // columns 8 and 24 of row 18, each with its own row above and column to the left.
    private const int WorkStride = 32;
    private const int YOffset = WorkStride + 8;
    private const int UOffset = YOffset + 16 * WorkStride + WorkStride;
    private const int VOffset = UOffset + 16;
    private const int TopRightOffset = YOffset - WorkStride + 16;
    private readonly byte[] _work = new byte[WorkStride * 17 + WorkStride * 9];

    // Outside the picture, the row above is 127 and the column to the left is 129.
    private const byte MissingTop = 127, MissingLeft = 129;

    /// <summary>Predicts the macroblock from its neighbours, adds the residuals and stores it in the picture.</summary>
    private void Reconstruct(int mbX, int mbY, bool is4x4, int chromaMode)
    {
        LoadEdges(mbX, mbY, is4x4);
        Span<byte> work = _work;
        if (is4x4)
        {
            // Each 4x4 block is predicted from the blocks reconstructed before it.
            for (int n = 0; n < 16; n++)
            {
                int block = YOffset + (n >> 2) * 4 * WorkStride + (n & 3) * 4;
                PredictSubblock(work, _modes[n], block);
                TransformAdd(n * CoefficientsPerBlock, block);
            }
        }
        else
        {
            PredictBlock(work, Predictor(mbX, mbY, _modes[0]), YOffset, 16);
            for (int n = 0; n < 16; n++) TransformAdd(n * CoefficientsPerBlock, YOffset + (n >> 2) * 4 * WorkStride + (n & 3) * 4);
        }
        var chroma = Predictor(mbX, mbY, chromaMode);
        PredictBlock(work, chroma, UOffset, 8);
        PredictBlock(work, chroma, VOffset, 8);
        for (int n = 0; n < 4; n++)
        {
            int block = (n >> 1) * 4 * WorkStride + (n & 1) * 4;
            TransformAdd(UCoefficients + n * CoefficientsPerBlock, UOffset + block);
            TransformAdd(VCoefficients + n * CoefficientsPerBlock, VOffset + block);
        }

        StoreBlock(_y, YOffset, mbX * 16, mbY * 16, 16);
        StoreBlock(_u, UOffset, mbX * 8, mbY * 8, 8);
        StoreBlock(_v, VOffset, mbX * 8, mbY * 8, 8);
    }

    /// <summary>Copies the samples above and to the left of the macroblock, and for 4x4 prediction those above and to the right.</summary>
    private void LoadEdges(int mbX, int mbY, bool is4x4)
    {
        Span<byte> work = _work;
        LoadLeftEdge(work, _y, YOffset, mbX * 16, mbY * 16, 16);
        LoadLeftEdge(work, _u, UOffset, mbX * 8, mbY * 8, 8);
        LoadLeftEdge(work, _v, VOffset, mbX * 8, mbY * 8, 8);
        if (mbY == 0)
        {
            // The luma row runs on over the top-right samples.
            work.Slice(YOffset - WorkStride - 1, 1 + 16 + 4).Fill(MissingTop);
            work.Slice(UOffset - WorkStride - 1, 1 + 8).Fill(MissingTop);
            work.Slice(VOffset - WorkStride - 1, 1 + 8).Fill(MissingTop);
        }
        else
        {
            LoadTopEdge(work, _y, YOffset, mbX * 16, mbY * 16, 16);
            LoadTopEdge(work, _u, UOffset, mbX * 8, mbY * 8, 8);
            LoadTopEdge(work, _v, VOffset, mbX * 8, mbY * 8, 8);
        }
        if (!is4x4) return;

        var topRight = work.Slice(TopRightOffset, 4);
        if (mbY > 0)
        {
            // The last macroblock of a row repeats its own last sample above.
            var above = _y.Row(mbY * 16 - 1);
            if (mbX >= _mbWidth - 1) topRight.Fill(above[mbX * 16 + 15]);
            else above.Slice(mbX * 16 + 16, 4).CopyTo(topRight);
        }
        // The blocks along the right edge use the macroblock's top-right samples as their own.
        for (int k = 1; k <= 3; k++) topRight.CopyTo(work.Slice(TopRightOffset + 4 * k * WorkStride, 4));
    }

    private static void LoadLeftEdge(Span<byte> work, in Plane plane, int offset, int x, int y, int size)
    {
        for (int j = 0; j < size; j++) work[offset + j * WorkStride - 1] = x > 0 ? plane.Samples[(y + j) * plane.Stride + x - 1] : MissingLeft;
    }

    private static void LoadTopEdge(Span<byte> work, in Plane plane, int offset, int x, int y, int size)
    {
        var above = plane.Row(y - 1);
        work[offset - WorkStride - 1] = x > 0 ? above[x - 1] : MissingLeft;
        above.Slice(x, size).CopyTo(work.Slice(offset - WorkStride, size));
    }

    private void StoreBlock(in Plane plane, int offset, int x, int y, int size)
    {
        for (int j = 0; j < size; j++) _work.AsSpan(offset + j * WorkStride, size).CopyTo(plane.Row(y + j).Slice(x, size));
    }

    /// <summary>The predictor of a 16x16 or chroma mode: DC prediction averages only the edges inside the picture.</summary>
    private static BlockPredictor Predictor(int mbX, int mbY, int mode)
    {
        if (mode != DcMode) return (BlockPredictor)mode;
        if (mbX == 0) return mbY == 0 ? BlockPredictor.DcWithoutEdges : BlockPredictor.DcWithoutLeft;
        return mbY == 0 ? BlockPredictor.DcWithoutTop : BlockPredictor.Dc;
    }

    /// <summary>Predicts a 16x16 luma or 8x8 chroma block at <paramref name="block"/> in the work buffer.</summary>
    private static void PredictBlock(Span<byte> work, BlockPredictor predictor, int block, int size)
    {
        int sizeBits = size == 16 ? 4 : 3;
        var top = work.Slice(block - WorkStride, size);
        switch (predictor)
        {
            case BlockPredictor.TrueMotion:
                TrueMotion(work, block, size);
                break;
            case BlockPredictor.Vertical:
                for (int y = 0; y < size; y++) top.CopyTo(work.Slice(block + y * WorkStride, size));
                break;
            case BlockPredictor.Horizontal:
                for (int y = 0; y < size; y++) work.Slice(block + y * WorkStride, size).Fill(work[block + y * WorkStride - 1]);
                break;
            case BlockPredictor.Dc:
                Fill(work, block, size, (SumTop(work, block, size) + SumLeft(work, block, size) + size) >> (sizeBits + 1));
                break;
            case BlockPredictor.DcWithoutTop:
                Fill(work, block, size, (SumLeft(work, block, size) + size / 2) >> sizeBits);
                break;
            case BlockPredictor.DcWithoutLeft:
                Fill(work, block, size, (SumTop(work, block, size) + size / 2) >> sizeBits);
                break;
            default:
                Fill(work, block, size, 0x80);
                break;
        }
    }

    private static int SumTop(ReadOnlySpan<byte> work, int block, int size)
    {
        int sum = 0;
        foreach (byte sample in work.Slice(block - WorkStride, size)) sum += sample;
        return sum;
    }

    private static int SumLeft(ReadOnlySpan<byte> work, int block, int size)
    {
        int sum = 0;
        for (int y = 0; y < size; y++) sum += work[block + y * WorkStride - 1];
        return sum;
    }

    private static void Fill(Span<byte> work, int block, int size, int value)
    {
        for (int y = 0; y < size; y++) work.Slice(block + y * WorkStride, size).Fill((byte)value);
    }

    /// <summary>Predicts each sample as the one above plus the one to the left minus the corner between them.</summary>
    private static void TrueMotion(Span<byte> work, int block, int size)
    {
        int corner = work[block - WorkStride - 1];
        var top = work.Slice(block - WorkStride, size);
        for (int y = 0; y < size; y++)
        {
            var row = work.Slice(block + y * WorkStride, size);
            int left = work[block + y * WorkStride - 1];
            for (int x = 0; x < row.Length; x++) row[x] = Clip8(top[x] + left - corner);
        }
    }

    private static byte Clip8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static byte Average3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

    private static byte Average2(int a, int b) => (byte)((a + b + 1) >> 1);

    // ------------------------------------------------------------------ 4x4 prediction

    /// <summary>
    /// Predicts a 4x4 luma block from the four samples to its left (top to bottom), the corner above them and the
    /// eight samples above it and to its right. The directional modes follow RFC 6386, 12.3.
    /// </summary>
    private static void PredictSubblock(Span<byte> work, int mode, int block)
    {
        var b = new Subblock(work, block);
        switch (mode)
        {
            case DcMode:
            {
                int sum = 4;
                for (int i = 0; i < 4; i++) sum += b.Top(i) + b.Left(i);
                b.Fill((byte)(sum >> 3));
                break;
            }
            case TrueMotionMode:
                TrueMotion(work, block, 4);
                break;
            case VerticalMode:
                PredictVertical(b);
                break;
            case HorizontalMode:
                PredictHorizontal(b);
                break;
            case DownRightMode:
                PredictDownRight(b);
                break;
            case DownLeftMode:
                PredictDownLeft(b);
                break;
            case VerticalRightMode:
                PredictVerticalRight(b);
                break;
            case VerticalLeftMode:
                PredictVerticalLeft(b);
                break;
            case HorizontalUpMode:
                PredictHorizontalUp(b);
                break;
            default:
                PredictHorizontalDown(b);
                break;
        }
    }

    /// <summary>Each column takes the sample above it, smoothed with its neighbours.</summary>
    private static void PredictVertical(Subblock b)
    {
        byte v0 = Average3(b.Corner, b.Top(0), b.Top(1)), v1 = Average3(b.Top(0), b.Top(1), b.Top(2));
        byte v2 = Average3(b.Top(1), b.Top(2), b.Top(3)), v3 = Average3(b.Top(2), b.Top(3), b.Top(4));
        for (int y = 0; y < 4; y++)
        {
            b[0, y] = v0;
            b[1, y] = v1;
            b[2, y] = v2;
            b[3, y] = v3;
        }
    }

    /// <summary>Each row takes the sample to its left, smoothed with its neighbours.</summary>
    private static void PredictHorizontal(Subblock b)
    {
        int l0 = b.Left(0), l1 = b.Left(1), l2 = b.Left(2), l3 = b.Left(3);
        b.FillRow(0, Average3(b.Corner, l0, l1));
        b.FillRow(1, Average3(l0, l1, l2));
        b.FillRow(2, Average3(l1, l2, l3));
        b.FillRow(3, Average3(l2, l3, l3));
    }

    /// <summary>Diagonals running down and to the right, from the left column, the corner and the row above.</summary>
    private static void PredictDownRight(Subblock b)
    {
        int l0 = b.Left(0), l1 = b.Left(1), l2 = b.Left(2), l3 = b.Left(3), c = b.Corner;
        int t0 = b.Top(0), t1 = b.Top(1), t2 = b.Top(2), t3 = b.Top(3);
        b[0, 3] = Average3(l1, l2, l3);
        b[1, 3] = b[0, 2] = Average3(l0, l1, l2);
        b[2, 3] = b[1, 2] = b[0, 1] = Average3(c, l0, l1);
        b[3, 3] = b[2, 2] = b[1, 1] = b[0, 0] = Average3(t0, c, l0);
        b[3, 2] = b[2, 1] = b[1, 0] = Average3(t1, t0, c);
        b[3, 1] = b[2, 0] = Average3(t2, t1, t0);
        b[3, 0] = Average3(t3, t2, t1);
    }

    /// <summary>Diagonals running down and to the left, from the row above and the samples to its right.</summary>
    private static void PredictDownLeft(Subblock b)
    {
        int t0 = b.Top(0), t1 = b.Top(1), t2 = b.Top(2), t3 = b.Top(3), t4 = b.Top(4), t5 = b.Top(5), t6 = b.Top(6), t7 = b.Top(7);
        b[0, 0] = Average3(t0, t1, t2);
        b[1, 0] = b[0, 1] = Average3(t1, t2, t3);
        b[2, 0] = b[1, 1] = b[0, 2] = Average3(t2, t3, t4);
        b[3, 0] = b[2, 1] = b[1, 2] = b[0, 3] = Average3(t3, t4, t5);
        b[3, 1] = b[2, 2] = b[1, 3] = Average3(t4, t5, t6);
        b[3, 2] = b[2, 3] = Average3(t5, t6, t7);
        b[3, 3] = Average3(t6, t7, t7);
    }

    /// <summary>Steep diagonals running down and to the right.</summary>
    private static void PredictVerticalRight(Subblock b)
    {
        int l0 = b.Left(0), l1 = b.Left(1), l2 = b.Left(2), c = b.Corner;
        int t0 = b.Top(0), t1 = b.Top(1), t2 = b.Top(2), t3 = b.Top(3);
        b[0, 0] = b[1, 2] = Average2(c, t0);
        b[1, 0] = b[2, 2] = Average2(t0, t1);
        b[2, 0] = b[3, 2] = Average2(t1, t2);
        b[3, 0] = Average2(t2, t3);
        b[0, 3] = Average3(l2, l1, l0);
        b[0, 2] = Average3(l1, l0, c);
        b[0, 1] = b[1, 3] = Average3(l0, c, t0);
        b[1, 1] = b[2, 3] = Average3(c, t0, t1);
        b[2, 1] = b[3, 3] = Average3(t0, t1, t2);
        b[3, 1] = Average3(t1, t2, t3);
    }

    /// <summary>Steep diagonals running down and to the left.</summary>
    private static void PredictVerticalLeft(Subblock b)
    {
        int t0 = b.Top(0), t1 = b.Top(1), t2 = b.Top(2), t3 = b.Top(3), t4 = b.Top(4), t5 = b.Top(5), t6 = b.Top(6), t7 = b.Top(7);
        b[0, 0] = Average2(t0, t1);
        b[1, 0] = b[0, 2] = Average2(t1, t2);
        b[2, 0] = b[1, 2] = Average2(t2, t3);
        b[3, 0] = b[2, 2] = Average2(t3, t4);
        b[0, 1] = Average3(t0, t1, t2);
        b[1, 1] = b[0, 3] = Average3(t1, t2, t3);
        b[2, 1] = b[1, 3] = Average3(t2, t3, t4);
        b[3, 1] = b[2, 3] = Average3(t3, t4, t5);
        b[3, 2] = Average3(t4, t5, t6);
        b[3, 3] = Average3(t5, t6, t7);
    }

    /// <summary>Shallow diagonals running up and to the right, from the left column alone.</summary>
    private static void PredictHorizontalUp(Subblock b)
    {
        int l0 = b.Left(0), l1 = b.Left(1), l2 = b.Left(2), l3 = b.Left(3);
        b[0, 0] = Average2(l0, l1);
        b[2, 0] = b[0, 1] = Average2(l1, l2);
        b[2, 1] = b[0, 2] = Average2(l2, l3);
        b[1, 0] = Average3(l0, l1, l2);
        b[3, 0] = b[1, 1] = Average3(l1, l2, l3);
        b[3, 1] = b[1, 2] = Average3(l2, l3, l3);
        b[3, 2] = b[2, 2] = b[0, 3] = b[1, 3] = b[2, 3] = b[3, 3] = (byte)l3;
    }

    /// <summary>Shallow diagonals running down and to the right.</summary>
    private static void PredictHorizontalDown(Subblock b)
    {
        int l0 = b.Left(0), l1 = b.Left(1), l2 = b.Left(2), l3 = b.Left(3), c = b.Corner;
        int t0 = b.Top(0), t1 = b.Top(1), t2 = b.Top(2);
        b[0, 0] = b[2, 1] = Average2(l0, c);
        b[0, 1] = b[2, 2] = Average2(l1, l0);
        b[0, 2] = b[2, 3] = Average2(l2, l1);
        b[0, 3] = Average2(l3, l2);
        b[3, 0] = Average3(t0, t1, t2);
        b[2, 0] = Average3(c, t0, t1);
        b[1, 0] = b[3, 1] = Average3(l0, c, t0);
        b[1, 1] = b[3, 2] = Average3(l1, l0, c);
        b[1, 2] = b[3, 3] = Average3(l2, l1, l0);
        b[1, 3] = Average3(l3, l2, l1);
    }

    /// <summary>A 4x4 block in the work buffer, with the samples around it.</summary>
    private readonly ref struct Subblock(Span<byte> work, int block)
    {
        private readonly Span<byte> work = work;
        private readonly int block = block;

        public int Corner => work[block - WorkStride - 1];

        /// <summary>The samples above the block, 0 to 3, then above and to the right, 4 to 7.</summary>
        public int Top(int x) => work[block - WorkStride + x];

        public int Left(int y) => work[block + y * WorkStride - 1];

        public byte this[int x, int y]
        {
            set => work[block + y * WorkStride + x] = value;
        }

        public void FillRow(int y, byte value) => work.Slice(block + y * WorkStride, 4).Fill(value);

        public void Fill(byte value)
        {
            for (int y = 0; y < 4; y++) FillRow(y, value);
        }
    }

    // ------------------------------------------------------------------ inverse transforms

    /// <summary>Spreads the Y2 block over the DC coefficients of the 16 luma blocks: the inverse Walsh-Hadamard transform.</summary>
    private static void TransformWht(ReadOnlySpan<short> input, Span<short> coeffs)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i] + input[12 + i], a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i], a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i * 4] + 3;
            int a0 = dc + tmp[3 + i * 4], a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
            int a2 = tmp[1 + i * 4] - tmp[2 + i * 4], a3 = dc - tmp[3 + i * 4];
            // Row i of the result goes to the luma blocks of row i.
            var blocks = coeffs.Slice(i * 4 * CoefficientsPerBlock, 4 * CoefficientsPerBlock);
            blocks[0] = (short)((a0 + a1) >> 3);
            blocks[CoefficientsPerBlock] = (short)((a3 + a2) >> 3);
            blocks[2 * CoefficientsPerBlock] = (short)((a0 - a1) >> 3);
            blocks[3 * CoefficientsPerBlock] = (short)((a3 - a2) >> 3);
        }
    }

    // The inverse DCT multiplies by sqrt(2) cos(pi/8) and sqrt(2) sin(pi/8) in 16-bit fixed point; the first constant
    // holds only the fraction above one.
    private const int CosPi8Sqrt2Minus1 = 20091;
    private const int SinPi8Sqrt2 = 35468;

    private static int MulCos(int a) => ((a * CosPi8Sqrt2Minus1) >> 16) + a;

    private static int MulSin(int a) => (a * SinPi8Sqrt2) >> 16;

    /// <summary>Adds the inverse DCT of a block of coefficients to the prediction at <paramref name="block"/> in the work buffer.</summary>
    private void TransformAdd(int coefficients, int block)
    {
        ReadOnlySpan<short> c = _coeffs.AsSpan(coefficients, CoefficientsPerBlock);
        if (!c.ContainsAnyExcept((short)0)) return;
        Span<int> tmp = stackalloc int[16];
        // The columns first, then the rows, each split into its even and odd halves; the rows round to three fraction bits.
        for (int i = 0; i < 4; i++)
        {
            int evenSum = c[i] + c[i + 8], evenDifference = c[i] - c[i + 8];
            int oddDifference = MulSin(c[i + 4]) - MulCos(c[i + 12]);
            int oddSum = MulCos(c[i + 4]) + MulSin(c[i + 12]);
            tmp[i * 4] = evenSum + oddSum;
            tmp[i * 4 + 1] = evenDifference + oddDifference;
            tmp[i * 4 + 2] = evenDifference - oddDifference;
            tmp[i * 4 + 3] = evenSum - oddSum;
        }
        Span<byte> work = _work;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i] + 4;
            int evenSum = dc + tmp[8 + i], evenDifference = dc - tmp[8 + i];
            int oddDifference = MulSin(tmp[4 + i]) - MulCos(tmp[12 + i]);
            int oddSum = MulCos(tmp[4 + i]) + MulSin(tmp[12 + i]);
            var row = work.Slice(block + i * WorkStride, 4);
            row[0] = Clip8(row[0] + ((evenSum + oddSum) >> 3));
            row[1] = Clip8(row[1] + ((evenDifference + oddDifference) >> 3));
            row[2] = Clip8(row[2] + ((evenDifference - oddDifference) >> 3));
            row[3] = Clip8(row[3] + ((evenSum - oddSum) >> 3));
        }
    }
}
