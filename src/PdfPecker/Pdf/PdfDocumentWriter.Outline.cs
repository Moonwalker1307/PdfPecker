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

    private static PdfLimitException TooManyPages(int max)
        => new($"The document would have more than {max:N0} pages (PdfOptions.MaxPages).");

    /// <summary>Nests the headings by level: a heading becomes a child of the nearest earlier heading of a higher level.</summary>
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

    /// <summary>Writes the outline dictionary and its items, each going to its heading.</summary>
    private void WriteOutline(WriteState doc)
    {
        var items = doc.OutlineItems;
        var top = items.Where(o => o.Parent == null).ToList();
        Begin(doc.OutlinesId).Ascii("<< /Type /Outlines /First ").Int(top[0].Id).Ascii(" 0 R /Last ").Int(top[^1].Id)
            .Ascii(" 0 R /Count ").Int(items.Count).Ascii(" >>");
        End();
        foreach (var item in items)
        {
            Begin(item.Id).Ascii("<< /Title ");
            WriteTextString(_out, item.Title);
            _out.Ascii(" /Parent ").Int(item.Parent?.Id ?? doc.OutlinesId).Ascii(" 0 R");
            if (item.Prev != null) _out.Ascii(" /Prev ").Int(item.Prev.Id).Ascii(" 0 R");
            if (item.Next != null) _out.Ascii(" /Next ").Int(item.Next.Id).Ascii(" 0 R");
            if (item.Children.Count > 0)
                _out.Ascii(" /First ").Int(item.Children[0].Id).Ascii(" 0 R /Last ").Int(item.Children[^1].Id)
                    .Ascii(" 0 R /Count ").Int(item.Descendants);
            _out.Sp();
            WriteGoTo(item.Y, item.Heading is { } h ? Structure?.ElementOf(h) : null);
            _out.Ascii(" >>");
            End();
        }
    }
}
