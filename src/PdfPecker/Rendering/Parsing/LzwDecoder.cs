using System.Buffers;
using PdfPecker.Images;

namespace PdfPecker.Rendering.Parsing;

/// <summary>
/// Decodes the variable-width LZW codes of a PDF stream or a TIFF strip, which are packed most significant bit first.
/// Damaged data ends the decoding early instead of failing, so a short stream still gives what it has.
/// </summary>
internal ref struct LzwDecoder
{
    private const int TableSize = 4096;
    private const int ClearCode = 256;
    private const int EndOfDataCode = 257;
    private const int FirstFreeCode = 258;
    private const int MinCodeLength = 9;
    private const int MaxCodeLength = 12;

    // A string is followed through its links at most this many times, which a table that links in a circle needs.
    private const int MaxStringLength = TableSize + 1;

    private SpanCursor input;
    private readonly bool earlyChange;

    // The table stores each string as its last byte and a link to the string without it, which is -1 for a single byte.
    // The length of a string is kept as well when every link of it leads to a byte, which is always so for data from a
    // sane encoder; it lets the string be written to the output back to front, without the stack. Damaged data can
    // refer to codes that are not defined yet. They read as the zeros of a table that was never filled, and the strings
    // built on them have no length, so that the slower walk through the links gives the same result as it always did.
    private readonly Span<short> prefix;
    private readonly Span<byte> suffix;
    private readonly Span<ushort> lengths;
    private readonly Span<byte> stack;

    private int bitBuffer;
    private int bitCount;

    private LzwDecoder(ReadOnlySpan<byte> data, bool earlyChange, Span<short> prefix, Span<byte> suffix, Span<ushort> lengths, Span<byte> stack)
    {
        input = new SpanCursor(data);
        this.earlyChange = earlyChange;
        this.prefix = prefix;
        this.suffix = suffix;
        this.lengths = lengths;
        this.stack = stack;
        for (int i = 0; i < ClearCode; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            lengths[i] = 1;
        }
    }

    /// <summary>
    /// Decodes <paramref name="data"/>. Decoding stops once the output passes <paramref name="limit"/>, which can leave it
    /// longer by up to one string.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, bool earlyChange, int limit, int initialCapacity)
    {
        Span<short> prefix = stackalloc short[TableSize];
        Span<byte> suffix = stackalloc byte[TableSize];
        Span<ushort> lengths = stackalloc ushort[TableSize];
        Span<byte> stack = stackalloc byte[MaxStringLength];
        var decoder = new LzwDecoder(data, earlyChange, prefix, suffix, lengths, stack);
        return decoder.Run(limit, initialCapacity);
    }

    private byte[] Run(int limit, int initialCapacity)
    {
        var output = new ArrayBufferWriter<byte>(initialCapacity);
        int nextCode = FirstFreeCode;
        int codeLength = MinCodeLength;
        int previous = -1;
        while (TryReadCode(codeLength, out int code))
        {
            if (code == ClearCode)
            {
                nextCode = FirstFreeCode;
                codeLength = MinCodeLength;
                previous = -1;
                lengths.Slice(FirstFreeCode).Clear();
                continue;
            }
            if (code == EndOfDataCode) break;
            if (previous < 0)
            {
                if (code >= ClearCode) break; // the first code after a clear must be a byte
                output.GetSpan(1)[0] = (byte)code;
                output.Advance(1);
                previous = code;
                continue;
            }

            // A code that is not in the table yet stands for the previous string followed by its own first byte. Any
            // such code is read that way, not only the next one to be defined.
            bool definedYet = code < nextCode;
            byte first = WriteString(output, definedYet ? code : previous, appendFirst: !definedYet);
            if (nextCode < TableSize)
            {
                prefix[nextCode] = (short)previous;
                suffix[nextCode] = first;
                lengths[nextCode] = lengths[previous] == 0 ? (ushort)0 : (ushort)(lengths[previous] + 1);
                nextCode++;
            }
            codeLength = GrownCodeLength(codeLength, nextCode);
            previous = code;
            if (output.WrittenCount > limit) break;
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Writes the string of <paramref name="code"/>, and with <paramref name="appendFirst"/> its first byte again. Returns that byte.</summary>
    private readonly byte WriteString(ArrayBufferWriter<byte> output, int code, bool appendFirst)
    {
        int length = lengths[code];
        if (length == 0) return WriteStringByLinks(output, code, appendFirst);

        var destination = output.GetSpan(length + 1);
        for (int i = length - 1; i >= 0; i--)
        {
            destination[i] = suffix[code];
            code = prefix[code];
        }
        byte first = destination[0];
        if (appendFirst) destination[length++] = first;
        output.Advance(length);
        return first;
    }

    /// <summary>Writes a string by following its links onto a stack, for strings whose length is not known.</summary>
    private readonly byte WriteStringByLinks(ArrayBufferWriter<byte> output, int code, bool appendFirst)
    {
        int start = stack.Length;
        while (code >= 0 && stack.Length - start < MaxStringLength)
        {
            stack[--start] = suffix[code];
            code = prefix[code];
        }
        var text = stack.Slice(start);
        byte first = text[0];
        output.Write(text);
        if (appendFirst) output.Write(new ReadOnlySpan<byte>(in first));
        return first;
    }

    /// <summary>
    /// Codes grow by a bit when the table fills up the current width. With early change, which is the default in PDF
    /// and the only mode in TIFF, that happens one code sooner.
    /// </summary>
    private readonly int GrownCodeLength(int codeLength, int nextCode)
    {
        int codes = nextCode + (earlyChange ? 1 : 0);
        return codeLength < MaxCodeLength && codes >= 1 << codeLength ? codeLength + 1 : codeLength;
    }

    /// <summary>Reads the next code, or returns false when fewer bits than a code remain.</summary>
    private bool TryReadCode(int codeLength, out int code)
    {
        while (bitCount < codeLength && input.TryReadByte(out byte next))
        {
            bitBuffer = (bitBuffer << 8) | next;
            bitCount += 8;
        }
        if (bitCount < codeLength)
        {
            code = 0;
            return false;
        }
        code = (bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1);
        bitCount -= codeLength;
        return true;
    }
}
