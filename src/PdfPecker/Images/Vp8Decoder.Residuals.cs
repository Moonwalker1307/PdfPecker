namespace PdfPecker.Images;

internal sealed partial class Vp8Decoder
{
    // The token probabilities: per block type, band of coefficient positions and context, one per node of the token
    // tree (RFC 6386, 13). The block types are luma after a Y2 block, the Y2 block, chroma, and luma with its DC.
    private const int NumBlockTypes = 4, NumBands = 8, NumContexts = 3, NumProbas = 11;
    private const int LumaAfterY2Type = 0, Y2Type = 1, ChromaType = 2, LumaWithDcType = 3;

    // The nodes of the token tree, as indexes into the probabilities of a context. Each names what its bit being one
    // means: the block goes on, the token is not zero, above one, above four (else two to four), above two, four
    // (else three), above category 2 (else category 1 or 2), category 2, above category 4, category 4, category 6.
    private const int NotEndNode = 0, NotZeroNode = 1, NotOneNode = 2, AboveFourNode = 3, AboveTwoNode = 4, FourNode = 5;
    private const int AboveCategory2Node = 6, Category2Node = 7, AboveCategory4Node = 8, Category4Node = 9, Category6Node = 10;

    // The extra bits of the larger tokens have fixed probabilities: categories 1 and 2 are written out, the others
    // are in Category3To6Probas, and the sign takes an even chance.
    private const int Category1Proba = 159;
    private const int Category2HighProba = 165, Category2LowProba = 145;
    private const int SignProba = 128;
    private static readonly byte[][] Category3To6Probas =
    {
        new byte[] { 173, 148, 140 },
        new byte[] { 176, 155, 140, 135 },
        new byte[] { 180, 157, 141, 134, 130 },
        new byte[] { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 },
    };

    private static readonly byte[] Bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 }; // band per position, and one past the end
    private static readonly byte[] Zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    // The coefficients of one macroblock: 16 luma blocks, then 4 U and 4 V blocks, 16 coefficients each.
    private const int CoefficientsPerBlock = 16;
    private const int UCoefficients = 16 * CoefficientsPerBlock, VCoefficients = 20 * CoefficientsPerBlock;
    private readonly short[] _coeffs = new short[24 * CoefficientsPerBlock];

    // Whether the blocks along the top and left edges of a macroblock had non-zero coefficients, which is the
    // context of the first token of the blocks next to them. One bit per block: four luma columns (or rows), two U,
    // two V, and the Y2 block of a 16x16 macroblock.
    private const int UContexts = 4, VContexts = 6, Y2Context = 8;
    private const int LumaAndChromaContexts = (1 << Y2Context) - 1, AllContexts = (1 << (Y2Context + 1)) - 1;
    private ushort[] _topNonZero = Array.Empty<ushort>(); // per macroblock column
    private int _leftNonZero;

    private static int Context(int flags, int index) => (flags >> index) & 1;

    private static int WithContext(int flags, int index, bool nonZero) => nonZero ? flags | 1 << index : flags & ~(1 << index);

    /// <summary>
    /// Reads the coefficients of a macroblock into <see cref="_coeffs"/>, dequantized, and returns whether any block
    /// has more than a zero DC.
    /// </summary>
    private bool ParseResiduals(BoolDecoder br, int mbX, bool is4x4, in Quantizer q)
    {
        int top = _topNonZero[mbX], left = _leftNonZero;
        Span<short> coeffs = _coeffs;
        int lumaType = LumaWithDcType, firstCoefficient = 0;
        if (!is4x4)
        {
            // The DC of the 16 luma blocks, coded apart and spread by an inverse Walsh-Hadamard transform.
            Span<short> dc = stackalloc short[CoefficientsPerBlock];
            dc.Clear();
            int last = GetCoeffs(br, Y2Type, Context(top, Y2Context) + Context(left, Y2Context), q.Y2Dc, q.Y2Ac, 0, dc);
            top = WithContext(top, Y2Context, last > 0);
            left = WithContext(left, Y2Context, last > 0);
            TransformWht(dc, coeffs);
            lumaType = LumaAfterY2Type;
            firstCoefficient = 1;
        }

        bool coded = false;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                var block = coeffs.Slice((y * 4 + x) * CoefficientsPerBlock, CoefficientsPerBlock);
                int last = GetCoeffs(br, lumaType, Context(top, x) + Context(left, y), q.Y1Dc, q.Y1Ac, firstCoefficient, block);
                top = WithContext(top, x, last > firstCoefficient);
                left = WithContext(left, y, last > firstCoefficient);
                coded |= IsCoded(last, block);
            }
        }
        coded |= ParseChroma(br, coeffs[UCoefficients..VCoefficients], UContexts, ref top, ref left, q);
        coded |= ParseChroma(br, coeffs[VCoefficients..], VContexts, ref top, ref left, q);
        _topNonZero[mbX] = (ushort)top;
        _leftNonZero = left;
        return coded;
    }

    /// <summary>The four blocks of a chroma plane, whose contexts start at bit <paramref name="contexts"/>.</summary>
    private bool ParseChroma(BoolDecoder br, Span<short> coeffs, int contexts, ref int top, ref int left, in Quantizer q)
    {
        bool coded = false;
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                var block = coeffs.Slice((y * 2 + x) * CoefficientsPerBlock, CoefficientsPerBlock);
                int last = GetCoeffs(br, ChromaType, Context(top, contexts + x) + Context(left, contexts + y), q.UvDc, q.UvAc, 0, block);
                top = WithContext(top, contexts + x, last > 0);
                left = WithContext(left, contexts + y, last > 0);
                coded |= IsCoded(last, block);
            }
        }
        return coded;
    }

    /// <summary>Whether a block has coefficients past the first, or a non-zero DC, which a 16x16 macroblock gets from its Y2 block.</summary>
    private static bool IsCoded(int end, ReadOnlySpan<short> block) => end > 1 || block[0] != 0;

    /// <summary>A skipped macroblock has no coefficients, which the blocks next to it take as their context.</summary>
    private void ClearNonZeroContexts(int mbX, bool is4x4)
    {
        // A 4x4 macroblock has no Y2 block, so the Y2 context passes over it.
        int cleared = is4x4 ? LumaAndChromaContexts : AllContexts;
        _topNonZero[mbX] = (ushort)(_topNonZero[mbX] & ~cleared);
        _leftNonZero &= ~cleared;
    }

    private static int ProbaIndex(int type, int position, int context) => ((type * NumBands + Bands[position]) * NumContexts + context) * NumProbas;

    /// <summary>
    /// Reads the tokens of one 4x4 block from position <paramref name="n"/> on and stores them dequantized; returns
    /// the position after the last token, which is 16 when the block runs to its end.
    /// </summary>
    private int GetCoeffs(BoolDecoder br, int type, int context, int dcFactor, int acFactor, int n, Span<short> output)
    {
        var probas = _coeffProbas;
        int p = ProbaIndex(type, n, context);
        for (; n < 16; n++)
        {
            if (br.GetBit(probas[p + NotEndNode]) == 0) return n;
            // A zero is never followed by the end of the block, so the next token is read without that node.
            while (br.GetBit(probas[p + NotZeroNode]) == 0)
            {
                if (++n == 16) return 16;
                p = ProbaIndex(type, n, 0);
            }
            // The context of the next token is whether this one was one or larger.
            int next = ProbaIndex(type, n + 1, 0), value;
            if (br.GetBit(probas[p + NotOneNode]) == 0)
            {
                value = 1;
                p = next + NumProbas;
            }
            else
            {
                value = GetLargeValue(br, p);
                p = next + 2 * NumProbas;
            }
            if (br.GetBit(SignProba) == 1) value = -value;
            output[Zigzag[n]] = (short)(value * (n > 0 ? acFactor : dcFactor));
        }
        return 16;
    }

    /// <summary>A token above one: two to four, or a category of larger values with its extra bits.</summary>
    private int GetLargeValue(BoolDecoder br, int p)
    {
        var probas = _coeffProbas;
        if (br.GetBit(probas[p + AboveFourNode]) == 0)
            return br.GetBit(probas[p + AboveTwoNode]) == 0 ? 2 : 3 + br.GetBit(probas[p + FourNode]);
        if (br.GetBit(probas[p + AboveCategory2Node]) == 0)
        {
            if (br.GetBit(probas[p + Category2Node]) == 0) return 5 + br.GetBit(Category1Proba);
            int high = br.GetBit(Category2HighProba);
            return 7 + 2 * high + br.GetBit(Category2LowProba);
        }
        int above4 = br.GetBit(probas[p + AboveCategory4Node]);
        int odd = br.GetBit(probas[p + (above4 == 0 ? Category4Node : Category6Node)]);
        int category = 2 * above4 + odd; // 0 to 3 for categories 3 to 6
        int extra = 0;
        foreach (byte proba in Category3To6Probas[category]) extra += extra + br.GetBit(proba);
        return 3 + (8 << category) + extra;
    }
}
