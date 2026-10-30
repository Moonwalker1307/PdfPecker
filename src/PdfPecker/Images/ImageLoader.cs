using System.Buffers;
namespace PdfPecker.Images;

/// <summary>
/// Resolves image sources (data URIs, files, or a user callback) and caches decoded images per document: raster images
/// as <see cref="PdfImage"/>, SVG files as vector drawings.
/// </summary>
internal sealed class ImageLoader
{
    // A query or fragment ends the file part of an image path.
    private static readonly SearchValues<char> QueryOrFragment = SearchValues.Create("?#");

    private readonly string? _baseDirectory;
    private readonly Func<string, byte[]?>? _resolver;
    private readonly bool _allowFiles; // files outside the base directory too
    private readonly Dictionary<string, object?> _cache = new(StringComparer.Ordinal); // PdfImage, SvgImage or null
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal); // SVG files being parsed, which may embed images

    /// <summary>Parsed inline SVG drawings, shared by every layout pass of a document (pages, headers, footers).</summary>
    public readonly Dictionary<PdfPecker.Html.Element, PdfPecker.Svg.SvgImage> Svgs = new();

    /// <summary>
    /// The pictures that srcset or a picture element chose for img elements, with their pixel densities (see
    /// <see cref="ResponsiveImages"/>). An img not listed shows its src at 1x.
    /// </summary>
    public readonly Dictionary<PdfPecker.Html.Element, (string Url, float Density)> Chosen = new();

    /// <summary>The source an img element shows.</summary>
    public string? SourceOf(PdfPecker.Html.Element img) => Chosen.TryGetValue(img, out var c) ? c.Url : img.GetAttribute("src");

    /// <summary>The image pixels per CSS pixel of the picture an img element shows: 2 for a 2x srcset candidate.</summary>
    public float DensityOf(PdfPecker.Html.Element img) => Chosen.TryGetValue(img, out var c) ? c.Density : img.GeneratedResolution;

    /// <summary>The drawing work left for the document's inline SVGs.</summary>
    public readonly PdfPecker.Svg.SvgBudget SvgBudget = new();

    /// <summary>The document's fonts, for the text in SVG images.</summary>
    public PdfPecker.Fonts.FontResolver? Fonts;

    public ImageLoader(string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles = false)
    {
        _allowFiles = allowFiles;
        _baseDirectory = baseDirectory;
        _resolver = resolver;
    }

    private readonly Dictionary<(PdfImage Image, string Rects), PdfImage?[]?> _slices = new();

    /// <summary>
    /// The rectangles of <paramref name="image"/> cut out as images of their own (see <see cref="PdfImage.Cut"/>), cut
    /// once per document, so that boxes with the same border image share the slices' image objects.
    /// </summary>
    public PdfImage?[]? Slices(PdfImage image, (int X, int Y, int W, int H)[] rects)
    {
        var key = (image, string.Join(";", rects));
        if (!_slices.TryGetValue(key, out var slices)) _slices[key] = slices = image.Cut(rects);
        return slices;
    }

    /// <summary>
    /// The raster image at <paramref name="src"/>, or null when there is none or it is an SVG file. A photo is turned as
    /// its EXIF orientation says unless <paramref name="asStored"/> (image-orientation: none).
    /// </summary>
    public PdfImage? Load(string? src, bool asStored = false)
        => LoadAny(src) is PdfImage image ? asStored ? image.AsStored ?? image : image : null;

    /// <summary>The SVG drawing at <paramref name="src"/>, or null when there is none or it is a raster image.</summary>
    public PdfPecker.Svg.SvgImage? LoadSvg(string? src) => LoadAny(src) as PdfPecker.Svg.SvgImage;

    /// <summary>The raster image or SVG drawing at <paramref name="src"/>, for the image elements of inline SVG.</summary>
    public object? LoadAny(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        src = src!.Trim();
        if (_cache.TryGetValue(src, out var cached)) return cached;
        if (!_loading.Add(src)) return null; // an SVG file that embeds itself
        object? img = null;
        try
        {
            var bytes = Fetch(src);
            if (bytes != null)
                img = DecodedImageCache.Decode(bytes) ?? (object?)PdfPecker.Svg.SvgImage.FromFile(bytes, Fonts, SvgBudget, LoadEmbedded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException
                                       or InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            SwallowedErrors.Record(ex);
            img = null; // a missing or damaged image must not fail the whole document
        }
        finally { _loading.Remove(src); }
        _cache[src] = img;
        return img;
    }

    /// <summary>
    /// An image embedded in an SVG file. Browsers load only data URIs for an SVG shown as an image, so a file's
    /// drawing looks the same wherever it is used.
    /// </summary>
    public object? LoadEmbedded(string src)
        => src.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? LoadAny(src) : null;

    /// <summary>Parses SVG file data (such as a watermark) with the document's fonts; its images may be data URIs.</summary>
    public PdfPecker.Svg.SvgImage? ParseSvg(byte[] data) => PdfPecker.Svg.SvgImage.FromFile(data, Fonts, SvgBudget, LoadEmbedded);

    /// <summary>
    /// The bytes of a data URI, base64 (which may be percent-encoded too) or percent-encoded; null without a comma. Bad
    /// base64 throws <see cref="FormatException"/>.
    /// </summary>
    internal static byte[]? DecodeDataUri(string src)
    {
        int comma = src.IndexOf(',');
        if (comma < 0) return null;
        var meta = src.Substring(5, comma - 5);
        var payload = src.Substring(comma + 1);
        if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            // Browsers take percent-encoded base64 too, such as %2F for '/' in Acid2's images.
            if (payload.Contains('%')) payload = Uri.UnescapeDataString(payload);
            return Convert.FromBase64String(payload.Replace("\n", "").Replace("\r", "").Replace(" ", ""));
        }
        return System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }

    /// <summary>The bytes of a resource (data URI, user resolver or local file), or null.</summary>
    public byte[]? Fetch(string src)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return DecodeDataUri(src);

        // A user resolver gets the first chance at everything else (http, custom schemes, embedded resources).
        if (_resolver != null)
        {
            var bytes = _resolver(src);
            if (bytes != null) return bytes;
        }

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null; // no network access unless the caller provides a resolver
        string path = src;
        if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(src, UriKind.Absolute, out var uri))
            path = uri.LocalPath;
        else
        {
            int q = path.AsSpan().IndexOfAny(QueryOrFragment);
            if (q >= 0) path = path.Substring(0, q);
            path = Uri.UnescapeDataString(path);
        }
        if (LocalFiles.Resolve(path, _baseDirectory, _allowFiles) is not { } file) return null;
        return LocalFiles.Read(file);
    }
}
