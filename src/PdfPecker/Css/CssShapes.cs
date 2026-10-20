namespace PdfPecker.Css;

/// <summary>
/// shape-outside on a float: a basic shape in a reference box, a reference box alone, or an image or gradient whose
/// alpha channel gives the shape. Lines beside the float wrap around this shape instead of the float's margin box.
/// </summary>
internal sealed class ShapeOutside
{
    public ClipShape? Shape;       // a basic shape or only a reference box; its box defaults to the margin box
    public BackgroundImage? Image; // url() or a gradient, laid out in the content box
}
