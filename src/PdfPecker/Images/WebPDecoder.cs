namespace PdfPecker.Images;

/// <summary>
/// Reads WebP files: lossy (VP8) and lossless (VP8L) images, alpha planes, and the first frame of animations, as
/// browsers show a WebP that cannot animate.
/// </summary>
internal static class WebPDecoder
{
    public static bool IsWebP(byte[] d)
        => d.Length >= 16 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

    /// <summary>The decoded picture, or null when the data is not a readable WebP image.</summary>
    public static DecodedImage? Decode(byte[] d, long maxPixels)
    {
        if (!IsWebP(d)) return null;
        long riffEnd = 8L + (uint)(d[4] | d[5] << 8 | d[6] << 16 | d[7] << 24);
        int end = (int)Math.Min(d.Length, riffEnd);
        int canvasW = 0, canvasH = 0;
        int pos = 12;
        int alphaPos = -1, alphaLen = 0;
        while (pos + 8 <= end)
        {
            string fourCc = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            long size = (uint)(d[pos + 4] | d[pos + 5] << 8 | d[pos + 6] << 16 | d[pos + 7] << 24);
            int body = pos + 8;
            int len = (int)Math.Min(size, end - body);
            switch (fourCc)
            {
                case "VP8X":
                    if (len >= 10)
                    {
                        canvasW = (d[body + 4] | d[body + 5] << 8 | d[body + 6] << 16) + 1;
                        canvasH = (d[body + 7] | d[body + 8] << 8 | d[body + 9] << 16) + 1;
                        if ((long)canvasW * canvasH > maxPixels) return null;
                    }
                    break;
                case "ALPH":
                    alphaPos = body;
                    alphaLen = len;
                    break;
                case "VP8 ":
                case "VP8L":
                    return DecodeFrame(d, fourCc, body, len, alphaPos, alphaLen, maxPixels);
                case "ANMF":
                    return len >= 16 && canvasW > 0 ? FirstFrame(d, body, len, canvasW, canvasH, maxPixels) : null;
            }
            pos = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
        }
        return null;
    }

    /// <summary>The first frame of an animation, placed on the transparent canvas.</summary>
    private static DecodedImage? FirstFrame(byte[] d, int body, int len, int canvasW, int canvasH, long maxPixels)
    {
        int offsetX = 2 * (d[body] | d[body + 1] << 8 | d[body + 2] << 16);
        int offsetY = 2 * (d[body + 3] | d[body + 4] << 8 | d[body + 5] << 16);
        int end = body + len, pos = body + 16;
        int alphaPos = -1, alphaLen = 0;
        DecodedImage? frame = null;
        while (pos + 8 <= end)
        {
            string fourCc = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            long size = (uint)(d[pos + 4] | d[pos + 5] << 8 | d[pos + 6] << 16 | d[pos + 7] << 24);
            int chunk = pos + 8;
            int chunkLen = (int)Math.Min(size, end - chunk);
            if (fourCc == "ALPH") { alphaPos = chunk; alphaLen = chunkLen; }
            else if (fourCc is "VP8 " or "VP8L")
            {
                frame = DecodeFrame(d, fourCc, chunk, chunkLen, alphaPos, alphaLen, maxPixels);
                break;
            }
            pos = chunk + (int)Math.Min(size + (size & 1), int.MaxValue - chunk);
        }
        if (frame is not { } f) return null;
        if (offsetX == 0 && offsetY == 0 && f.Width == canvasW && f.Height == canvasH) return f;
        // Around the frame the canvas is only transparent pixels, which the header asks for with no data behind them: a
        // huge canvas from a tiny file and a small frame is not allocated, as with GIF screens.
        long canvasPixels = (long)canvasW * canvasH;
        if (canvasPixels > 1 << 22 && canvasPixels > 4L * f.Width * f.Height && canvasPixels > d.Length * 1000L) return null;

        var rgb = new byte[canvasW * canvasH * 3];
        var alpha = new byte[canvasW * canvasH];
        for (int y = 0; y < f.Height && offsetY + y < canvasH; y++)
            for (int x = 0; x < f.Width && offsetX + x < canvasW; x++)
            {
                int s = y * f.Width + x, t = (offsetY + y) * canvasW + offsetX + x;
                rgb[t * 3] = f.Rgb[s * 3];
                rgb[t * 3 + 1] = f.Rgb[s * 3 + 1];
                rgb[t * 3 + 2] = f.Rgb[s * 3 + 2];
                alpha[t] = f.Alpha?[s] ?? 255;
            }
        return new DecodedImage(canvasW, canvasH, rgb, alpha);
    }

    private static DecodedImage? DecodeFrame(byte[] d, string fourCc, int body, int len, int alphaPos, int alphaLen, long maxPixels)
    {
        if (fourCc == "VP8L")
        {
            var argb = Vp8lDecoder.Decode(d, body, len, maxPixels, out int w, out int h, out _);
            if (argb == null) return null;
            var rgb = new byte[w * h * 3];
            var alpha = new byte[w * h];
            for (int i = 0; i < argb.Length; i++)
            {
                uint p = argb[i];
                rgb[i * 3] = (byte)(p >> 16);
                rgb[i * 3 + 1] = (byte)(p >> 8);
                rgb[i * 3 + 2] = (byte)p;
                alpha[i] = (byte)(p >> 24);
            }
            return new DecodedImage(w, h, rgb, alpha);
        }

        var image = Vp8Decoder.Decode(d, body, len, maxPixels);
        if (image is not { } img || alphaPos < 0) return image;
        // A missing or damaged alpha plane leaves the picture opaque.
        var plane = DecodeAlpha(d, alphaPos, alphaLen, img.Width, img.Height, maxPixels);
        return plane == null ? img : img with { Alpha = plane };
    }

    /// <summary>The alpha plane of a lossy image (the ALPH chunk): raw or lossless-coded, then unfiltered.</summary>
    private static byte[]? DecodeAlpha(byte[] d, int pos, int len, int width, int height, long maxPixels)
    {
        if (len <= 1) return null;
        int header = d[pos];
        int method = header & 3, filter = (header >> 2) & 3, reserved = header >> 6;
        if (method > 1 || reserved != 0) return null;
        int count = width * height;
        var values = new byte[count];
        if (method == 0)
        {
            if (len - 1 < count) return null;
            Array.Copy(d, pos + 1, values, 0, count);
        }
        else
        {
            var argb = Vp8lDecoder.DecodeStream(d, pos + 1, len - 1, width, height, maxPixels);
            if (argb == null) return null;
            for (int i = 0; i < count; i++) values[i] = (byte)(argb[i] >> 8); // the green channel
        }
        if (filter != 0) Unfilter(values, width, height, filter);
        return values;
    }

    /// <summary>Undoes the horizontal (1), vertical (2) or gradient (3) prediction of an alpha plane, in place.</summary>
    private static void Unfilter(byte[] a, int width, int height, int filter)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width, prev = row - width;
            if (y == 0 || filter == 1)
            {
                // The first row predicts from the left; with the horizontal filter, later rows start from above.
                byte pred = y == 0 ? (byte)0 : a[prev];
                for (int x = 0; x < width; x++) pred = a[row + x] = (byte)(pred + a[row + x]);
            }
            else if (filter == 2)
            {
                for (int x = 0; x < width; x++) a[row + x] = (byte)(a[prev + x] + a[row + x]);
            }
            else
            {
                int left = a[prev], topLeft = a[prev];
                for (int x = 0; x < width; x++)
                {
                    int top = a[prev + x];
                    int g = left + top - topLeft;
                    left = a[row + x] = (byte)(a[row + x] + (g < 0 ? 0 : g > 255 ? 255 : g));
                    topLeft = top;
                }
            }
        }
    }
}
