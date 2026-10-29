using System.Globalization;
using PdfPecker.Images;

namespace PdfPecker.Css;

/// <summary>
/// image-set() and -webkit-image-set(): a list of images, each with a resolution and an optional type, from which
/// Chrome takes one. A printed page has one device pixel per CSS pixel, so the choice is the first option, in order of
/// resolution, of 1x or more, else the option of the highest resolution. An image drawn at 2x shows at half its pixel
/// size.
/// </summary>
internal static class CssImageSet
{
    /// <summary>The options read from one image-set, at most.</summary>
    private const int MaxOptions = 64;

    /// <summary>The range of resolutions an option may have; outside it the image-set is invalid.</summary>
    private const float MinResolution = 1e-6f, MaxResolution = 1e6f;

    /// <summary>Whether a value is an image-set() or -webkit-image-set() function.</summary>
    public static bool IsImageSet(string value)
    {
        var t = value.AsSpan().TrimStart();
        return t.StartsWith("image-set(", StringComparison.OrdinalIgnoreCase) || t.StartsWith("-webkit-image-set(", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The option an image-set takes: its image as a url() or gradient function, and its resolution in image pixels per
    /// CSS pixel. Null when the value is not a valid image-set, or no option has a type PdfPecker decodes.
    /// </summary>
    public static (string Image, float Resolution)? Choose(string value)
    {
        var t = value.Trim();
        if (!IsImageSet(t) || !t.EndsWith(')')) return null;
        int open = t.IndexOf('(');
        var options = CssParser.SplitTopLevel(t.Substring(open + 1, t.Length - open - 2), ',');
        if (options.Count == 0 || options.Count > MaxOptions) return null;
        string? best = null;
        float bestResolution = 0;
        bool bestReachesTarget = false;
        foreach (var option in options)
        {
            if (Option(option) is not { } o) return null;
            if (!o.Decoded) continue;
            // The first option of the lowest resolution of 1x or more wins; without one, the first of the highest.
            bool reaches = o.Resolution >= 1;
            bool better = best == null
                          || reaches && (!bestReachesTarget || o.Resolution < bestResolution)
                          || !reaches && !bestReachesTarget && o.Resolution > bestResolution;
            if (!better) continue;
            (best, bestResolution, bestReachesTarget) = (o.Image, o.Resolution, reaches);
        }
        return best == null ? null : (best, bestResolution);
    }

    /// <summary>One option: an image (a url(), a string or a gradient), then a resolution and a type in either order.</summary>
    private static (string Image, float Resolution, bool Decoded)? Option(string option)
    {
        var tokens = CssParser.Tokens(option);
        if (tokens.Count is 0 or > 3) return null;
        var first = tokens[0];
        string image;
        if (first[0] is '"' or '\'')
        {
            if (first.Length < 2 || first[^1] != first[0]) return null;
            image = "url(" + first + ")";
        }
        else if (CssBackground.Url(first) != null || first.Contains("gradient(", StringComparison.OrdinalIgnoreCase)) image = first;
        else return null;
        float resolution = float.NaN;
        bool decoded = true, typed = false;
        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("type(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(')'))
            {
                var type = token.AsSpan(5, token.Length - 6).Trim();
                if (typed || type.Length < 2 || type[0] is not ('"' or '\'') || type[^1] != type[0]) return null;
                typed = true;
                decoded = ResponsiveImages.IsDecodedType(type[1..^1].ToString());
            }
            else if (!float.IsNaN(resolution) || Resolution(token) is not { } r) return null;
            else resolution = r;
        }
        return (image, float.IsNaN(resolution) ? 1 : resolution, decoded);
    }

    /// <summary>A resolution in dppx: x, dppx, dpi or dpcm; null when the token is none or out of range.</summary>
    private static float? Resolution(string token)
    {
        var lower = token.ToLowerInvariant();
        (string unit, float scale) = lower.EndsWith("dppx", StringComparison.Ordinal) ? ("dppx", 1f)
            : lower.EndsWith("dpcm", StringComparison.Ordinal) ? ("dpcm", 2.54f / 96)
            : lower.EndsWith("dpi", StringComparison.Ordinal) ? ("dpi", 1f / 96)
            : lower.EndsWith('x') ? ("x", 1f)
            : ("", 0f);
        if (unit.Length == 0
            || !float.TryParse(lower.AsSpan(0, lower.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return null;
        float r = n * scale;
        return r >= MinResolution && r <= MaxResolution ? r : null;
    }
}
