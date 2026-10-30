using System.IO.Compression;
using PdfPecker.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

internal sealed partial class PdfCompactWriter
{
    /// <summary>The largest image whose samples are also tried deflated without a predictor.</summary>
    private const int PlainDeflateLimit = 1 << 20;

    /// <summary>The filters that decode without loss, which a stream can drop and replace with one stronger Flate.</summary>
    private static bool IsLosslessFilter(string name) => name is "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "RunLengthDecode";

    /// <summary>
    /// The smallest form of a stream: as it is, its data with the lossless filters undone and compressed again with the
    /// strongest Flate level (with a PNG predictor for images when that helps), or, with the image settings, the image
    /// downsampled or recompressed as JPEG. An image codec (JPEG, JPEG 2000, JBIG2, CCITT) is kept; only lossless
    /// filters around it can go. Streams that cannot be decoded, that decode to more than the limits allow, or that live
    /// in external files are kept as they are, as is XMP metadata, which PDF/A wants readable without decoding.
    /// </summary>
    private PdfStream Recompress(int key, PdfStream s, Dictionary<int, double>? placements)
    {
        if (s.ContainsKey("F") || s.ContainsKey("FFilter") || s.ContainsKey("FDecodeParms") || _file.GetName(s, "Type") == "Metadata") return s;
        var chain = FilterNames(s);
        if (chain == null) return s;
        int imageFilters = chain.Count(Rendering.Parsing.Filters.IsImageFilter);
        if (imageFilters > 1 || (imageFilters == 1 && !Rendering.Parsing.Filters.IsImageFilter(chain[^1]))) return s;

        byte[] data;
        string? imageFilter;
        PdfDict? imageParms;
        long limit = Math.Min(Rendering.Parsing.Filters.MaxDecodedSize, PdfFile.DecodeLimit);
        try
        {
            data = _file.DecodeStream(s, stopAtImageFilter: true, out imageFilter, out imageParms);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return s;
        }
        // The decoders stop at their limit rather than fail, so data that reaches it may be cut short.
        if (data.Length >= limit) return s;

        PdfStream best = s;
        if (imageFilter == null)
        {
            // Predictors almost always win for images, so a large one is not also deflated without, which takes as long.
            var layout = PredictorLayout(s) is { } l && data.Length == (long)l.RowBytes * l.Height ? l : default;
            bool predict = layout.Width > 0;
            if (predict)
                Consider(ref best, WithData(s, Deflate(PngPredict(data, layout.RowBytes, layout.BytesPerPixel)), PdfName.Get("FlateDecode"), PredictorParms(layout)));
            if (!predict || data.Length <= PlainDeflateLimit) Consider(ref best, WithData(s, Deflate(data), PdfName.Get("FlateDecode"), null));
        }
        else
        {
            var codec = PdfName.Get(imageFilter);
            if (chain.Count > 1) Consider(ref best, WithData(s, data, codec, imageParms));
            var parms = imageParms != null ? new PdfArray(new List<PdfObject> { PdfNull.Instance, imageParms }) : null;
            Consider(ref best, WithData(s, Deflate(data), new PdfArray(new List<PdfObject> { PdfName.Get("FlateDecode"), codec }), parms));
        }
        if (_options.ImageQuality != null || placements != null)
        {
            var image = RecompressImage(key, s, placements);
            if (image != null) Consider(ref best, image);
        }
        return best;
    }

    private static void Consider(ref PdfStream best, PdfStream candidate)
    {
        if (candidate.RawData.Length < best.RawData.Length) best = candidate;
    }

    /// <summary>The stream's filters in decoding order, or null when one is not a filter this library can undo.</summary>
    private List<string>? FilterNames(PdfStream s)
    {
        var names = new List<string>();
        switch (_file.Resolve(s["Filter"]))
        {
            case null or PdfNull:
                break;
            case PdfName name:
                names.Add(Rendering.Parsing.Filters.Canonical(name.Value));
                break;
            case PdfArray array:
                foreach (var item in array.Items)
                {
                    if (_file.Resolve(item) is not PdfName n) return null;
                    names.Add(Rendering.Parsing.Filters.Canonical(n.Value));
                }
                break;
            default:
                return null;
        }
        foreach (var name in names)
            if (!IsLosslessFilter(name) && !Rendering.Parsing.Filters.IsImageFilter(name)) return null;
        return names;
    }

    /// <summary>The stream with new data and filters, and without the parameters and decoded length of the old ones.</summary>
    private static PdfStream WithData(PdfStream s, byte[] data, PdfObject filter, PdfObject? parms)
    {
        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("DecodeParms");
        entries["Filter"] = filter;
        if (parms != null) entries["DecodeParms"] = parms;
        return new PdfStream(entries, data);
    }

    /// <summary>Deflates with the strongest level .NET offers, in the zlib format PDF's FlateDecode expects.</summary>
    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>How the samples of an uncompressed image are laid out, for a PNG predictor.</summary>
    private readonly record struct SampleLayout(int Width, int Height, int Colors, int BitsPerComponent)
    {
        public int RowBytes => (int)(((long)Width * Colors * BitsPerComponent + 7) / 8);

        public int BytesPerPixel => Math.Max(1, Colors * BitsPerComponent / 8);
    }

    /// <summary>
    /// The sample layout of an image of 8 or 16 bits per component whose colour space has a known number of components,
    /// or null for anything else (stencil masks, small bit depths, unknown colour spaces).
    /// </summary>
    private SampleLayout? PredictorLayout(PdfStream s)
    {
        if (_file.GetName(s, "Subtype") != "Image" || _file.GetBool(s, "ImageMask", false)) return null;
        int width = _file.GetInt(s, "Width", 0), height = _file.GetInt(s, "Height", 0), bits = _file.GetInt(s, "BitsPerComponent", 0);
        if (width <= 0 || height <= 0 || bits is not (8 or 16)) return null;
        int colors = ColorComponents(s["ColorSpace"]);
        if (colors <= 0 || (long)width * colors * bits / 8 > int.MaxValue / 2) return null;
        return new SampleLayout(width, height, colors, bits);
    }

    /// <summary>The number of components of a colour space, or 0 when it is unknown.</summary>
    private int ColorComponents(PdfObject? colorSpace)
    {
        switch (_file.Resolve(colorSpace))
        {
            case PdfName n:
                return n.Value switch
                {
                    "DeviceGray" or "CalGray" => 1,
                    "DeviceRGB" or "CalRGB" or "Lab" => 3,
                    "DeviceCMYK" => 4,
                    _ => 0,
                };
            case PdfArray { Count: > 0 } a when _file.Resolve(a[0]) is PdfName family:
                return family.Value switch
                {
                    "DeviceGray" or "CalGray" or "Indexed" or "Separation" => 1,
                    "DeviceRGB" or "CalRGB" or "Lab" => 3,
                    "DeviceCMYK" => 4,
                    "ICCBased" when a.Count > 1 && _file.Resolve(a[1]) is PdfStream profile => _file.GetInt(profile, "N", 0) is var n and (1 or 3 or 4) ? n : 0,
                    "DeviceN" when a.Count > 1 && _file.Resolve(a[1]) is PdfArray names => names.Count is > 0 and <= 32 ? names.Count : 0,
                    _ => 0,
                };
            default:
                return 0;
        }
    }

    private static PdfDict PredictorParms(SampleLayout layout) => new(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
    {
        ["Predictor"] = PdfNumber.Int(15),
        ["Colors"] = PdfNumber.Int(layout.Colors),
        ["BitsPerComponent"] = PdfNumber.Int(layout.BitsPerComponent),
        ["Columns"] = PdfNumber.Int(layout.Width),
    });

    /// <summary>
    /// The rows with a PNG predictor each, as PDF's predictor 15 allows: whichever of None, Sub, Up, Average and Paeth
    /// leaves the smallest sum of absolute differences, the usual heuristic.
    /// </summary>
    private static byte[] PngPredict(byte[] data, int rowBytes, int bpp)
    {
        int rows = data.Length / rowBytes;
        var result = new byte[rows * (rowBytes + 1)];
        var candidates = new byte[5][];
        for (int t = 0; t < candidates.Length; t++) candidates[t] = new byte[rowBytes];
        var empty = new byte[rowBytes];
        for (int r = 0; r < rows; r++)
        {
            PdfFile.CheckCancel();
            ReadOnlySpan<byte> row = data.AsSpan(r * rowBytes, rowBytes);
            ReadOnlySpan<byte> above = r > 0 ? data.AsSpan((r - 1) * rowBytes, rowBytes) : empty;
            row.CopyTo(candidates[0]);
            Span<byte> sub = candidates[1], up = candidates[2], average = candidates[3], paeth = candidates[4];
            for (int i = 0; i < rowBytes; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0, upValue = above[i], upLeft = i >= bpp ? above[i - bpp] : 0;
                byte x = row[i];
                sub[i] = (byte)(x - left);
                up[i] = (byte)(x - upValue);
                average[i] = (byte)(x - ((left + upValue) >> 1));
                paeth[i] = (byte)(x - PngFilters.PaethPredictor(left, upValue, upLeft));
            }
            int best = 0;
            long bestSum = long.MaxValue;
            for (int t = 0; t < candidates.Length; t++)
            {
                long sum = 0;
                foreach (byte v in candidates[t]) sum += v < 128 ? v : 256 - v;
                if (sum < bestSum) (best, bestSum) = (t, sum);
            }
            result[r * (rowBytes + 1)] = (byte)best;
            candidates[best].CopyTo(result, r * (rowBytes + 1) + 1);
        }
        return result;
    }
}
