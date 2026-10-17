using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using PdfPecker.Images;

namespace PdfPecker.Rendering.Parsing;

/// <summary>Stream decoders for the general-purpose PDF filters.</summary>
internal static partial class Filters
{
    /// <summary>Upper bound on the decoded size of one stream, against decompression bombs.</summary>
    public const int MaxDecodedSize = 256 * 1024 * 1024;

    // The read size decides how much of a damaged Flate stream survives: the inflater drops the bytes it produced in
    // the call that finds the damage, so a different size would keep a different amount of data.
    private const int InflateChunkSize = 16384;

    private const int ZlibHeaderLength = 2;
    private const int ZlibDeflateMethod = 8;

    // A byte of 128 ends run length data; a lower one starts a literal run, a higher one a repeated byte.
    private const int RunLengthEndOfData = 128;

    /// <summary>The size one stream may decode to now: the per-stream bound, or less when the page's budget is low.</summary>
    private static int Limit => (int)Math.Min(MaxDecodedSize, PdfFile.DecodeLimit);

    public static string Canonical(string name) => name switch
    {
        "Fl" => "FlateDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => name,
    };

    public static bool IsImageFilter(string name) => name is "DCTDecode" or "JPXDecode" or "CCITTFaxDecode" or "JBIG2Decode";

    public static byte[] Decode(string filter, byte[] data, PdfDict? parms, PdfFile? file)
    {
        switch (filter)
        {
            case "FlateDecode":
                return ApplyPredictor(Inflate(data), parms, file);
            case "LZWDecode":
            {
                int early = file?.GetInt(parms, "EarlyChange", 1) ?? 1;
                return ApplyPredictor(Lzw(data, early != 0), parms, file);
            }
            case "ASCIIHexDecode": return AsciiHex(data);
            case "ASCII85Decode": return Ascii85(data);
            case "RunLengthDecode": return RunLength(data);
            case "Crypt": return data; // identity crypt filter; real decryption happens on load
            default: throw new NotSupportedException("Unsupported filter " + filter);
        }
    }

    // ------------------------------------------------------------------ Flate

    public static byte[] Inflate(byte[] data)
    {
        if (data.Length == 0) return data;
        bool zlibHeader = HasZlibHeader(data);
        var result = InflateCore(data, zlibHeader ? ZlibHeaderLength : 0, out bool failedEarly);
        if (failedEarly && !zlibHeader && data.Length > ZlibHeaderLength)
        {
            // Some writers emit a bogus header: try raw deflate after skipping it.
            var skipped = InflateCore(data, ZlibHeaderLength, out _);
            if (skipped.Length > result.Length) return skipped;
        }
        return result;
    }

    /// <summary>A zlib header has the deflate method in the low nibble and a checksum that makes the pair a multiple of 31.</summary>
    private static bool HasZlibHeader(byte[] data)
        => data.Length >= ZlibHeaderLength && (data[0] & 0x0F) == ZlibDeflateMethod && ((data[0] << 8) | data[1]) % 31 == 0;

    /// <summary>Inflates raw deflate data from <paramref name="offset"/>. Damaged data keeps what was decoded before it.</summary>
    private static byte[] InflateCore(byte[] data, int offset, out bool failedEarly)
    {
        failedEarly = false;
        using var input = new MemoryStream(data, offset, data.Length - offset, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        int limit = Limit;
        var output = new ArrayBufferWriter<byte>(Math.Max(1, Math.Min(limit, Math.Max(256, data.Length * 4))));
        Span<byte> chunk = stackalloc byte[InflateChunkSize];
        try
        {
            int count;
            while ((count = inflater.Read(chunk)) > 0)
            {
                output.Write(chunk.Slice(0, count));
                if (output.WrittenCount > limit) break;
            }
        }
        catch (InvalidDataException)
        {
            // Corrupt or truncated data: keep what was decoded.
            if (output.WrittenCount == 0) failedEarly = true;
        }
        return output.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------ ASCII filters, run length

    private static byte[] AsciiHex(byte[] data)
    {
        var digits = data.AsSpan();
        int end = digits.IndexOf((byte)'>');
        return PdfLexer.DecodeHex(end < 0 ? digits : digits.Slice(0, end));
    }

    private static byte[] Ascii85(byte[] data)
    {
        const int GroupLength = 5;
        const int Radix = 85;

        var output = new ArrayBufferWriter<byte>(Math.Max(1, data.Length));
        var encoded = data.AsSpan();
        if (encoded.StartsWith("<~"u8)) encoded = encoded.Slice(2);

        int count = 0;
        uint tuple = 0;
        foreach (byte c in encoded)
        {
            if (c == '~') break;
            if (c == 'z' && count == 0)
            {
                // A lone 'z' stands for four zero bytes.
                output.GetSpan(4).Slice(0, 4).Clear();
                output.Advance(4);
                continue;
            }
            if (c < '!' || c > 'u') continue;

            tuple = tuple * Radix + (uint)(c - '!');
            if (++count == GroupLength)
            {
                WriteTuple(output, tuple, 4);
                count = 0;
                tuple = 0;
            }
        }
        if (count > 1)
        {
            // A last group of n characters is padded with the highest digit and yields n - 1 bytes.
            for (int i = count; i < GroupLength; i++) tuple = tuple * Radix + (Radix - 1);
            WriteTuple(output, tuple, count - 1);
        }
        return output.WrittenSpan.ToArray();
    }

    private static void WriteTuple(ArrayBufferWriter<byte> output, uint tuple, int byteCount)
    {
        BinaryPrimitives.WriteUInt32BigEndian(output.GetSpan(4), tuple);
        output.Advance(byteCount);
    }

    private static byte[] RunLength(byte[] data)
    {
        int limit = Limit;
        var output = new ArrayBufferWriter<byte>(Math.Max(1, data.Length * 2));
        var cursor = new SpanCursor(data);
        while (cursor.TryReadByte(out byte control) && control != RunLengthEndOfData)
        {
            if (control < RunLengthEndOfData)
            {
                // The literal run may be cut short by the end of the data.
                cursor.TryRead(Math.Min(control + 1, cursor.Remaining), out var literal);
                output.Write(literal);
            }
            else if (cursor.TryReadByte(out byte repeated))
            {
                int repeat = 257 - control;
                output.GetSpan(repeat).Slice(0, repeat).Fill(repeated);
                output.Advance(repeat);
                if (output.WrittenCount > limit) break; // each 2-byte run expands to 128 bytes
            }
        }
        return output.WrittenSpan.ToArray();
    }
}
