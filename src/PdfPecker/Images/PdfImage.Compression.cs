using System.Globalization;
using System.IO.Compression;

namespace PdfPecker.Images;

internal sealed partial class PdfImage
{
    /// <summary>Decompresses zlib data, failing once it grows past <paramref name="maxLength"/> bytes.</summary>
    private static byte[] Inflate(byte[] zlib, long maxLength)
    {
        using var input = new MemoryStream(zlib);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream((int)Math.Min(maxLength, Math.Max(256, zlib.Length * 4L)));
        var buffer = new byte[16384];
        int n;
        while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + n > maxLength) throw new FormatException("The PNG image decompresses to more data than its size allows.");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Compresses 8-bit pixels with PNG row filters (/Predictor 15): Paeth on every row but the first, which suits the
    /// smooth images of blurs and shadows; they then compress several times better than the plain pixels.
    /// </summary>
    internal static byte[] DeflatePredicted(byte[] pixels, int width, int height, int colors, out string decodeParms)
    {
        int stride = width * colors;
        var output = new byte[(stride + 1L) * height];
        for (int row = 0; row < height; row++)
        {
            int line = row * stride, prev = line - stride, at = row * (stride + 1) + 1;
            if (row == 0)
            {
                // The row above the first is zeros, so Sub, which looks only to the left, is the useful filter there.
                output[at - 1] = PngFilters.Sub;
                for (int i = 0; i < stride; i++) output[at + i] = (byte)(pixels[i] - (i >= colors ? pixels[i - colors] : 0));
                continue;
            }
            output[at - 1] = PngFilters.Paeth;
            for (int i = 0; i < colors; i++) output[at + i] = (byte)(pixels[line + i] - pixels[prev + i]);
            for (int i = colors; i < stride; i++)
                output[at + i] = (byte)(pixels[line + i] - PngFilters.PaethPredictor(pixels[line + i - colors], pixels[prev + i], pixels[prev + i - colors]));
        }
        decodeParms = "<< /Predictor 15 /Colors " + colors.ToString(CultureInfo.InvariantCulture) + " /BitsPerComponent 8 /Columns "
            + width.ToString(CultureInfo.InvariantCulture) + " >>";
        return Deflate(output, CompressionLevel.Optimal);
    }

    internal static byte[] Deflate(byte[] data, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
