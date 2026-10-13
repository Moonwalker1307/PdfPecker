namespace PdfPecker.Images;

/// <summary>
/// Resolves image sources (data URIs, files, or a user callback) and caches decoded images per document: raster images
/// as <see cref="PdfImage"/>, SVG files as vector drawings.
/// </summary>
internal sealed class ImageLoader
{
    private readonly string? _baseDirectory;
    private readonly Func<string, byte[]?>? _resolver;
    private readonly bool _allowFiles; // files outside the base directory too
    private readonly Dictionary<string, object?> _cache = new(StringComparer.Ordinal); // PdfImage, SvgImage or null

    /// <summary>Parsed inline SVG drawings, shared by every layout pass of a document (pages, headers, footers).</summary>
    public readonly Dictionary<PdfPecker.Html.Element, PdfPecker.Svg.SvgImage> Svgs = new();

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

    /// <summary>The raster image at <paramref name="src"/>, or null when there is none or it is an SVG file.</summary>
    public PdfImage? Load(string? src) => LoadAny(src) as PdfImage;

    /// <summary>The SVG drawing at <paramref name="src"/>, or null when there is none or it is a raster image.</summary>
    public PdfPecker.Svg.SvgImage? LoadSvg(string? src) => LoadAny(src) as PdfPecker.Svg.SvgImage;

    private object? LoadAny(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        src = src!.Trim();
        if (_cache.TryGetValue(src, out var cached)) return cached;
        object? img = null;
        try
        {
            var bytes = Fetch(src);
            if (bytes != null)
                img = PdfImage.FromBytes(bytes) ?? (object?)PdfPecker.Svg.SvgImage.FromFile(bytes, Fonts, SvgBudget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException
                                       or InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            img = null; // a missing or damaged image must not fail the whole document
        }
        _cache[src] = img;
        return img;
    }

    /// <summary>The bytes of a resource (data URI, user resolver or local file), or null.</summary>
    public byte[]? Fetch(string src)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = src.IndexOf(',');
            if (comma < 0) return null;
            var meta = src.Substring(5, comma - 5);
            var payload = src.Substring(comma + 1);
            if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(payload.Replace("\n", "").Replace("\r", "").Replace(" ", ""));
            return System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }

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
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            path = Uri.UnescapeDataString(path);
        }
        if (LocalFiles.Resolve(path, _baseDirectory, _allowFiles) is not { } file) return null;
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }
}
