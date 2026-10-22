using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

internal static partial class PdfImageDecoder
{
    /// <summary>
    /// Applies the image's soft mask (alpha), explicit stencil mask, or colour-key mask, in that order of precedence.
    /// A colour key compares coded samples, so it applies only to an image without an image filter.
    /// </summary>
    private static void ApplyMasks(PdfFile file, PdfDict dict, PdfDict? resources, DecodedImage img, in Samples samples, bool unfiltered)
    {
        var pixels = img.Pixels!;
        if (file.Resolve(dict["SMask"]) is PdfStream smask)
        {
            // A mask finer than its image keeps its resolution, as the outline it draws is usually the point of it.
            var (w, h) = MaskedSize(img, file.GetInt(smask, "Width", 0), file.GetInt(smask, "Height", 0));
            var alpha = DecodeAlpha(file, smask, w, h, resources);
            if (alpha != null)
            {
                pixels = Enlarge(img, w, h);
                var cs = samples.ColorSpace!;
                if (file.GetNumbers(smask, "Matte") is { } matte && matte.Length >= cs.Components) UnblendMatte(pixels, alpha, cs, matte);
                ApplyAlpha(pixels, alpha);
            }
            img.Opaque = false;
            return;
        }
        var mask = file.Resolve(dict["Mask"]);
        if (mask is PdfStream maskStream)
        {
            // The mask is decoded like a soft mask, without its own masks: a valid one is an image mask, which has
            // none, and following them let a mask that leads back to the image recurse until the stack overflowed.
            var stencil = Decode(file, maskStream, Array.Empty<byte>(), false, resources, true);
            if (stencil?.Stencil != null)
            {
                // An explicit mask's samples mean what they mean in a stencil mask (ISO 32000-1, 8.9.6.3): where
                // the stencil would paint, the base image shows, so the stencil coverage is the image's alpha.
                var (w, h) = MaskedSize(img, stencil.Width, stencil.Height);
                pixels = Enlarge(img, w, h);
                ApplyAlpha(pixels, Resample(stencil.Stencil, stencil.Width, stencil.Height, w, h));
                img.Opaque = false;
            }
        }
        else if (mask is PdfArray keyRanges && unfiltered)
        {
            var key = file.ToNumbers(keyRanges)!;
            if (ApplyColorKey(samples.Data, img.Width, img.Height, samples.BitsPerComponent, samples.Components, key, pixels)) img.Opaque = false;
        }
    }

    /// <summary>
    /// The size a masked image is drawn at: the mask's when it is finer in both directions (such as a 1000x800 outline
    /// over a 50x40 gradient), so its edges are not cut to the image's pixels; otherwise the image's.
    /// </summary>
    private static (int Width, int Height) MaskedSize(DecodedImage img, int maskWidth, int maskHeight)
        => maskWidth >= img.Width && maskHeight >= img.Height && (maskWidth > img.Width || maskHeight > img.Height)
            && (long)maskWidth * maskHeight <= MaxPixels
            ? (maskWidth, maskHeight)
            : (img.Width, img.Height);

    /// <summary>Scales the image's pixels up to the given size by nearest sample, when it is larger, and returns them.</summary>
    private static uint[] Enlarge(DecodedImage img, int w, int h)
    {
        if (w == img.Width && h == img.Height) return img.Pixels!;
        var src = img.Pixels!;
        int sw = img.Width, sh = img.Height;
        var dst = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = Math.Min(sh - 1, (int)((y + 0.5) * sh / h)) * sw;
            for (int x = 0; x < w; x++) dst[y * w + x] = src[row + Math.Min(sw - 1, (int)((x + 0.5) * sw / w))];
        }
        img.Pixels = dst;
        img.Width = w;
        img.Height = h;
        return dst;
    }

    /// <summary>Decodes a soft mask image to alpha values at the given size.</summary>
    private static byte[]? DecodeAlpha(PdfFile file, PdfStream smask, int w, int h, PdfDict? resources)
    {
        int sw = file.GetInt(smask, "Width", 0), sh = file.GetInt(smask, "Height", 0);
        if (sw <= 0 || sh <= 0 || (long)sw * sh > MaxPixels) return null;
        var img = Decode(file, smask, Array.Empty<byte>(), false, resources, true);
        if (img?.Pixels is not { } pixels) return null;
        var gray = new byte[img.Width * img.Height];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(pixels[i] & 0xFF); // gray images: R = G = B
        return img.Width == w && img.Height == h ? gray : Resample(gray, img.Width, img.Height, w, h);
    }

    /// <summary>Scales a plane to another size by taking the nearest sample to each pixel's centre.</summary>
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

    /// <summary>
    /// Undoes the pre-blending a /Matte entry declares: the samples were stored as m + alpha * (c - m), so compositing
    /// them as they are would mix in the matte colour twice (ISO 32000-1, 11.6.5.3). The matte is converted to RGB and
    /// un-blended there, which is exact for the device spaces and close enough for the others.
    /// </summary>
    private static void UnblendMatte(uint[] pixels, byte[] alpha, ColorSpace cs, double[] matte)
    {
        Span<float> components = stackalloc float[cs.Components];
        for (int i = 0; i < components.Length; i++) components[i] = (float)matte[i];
        uint m = cs.ToRgb(components);
        int mr = (int)(m & 0xFF), mg = (int)(m >> 8 & 0xFF), mb = (int)(m >> 16 & 0xFF);
        int n = Math.Min(pixels.Length, alpha.Length);
        for (int i = 0; i < n; i++)
        {
            int a = alpha[i];
            // Fully transparent samples carry no colour to recover, and opaque ones are not blended at all.
            if (a == 0 || a == 255) continue;
            uint p = pixels[i];
            int r = Unblend((int)(p & 0xFF), mr, a), g = Unblend((int)(p >> 8 & 0xFF), mg, a), b = Unblend((int)(p >> 16 & 0xFF), mb, a);
            pixels[i] = (p & 0xFF000000) | (uint)(r | g << 8 | b << 16);
        }
    }

    private static int Unblend(int c, int m, int a) => Math.Clamp(m + ((c - m) * 255 + (c >= m ? a / 2 : -a / 2)) / a, 0, 255);

    /// <summary>Premultiplies opaque pixels by an alpha plane.</summary>
    private static void ApplyAlpha(uint[] pixels, byte[] alpha)
    {
        int n = Math.Min(pixels.Length, alpha.Length);
        for (int i = 0; i < n; i++)
        {
            int a = alpha[i];
            if (a == 255) continue;
            if (a == 0)
            {
                pixels[i] = 0;
                continue;
            }
            uint scaled = Canvas.Scale(pixels[i] & 0x00FFFFFF, (uint)(a + (a >> 7)));
            pixels[i] = (scaled & 0x00FFFFFF) | (uint)a << 24;
        }
    }
}
