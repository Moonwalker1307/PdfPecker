using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>The document an iframe shows, and the style whose background fills its viewport (with its root's style).</summary>
internal sealed record FrameContent(Element Document, ComputedStyle? Canvas, ComputedStyle? Root);

/// <summary>The document of an iframe's srcdoc, laid out in the iframe's content box as the frame's viewport.</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// The document an iframe shows for a viewport of the given width and height in points, its styles resolved and
    /// apart from the document around it; null when it shows none. Set only for documents with iframes.
    /// </summary>
    public Func<Element, float, float, FrameContent?>? FrameDocument;

    /// <summary>
    /// How deep frames are laid out inside frames. A srcdoc can hold an iframe with a srcdoc of its own, each level a
    /// whole document laid out again for each layout of the level around it.
    /// </summary>
    private const int MaxFrameDepth = 4;

    private int _frameDepth;

    /// <summary>
    /// Lays out the iframe's document in its content box (x, y, w, h), clipped to that box. The frame is drawn whole
    /// where it is, as an image would be: its document is not split across pages.
    /// </summary>
    private void DrawFrame(Element e, float x, float y, float w, float h)
    {
        if (_measuring || !(w > 0 && h > 0) || _frameDepth >= MaxFrameDepth || _frameDepth > 0 && !MayLayOutAgain) return;
        if (FrameDocument!(e, w, h) is not { Document: var doc } frame) return;
        Captured content;
        _frameDepth++;
        float lastBottom = _lastContentBottom;
        var (outerCb, outerKind) = (_cbHeight, _cbKind);
        // Percentage heights of the frame's root resolve against its viewport, as against the page area outside.
        _cbHeight = new CbHeights(h, h, h);
        try
        {
            content = Capture(() =>
            {
                // The frame's viewport is the containing block of its positioned and fixed boxes.
                _frames.Add(new CbFrame { Transformed = true });
                float bottom = LayoutContent(doc, 0, 0, w, h, 0);
                LayoutAbsolutes(_frames[^1], 0, 0, w, h);
                _frames.RemoveAt(_frames.Count - 1);
                return bottom;
            });
        }
        finally
        {
            _frameDepth--;
            _lastContentBottom = lastBottom;
            (_cbHeight, _cbKind) = (outerCb, outerKind);
        }
        if (content.Ops.Count == 0 && frame.Canvas == null) return;
        var clip = new ClipOp(x, y, w, h, null);
        if (frame is { Canvas: { } canvas, Root: { } root })
        {
            var group = new GroupOp();
            BoxPainter.PaintCanvas(group, canvas, root, x, y, w, Math.Max(h, content.Height), x, y, w, h, _images, w);
            clip.Ops.Add(group);
        }
        foreach (var op in content.Ops)
        {
            op.Offset(x, y);
            clip.Ops.Add(op);
        }
        _ops.Add(clip);
    }
}
