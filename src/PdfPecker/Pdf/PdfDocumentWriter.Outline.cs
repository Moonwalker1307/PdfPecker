using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>The bookmarks (document outline) built from the headings.</summary>
internal sealed partial class PdfDocumentWriter
{
    private sealed class OutlineItem
    {
        public string Title = "";
        public int Level, Id, Descendants;
        public float Y;
        public Html.Element? Heading;
        public OutlineItem? Parent, Prev, Next;
        public readonly List<OutlineItem> Children = new();
    }

    /// <summary>Nests the headings by level: a heading becomes a child of the nearest earlier heading of a higher level.</summary>
    private static PdfLimitException TooManyPages(int max)
        => new($"The document would have more than {max:N0} pages (PdfOptions.MaxPages).");

    private static List<OutlineItem> BuildOutline(IReadOnlyList<OutlineHeading>? headings, IReadOnlyDictionary<string, float> anchors)
    {
        var all = new List<OutlineItem>();
        if (headings == null) return all;
        var stack = new List<OutlineItem>();
        OutlineItem? lastTop = null;
        foreach (var h in headings)
        {
            if (!anchors.TryGetValue(h.Key, out float y)) continue;
            var item = new OutlineItem { Title = h.Title, Level = h.Level, Y = y, Heading = h.Element };
            while (stack.Count > 0 && stack[^1].Level >= h.Level) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0)
            {
                var parent = stack[^1];
                item.Parent = parent;
                if (parent.Children.Count > 0) { item.Prev = parent.Children[^1]; item.Prev.Next = item; }
                parent.Children.Add(item);
                for (var p = parent; p != null; p = p.Parent) p.Descendants++;
            }
            else
            {
                if (lastTop != null) { item.Prev = lastTop; lastTop.Next = item; }
                lastTop = item;
            }
            stack.Add(item);
            all.Add(item);
        }
        return all;
    }
}
