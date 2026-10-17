namespace PdfPecker.Rendering.Parsing;

internal static partial class Filters
{
    /// <summary>
    /// Decodes LZW data, as PDF streams and TIFF strips use it. Damaged data ends the decoding with what was decoded so
    /// far. Decoding stops once the output passes the smaller of the stream limit and <paramref name="maxOutput"/>, by
    /// at most one more string.
    /// </summary>
    internal static byte[] Lzw(byte[] data, bool earlyChange, int maxOutput = int.MaxValue)
    {
        int limit = Math.Min(Limit, maxOutput);
        int capacity = (int)Math.Min(data.Length * 3L, limit + 1L);
        // A negative capacity failed here when the output was a MemoryStream, and still does.
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return LzwDecoder.Decode(data, earlyChange, limit, Math.Max(1, capacity));
    }
}
