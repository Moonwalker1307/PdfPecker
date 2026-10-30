using System.Buffers.Binary;

namespace PdfPecker.Images;

internal sealed partial class PdfImage
{
    private const int PngSignatureLength = 8;
    private const int ChunkHeaderLength = 8; // the length, then the type
    private const int ChunkCrcLength = 4;

    // The IHDR fields after the width and height.
    private const int BitDepthOffset = 8, ColorTypeOffset = 9, InterlaceOffset = 12;

    private const int Grayscale = 0, Truecolor = 2, Indexed = 3, GrayscaleAlpha = 4, TruecolorAlpha = 6;

    private static bool IsPng(byte[] data)
        => data.Length > PngSignatureLength && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

    /// <summary>What the chunks of a PNG say: the header fields, the palette, the transparency and the joined pixel data.</summary>
    private struct PngInfo
    {
        public int Width, Height, BitDepth, ColorType, Interlace;
        public byte[]? Palette, Transparency;
    }

    private static PdfImage? FromPng(byte[] d)
    {
        var png = new PngInfo();
        using var idat = new MemoryStream();
        ReadChunks(d, ref png, idat);
        if (png.Width <= 0 || png.Height <= 0 || idat.Length == 0 || (long)png.Width * png.Height > MaxPixels) return null;

        int colors = png.ColorType switch { Grayscale => 1, Truecolor => 3, Indexed => 1, GrayscaleAlpha => 2, TruecolorAlpha => 4, _ => 0 };
        if (colors == 0 || png.BitDepth is not (1 or 2 or 4 or 8 or 16)) return null;
        if (png.ColorType == Indexed && png.Palette == null) return null;

        bool hasAlpha = png.ColorType is GrayscaleAlpha or TruecolorAlpha || png.Transparency != null;
        if (!hasAlpha && png.Interlace == 0)
        {
            // Fast path: the zlib stream with PNG row filters is directly understood by PDF readers.
            return new PdfImage
            {
                Width = png.Width,
                Height = png.Height,
                BitsPerComponent = png.BitDepth,
                Data = idat.ToArray(),
                DecodeParms = $"<< /Predictor 15 /Colors {colors} /BitsPerComponent {png.BitDepth} /Columns {png.Width} >>",
                ColorSpace = png.ColorType == Grayscale ? DeviceGray : png.ColorType == Truecolor ? DeviceRgb : IndexedColorSpace(png.Palette!),
            };
        }
        return DecodePng(png, idat.ToArray(), colors, hasAlpha);
    }

    /// <summary>
    /// Reads the chunks up to IEND or the first one that does not fit in the data. A length so large that the end of
    /// the chunk wraps past int.MaxValue is not caught here: reading the chunk, or the one after it, then fails.
    /// </summary>
    private static void ReadChunks(byte[] d, ref PngInfo png, MemoryStream idat)
    {
        int pos = PngSignatureLength;
        while (pos + ChunkHeaderLength <= d.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos));
            var type = d.AsSpan(pos + 4, 4);
            int body = pos + ChunkHeaderLength;
            if (length < 0 || body + length > d.Length) break;
            if (type.SequenceEqual("IHDR"u8))
            {
                png.Width = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(body));
                png.Height = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(body + 4));
                png.BitDepth = d[body + BitDepthOffset];
                png.ColorType = d[body + ColorTypeOffset];
                png.Interlace = d[body + InterlaceOffset];
            }
            else if (type.SequenceEqual("PLTE"u8)) png.Palette = d.AsSpan(body, length).ToArray();
            else if (type.SequenceEqual("tRNS"u8)) png.Transparency = d.AsSpan(body, length).ToArray();
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(d, body, length);
            else if (type.SequenceEqual("IEND"u8)) break;
            pos = body + length + ChunkCrcLength;
        }
    }

    /// <summary>Full decode: needed to split the alpha channel into a soft mask, and for interlaced images.</summary>
    private static PdfImage DecodePng(in PngInfo png, byte[] zlib, int colors, bool hasAlpha)
    {
        int width = png.Width, height = png.Height;
        // The pixel data can be no longer than its rows plus their filter bytes (a little more when interlaced): stop
        // decompressing there, so a small file cannot inflate into gigabytes.
        long rowBytes = ((long)width * colors * png.BitDepth + 7) / 8;
        byte[] raw = Inflate(zlib, (rowBytes + 1) * height + 8L * height + 1024);
        int outColors = png.ColorType is Grayscale or GrayscaleAlpha ? 1 : 3;
        var color = new byte[width * height * outColors];
        var alpha = hasAlpha ? new byte[width * height] : null;
        alpha?.AsSpan().Fill(255);

        int bitsPerPixel = colors * png.BitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8); // the distance to the corresponding byte of the pixel before
        var target = new PngTarget(png, width, color, alpha);
        if (png.Interlace == 0 && png.BitDepth == 8 && png.ColorType is GrayscaleAlpha or TruecolorAlpha)
            SplitAlpha8(raw, width, height, colors, color, alpha!);
        else if (png.Interlace == 0)
        {
            int offset = 0;
            UnfilterRows(raw, ref offset, width, height, bitsPerPixel, bpp, target, new Pass(0, 0, 1, 1));
        }
        else
        {
            int offset = 0;
            foreach (var pass in Adam7)
            {
                int passWidth = (width - pass.X + pass.Dx - 1) / pass.Dx;
                int passHeight = (height - pass.Y + pass.Dy - 1) / pass.Dy;
                if (passWidth <= 0 || passHeight <= 0) continue;
                UnfilterRows(raw, ref offset, passWidth, passHeight, bitsPerPixel, bpp, target, pass);
            }
        }

        var result = new PdfImage { Width = width, Height = height, BitsPerComponent = 8, ColorSpace = outColors == 1 ? DeviceGray : DeviceRgb, Data = Deflate(color) };
        if (alpha != null && HasTransparency(alpha))
            result.SMask = new PdfImage { Width = width, Height = height, BitsPerComponent = 8, ColorSpace = DeviceGray, Data = Deflate(alpha) };
        return result;
    }

    /// <summary>The common 8-bit grey with alpha and RGBA images: unfiltered in place, then split into colour and alpha.</summary>
    private static void SplitAlpha8(byte[] raw, int width, int height, int colors, byte[] color, byte[] alpha)
    {
        int stride = width * colors;
        UnfilterInPlace(raw, stride, height, colors);
        int colorChannels = colors - 1;
        for (int y = 0, ci = 0, ai = 0; y < height; y++)
        {
            int p = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++, p += colors)
            {
                for (int c = 0; c < colorChannels; c++) color[ci++] = raw[p + c];
                alpha[ai++] = raw[p + colorChannels];
            }
        }
    }

    /// <summary>An Adam7 pass: its first pixel and the step between its pixels, across and down.</summary>
    private readonly record struct Pass(int X, int Y, int Dx, int Dy);

    private static readonly Pass[] Adam7 =
    {
        new(0, 0, 8, 8), new(4, 0, 8, 8), new(0, 4, 4, 8), new(2, 0, 4, 4), new(0, 2, 2, 4), new(1, 0, 2, 2), new(0, 1, 1, 2),
    };

    /// <summary>The colour and alpha planes the pixels of every pass are stored into.</summary>
    private readonly record struct PngTarget(PngInfo Png, int Width, byte[] Color, byte[]? Alpha);

    private static string IndexedColorSpace(byte[] palette)
    {
        int n = palette.Length / 3;
        var sb = new System.Text.StringBuilder("[/Indexed /DeviceRGB ", 30 + palette.Length * 2);
        sb.Append(n - 1).Append(" <").Append(Convert.ToHexString(palette, 0, n * 3)).Append(">]");
        return sb.ToString();
    }

    /// <summary>Reverses PNG row filters in place. Each row is preceded by its filter-type byte.</summary>
    private static void UnfilterInPlace(byte[] raw, int stride, int height, int bpp)
    {
        if (raw.Length < (stride + 1) * height) throw new InvalidDataException("Truncated PNG data");
        // The row above the first is zeros.
        ReadOnlySpan<byte> above = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            var current = raw.AsSpan(row + 1, stride);
            PngFilters.Undo(raw[row], current, above, bpp);
            above = current;
        }
    }

    /// <summary>Reverses the row filters of one pass (or the whole image) row by row and stores its pixels.</summary>
    private static void UnfilterRows(byte[] raw, ref int offset, int width, int height, int bitsPerPixel, int bpp, in PngTarget target, Pass pass)
    {
        int stride = (width * bitsPerPixel + 7) / 8;
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            if (offset + 1 + stride > raw.Length) throw new InvalidDataException("Truncated PNG data");
            byte filter = raw[offset++];
            Buffer.BlockCopy(raw, offset, cur, 0, stride);
            offset += stride;
            PngFilters.Undo(filter, cur, prev, bpp);
            StoreRow(cur, width, pass.Y + y * pass.Dy, pass, target);
            (prev, cur) = (cur, prev);
        }
    }

    /// <summary>
    /// Stores one unfiltered row of a pass: 8-bit samples as they are, the high byte of 16-bit ones, and grey levels of
    /// fewer bits scaled to 8. Transparency comes from the alpha samples, or from tRNS (a palette's alpha values, or the
    /// one grey level or 8-bit colour that is transparent).
    /// </summary>
    private static void StoreRow(byte[] row, int count, int y, Pass pass, in PngTarget target)
    {
        var png = target.Png;
        int bitDepth = png.BitDepth;
        var color = target.Color;
        var alpha = target.Alpha;
        var trns = png.Transparency;
        int Sample(int index)
        {
            if (bitDepth == 8) return row[index];
            if (bitDepth == 16) return row[index * 2];
            int bitPos = index * bitDepth;
            return (row[bitPos >> 3] >> (8 - bitDepth - (bitPos & 7))) & ((1 << bitDepth) - 1);
        }
        for (int x = 0; x < count; x++)
        {
            int di = y * target.Width + pass.X + x * pass.Dx;
            switch (png.ColorType)
            {
                case Grayscale:
                {
                    int g = Sample(x);
                    color[di] = (byte)(bitDepth >= 8 ? g : g * 255 / ((1 << bitDepth) - 1));
                    if (alpha != null && trns != null && trns.Length >= 2)
                    {
                        int key = trns[0] << 8 | trns[1];
                        int value = bitDepth == 16 ? row[x * 2] << 8 | row[x * 2 + 1] : g;
                        if (value == key) alpha[di] = 0;
                    }
                    break;
                }
                case Truecolor:
                    color[di * 3] = (byte)Sample(x * 3);
                    color[di * 3 + 1] = (byte)Sample(x * 3 + 1);
                    color[di * 3 + 2] = (byte)Sample(x * 3 + 2);
                    if (alpha != null && trns != null && trns.Length >= 6 && bitDepth == 8
                        && color[di * 3] == trns[1] && color[di * 3 + 1] == trns[3] && color[di * 3 + 2] == trns[5])
                        alpha[di] = 0;
                    break;
                case Indexed:
                {
                    int index = Sample(x);
                    var palette = png.Palette!;
                    if (index * 3 + 2 < palette.Length)
                    {
                        color[di * 3] = palette[index * 3];
                        color[di * 3 + 1] = palette[index * 3 + 1];
                        color[di * 3 + 2] = palette[index * 3 + 2];
                    }
                    if (alpha != null && trns != null && index < trns.Length) alpha[di] = trns[index];
                    break;
                }
                case GrayscaleAlpha:
                    color[di] = (byte)Sample(x * 2);
                    alpha![di] = (byte)Sample(x * 2 + 1);
                    break;
                case TruecolorAlpha:
                    color[di * 3] = (byte)Sample(x * 4);
                    color[di * 3 + 1] = (byte)Sample(x * 4 + 1);
                    color[di * 3 + 2] = (byte)Sample(x * 4 + 2);
                    alpha![di] = (byte)Sample(x * 4 + 3);
                    break;
            }
        }
    }
}
