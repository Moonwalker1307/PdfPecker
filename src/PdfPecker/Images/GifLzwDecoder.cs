namespace PdfPecker.Images;

/// <summary>
/// Decodes the variable-width LZW codes of one GIF frame, which are packed into length-prefixed sub-blocks, into
/// palette indices. Damaged data ends the decoding early instead of failing, so a short frame still shows what it has.
/// </summary>
internal ref struct GifLzwDecoder
{
    private const int MaxCodes = 4096;
    private const int MaxCodeSize = 12;

    private SpanCursor cursor;
    private readonly int minCodeSize;
    private readonly int clearCode;
    private readonly int endCode;

    // The table stores each string as its last pixel plus a link to the string without it.
    private readonly short[] prefix = new short[MaxCodes];
    private readonly byte[] suffix = new byte[MaxCodes];
    private readonly byte[] firstPixel = new byte[MaxCodes];
    private readonly byte[] reversed = new byte[MaxCodes + 1];

    private int bitBuffer;
    private int bitCount;
    private int blockBytesLeft;

    /// <param name="cursor">Positioned at the first sub-block length byte.</param>
    /// <param name="minCodeSize">The minimum code size from the image data header, 1 to 11.</param>
    public GifLzwDecoder(SpanCursor cursor, int minCodeSize)
    {
        this.cursor = cursor;
        this.minCodeSize = minCodeSize;
        clearCode = 1 << minCodeSize;
        endCode = clearCode + 1;
        for (int i = 0; i < clearCode; i++)
        {
            suffix[i] = (byte)i;
            firstPixel[i] = (byte)i;
            prefix[i] = -1;
        }
    }

    /// <summary>Fills <paramref name="output"/> from the start and returns the number of pixels decoded.</summary>
    public int Decode(Span<byte> output)
    {
        int codeSize = minCodeSize + 1;
        int nextCode = clearCode + 2;
        int previous = -1;
        int written = 0;
        while (written < output.Length)
        {
            if (!TryReadCode(codeSize, out int code)) break;

            if (code == clearCode)
            {
                codeSize = minCodeSize + 1;
                nextCode = clearCode + 2;
                previous = -1;
                continue;
            }
            if (code == endCode) break;
            if (previous < 0)
            {
                if (code >= clearCode) break; // the first code after a clear must be a pixel
                output[written++] = (byte)code;
                previous = code;
                continue;
            }

            // The string is written back to front onto a stack, then copied out in order.
            int depth = 0;
            int link = code;
            byte first;
            if (code < nextCode)
            {
                first = firstPixel[code];
            }
            else if (code == nextCode)
            {
                // The code being defined: the previous string plus its own first pixel.
                first = firstPixel[previous];
                reversed[depth++] = first;
                link = previous;
            }
            else break; // a code not defined yet: damaged data

            while (link >= 0 && depth < reversed.Length)
            {
                reversed[depth++] = suffix[link];
                link = prefix[link];
            }
            while (depth > 0 && written < output.Length) output[written++] = reversed[--depth];

            if (nextCode < MaxCodes)
            {
                prefix[nextCode] = (short)previous;
                suffix[nextCode] = first;
                firstPixel[nextCode] = firstPixel[previous];
                nextCode++;
                if (nextCode == 1 << codeSize && codeSize < MaxCodeSize) codeSize++;
            }
            previous = code;
        }
        return written;
    }

    /// <summary>Reads the next code, or returns false at the end of the image data or of the input.</summary>
    private bool TryReadCode(int codeSize, out int code)
    {
        while (bitCount < codeSize)
        {
            if (blockBytesLeft == 0)
            {
                // A zero length is the end of the image data.
                if (!cursor.TryReadByte(out byte blockLength) || blockLength == 0)
                {
                    code = 0;
                    return false;
                }
                blockBytesLeft = blockLength;
            }
            if (!cursor.TryReadByte(out byte next))
            {
                code = 0;
                return false;
            }
            bitBuffer |= next << bitCount;
            bitCount += 8;
            blockBytesLeft--;
        }
        code = bitBuffer & ((1 << codeSize) - 1);
        bitBuffer >>= codeSize;
        bitCount -= codeSize;
        return true;
    }
}
