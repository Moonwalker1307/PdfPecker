using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPecker.Images;

/// <summary>An image ready to be written as a PDF image XObject.</summary>
internal sealed class PdfImage
{
    public int Width, Height;
    public string ColorSpace = "/DeviceRGB"; // a PDF object (name or array)
    public int BitsPerComponent = 8;
    public string Filter = "/FlateDecode";
    public string? DecodeParms;
    public string? Decode;
    public byte[] Data = Array.Empty<byte>();
    public PdfImage? SMask;
    public int ObjectNumber; // assigned by the writer

    /// <summary>A copy for another document: it shares the pixel data, which is never changed, but not the object number.</summary>
    public PdfImage Copy()
    {
        var copy = (PdfImage)MemberwiseClone();
        copy.ObjectNumber = 0;
        copy.SMask = SMask?.Copy();
        return copy;
    }

    /// <summary>
    /// Cuts the given rectangles (in pixels) out of this image as images of their own: the slices of a border image.
    /// Drawing a slice out of the whole image instead would let a viewer's smoothing blend in the pixels around it. A
    /// rectangle with no pixels gives null; the whole result is null when the image's encoding cannot be cut here
    /// (CMYK, other than 8 bits per component, or an unusual filter).
    /// </summary>
    public PdfImage?[]? Cut((int X, int Y, int W, int H)[] rects)
    {
        try
        {
            if (Pixels() is not { } color) return null;
            byte[]? alpha = null;
            if (SMask != null)
            {
                if (SMask.Width != Width || SMask.Height != Height || SMask.Pixels() is not { Colors: 1 } mask) return null;
                alpha = mask.Data;
            }
            var result = new PdfImage?[rects.Length];
            for (int i = 0; i < rects.Length; i++)
            {
                var (x, y, w, h) = rects[i];
                if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > Width || y + h > Height) continue;
                var image = new PdfImage { Width = w, Height = h, ColorSpace = ColorSpace, Data = Deflate(CopyRect(color.Data, color.Colors, x, y, w, h)) };
                if (alpha != null) image.SMask = new PdfImage { Width = w, Height = h, ColorSpace = "/DeviceGray", Data = Deflate(CopyRect(alpha, 1, x, y, w, h)) };
                result[i] = image;
            }
            return result;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or FormatException or EndOfStreamException)
        {
            return null;
        }
    }

    private byte[] CopyRect(byte[] pixels, int colors, int x, int y, int w, int h)
    {
        var part = new byte[w * h * colors];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(pixels, ((y + row) * Width + x) * colors, part, row * w * colors, w * colors);
        return part;
    }

    /// <summary>The image's samples, 8 bits each and one to three per pixel, without row filters; null when not supported.</summary>
    private (byte[] Data, int Colors)? Pixels()
    {
        if (BitsPerComponent != 8 || Decode != null || (long)Width * Height > MaxPixels) return null;
        int colors = ColorSpace == "/DeviceRGB" ? 3 : ColorSpace == "/DeviceGray" || ColorSpace.StartsWith("[/Indexed", StringComparison.Ordinal) ? 1 : 0;
        if (colors == 0) return null;
        long size = (long)Width * Height * colors;
        if (Filter == "/DCTDecode")
        {
            if (Rendering.Images.JpegDecoder.Decode(Data) is not { } jpg || jpg.Width != Width || jpg.Height != Height
                || jpg.Components != colors || jpg.Data.Length < size) return null;
            return (jpg.Data, colors);
        }
        if (Filter != "/FlateDecode") return null;
        if (DecodeParms == null)
        {
            var raw = Inflate(Data, size);
            return raw.Length >= size ? (raw, colors) : null;
        }
        // The PNG row filters of an image passed through from a PNG file.
        if (!DecodeParms.StartsWith("<< /Predictor 15 ", StringComparison.Ordinal)) return null;
        int stride = Width * colors;
        var filtered = Inflate(Data, (stride + 1L) * Height);
        UnfilterInPlace(filtered, stride, Height, colors);
        var pixels = new byte[size];
        for (int row = 0; row < Height; row++) Buffer.BlockCopy(filtered, row * (stride + 1) + 1, pixels, row * stride, stride);
        return (pixels, colors);
    }

    /// <summary>Decodes JPEG, PNG, GIF or WebP data. Returns null for unsupported or corrupt images.</summary>
    public static PdfImage? FromBytes(byte[] data)
    {
        try
        {
            if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return FromJpeg(data);
            if (data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') return FromPng(data);
            if (GifDecoder.IsGif(data)) return FromPixels(GifDecoder.Decode(data, MaxPixels));
            if (WebPDecoder.IsWebP(data)) return FromPixels(WebPDecoder.Decode(data, MaxPixels));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or EndOfStreamException)
        {
        }
        return null;
    }

    /// <summary>
    /// A DeviceRGB copy of a CMYK JPEG, for PDF/A files whose sRGB output intent does not allow DeviceCMYK.
    /// Returns the image itself when it is not CMYK, or null when it cannot be decoded.
    /// </summary>
    public PdfImage? ToRgb()
    {
        if (ColorSpace != "/DeviceCMYK") return this;
        if (Filter != "/DCTDecode") return null;
        var jpg = Rendering.Images.JpegDecoder.Decode(Data);
        if (jpg == null || jpg.Value.Components != 4) return null;
        var (cmyk, w, h, _) = jpg.Value;
        bool inverted = Decode != null;
        var rgb = new byte[w * h * 3];
        var cache = new Dictionary<int, int>();
        for (int i = 0, o = 0; i + 3 < cmyk.Length && o + 2 < rgb.Length; i += 4, o += 3)
        {
            int key = cmyk[i] << 24 | cmyk[i + 1] << 16 | cmyk[i + 2] << 8 | cmyk[i + 3];
            if (!cache.TryGetValue(key, out int packed))
            {
                float c = cmyk[i] / 255f, m = cmyk[i + 1] / 255f, y = cmyk[i + 2] / 255f, k = cmyk[i + 3] / 255f;
                if (inverted) { c = 1 - c; m = 1 - m; y = 1 - y; k = 1 - k; }
                var (r, g, b) = Rendering.Content.DeviceCmyk.Convert(c, m, y, k);
                packed = r << 16 | g << 8 | b;
                if (cache.Count < 65536) cache[key] = packed;
            }
            rgb[o] = (byte)(packed >> 16);
            rgb[o + 1] = (byte)(packed >> 8);
            rgb[o + 2] = (byte)packed;
        }
        return new PdfImage { Width = w, Height = h, ColorSpace = "/DeviceRGB", BitsPerComponent = 8, Data = Deflate(rgb), SMask = SMask };
    }

    /// <summary>
    /// The image's colours as 8-bit RGB, without its soft mask. The renderer decodes it from a one-image PDF, so that
    /// every form the writer embeds (JPEG, PNG rows with predictors, palettes, grey, CMYK) is read as viewers read it.
    /// Null when it cannot be decoded.
    /// </summary>
    public (byte[] Rgb, int Width, int Height)? DecodeRgb()
    {
        var head = new System.Text.StringBuilder("%PDF-1.4\n");
        int objOffset = head.Length;
        head.Append("1 0 obj\n<< /Type /XObject /Subtype /Image /Width ").Append(Width).Append(" /Height ").Append(Height)
            .Append(" /ColorSpace ").Append(ColorSpace).Append(" /BitsPerComponent ").Append(BitsPerComponent);
        if (Filter.Length > 0) head.Append(" /Filter ").Append(Filter);
        if (DecodeParms != null) head.Append(" /DecodeParms ").Append(DecodeParms);
        if (Decode != null) head.Append(" /Decode ").Append(Decode);
        head.Append(" /Length ").Append(Data.Length).Append(" >>\nstream\n");
        var tail = "\nendstream\nendobj\n";
        long catalog = head.Length + Data.Length + tail.Length;
        const string CatalogObj = "2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n3 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n";
        long pages = catalog + CatalogObj.IndexOf("3 0 obj", StringComparison.Ordinal);
        long xref = catalog + CatalogObj.Length;
        static string Entry(long offset) => offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n";
        var trailer = CatalogObj + "xref\n0 4\n0000000000 65535 f \n" + Entry(objOffset) + Entry(catalog) + Entry(pages)
            + "trailer\n<< /Size 4 /Root 2 0 R >>\nstartxref\n" + xref.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n%%EOF\n";
        using var pdf = new MemoryStream();
        pdf.Write(System.Text.Encoding.Latin1.GetBytes(head.ToString()));
        pdf.Write(Data);
        pdf.Write(System.Text.Encoding.Latin1.GetBytes(tail + trailer));
        try
        {
            var file = Rendering.Parsing.PdfFile.Open(pdf.ToArray(), null);
            if (file.GetObject(1) is not Rendering.Parsing.PdfStream stream) return null;
            var decoded = Rendering.Images.PdfImageDecoder.Decode(file, stream, Array.Empty<byte>(), alreadyDecoded: false, null, inline: false);
            if (decoded?.Pixels is not { } px) return null;
            var rgb = new byte[px.Length * 3];
            for (int i = 0, o = 0; i < px.Length; i++, o += 3)
            {
                uint p = px[i];
                rgb[o] = (byte)p;
                rgb[o + 1] = (byte)(p >> 8);
                rgb[o + 2] = (byte)(p >> 16);
            }
            return (rgb, decoded.Width, decoded.Height);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or NotSupportedException
                                       or FormatException or Rendering.Parsing.PdfFormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ JPEG: embedded as-is (DCTDecode)

    private static PdfImage? FromJpeg(byte[] d)
    {
        int i = 2;
        bool adobe = false;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01) { i += 2; continue; }
            int len = d[i + 2] << 8 | d[i + 3];
            if (marker == 0xEE && len >= 12 && d[i + 4] == 'A' && d[i + 5] == 'd' && d[i + 6] == 'o' && d[i + 7] == 'b' && d[i + 8] == 'e')
                adobe = true;
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                int bpc = d[i + 4];
                int h = d[i + 5] << 8 | d[i + 6];
                int w = d[i + 7] << 8 | d[i + 8];
                int comps = d[i + 9];
                if (w == 0 || h == 0) return null;
                var img = new PdfImage
                {
                    Width = w,
                    Height = h,
                    BitsPerComponent = bpc == 12 ? 8 : bpc,
                    Filter = "/DCTDecode",
                    Data = d,
                    ColorSpace = comps switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" },
                };
                // Adobe CMYK JPEGs store inverted values.
                if (comps == 4 && adobe) img.Decode = "[1 0 1 0 1 0 1 0]";
                if (comps == 4 && !adobe)
                {
                    // An APP14 segment may come after SOF; scan the rest of the header for it.
                    for (int k = i; k + 9 < d.Length && k < 65536; k++)
                        if (d[k] == 0xFF && d[k + 1] == 0xEE && d[k + 4] == 'A' && d[k + 5] == 'd') { img.Decode = "[1 0 1 0 1 0 1 0]"; break; }
                }
                return img;
            }
            if (marker == 0xDA) return null; // start of scan before frame header: corrupt
            i += 2 + len;
        }
        return null;
    }

    // ------------------------------------------------------------------ PNG

    private static PdfImage? FromPng(byte[] d)
    {
        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            int dataStart = pos + 8;
            if (len < 0 || dataStart + len > d.Length) break;
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(dataStart));
                    height = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(dataStart + 4));
                    bitDepth = d[dataStart + 8];
                    colorType = d[dataStart + 9];
                    interlace = d[dataStart + 12];
                    break;
                case "PLTE":
                    palette = d.AsSpan(dataStart, len).ToArray();
                    break;
                case "tRNS":
                    trns = d.AsSpan(dataStart, len).ToArray();
                    break;
                case "IDAT":
                    idat.Write(d, dataStart, len);
                    break;
            }
            if (type == "IEND") break;
            pos = dataStart + len + 4;
        }
        if (width <= 0 || height <= 0 || idat.Length == 0 || (long)width * height > MaxPixels) return null;

        int colors = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (colors == 0 || bitDepth is not (1 or 2 or 4 or 8 or 16)) return null;
        if (colorType == 3 && palette == null) return null;

        bool hasAlpha = colorType is 4 or 6 || trns != null;
        if (!hasAlpha && interlace == 0)
        {
            // Fast path: the zlib stream with PNG row filters is directly understood by PDF readers.
            var img = new PdfImage
            {
                Width = width,
                Height = height,
                BitsPerComponent = bitDepth,
                Data = idat.ToArray(),
                DecodeParms = "<< /Predictor 15 /Colors " + colors + " /BitsPerComponent " + bitDepth + " /Columns " + width + " >>",
                ColorSpace = colorType == 0 ? "/DeviceGray" : colorType == 2 ? "/DeviceRGB" : IndexedColorSpace(palette!),
            };
            return img;
        }

        // Full decode: needed to split the alpha channel into a soft mask.
        // The pixel data can be no longer than its rows plus their filter bytes (a little more when interlaced): stop
        // decompressing there, so a small file cannot inflate into gigabytes.
        long rowBytes = ((long)width * colors * bitDepth + 7) / 8;
        byte[] raw = Inflate(idat.ToArray(), (rowBytes + 1) * height + 8L * height + 1024);
        int outColors = colorType is 0 or 4 ? 1 : 3;
        var color = new byte[width * height * outColors];
        var alpha = hasAlpha ? new byte[width * height] : null;
        if (alpha != null) alpha.AsSpan().Fill(255);

        int bitsPerPixel = colors * bitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int offset = 0;
        if (interlace == 0 && bitDepth == 8 && colorType is 4 or 6)
        {
            // Fast path for the common 8-bit gray+alpha / RGBA case.
            int stride = width * colors;
            UnfilterInPlace(raw, stride, height, bpp);
            int ci = 0, ai = 0;
            for (int y = 0; y < height; y++)
            {
                int p = y * (stride + 1) + 1;
                if (colorType == 6)
                {
                    for (int x = 0; x < width; x++, p += 4)
                    {
                        color[ci++] = raw[p];
                        color[ci++] = raw[p + 1];
                        color[ci++] = raw[p + 2];
                        alpha![ai++] = raw[p + 3];
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++, p += 2)
                    {
                        color[ci++] = raw[p];
                        alpha![ai++] = raw[p + 1];
                    }
                }
            }
        }
        else if (interlace == 0)
        {
            Unfilter(raw, ref offset, width, height, bitsPerPixel, bpp, (x, y, row, rowStart) =>
                StorePixel(row, rowStart, x, y, width, colorType, bitDepth, palette, trns, color, alpha));
        }
        else
        {
            int[] sx = { 0, 4, 0, 2, 0, 1, 0 }, sy = { 0, 0, 4, 0, 2, 0, 1 }, dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
            for (int p = 0; p < 7; p++)
            {
                int pw = (width - sx[p] + dx[p] - 1) / dx[p];
                int ph = (height - sy[p] + dy[p] - 1) / dy[p];
                if (pw <= 0 || ph <= 0) continue;
                int pass = p;
                Unfilter(raw, ref offset, pw, ph, bitsPerPixel, bpp, (x, y, row, rowStart) =>
                    StorePixel(row, rowStart, x, sy[pass] + y * dy[pass], width, colorType, bitDepth, palette, trns, color, alpha, sx[pass] + x * dx[pass]));
            }
        }

        var result = new PdfImage
        {
            Width = width,
            Height = height,
            BitsPerComponent = 8,
            ColorSpace = outColors == 1 ? "/DeviceGray" : "/DeviceRGB",
            Data = Deflate(color),
        };
        if (alpha != null && HasTransparency(alpha))
        {
            result.SMask = new PdfImage
            {
                Width = width,
                Height = height,
                BitsPerComponent = 8,
                ColorSpace = "/DeviceGray",
                Data = Deflate(alpha),
            };
        }
        return result;
    }

    /// <summary>Decoded RGB pixels, with a soft mask when some of them are not opaque.</summary>
    private static PdfImage? FromPixels(DecodedImage? decoded)
    {
        if (decoded is not { } d) return null;
        var image = new PdfImage { Width = d.Width, Height = d.Height, BitsPerComponent = 8, ColorSpace = "/DeviceRGB", Data = Deflate(d.Rgb) };
        if (d.Alpha != null && HasTransparency(d.Alpha))
            image.SMask = new PdfImage { Width = d.Width, Height = d.Height, BitsPerComponent = 8, ColorSpace = "/DeviceGray", Data = Deflate(d.Alpha) };
        return image;
    }

    private static bool HasTransparency(byte[] alpha)
    {
        foreach (var a in alpha) if (a != 255) return true;
        return false;
    }

    private static string IndexedColorSpace(byte[] palette)
    {
        int n = palette.Length / 3;
        var sb = new System.Text.StringBuilder("[/Indexed /DeviceRGB ", 30 + palette.Length * 2);
        sb.Append(n - 1).Append(" <");
        for (int i = 0; i < n * 3; i++) sb.Append(palette[i].ToString("X2"));
        sb.Append(">]");
        return sb.ToString();
    }

    /// <summary>Reverses PNG row filters in place. Each row is preceded by its filter-type byte.</summary>
    private static void UnfilterInPlace(byte[] raw, int stride, int height, int bpp)
    {
        if (raw.Length < (stride + 1) * height) throw new InvalidDataException("Truncated PNG data");
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            byte filter = raw[row];
            int cur = row + 1, prev = cur - (stride + 1);
            bool first = y == 0;
            switch (filter)
            {
                case 1:
                    for (int i = bpp; i < stride; i++) raw[cur + i] += raw[cur + i - bpp];
                    break;
                case 2:
                    if (!first) for (int i = 0; i < stride; i++) raw[cur + i] += raw[prev + i];
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? raw[cur + i - bpp] : 0, b = first ? 0 : raw[prev + i];
                        raw[cur + i] += (byte)((a + b) >> 1);
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? raw[cur + i - bpp] : 0, b = first ? 0 : raw[prev + i], c = !first && i >= bpp ? raw[prev + i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        raw[cur + i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    break;
            }
        }
    }

    private delegate void RowCallback(int x, int y, byte[] row, int rowStart);

    private static void Unfilter(byte[] raw, ref int offset, int width, int height, int bitsPerPixel, int bpp, RowCallback store)
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
            switch (filter)
            {
                case 1:
                    for (int i = bpp; i < stride; i++) cur[i] += cur[i - bpp];
                    break;
                case 2:
                    for (int i = 0; i < stride; i++) cur[i] += prev[i];
                    break;
                case 3:
                    for (int i = 0; i < stride; i++) cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    break;
            }
            for (int x = 0; x < width; x++) store(x, y, cur, 0);
            (prev, cur) = (cur, prev);
        }
    }

    private static void StorePixel(byte[] row, int rowStart, int x, int y, int width, int colorType, int bitDepth,
        byte[]? palette, byte[]? trns, byte[] color, byte[]? alpha, int destX = -1)
    {
        if (destX < 0) destX = x;
        int di = y * width + destX;
        int Sample(int index)
        {
            // index: component index in the row
            if (bitDepth == 8) return row[rowStart + index];
            if (bitDepth == 16) return row[rowStart + index * 2];
            int bitPos = index * bitDepth;
            int b = row[rowStart + (bitPos >> 3)];
            int shift = 8 - bitDepth - (bitPos & 7);
            return (b >> shift) & ((1 << bitDepth) - 1);
        }
        int Scale(int v) => bitDepth >= 8 ? v : v * 255 / ((1 << bitDepth) - 1);

        switch (colorType)
        {
            case 0:
            {
                int g = Sample(x);
                color[di] = (byte)Scale(g);
                if (alpha != null && trns != null && trns.Length >= 2)
                {
                    int t = trns[0] << 8 | trns[1];
                    int raw16 = bitDepth == 16 ? row[rowStart + x * 2] << 8 | row[rowStart + x * 2 + 1] : g;
                    if (raw16 == t) alpha[di] = 0;
                }
                break;
            }
            case 2:
            {
                color[di * 3] = (byte)Sample(x * 3);
                color[di * 3 + 1] = (byte)Sample(x * 3 + 1);
                color[di * 3 + 2] = (byte)Sample(x * 3 + 2);
                if (alpha != null && trns != null && trns.Length >= 6 && bitDepth == 8
                    && color[di * 3] == trns[1] && color[di * 3 + 1] == trns[3] && color[di * 3 + 2] == trns[5])
                    alpha[di] = 0;
                break;
            }
            case 3:
            {
                int idx = Sample(x);
                if (idx * 3 + 2 < palette!.Length)
                {
                    color[di * 3] = palette[idx * 3];
                    color[di * 3 + 1] = palette[idx * 3 + 1];
                    color[di * 3 + 2] = palette[idx * 3 + 2];
                }
                if (alpha != null && trns != null && idx < trns.Length) alpha[di] = trns[idx];
                break;
            }
            case 4:
                color[di] = (byte)Sample(x * 2);
                alpha![di] = (byte)Sample(x * 2 + 1);
                break;
            case 6:
                color[di * 3] = (byte)Sample(x * 4);
                color[di * 3 + 1] = (byte)Sample(x * 4 + 1);
                color[di * 3 + 2] = (byte)Sample(x * 4 + 2);
                alpha![di] = (byte)Sample(x * 4 + 3);
                break;
        }
    }

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

    /// <summary>The largest image, in pixels, that is decoded (100 megapixels, as for PDF rendering).</summary>
    private const long MaxPixels = Rendering.Images.PdfImageDecoder.MaxPixels;

    internal static byte[] Deflate(byte[] data, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
