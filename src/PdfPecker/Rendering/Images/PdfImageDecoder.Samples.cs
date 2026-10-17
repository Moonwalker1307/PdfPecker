using System.Runtime.CompilerServices;
using PdfPecker.Rendering.Content;

namespace PdfPecker.Rendering.Images;

internal static partial class PdfImageDecoder
{
    private const uint OpaqueAlpha = 0xFF000000;

    private static uint Rgb(int r, int g, int b) => (uint)(r | g << 8 | b << 16) | OpaqueAlpha;

    /// <summary>
    /// The sample at bit <paramref name="bitPos"/>: a byte, a big-endian 16-bit value, or a field of 1, 2 or 4 bits.
    /// Samples past the end of the data are 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sample(byte[] data, long bitPos, int bpc, int maxValue)
    {
        long index = bitPos >> 3;
        return bpc switch
        {
            8 => index < data.Length ? data[index] : 0,
            16 => index + 1 < data.Length ? data[index] << 8 | data[index + 1] : 0,
            _ => index < data.Length ? (data[index] >> (8 - bpc - (int)(bitPos & 7))) & maxValue : 0,
        };
    }

    /// <summary>A stencil mask as coverage: 255 where it paints. Rows past the end of the data do not paint.</summary>
    private static byte[] UnpackMask(byte[] data, int w, int h, bool invert)
    {
        var mask = new byte[w * h];
        int rowBytes = (w + 7) / 8;
        for (int y = 0; y < h; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int index = row + (x >> 3);
                int bit = index < data.Length ? data[index] >> (7 - (x & 7)) & 1 : 1;
                mask[y * w + x] = (bit == 0) != invert ? (byte)255 : (byte)0;
            }
        }
        return mask;
    }

    /// <summary>Converts samples to opaque RGBA pixels. Each row starts on a byte boundary; missing samples are 0.</summary>
    private static void ConvertToRgb(byte[] data, int w, int h, int bpc, int comps, ColorSpace cs, double[]? decode, uint[] pixels)
    {
        int rowBytes = (w * comps * bpc + 7) / 8;
        var defaultDecode = cs.DefaultDecode(bpc);
        bool isDefault = decode == null || decode.Length < 2 * comps || IsDefault(decode, defaultDecode);
        if (isDefault) decode = defaultDecode;

        if (bpc == 8 && isDefault && cs is DeviceRgb) ConvertRgb8(data, w, h, rowBytes, pixels);
        else if (bpc == 8 && isDefault && cs is DeviceGray) ConvertGray8(data, w, h, rowBytes, pixels);
        else if (bpc == 8 && cs is DeviceCmyk && comps == 4) ConvertCmyk8(data, w, h, rowBytes, inverted: decode![0] > decode[1], pixels);
        else if (cs is CalSpace cal && comps == 3 && bpc <= 8) ConvertCalRgb(data, w, h, rowBytes, bpc, cal, decode!, pixels);
        else if (comps == 1 && bpc <= 8) ConvertWithLookup(data, w, h, rowBytes, bpc, cs, decode!, pixels);
        else ConvertGeneral(data, w, h, rowBytes, bpc, comps, cs, decode!, pixels);
    }

    private static bool IsDefault(double[] d, double[] defaultDecode)
    {
        for (int i = 0; i < defaultDecode.Length && i < d.Length; i++)
            if (Math.Abs(d[i] - defaultDecode[i]) > 1e-6) return false;
        return true;
    }

    private static void ConvertRgb8(byte[] data, int w, int h, int rowBytes, uint[] pixels)
    {
        for (int y = 0; y < h; y++)
        {
            int s = y * rowBytes, o = y * w;
            int complete = Math.Min(w, Math.Max(0, (data.Length - s) / 3));
            for (int x = 0; x < complete; x++, s += 3) pixels[o + x] = Rgb(data[s], data[s + 1], data[s + 2]);
            pixels.AsSpan(o + complete, w - complete).Fill(OpaqueAlpha);
        }
    }

    private static void ConvertGray8(byte[] data, int w, int h, int rowBytes, uint[] pixels)
    {
        for (int y = 0; y < h; y++)
        {
            int s = y * rowBytes, o = y * w;
            for (int x = 0; x < w; x++)
            {
                int g = s + x < data.Length ? data[s + x] : 0;
                pixels[o + x] = Rgb(g, g, g);
            }
        }
    }

    private static void ConvertCmyk8(byte[] data, int w, int h, int rowBytes, bool inverted, uint[] pixels)
    {
        for (int y = 0; y < h; y++)
        {
            int s = y * rowBytes, o = y * w;
            for (int x = 0; x < w; x++, s += 4)
            {
                if (s + 3 >= data.Length)
                {
                    pixels[o + x] = OpaqueAlpha;
                    continue;
                }
                float c = data[s] / 255f, m = data[s + 1] / 255f, yy = data[s + 2] / 255f, k = data[s + 3] / 255f;
                if (inverted) { c = 1 - c; m = 1 - m; yy = 1 - yy; k = 1 - k; }
                var (r, g, b) = DeviceCmyk.Convert(c, m, yy, k);
                pixels[o + x] = Rgb(r, g, b);
            }
        }
    }

    /// <summary>
    /// CalRGB: gamma tables per component over all sample values, a matrix multiply and a table for the sRGB curve,
    /// so a large image costs no power functions per pixel.
    /// </summary>
    private static void ConvertCalRgb(byte[] data, int w, int h, int rowBytes, int bpc, CalSpace cal, double[] decode, uint[] pixels)
    {
        int maxValue = (1 << bpc) - 1;
        var linear = new double[3][];
        for (int i = 0; i < 3; i++)
        {
            linear[i] = new double[maxValue + 1];
            for (int v = 0; v <= maxValue; v++) linear[i][v] = cal.Linear(decode[2 * i] + v * (decode[2 * i + 1] - decode[2 * i]) / maxValue, i);
        }
        var m = cal.ToLinearSrgb;
        var encode = CalSpace.EncodeTable;
        static int Index(double v) => v <= 0 ? 0 : v >= 1 ? 4095 : (int)(v * 4095 + 0.5);
        for (int y = 0; y < h; y++)
        {
            long bitPos = (long)y * rowBytes * 8;
            int o = y * w;
            for (int x = 0; x < w; x++, bitPos += 3 * bpc)
            {
                double a = linear[0][Sample(data, bitPos, bpc, maxValue)];
                double b = linear[1][Sample(data, bitPos + bpc, bpc, maxValue)];
                double c = linear[2][Sample(data, bitPos + 2 * bpc, bpc, maxValue)];
                pixels[o + x] = Rgb(encode[Index(m[0] * a + m[1] * b + m[2] * c)], encode[Index(m[3] * a + m[4] * b + m[5] * c)],
                    encode[Index(m[6] * a + m[7] * b + m[8] * c)]);
            }
        }
    }

    /// <summary>Single-component spaces (gray, indexed, separation, ICC gray): a lookup table over all sample values.</summary>
    private static void ConvertWithLookup(byte[] data, int w, int h, int rowBytes, int bpc, ColorSpace cs, double[] decode, uint[] pixels)
    {
        int maxValue = (1 << bpc) - 1;
        var lookup = new uint[maxValue + 1];
        Span<float> component = stackalloc float[1];
        if (cs is SeparationSpace separation) separation.BuildLut();
        for (int v = 0; v <= maxValue; v++)
        {
            component[0] = (float)(decode[0] + v * (decode[1] - decode[0]) / maxValue);
            lookup[v] = cs.ToRgb(component) | OpaqueAlpha;
        }
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            if (bpc == 8)
            {
                int s = y * rowBytes;
                for (int x = 0; x < w; x++) pixels[o + x] = s + x < data.Length ? lookup[data[s + x]] : lookup[0];
                continue;
            }
            long bitPos = (long)y * rowBytes * 8;
            for (int x = 0; x < w; x++, bitPos += bpc) pixels[o + x] = lookup[Sample(data, bitPos, bpc, maxValue)];
        }
    }

    /// <summary>Any other space and depth: each sample through the Decode array, then the colour space's conversion.</summary>
    private static void ConvertGeneral(byte[] data, int w, int h, int rowBytes, int bpc, int comps, ColorSpace cs, double[] decode, uint[] pixels)
    {
        int maxValue = (1 << bpc) - 1;
        Span<float> components = stackalloc float[Math.Max(1, comps)];
        var scale = new double[comps];
        for (int i = 0; i < comps; i++) scale[i] = (decode[2 * i + 1] - decode[2 * i]) / (bpc == 16 ? 65535.0 : maxValue);
        for (int y = 0; y < h; y++)
        {
            long bitPos = (long)y * rowBytes * 8;
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                for (int i = 0; i < comps; i++, bitPos += bpc)
                    components[i] = (float)(decode[2 * i] + Sample(data, bitPos, bpc, maxValue) * scale[i]);
                pixels[o + x] = cs.ToRgb(components) | OpaqueAlpha;
            }
        }
    }

    /// <summary>
    /// Makes the pixels whose samples all lie in the colour key's ranges transparent. Returns whether any did.
    /// </summary>
    private static bool ApplyColorKey(byte[] data, int w, int h, int bpc, int comps, double[] key, uint[] pixels)
    {
        if (key.Length < 2 * comps) return false;
        int rowBytes = (w * comps * bpc + 7) / 8;
        int maxValue = (1 << bpc) - 1;
        bool any = false;
        for (int y = 0; y < h; y++)
        {
            long bitPos = (long)y * rowBytes * 8;
            for (int x = 0; x < w; x++)
            {
                bool masked = true;
                for (int i = 0; i < comps; i++, bitPos += bpc)
                {
                    int v = Sample(data, bitPos, bpc, maxValue);
                    if (v < key[2 * i] || v > key[2 * i + 1]) masked = false;
                }
                if (masked)
                {
                    pixels[y * w + x] = 0;
                    any = true;
                }
            }
        }
        return any;
    }
}
