using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Fonts;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>A slot reserved before an element's content, filled with its background and borders once its size is known.</summary>
internal class GroupOp : DrawOp
{
    public List<DrawOp> Ops { get; private set; } = new(2);

    public override void Offset(float dx, float dy)
    {
        foreach (var op in Ops) op.Offset(dx, dy);
    }

    protected override void OwnArrays() => Ops = new List<DrawOp>(2);
}

/// <summary>
/// The content of a box with overflow other than visible: painted only inside (X, Y, W, H), the box's padding box, with
/// its rounded corners when it has them. An axis that does not clip reaches far out on both sides.
/// </summary>
internal sealed class ClipOp : GroupOp
{
    /// <summary>How far an axis that does not clip reaches out on each side.</summary>
    public const float Far = 1e6f;

    public float X, Y, W, H;
    public CornerRadii? Radii;
    /// <summary>A rectangle cut out of the clip, in which nothing is painted: the gap a fieldset's legend makes in its border.</summary>
    public (float X, float Y, float W, float H)? Hole;

    public ClipOp(float x, float y, float w, float h, CornerRadii? radii)
    {
        X = x; Y = y; W = w; H = h;
        Radii = radii;
    }

    public override void Offset(float dx, float dy)
    {
        base.Offset(dx, dy);
        X += dx;
        Y += dy;
        if (Hole is { } h) Hole = (h.X + dx, h.Y + dy, h.W, h.H);
    }
}

/// <summary>
/// An interactive form field over the box (X, Y, W, H): a widget annotation whose appearance is <see cref="GroupOp.Ops"/>,
/// the field's value (or, for a check box or radio button, its checked look).
/// </summary>
internal sealed class FieldOp : GroupOp
{
    public float X, Y, W, H;
    public PdfFormField Field;
    public ControlKind Kind;
    public bool Checked;
    /// <summary>The value's font and size, for the field's default appearance when a viewer redraws it.</summary>
    public TextFont Font;
    public float FontSize;
    public uint Color;
    /// <summary>0 left, 1 centred, 2 right.</summary>
    public int Align;
    /// <summary>The Form element of a tagged PDF.</summary>
    public StructElem? Form;

    public FieldOp(float x, float y, float w, float h, PdfFormField field)
    {
        X = x; Y = y; W = w; H = h;
        Field = field;
        Font = null!;
    }

    public override void Offset(float dx, float dy)
    {
        base.Offset(dx, dy);
        X += dx;
        Y += dy;
    }
}

/// <summary>A 3D transform of an element's layer: the functions, the border box and the transform origin inside it.</summary>
internal sealed record Transform3D(Element Owner, TransformFn[] Functions, float W, float H, float OriginX, float OriginY);

/// <summary>
/// The ops of a float, a positioned, transformed, clipped, blended or filtered box: painted in its own layer (CSS 2.1
/// Appendix E), and drawn with <see cref="Transform"/> about (<see cref="OriginX"/>, <see cref="OriginY"/>) when it has one.
/// </summary>
internal sealed class LayerOp : GroupOp
{
    // Painting order among siblings: negative z-index, then the normal flow (0), floats, positioned boxes with z-index
    // auto or 0, and positive z-index.
    public const float FloatOrder = 0.25f, PositionedOrder = 0.5f;

    public float Order;
    /// <summary>Below 1: painted as one transparency group at this opacity.</summary>
    public float Opacity = 1;
    /// <summary>Its first op, the box's own background, stays below its negative z-index children.</summary>
    public bool StackingContext;
    /// <summary>The CSS transform, y-down, about the origin.</summary>
    public Matrix? Transform;
    public float OriginX, OriginY;
    /// <summary>
    /// What a parent's perspective needs to draw this layer's 3D transform again: set only when the transform has a depth
    /// to it, since a flat one looks the same from every distance.
    /// </summary>
    public Transform3D? Transform3D;
    /// <summary>mix-blend-mode as a PDF name: painted as a group blended with what is below.</summary>
    public string? BlendMode;
    /// <summary>isolation: isolate, or a box whose background layers blend only with each other.</summary>
    public bool Isolated;
    // clip-path: the outline the layer is painted inside, in flow coordinates before the transform.
    /// <summary>SvgShape.MoveTo, LineTo, CurveTo and Close.</summary>
    public byte[]? ClipVerbs;
    public float[]? ClipPoints;
    public bool ClipEvenOdd;
    // A filter with blur() or drop-shadow(): run on the pixels of the layer's content. With RasterReplaces the result is
    // drawn instead of the content (blur); without, under it (the shadows of drop-shadow()).
    public SvgFilter? Raster;
    public bool RasterReplaces;
    /// <summary>Pixels per point of the filtered image; 0 for the writer's default. A wide blur has no fine detail to keep.</summary>
    public float RasterDensity;
    /// <summary>
    /// The blurred text-shadows of a run of text: drawn only as pixels, never as invisible text, since the shadow is
    /// not the text.
    /// </summary>
    public bool TextShadow;
    /// <summary>
    /// With a colour (0xFFRRGGBB), the layer's ops are not painted but make a luminosity soft mask: their grey levels
    /// say how much of this colour covers what is below. The bands of a blurred box shadow, which are then truly
    /// translucent instead of blended onto white.
    /// </summary>
    public uint? MaskFill;
    /// <summary>
    /// background-clip: text. The layer's text ops are copies of the box's text, which are not drawn but make the mask
    /// its other ops, the background, are painted through.
    /// </summary>
    public bool TextClip;

    /// <summary>Whether the layer is drawn as a block of its own: transformed, translucent, blended, clipped, filtered, a mask or clipped to text.</summary>
    public bool NeedsBlock => Transform != null || Opacity < 1 || BlendMode != null || Isolated || ClipVerbs != null || Raster != null || MaskFill != null
                              || TextClip;

    /// <summary>Whether the layer is painted as one transparency group.</summary>
    public bool NeedsGroup => Opacity < 1 || BlendMode != null || Isolated;

    protected override void OwnArrays()
    {
        base.OwnArrays();
        ClipPoints = (float[]?)ClipPoints?.Clone();
    }

    public override void Offset(float dx, float dy)
    {
        base.Offset(dx, dy);
        OriginX += dx;
        OriginY += dy;
        if (ClipPoints == null) return;
        for (int i = 0; i + 1 < ClipPoints.Length; i += 2)
        {
            ClipPoints[i] += dx;
            ClipPoints[i + 1] += dy;
        }
    }

    /// <summary>
    /// Sorts ops into painting order, keeping document order within a layer. The first <paramref name="pinned"/> ops
    /// stay where they are: a stacking context's own background.
    /// </summary>
    public static void SortLayers(List<DrawOp> ops, int pinned = 0)
    {
        bool anyLayer = false;
        foreach (var op in ops)
        {
            // A clipped box's content is sorted within the clip, which stays where the box is.
            if (op is ClipOp clip) SortLayers(clip.Ops);
            if (op is not LayerOp layer) continue;
            anyLayer = true;
            SortLayers(layer.Ops, layer.StackingContext ? 1 : 0);
        }
        if (!anyLayer) return;
        // A stable sort by order: ties keep their position, so ops that are not layers stay in document order.
        var keyed = new SortedOp[ops.Count - pinned];
        for (int i = 0; i < keyed.Length; i++)
        {
            var op = ops[pinned + i];
            keyed[i] = new SortedOp(op, op is LayerOp l ? l.Order : 0, i);
        }
        Array.Sort(keyed);
        for (int i = 0; i < keyed.Length; i++) ops[pinned + i] = keyed[i].Op;
    }

    private readonly record struct SortedOp(DrawOp Op, float Order, int Index) : IComparable<SortedOp>
    {
        public int CompareTo(SortedOp other)
        {
            int byOrder = Order.CompareTo(other.Order);
            return byOrder != 0 ? byOrder : Index.CompareTo(other.Index);
        }
    }
}
