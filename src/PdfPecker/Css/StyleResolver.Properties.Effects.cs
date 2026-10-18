using System.Globalization;

namespace PdfPecker.Css;

/// <summary>Transforms, clipping, filters and compositing.</summary>
internal sealed partial class StyleResolver
{
    private void ApplyTransform(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Lower == "none") s.Transform = null;
        else if (d.Inherit) s.Transform = parent.Transform;
        else if (CssTransform.TryParse(d.Lower, d.Em, _rootFontSize, _vw, _vh, out var tf)) s.Transform = tf;
    }

    /// <summary>translate: x [y [z]]; a z offset is ignored (no 3D).</summary>
    private void ApplyTranslate(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        var t = CssParser.Tokens(d.Lower);
        if (d.Lower is "none" or "initial" or "unset") s.Translate = null;
        else if (d.Inherit) s.Translate = parent.Translate;
        else if (t.Count is >= 1 and <= 3 && CssTransform.TryParse("translate(" + t[0] + ", " + (t.Count > 1 ? t[1] : "0") + ")",
                     d.Em, _rootFontSize, _vw, _vh, out var tr)) s.Translate = tr;
    }

    /// <summary>rotate: an angle, or "z angle"; rotations about x, y or a vector are 3D and ignored.</summary>
    private void ApplyRotate(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        var t = CssParser.Tokens(d.Lower);
        if (d.Lower is "none" or "initial" or "unset") s.Rotate = null;
        else if (d.Inherit) s.Rotate = parent.Rotate;
        else if (t.Count == 2 && t[0] == "z" || t.Count == 1)
        {
            if (CssTransform.TryParse("rotate(" + t[^1] + ")", d.Em, _rootFontSize, _vw, _vh, out var ro)) s.Rotate = ro;
        }
        else if (t.Count == 4 && t[0] == "0" && t[1] == "0" && float.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) && z > 0
                 && CssTransform.TryParse("rotate(" + t[3] + ")", d.Em, _rootFontSize, _vw, _vh, out var rz)) s.Rotate = rz;
    }

    private void ApplyScale(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        var t = CssParser.Tokens(d.Lower);
        if (d.Lower is "none" or "initial" or "unset") s.Scale = null;
        else if (d.Inherit) s.Scale = parent.Scale;
        else if (t.Count is >= 1 and <= 3 && CssTransform.TryParse("scale(" + t[0] + ", " + (t.Count > 1 ? t[1] : t[0]) + ")",
                     d.Em, _rootFontSize, _vw, _vh, out var sc)) s.Scale = sc;
    }

    private void ApplyTransformOrigin(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.TransformOriginX = parent.TransformOriginX; s.TransformOriginY = parent.TransformOriginY; }
        else if (CssTransform.TryParseOrigin(d.Lower, d.Em, _rootFontSize, _vw, _vh, out var ox, out var oy)) { s.TransformOriginX = ox; s.TransformOriginY = oy; }
    }

    /// <summary>clip-path: a reference to an SVG clipPath, or a basic shape; setting one clears the other.</summary>
    private void ApplyClipPath(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.SvgClipPath = parent.SvgClipPath; s.ClipShape = parent.ClipShape; }
        else if (d.Lower is "none" or "initial" or "unset") { s.SvgClipPath = null; s.ClipShape = null; }
        else if (SvgUrl(d.Value, d.Lower) is { } clipUrl) { s.SvgClipPath = clipUrl; s.ClipShape = null; }
        else if (ClipShape.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } shape) { s.ClipShape = shape; s.SvgClipPath = null; }
    }

    /// <summary>filter: a reference to an SVG filter, or a list of filter functions; setting one clears the other.</summary>
    private void ApplyFilter(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.SvgFilter = parent.SvgFilter; s.Filters = parent.Filters; }
        else if (d.Lower is "none" or "initial" or "unset") { s.SvgFilter = null; s.Filters = null; }
        else if (SvgUrl(d.Value, d.Lower) is { } filterUrl && CssParser.Tokens(d.Lower).Count == 1) { s.SvgFilter = filterUrl; s.Filters = null; }
        else if (FilterFunction.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } filters) { s.Filters = filters.Length > 0 ? filters : null; s.SvgFilter = null; }
    }

    private static void ApplyMixBlendMode(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BlendMode = parent.BlendMode;
        else if (d.Lower is "initial" or "unset") s.BlendMode = null;
        else if (BlendModes.PdfName(d.Lower, out bool blendValid) is var blend && blendValid) s.BlendMode = blend;
    }

    /// <summary>background-blend-mode: one mode per background layer; one invalid mode makes the whole list invalid.</summary>
    private static void ApplyBackgroundBlendMode(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.BackgroundBlendModes = parent.BackgroundBlendModes; return; }
        if (d.Lower is "initial" or "unset") { s.BackgroundBlendModes = null; return; }
        var parts = CssParser.SplitTopLevel(d.Lower, ',');
        var modes = new string?[parts.Count];
        bool ok = parts.Count > 0, any = false;
        for (int i = 0; i < parts.Count && ok; i++)
        {
            modes[i] = BlendModes.PdfName(parts[i].Trim(), out ok);
            any |= modes[i] != null;
        }
        if (ok) s.BackgroundBlendModes = any ? modes : null;
    }
}
