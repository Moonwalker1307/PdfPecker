using PdfPecker.Html;

namespace PdfPecker.Images;

/// <summary>
/// Shows the picture an embed or object element points at, as Chrome prints it: an embed's src or an object's data
/// that loads as a PNG, JPEG, SVG or other picture makes the element an img of that picture, and an object that shows
/// one no longer shows its fallback content. Other resources (a PDF, a plugin) are not loaded: an embed of one stays
/// empty and an object of one shows its fallback content.
/// </summary>
internal static class EmbeddedPictures
{
    /// <summary>Prepares every embed and object element under <paramref name="root"/>.</summary>
    public static void Prepare(Element root, ImageLoader images)
    {
        foreach (var c in root.Children)
        {
            if (c is not Element e) continue;
            if (e.Name is "embed" or "object" && Picture(e, images) is { } url)
            {
                e.EmbedsPicture = true;
                images.Chosen[e] = (url, 1);
                // The picture replaces the fallback content, which may hold further objects of its own.
                if (e.Name == "object") e.Children.Clear();
                continue;
            }
            Prepare(e, images);
        }
    }

    /// <summary>The URL of the picture the element shows, or null when it has none or its type says it is no picture.</summary>
    private static string? Picture(Element e, ImageLoader images)
    {
        var url = e.GetAttribute(e.Name == "embed" ? "src" : "data");
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (e.GetAttribute("type") is { } type && !type.TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        return images.LoadAny(url) != null ? url : null;
    }
}
