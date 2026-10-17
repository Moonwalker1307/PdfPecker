using System.Buffers.Binary;
using PdfPecker.Images;

namespace PdfPecker.Rendering.Parsing;

// The predictors of the Flate and LZW filters, which undo a row-wise difference that the writer applied to improve
// compression.
internal static partial class Filters
{
    private const int NoPredictor = 1;
    private const int TiffPredictor = 2;
    private const int MaxColors = 32;
    private const int DefaultBitsPerComponent = 8;

    /// <summary>The filter type that starts each row of data written with a PNG predictor (10 to 15).</summary>
    private enum PngFilter : byte
    {
        None,
        Sub,
        Up,
        Average,
        Paeth,
    }

    private static byte[] ApplyPredictor(byte[] data, PdfDict? parms, PdfFile? file)
    {
        if (parms == null || file == null) return data;
        int predictor = file.GetInt(parms, "Predictor", NoPredictor);
        if (predictor <= NoPredictor) return data;
        int colors = Math.Clamp(file.GetInt(parms, "Colors", 1), 1, MaxColors);
        int bitsPerComponent = file.GetInt(parms, "BitsPerComponent", DefaultBitsPerComponent);
        if (bitsPerComponent is not (1 or 2 or 4 or 8 or 16)) bitsPerComponent = DefaultBitsPerComponent;
        int columns = Math.Max(1, file.GetInt(parms, "Columns", 1));
        long rowBits = (long)colors * bitsPerComponent * columns;
        if (rowBits > int.MaxValue / 2) return data;
        int rowBytes = (int)((rowBits + 7) / 8);
        int bytesPerPixel = Math.Max(1, colors * bitsPerComponent / 8);

        return predictor == TiffPredictor
            ? UndoTiffPredictor(data, bitsPerComponent, rowBytes, bytesPerPixel)
            : UndoPngPredictor(data, rowBytes, bytesPerPixel);
    }

    /// <summary>
    /// Adds each sample to the one before it in its row. Only 8 and 16 bit samples are handled; the data of other sizes
    /// is returned as it is.
    /// </summary>
    private static byte[] UndoTiffPredictor(byte[] data, int bitsPerComponent, int rowBytes, int bytesPerPixel)
    {
        var result = data.AsSpan().ToArray();
        if (bitsPerComponent is not (8 or 16)) return result;

        // A row that the data ends in the middle of is left as it is.
        for (int start = 0; start + rowBytes <= result.Length; start += rowBytes)
        {
            var row = result.AsSpan(start, rowBytes);
            if (bitsPerComponent == 8) AddLeftBytes(row, bytesPerPixel);
            else AddLeftWords(row, bytesPerPixel);
        }
        return result;
    }

    private static void AddLeftBytes(Span<byte> row, int distance)
    {
        for (int i = distance; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - distance]);
    }

    private static void AddLeftWords(Span<byte> row, int distance)
    {
        for (int i = distance; i + 1 < row.Length; i += 2)
        {
            ushort sum = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i)) + BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i - distance)));
            BinaryPrimitives.WriteUInt16BigEndian(row.Slice(i), sum);
        }
    }

    /// <summary>Undoes the PNG row filters. A missing end of the last row is zero filled.</summary>
    private static byte[] UndoPngPredictor(byte[] data, int rowBytes, int bytesPerPixel)
    {
        int rows = (data.Length + rowBytes) / (rowBytes + 1);
        var output = new byte[rows * rowBytes];
        var previous = new byte[rowBytes];
        var cursor = new SpanCursor(data);
        int written = 0;
        for (int r = 0; r < rows && cursor.TryReadByte(out byte filter); r++)
        {
            var row = output.AsSpan(written, rowBytes);
            cursor.TryRead(Math.Min(rowBytes, cursor.Remaining), out var filtered);
            filtered.CopyTo(row);
            UndoPngFilter((PngFilter)filter, row, previous, bytesPerPixel);
            row.CopyTo(previous);
            written += rowBytes;
        }
        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }

    // A filter type that no PNG filter has leaves the row as it is.
    private static void UndoPngFilter(PngFilter filter, Span<byte> row, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        switch (filter)
        {
            case PngFilter.Sub: AddLeftBytes(row, bytesPerPixel); break;
            case PngFilter.Up: AddAbove(row, above); break;
            case PngFilter.Average: UndoAverage(row, above, bytesPerPixel); break;
            case PngFilter.Paeth: UndoPaeth(row, above, bytesPerPixel); break;
        }
    }

    private static void AddAbove(Span<byte> row, ReadOnlySpan<byte> above)
    {
        for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + above[i]);
    }

    private static void UndoAverage(Span<byte> row, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        // Before the first pixel there is no byte to the left.
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
            row[i] = (byte)(row[i] + ((left + above[i]) >> 1));
        }
    }

    private static void UndoPaeth(Span<byte> row, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
            int upperLeft = i >= bytesPerPixel ? above[i - bytesPerPixel] : 0;
            row[i] = (byte)(row[i] + PaethPredictor(left, above[i], upperLeft));
        }
    }

    /// <summary>Picks whichever of the left, upper and upper-left bytes is closest to left + upper - upper-left.</summary>
    private static int PaethPredictor(int left, int upper, int upperLeft)
    {
        int estimate = left + upper - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int upperDistance = Math.Abs(estimate - upper);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= upperDistance && leftDistance <= upperLeftDistance) return left;
        return upperDistance <= upperLeftDistance ? upper : upperLeft;
    }
}
