using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>Decodes image XObjects and inline images: all bit depths, color spaces, Decode arrays, masks and soft masks.</summary>
internal static partial class PdfImageDecoder
{
    public const long MaxPixels = 100_000_000;

    /// <summary>
    /// The samples of an image once its image filter (if any) has been undone: their size and layout may come from the
    /// coded data rather than the image dictionary.
    /// </summary>
    private struct Samples
    {
        public byte[] Data;
        public int Width, Height, BitsPerComponent, Components;
        public ColorSpace? ColorSpace; // null only for a stencil mask
        public double[]? Decode;
        public byte[]? JpxAlpha; // the alpha channel of a JPEG 2000 image with SMaskInData
    }

    public static DecodedImage? Decode(PdfFile file, PdfDict dict, byte[] rawOrDecoded, bool alreadyDecoded, PdfDict? resources,
        bool inline, uint fillRgbForStencil = 0)
    {
        int width = GetInt(file, dict, "Width", "W");
        int height = GetInt(file, dict, "Height", "H");
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;
        bool isMask = GetBool(file, dict, "ImageMask", "IM");
        var samples = new Samples
        {
            Width = width,
            Height = height,
            BitsPerComponent = isMask ? 1 : GetInt(file, dict, "BitsPerComponent", "BPC"),
            Decode = file.ToNumbers(file.Resolve(dict["Decode"] ?? dict["D"]) as PdfArray),
        };

        string? imageFilter = null;
        PdfDict? imageParms = null;
        if (alreadyDecoded) samples.Data = rawOrDecoded;
        else if (dict is PdfStream s) samples.Data = file.DecodeStream(s, stopAtImageFilter: true, out imageFilter, out imageParms);
        else samples.Data = InlineDecode(file, dict, rawOrDecoded, out imageFilter, out imageParms);

        if (!isMask && (dict["ColorSpace"] ?? dict["CS"]) is { } csObj) samples.ColorSpace = ColorSpace.Parse(file, csObj, resources);

        bool decoded = imageFilter switch
        {
            null => UseRawSamples(ref samples, isMask),
            "DCTDecode" => DecodeJpeg(ref samples),
            "CCITTFaxDecode" => DecodeFax(ref samples, file, imageParms),
            "JPXDecode" => DecodeJpx(ref samples, file, dict),
            "JBIG2Decode" => DecodeJbig2(ref samples, file, imageParms, isMask),
            _ => false,
        };
        if (!decoded) return null;
        width = samples.Width;
        height = samples.Height;

        var img = new DecodedImage
        {
            Width = width,
            Height = height,
            Interpolate = GetBool(file, dict, "Interpolate", "I"),
        };

        if (isMask)
        {
            // 0 paints unless the Decode array is [1 0].
            bool invert = samples.Decode is { Length: >= 2 } d && d[0] > d[1];
            img.Stencil = UnpackMask(samples.Data, width, height, invert);
            return img;
        }

        var pixels = new uint[width * height];
        ConvertToRgb(samples.Data, width, height, samples.BitsPerComponent, samples.Components, samples.ColorSpace!, samples.Decode, pixels);
        img.Pixels = pixels;
        img.Opaque = true;
        if (inline) return img;

        if (samples.JpxAlpha != null && file.Resolve(dict["SMask"]) is not PdfStream)
        {
            ApplyAlpha(pixels, samples.JpxAlpha);
            img.Opaque = false;
            return img;
        }
        if (dict is PdfStream) ApplyMasks(file, dict, resources, img, samples, imageFilter == null);
        return img;
    }

    private static int GetInt(PdfFile file, PdfDict d, string key, string abbr) => file.GetIntValue(d[key] ?? d[abbr]);

    private static bool GetBool(PdfFile file, PdfDict d, string key, string abbr) => file.Resolve(d[key] ?? d[abbr]) is PdfBool { Value: true };

    /// <summary>Unfiltered samples, laid out as the dictionary says; an unusable bit depth is read as 8.</summary>
    private static bool UseRawSamples(ref Samples samples, bool isMask)
    {
        if (samples.BitsPerComponent is not (1 or 2 or 4 or 8 or 16)) samples.BitsPerComponent = 8;
        samples.ColorSpace ??= isMask ? null : ColorSpace.Gray;
        samples.Components = samples.ColorSpace?.Components ?? 1;
        if (samples.Components <= 0) samples.Components = 1;
        return true;
    }

    /// <summary>
    /// A JPEG decides its own size and component count; its declared colour space is used when it has as many
    /// components, else the device space that has.
    /// </summary>
    private static bool DecodeJpeg(ref Samples samples)
    {
        if (JpegDecoder.Decode(samples.Data) is not { } jpeg) return false;
        var (data, width, height, components) = jpeg;
        samples.Data = data;
        samples.Width = width;
        samples.Height = height;
        samples.Components = components;
        samples.BitsPerComponent = 8;
        if (samples.ColorSpace == null || samples.ColorSpace.Components != components) samples.ColorSpace = DeviceSpace(components);
        return true;
    }

    private static ColorSpace DeviceSpace(int components) => components switch { 1 => ColorSpace.Gray, 4 => ColorSpace.Cmyk, _ => ColorSpace.RgbSpace };

    private static bool DecodeFax(ref Samples samples, PdfFile file, PdfDict? parms)
    {
        if (CcittFaxDecoder.Decode(samples.Data, parms, file, samples.Width, samples.Height) is not { } bits) return false;
        SetBilevel(ref samples, bits);
        samples.ColorSpace ??= ColorSpace.Gray;
        return true;
    }

    private static bool DecodeJbig2(ref Samples samples, PdfFile file, PdfDict? parms, bool isMask)
    {
        var globals = file.GetStream(parms, "JBIG2Globals");
        var bits = Jbig2Decoder.Decode(samples.Data, globals != null ? file.DecodeStream(globals) : null, samples.Width, samples.Height);
        if (bits == null) return false;
        SetBilevel(ref samples, bits);
        samples.ColorSpace ??= isMask ? null : ColorSpace.Gray;
        return true;
    }

    private static void SetBilevel(ref Samples samples, byte[] bits)
    {
        samples.Data = bits;
        samples.BitsPerComponent = 1;
        samples.Components = 1;
    }

    /// <summary>
    /// A JPEG 2000 image decides its own size, components and colour space, and its Decode array is ignored
    /// (ISO 32000-1, 8.9.5.3). Its alpha channel is kept when SMaskInData asks for it.
    /// </summary>
    private static bool DecodeJpx(ref Samples samples, PdfFile file, PdfDict dict)
    {
        var jpx = JpxDecoder.Decode(samples.Data);
        if (jpx == null) return false;
        samples.Width = jpx.Width;
        samples.Height = jpx.Height;
        samples.Data = jpx.Data;
        samples.Components = jpx.Components;
        samples.BitsPerComponent = 8;
        samples.Decode = null;
        if (samples.ColorSpace == null || samples.ColorSpace.Components != jpx.Components)
            samples.ColorSpace = jpx.Color switch { JpxColor.Gray => ColorSpace.Gray, JpxColor.Cmyk => ColorSpace.Cmyk, _ => ColorSpace.RgbSpace };
        if (samples.ColorSpace.Components != jpx.Components) return false;
        if (file.GetInt(dict, "SMaskInData", 0) != 0) samples.JpxAlpha = jpx.Alpha;
        return true;
    }

    /// <summary>Undoes the filters of an inline image up to its image filter, which is left for the caller.</summary>
    private static byte[] InlineDecode(PdfFile file, PdfDict dict, byte[] data, out string? imageFilter, out PdfDict? imageParms)
    {
        imageFilter = null;
        imageParms = null;
        var filter = file.Resolve(dict["Filter"] ?? dict["F"]);
        var parms = file.Resolve(dict["DecodeParms"] ?? dict["DP"]);
        var filters = new List<(string Name, PdfDict? Parms)>();
        if (filter is PdfName name) filters.Add((Filters.Canonical(name.Value), parms as PdfDict));
        else if (filter is PdfArray names)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (file.Resolve(names[i]) is not PdfName n) continue;
                filters.Add((Filters.Canonical(n.Value), parms is PdfArray parmsArray && i < parmsArray.Count ? file.Resolve(parmsArray[i]) as PdfDict : null));
            }
        }
        foreach (var (n, p) in filters)
        {
            if (Filters.IsImageFilter(n))
            {
                imageFilter = n;
                imageParms = p;
                return data;
            }
            data = Filters.Decode(n, data, p, file);
        }
        return data;
    }
}
