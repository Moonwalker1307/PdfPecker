using PdfPecker.Html;

namespace PdfPecker.Images;

/// <summary>
/// Gives an img that has alt text and no picture its alt text as content, as browsers print it: the text stands where
/// the picture would have been. An img without alt text, or with an empty one, keeps its empty box.
/// </summary>
internal static class MissingImageText
{
    /// <summary>Prepares every img under <paramref name="root"/> whose source <paramref name="images"/> cannot load.</summary>
    public static void Prepare(Element root, ImageLoader images)
    {
        foreach (var c in root.Children)
        {
            if (c is not Element e) continue;
            if (e.Name == "img")
            {
                // Looking at the alt text first spares loading the picture of the many images that have none.
                if (e.GetAttribute("alt") is not { } alt || string.IsNullOrWhiteSpace(alt) || images.LoadAny(e.GetAttribute("src")) != null) continue;
                e.AltText = alt;
                e.AppendChild(new TextNode(alt));
            }
            else Prepare(e, images);
        }
    }
}
