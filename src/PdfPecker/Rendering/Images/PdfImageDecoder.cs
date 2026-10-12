using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>A decoded raster image: premultiplied RGBA pixels, or (for stencil masks) coverage only.</summary>
internal sealed class DecodedImage
{
    public int Width, Height;
    public uint[]? Pixels;   // premultiplied RGBA, row 0 = top
    public byte[]? Stencil;  // ImageMask: 255 = paint with the fill color
    public bool Interpolate;
    public bool Opaque;

    private DecodedImage? _half;

    /// <summary>A copy at half resolution (box filter), for drawing images much smaller than their pixel size.</summary>
    public DecodedImage Half()
    {
        if (_half != null) return _half;
        int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
        var r = new DecodedImage { Width = w, Height = h, Interpolate = Interpolate, Opaque = Opaque };
        if (Pixels != null)
        {
            var src = Pixels;
            var dst = new uint[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Min(Height - 1, y * 2), y1 = Math.Min(Height - 1, y * 2 + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(Width - 1, x * 2), x1 = Math.Min(Width - 1, x * 2 + 1);
                    uint a = src[y0 * Width + x0], b = src[y0 * Width + x1], c = src[y1 * Width + x0], d = src[y1 * Width + x1];
                    dst[y * w + x] = Avg4(a, b, c, d);
                }
            }
            r.Pixels = dst;
        }
        if (Stencil != null)
        {
            var src = Stencil;
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Min(Height - 1, y * 2), y1 = Math.Min(Height - 1, y * 2 + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(Width - 1, x * 2), x1 = Math.Min(Width - 1, x * 2 + 1);
                    dst[y * w + x] = (byte)((src[y0 * Width + x0] + src[y0 * Width + x1] + src[y1 * Width + x0] + src[y1 * Width + x1] + 2) >> 2);
                }
            }
            r.Stencil = dst;
        }
        Interlocked.CompareExchange(ref _half, r, null);
        return _half;
    }

    private static uint Avg4(uint a, uint b, uint c, uint d)
    {
        uint rb = ((a & 0x00FF00FF) + (b & 0x00FF00FF) + (c & 0x00FF00FF) + (d & 0x00FF00FF) + 0x00020002) >> 2 & 0x00FF00FF;
        uint ag = (((a >> 8) & 0x00FF00FF) + ((b >> 8) & 0x00FF00FF) + ((c >> 8) & 0x00FF00FF) + ((d >> 8) & 0x00FF00FF) + 0x00020002) >> 2 & 0x00FF00FF;
        return rb | ag << 8;
    }

    public long ByteSize => (Pixels?.LongLength ?? 0) * 4 + (Stencil?.LongLength ?? 0);
}

/// <summary>Decodes image XObjects and inline images: all bit depths, color spaces, Decode arrays, masks and soft masks.</summary>
internal static class PdfImageDecoder
{
    public const long MaxPixels = 100_000_000;

    public static DecodedImage? Decode(PdfFile file, PdfDict dict, byte[] rawOrDecoded, bool alreadyDecoded, PdfDict? resources,
        bool inline, uint fillRgbForStencil = 0)
    {
        int width = GetInt(file, dict, "Width", "W");
        int height = GetInt(file, dict, "Height", "H");
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;
        bool isMask = GetBool(file, dict, "ImageMask", "IM");
        int bpc = isMask ? 1 : GetInt(file, dict, "BitsPerComponent", "BPC");
        var decodeArr = file.ToNumbers(file.Resolve(dict["Decode"] ?? dict["D"]) as PdfArray);

        byte[] data;
        string? imageFilter = null;
        PdfDict? imageParms = null;
        if (alreadyDecoded) data = rawOrDecoded;
        else if (dict is PdfStream s) data = file.DecodeStream(s, stopAtImageFilter: true, out imageFilter, out imageParms);
        else data = InlineDecode(file, dict, rawOrDecoded, out imageFilter, out imageParms);

        ColorSpace? cs = null;
        if (!isMask)
        {
            var csObj = dict["ColorSpace"] ?? dict["CS"];
            cs = csObj != null ? ColorSpace.Parse(file, csObj, resources) : null;
        }

        int comps;
        byte[]? jpxAlpha = null;
        if (imageFilter == "DCTDecode")
        {
            var jpg = JpegDecoder.Decode(data);
            if (jpg == null) return null;
            var (jd, jw, jh, jc) = jpg.Value;
            data = jd;
            if (jw != width || jh != height)
            {
                // Trust the JPEG's own dimensions.
                width = jw;
                height = jh;
            }
            comps = jc;
            bpc = 8;
            if (cs == null || cs.Components != comps) cs = comps switch { 1 => ColorSpace.Gray, 4 => ColorSpace.Cmyk, _ => ColorSpace.RgbSpace };
        }
        else if (imageFilter == "CCITTFaxDecode")
        {
            var fax = CcittFaxDecoder.Decode(data, imageParms, file, width, height);
            if (fax == null) return null;
            data = fax;
            bpc = 1;
            comps = 1;
            cs ??= ColorSpace.Gray;
        }
        else if (imageFilter == "JPXDecode")
        {
            var jpx = JpxDecoder.Decode(data);
            if (jpx == null) return null;
            width = jpx.Width;
            height = jpx.Height;
            data = jpx.Data;
            comps = jpx.Components;
            bpc = 8;
            decodeArr = null; // ignored for JPEG 2000 images
            if (cs == null || cs.Components != comps)
                cs = jpx.Color switch { JpxColor.Gray => ColorSpace.Gray, JpxColor.Cmyk => ColorSpace.Cmyk, _ => ColorSpace.RgbSpace };
            if (cs.Components != comps) return null;
            if (file.GetInt(dict, "SMaskInData", 0) != 0) jpxAlpha = jpx.Alpha;
        }
        else if (imageFilter == "JBIG2Decode")
        {
            var globals = file.GetStream(imageParms, "JBIG2Globals");
            var bits = Jbig2Decoder.Decode(data, globals != null ? file.DecodeStream(globals) : null, width, height);
            if (bits == null) return null;
            data = bits;
            bpc = 1;
            comps = 1;
            cs ??= isMask ? null : ColorSpace.Gray;
        }
        else if (imageFilter != null)
        {
            return null;
        }
        else
        {
            if (bpc is not (1 or 2 or 4 or 8 or 16)) bpc = 8;
            cs ??= isMask ? null : ColorSpace.Gray;
            comps = cs?.Components ?? 1;
            if (comps <= 0) comps = 1;
        }

        var img = new DecodedImage
        {
            Width = width,
            Height = height,
            Interpolate = GetBool(file, dict, "Interpolate", "I"),
        };

        if (isMask)
        {
            // 0 paints unless the Decode array is [1 0].
            bool invert = decodeArr is { Length: >= 2 } && decodeArr[0] > decodeArr[1];
            img.Stencil = UnpackMask(data, width, height, invert);
            return img;
        }

        var pixels = new uint[width * height];
        ConvertToRgb(data, width, height, bpc, comps, cs!, decodeArr, pixels);
        img.Pixels = pixels;
        img.Opaque = true;

        if (inline) return img;

        if (jpxAlpha != null && file.Resolve(dict["SMask"]) is not PdfStream)
        {
            ApplyAlpha(pixels, jpxAlpha);
            img.Opaque = false;
            return img;
        }

        // Soft mask (alpha), explicit stencil mask, or color-key mask.
        if (dict is PdfStream)
        {
            var smask = file.Resolve(dict["SMask"]) as PdfStream;
            if (smask != null)
            {
                var alpha = DecodeAlpha(file, smask, width, height, resources);
                if (alpha != null) ApplyAlpha(pixels, alpha);
                img.Opaque = false;
            }
            else
            {
                var mask = file.Resolve(dict["Mask"]);
                if (mask is PdfStream ms)
                {
                    var m = Decode(file, ms, Array.Empty<byte>(), false, resources, false);
                    if (m?.Stencil != null)
                    {
                        var alpha = Resample(m.Stencil, m.Width, m.Height, width, height);
                        // In an explicit mask, 1 (stencil "paint") marks pixels that are masked out.
                        for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)(255 - alpha[i]);
                        ApplyAlpha(pixels, alpha);
                        img.Opaque = false;
                    }
                }
                else if (mask is PdfArray keyArr && imageFilter == null)
                {
                    var key = file.ToNumbers(keyArr)!;
                    if (ApplyColorKey(data, width, height, bpc, comps, key, pixels)) img.Opaque = false;
                }
            }
        }
        return img;
    }

    private static int GetInt(PdfFile file, PdfDict d, string key, string abbr) => file.GetIntValue(d[key] ?? d[abbr]);

    private static bool GetBool(PdfFile file, PdfDict d, string key, string abbr) => file.Resolve(d[key] ?? d[abbr]) is PdfBool { Value: true };

    private static byte[] InlineDecode(PdfFile file, PdfDict dict, byte[] data, out string? imageFilter, out PdfDict? imageParms)
    {
        imageFilter = null;
        imageParms = null;
        var f = file.Resolve(dict["Filter"] ?? dict["F"]);
        var p = file.Resolve(dict["DecodeParms"] ?? dict["DP"]);
        var filters = new List<string>();
        var parms = new List<PdfDict?>();
        if (f is PdfName n) { filters.Add(Filters.Canonical(n.Value)); parms.Add(p as PdfDict); }
        else if (f is PdfArray fa)
            for (int i = 0; i < fa.Count; i++)
                if (file.Resolve(fa[i]) is PdfName fn)
                {
                    filters.Add(Filters.Canonical(fn.Value));
                    parms.Add(p is PdfArray pa && i < pa.Count ? file.Resolve(pa[i]) as PdfDict : null);
                }
        for (int i = 0; i < filters.Count; i++)
        {
            if (Filters.IsImageFilter(filters[i])) { imageFilter = filters[i]; imageParms = parms[i]; return data; }
            data = Filters.Decode(filters[i], data, parms[i], file);
        }
        return data;
    }

    private static byte[] UnpackMask(byte[] data, int w, int h, bool invert)
    {
        var m = new byte[w * h];
        int rowBytes = (w + 7) / 8;
        for (int y = 0; y < h; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int idx = row + (x >> 3);
                int bit = idx < data.Length ? data[idx] >> (7 - (x & 7)) & 1 : 1;
                m[y * w + x] = (bit == 0) != invert ? (byte)255 : (byte)0;
            }
        }
        return m;
    }

    /// <summary>Converts samples to opaque RGBA pixels.</summary>
    private static void ConvertToRgb(byte[] data, int w, int h, int bpc, int comps, ColorSpace cs, double[]? decode, uint[] pixels)
    {
        int rowBytes = (w * comps * bpc + 7) / 8;
        int maxV = (1 << bpc) - 1;
        var dflt = cs.DefaultDecode(bpc);
        bool defaultDecode = decode == null || decode.Length < 2 * comps || IsDefault(decode, dflt);
        if (defaultDecode) decode = dflt;

        // Fast paths for 8-bit device spaces with the default Decode.
        if (bpc == 8 && defaultDecode && cs is DeviceRgb)
        {
            for (int y = 0; y < h; y++)
            {
                int s = y * rowBytes, o = y * w;
                int n = Math.Min(w, Math.Max(0, (data.Length - s) / 3));
                for (int x = 0; x < n; x++, s += 3) pixels[o + x] = (uint)(data[s] | data[s + 1] << 8 | data[s + 2] << 16) | 0xFF000000;
                for (int x = n; x < w; x++) pixels[o + x] = 0xFF000000;
            }
            return;
        }
        if (bpc == 8 && defaultDecode && cs is DeviceGray)
        {
            for (int y = 0; y < h; y++)
            {
                int s = y * rowBytes, o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int g = s + x < data.Length ? data[s + x] : 0;
                    pixels[o + x] = (uint)(g | g << 8 | g << 16) | 0xFF000000;
                }
            }
            return;
        }
        if (bpc == 8 && cs is DeviceCmyk && comps == 4)
        {
            bool inv = decode![0] > decode[1];
            for (int y = 0; y < h; y++)
            {
                int s = y * rowBytes, o = y * w;
                for (int x = 0; x < w; x++, s += 4)
                {
                    if (s + 3 >= data.Length) { pixels[o + x] = 0xFF000000; continue; }
                    float c = data[s] / 255f, m = data[s + 1] / 255f, yy = data[s + 2] / 255f, k = data[s + 3] / 255f;
                    if (inv) { c = 1 - c; m = 1 - m; yy = 1 - yy; k = 1 - k; }
                    var (r, g, b) = DeviceCmyk.Convert(c, m, yy, k);
                    pixels[o + x] = (uint)(r | g << 8 | b << 16) | 0xFF000000;
                }
            }
            return;
        }

        // Single-component spaces (gray, indexed, separation, ICC gray): a lookup table over all sample values.
        if (comps == 1 && bpc <= 8)
        {
            var lut = new uint[maxV + 1];
            Span<float> c1 = stackalloc float[1];
            if (cs is SeparationSpace sep) sep.BuildLut();
            for (int v = 0; v <= maxV; v++)
            {
                double dv = decode![0] + v * (decode[1] - decode[0]) / maxV;
                c1[0] = (float)dv;
                lut[v] = cs.ToRgb(c1) | 0xFF000000;
            }
            for (int y = 0; y < h; y++)
            {
                int bitPos = y * rowBytes * 8, o = y * w;
                if (bpc == 8)
                {
                    int s = y * rowBytes;
                    for (int x = 0; x < w; x++) pixels[o + x] = s + x < data.Length ? lut[data[s + x]] : lut[0];
                    continue;
                }
                for (int x = 0; x < w; x++, bitPos += bpc)
                {
                    int byteIdx = bitPos >> 3;
                    int v = byteIdx < data.Length ? (data[byteIdx] >> (8 - bpc - (bitPos & 7))) & maxV : 0;
                    pixels[o + x] = lut[v];
                }
            }
            return;
        }

        // General case.
        Span<float> comp = stackalloc float[Math.Max(1, comps)];
        var scale = new double[comps];
        for (int i = 0; i < comps; i++) scale[i] = (decode![2 * i + 1] - decode[2 * i]) / (bpc == 16 ? 65535.0 : maxV);
        for (int y = 0; y < h; y++)
        {
            long bitPos = (long)y * rowBytes * 8;
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                for (int i = 0; i < comps; i++, bitPos += bpc)
                {
                    long byteIdx = bitPos >> 3;
                    int v;
                    if (bpc == 8) v = byteIdx < data.Length ? data[byteIdx] : 0;
                    else if (bpc == 16) v = byteIdx + 1 < data.Length ? data[byteIdx] << 8 | data[byteIdx + 1] : 0;
                    else v = byteIdx < data.Length ? (data[byteIdx] >> (8 - bpc - (int)(bitPos & 7))) & maxV : 0;
                    comp[i] = (float)(decode![2 * i] + v * scale[i]);
                }
                pixels[o + x] = cs.ToRgb(comp) | 0xFF000000;
            }
        }
    }

    private static bool IsDefault(double[] d, double[] dflt)
    {
        for (int i = 0; i < dflt.Length && i < d.Length; i++) if (Math.Abs(d[i] - dflt[i]) > 1e-6) return false;
        return true;
    }

    private static bool ApplyColorKey(byte[] data, int w, int h, int bpc, int comps, double[] key, uint[] pixels)
    {
        if (key.Length < 2 * comps) return false;
        int rowBytes = (w * comps * bpc + 7) / 8;
        int maxV = (1 << bpc) - 1;
        bool any = false;
        for (int y = 0; y < h; y++)
        {
            long bitPos = (long)y * rowBytes * 8;
            for (int x = 0; x < w; x++)
            {
                bool masked = true;
                for (int i = 0; i < comps; i++, bitPos += bpc)
                {
                    long bi = bitPos >> 3;
                    int v = bpc == 8 ? (bi < data.Length ? data[bi] : 0)
                        : bpc == 16 ? (bi + 1 < data.Length ? data[bi] << 8 | data[bi + 1] : 0)
                        : bi < data.Length ? (data[bi] >> (8 - bpc - (int)(bitPos & 7))) & maxV : 0;
                    if (v < key[2 * i] || v > key[2 * i + 1]) masked = false;
                }
                if (masked) { pixels[y * w + x] = 0; any = true; }
            }
        }
        return any;
    }

    /// <summary>Decodes a soft mask image to alpha values at the given size.</summary>
    private static byte[]? DecodeAlpha(PdfFile file, PdfStream smask, int w, int h, PdfDict? resources)
    {
        int sw = file.GetInt(smask, "Width", 0), sh = file.GetInt(smask, "Height", 0);
        if (sw <= 0 || sh <= 0 || (long)sw * sh > MaxPixels) return null;
        var img = Decode(file, smask, Array.Empty<byte>(), false, resources, true);
        if (img?.Pixels == null) return null;
        var gray = new byte[img.Width * img.Height];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(img.Pixels[i] & 0xFF); // gray images: R = G = B
        // /Matte (pre-blended colors) is not un-blended; the effect is minor for typical masks.
        return img.Width == w && img.Height == h ? gray : Resample(gray, img.Width, img.Height, w, h);
    }

    private static byte[] Resample(byte[] src, int sw, int sh, int w, int h)
    {
        if (sw == w && sh == h) return (byte[])src.Clone();
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / h));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(sw - 1, (int)((x + 0.5) * sw / w));
                dst[y * w + x] = src[sy * sw + sx];
            }
        }
        return dst;
    }

    private static void ApplyAlpha(uint[] pixels, byte[] alpha)
    {
        int n = Math.Min(pixels.Length, alpha.Length);
        for (int i = 0; i < n; i++)
        {
            int a = alpha[i];
            if (a == 255) continue;
            uint p = pixels[i];
            if (a == 0) { pixels[i] = 0; continue; }
            uint s = Canvas.Scale(p & 0x00FFFFFF, (uint)(a + (a >> 7)));
            pixels[i] = (s & 0x00FFFFFF) | (uint)a << 24;
        }
    }
}
