namespace PdfPecker.Images;

/// <summary>
/// Decodes the first frame of a GIF image (GIF87a or GIF89a) to RGB samples and an alpha channel, as browsers show a
/// GIF that cannot animate.
/// </summary>
internal static class GifDecoder
{
    public static bool IsGif(byte[] d)
        => d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a';

    /// <summary>The first frame on the logical screen, or null when the data is not a readable GIF.</summary>
    public static DecodedImage? Decode(byte[] d, long maxPixels)
    {
        if (!IsGif(d) || d.Length < 13) return null;
        int screenW = d[6] | d[7] << 8, screenH = d[8] | d[9] << 8;
        int flags = d[10];
        int pos = 13;
        byte[]? globalPalette = null;
        if ((flags & 0x80) != 0)
        {
            int size = 3 << ((flags & 7) + 1);
            if (pos + size > d.Length) return null;
            globalPalette = d.AsSpan(pos, size).ToArray();
            pos += size;
        }

        int transparent = -1;
        while (pos < d.Length)
        {
            byte block = d[pos++];
            if (block == 0x3B) return null; // trailer before any image
            if (block == 0x21)
            {
                if (pos >= d.Length) return null;
                byte label = d[pos++];
                if (label == 0xF9 && pos + 5 <= d.Length && d[pos] >= 4)
                    transparent = (d[pos + 1] & 1) != 0 ? d[pos + 4] : -1; // graphic control extension
                pos = SkipSubBlocks(d, pos);
                continue;
            }
            if (block != 0x2C || pos + 9 > d.Length) return null;

            int left = d[pos] | d[pos + 1] << 8, top = d[pos + 2] | d[pos + 3] << 8;
            int w = d[pos + 4] | d[pos + 5] << 8, h = d[pos + 6] | d[pos + 7] << 8;
            int imageFlags = d[pos + 8];
            pos += 9;
            byte[]? palette = globalPalette;
            if ((imageFlags & 0x80) != 0)
            {
                int size = 3 << ((imageFlags & 7) + 1);
                if (pos + size > d.Length) return null;
                palette = d.AsSpan(pos, size).ToArray();
                pos += size;
            }
            if (palette == null || w == 0 || h == 0 || pos >= d.Length) return null;
            bool interlaced = (imageFlags & 0x40) != 0;

            // The canvas is the logical screen, grown to hold the frame when a writer left the screen size out.
            int canvasW = Math.Max(screenW, left + w), canvasH = Math.Max(screenH, top + h);
            if ((long)canvasW * canvasH > maxPixels || (long)w * h > maxPixels) return null;

            int minCodeSize = d[pos++];
            if (minCodeSize is < 1 or > 11) return null;
            // n codes of LZW data make at most about n * n / 2 pixels (4096 per code once the table is full): a few
            // bytes cannot fill a huge canvas, so it is not allocated for them.
            long codes = DataLength(d, pos) * 8L / (minCodeSize + 1);
            long producible = codes <= 4096 ? codes * (codes + 3) / 2 : 4096 * codes;
            long canvasPixels = (long)canvasW * canvasH;
            if (canvasPixels > 1 << 22 && canvasPixels > 4 * producible) return null;
            var indices = new byte[w * h];
            int decoded = Lzw(d, pos, minCodeSize, indices);
            if (decoded == 0) return null; // a frame with no pixels at all is damaged; a short one shows what it has

            var rgb = new byte[canvasW * canvasH * 3];
            var alpha = new byte[canvasW * canvasH]; // outside the frame and on the transparent index: see-through
            int colors = palette.Length / 3;
            for (int i = 0; i < decoded; i++)
            {
                int row = i / w, x = i % w;
                int y = interlaced ? InterlacedRow(row, h) : row;
                int index = indices[i];
                if (index == transparent || index >= colors) continue;
                int p = (top + y) * canvasW + left + x;
                rgb[p * 3] = palette[index * 3];
                rgb[p * 3 + 1] = palette[index * 3 + 1];
                rgb[p * 3 + 2] = palette[index * 3 + 2];
                alpha[p] = 255;
            }
            return new DecodedImage(canvasW, canvasH, rgb, alpha);
        }
        return null;
    }

    /// <summary>The number of data bytes in the sub-blocks starting at <paramref name="pos"/>.</summary>
    private static long DataLength(byte[] d, int pos)
    {
        long total = 0;
        while (pos < d.Length)
        {
            int len = d[pos++];
            if (len == 0) break;
            total += Math.Min(len, d.Length - pos);
            pos += len;
        }
        return total;
    }

    private static int SkipSubBlocks(byte[] d, int pos)
    {
        while (pos < d.Length)
        {
            int len = d[pos++];
            if (len == 0) break;
            pos += len;
        }
        return pos;
    }

    /// <summary>The image row stored at position <paramref name="row"/> of an interlaced frame (passes of every 8th, 8th, 4th and 2nd row).</summary>
    private static int InterlacedRow(int row, int h)
    {
        int pass1 = (h + 7) / 8, pass2 = (h + 3) / 8, pass3 = (h + 1) / 4;
        if (row < pass1) return row * 8;
        row -= pass1;
        if (row < pass2) return row * 8 + 4;
        row -= pass2;
        if (row < pass3) return row * 4 + 2;
        return (row - pass3) * 2 + 1;
    }

    /// <summary>Decodes the LZW data in the sub-blocks starting at <paramref name="pos"/> into <paramref name="output"/>; returns the number of pixels decoded.</summary>
    private static int Lzw(byte[] d, int pos, int minCodeSize, byte[] output)
    {
        const int MaxCodes = 4096;
        var prefix = new short[MaxCodes];
        var suffix = new byte[MaxCodes];
        var first = new byte[MaxCodes]; // first pixel of each code's string
        var stack = new byte[MaxCodes + 1];
        int clear = 1 << minCodeSize, end = clear + 1;
        for (int i = 0; i < clear; i++) { suffix[i] = (byte)i; first[i] = (byte)i; prefix[i] = -1; }

        int codeSize = minCodeSize + 1, next = clear + 2, previous = -1;
        int bits = 0, bitCount = 0, blockLeft = 0, outPos = 0;
        while (outPos < output.Length)
        {
            // Fill the bit buffer from the sub-blocks.
            while (bitCount < codeSize)
            {
                if (blockLeft == 0)
                {
                    if (pos >= d.Length) return outPos;
                    blockLeft = d[pos++];
                    if (blockLeft == 0) return outPos; // end of the image data
                }
                if (pos >= d.Length) return outPos;
                bits |= d[pos++] << bitCount;
                bitCount += 8;
                blockLeft--;
            }
            int code = bits & ((1 << codeSize) - 1);
            bits >>= codeSize;
            bitCount -= codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = clear + 2;
                previous = -1;
                continue;
            }
            if (code == end) return outPos;
            if (previous < 0)
            {
                if (code >= clear) return outPos; // the first code after a clear must be a pixel
                output[outPos++] = (byte)code;
                previous = code;
                continue;
            }

            int sp = 0, c = code;
            byte firstPixel;
            if (code < next)
            {
                firstPixel = first[code];
            }
            else if (code == next)
            {
                // The code being defined: the previous string plus its own first pixel.
                firstPixel = first[previous];
                stack[sp++] = firstPixel;
                c = previous;
            }
            else return outPos; // a code not defined yet: damaged data

            while (c >= 0 && sp < stack.Length)
            {
                stack[sp++] = suffix[c];
                c = prefix[c];
            }
            while (sp > 0 && outPos < output.Length) output[outPos++] = stack[--sp];

            if (next < MaxCodes)
            {
                prefix[next] = (short)previous;
                suffix[next] = firstPixel;
                first[next] = first[previous];
                next++;
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
            }
            previous = code;
        }
        return outPos;
    }
}

/// <summary>Decoded pixels: 3 bytes of RGB per pixel, and an optional alpha byte per pixel.</summary>
internal readonly record struct DecodedImage(int Width, int Height, byte[] Rgb, byte[]? Alpha);
