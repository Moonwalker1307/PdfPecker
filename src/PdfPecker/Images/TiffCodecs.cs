using System.IO.Compression;

namespace PdfPecker.Images;

/// <summary>
/// The TIFF compressions and filters that have no PDF counterpart in the renderer: PackBits, Deflate cut to a chunk's
/// size, the horizontal predictor and reversed fill order. TIFF LZW is the same dialect as PDF's LZWDecode with early
/// change (codes packed most significant bit first), so it uses <see cref="Rendering.Parsing.Filters.Lzw"/> instead of
/// GIF's decoder, whose codes are packed least significant bit first into sub-blocks.
/// </summary>
internal static class TiffCodecs
{
    /// <summary>
    /// Inflates a zlib stream into a buffer of exactly <paramref name="expected"/> bytes. A damaged stream keeps what was
    /// decoded before the damage, and the rest of the buffer stays zero.
    /// </summary>
    public static byte[] Inflate(byte[] compressed, int expected)
    {
        using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var output = new byte[expected];
        int written = 0;
        try
        {
            while (written < expected)
            {
                int read = zlib.Read(output, written, expected - written);
                if (read == 0) break;
                written += read;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // The chunk is damaged from here on; what was decoded before stays.
        }
        return output;
    }

    /// <summary>
    /// Decodes PackBits runs into a buffer of exactly <paramref name="expected"/> bytes. Each run starts with a signed
    /// header byte: 0 to 127 copies the next n + 1 bytes, -1 to -127 repeats the next byte 1 - n times, and -128 is a
    /// no-op. Runs cut off by the end of the data or of the buffer are kept as far as they go.
    /// </summary>
    public static byte[] UnpackBits(ReadOnlySpan<byte> packed, int expected)
    {
        var output = new byte[expected];
        int read = 0, written = 0;
        while (read < packed.Length && written < expected)
        {
            int header = (sbyte)packed[read++];
            if (header >= 0)
            {
                int length = Math.Min(Math.Min(header + 1, packed.Length - read), expected - written);
                packed.Slice(read, length).CopyTo(output.AsSpan(written));
                read += header + 1;
                written += length;
            }
            else if (header != -128 && read < packed.Length)
            {
                int length = Math.Min(1 - header, expected - written);
                output.AsSpan(written, length).Fill(packed[read++]);
                written += length;
            }
        }
        return output;
    }

    /// <summary>Reverses the bit order of every byte, for data written with fill order 2 (least significant bit first).</summary>
    public static void ReverseBitOrder(Span<byte> data)
    {
        foreach (ref byte b in data)
        {
            int v = b;
            v = (v & 0xF0) >> 4 | (v & 0x0F) << 4;
            v = (v & 0xCC) >> 2 | (v & 0x33) << 2;
            b = (byte)((v & 0xAA) >> 1 | (v & 0x55) << 1);
        }
    }

    /// <summary>
    /// Undoes horizontal differencing in each whole row of 8-bit or 16-bit samples: every sample was stored as its
    /// difference from the same sample of the pixel to its left, modulo the sample size.
    /// </summary>
    public static void UndoHorizontalPredictor(byte[] data, int rowBytes, int samplesPerPixel, int bitsPerSample, bool littleEndian)
    {
        for (int row = 0; row + rowBytes <= data.Length; row += rowBytes)
        {
            int rowEnd = row + rowBytes;
            if (bitsPerSample == 8)
            {
                for (int i = row + samplesPerPixel; i < rowEnd; i++) data[i] += data[i - samplesPerPixel];
                continue;
            }
            int pixelBytes = samplesPerPixel * 2;
            for (int i = row + pixelBytes; i + 1 < rowEnd; i += 2)
                WriteUInt16(data, i, littleEndian, (ushort)(ReadUInt16(data, i - pixelBytes, littleEndian) + ReadUInt16(data, i, littleEndian)));
        }
    }

    /// <summary>Reads a 16-bit sample in the file's byte order.</summary>
    public static int ReadUInt16(byte[] data, long at, bool littleEndian)
        => littleEndian ? data[at + 1] << 8 | data[at] : data[at] << 8 | data[at + 1];

    private static void WriteUInt16(byte[] data, int at, bool littleEndian, ushort value)
    {
        data[littleEndian ? at : at + 1] = (byte)value;
        data[littleEndian ? at + 1 : at] = (byte)(value >> 8);
    }
}
