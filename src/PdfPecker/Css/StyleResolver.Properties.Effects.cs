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

    /// <summary>translate: x [y [z]].</summary>
    private void ApplyTranslate(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        var t = CssParser.Tokens(d.Lower);
        if (d.Lower is "none" or "initial" or "unset") s.Translate = null;
        else if (d.Inherit) s.Translate = parent.Translate;
        else if (t.Count == 3 && CssTransform.TryParse("translate3d(" + t[0] + ", " + t[1] + ", " + t[2] + ")",
                     d.Em, _rootFontSize, _vw, _vh, out var tr3)) s.Translate = tr3;
        else if (t.Count is 1 or 2 && CssTransform.TryParse("translate(" + t[0] + ", " + (t.Count > 1 ? t[1] : "0") + ")",
                     d.Em, _rootFontSize, _vw, _vh, out var tr)) s.Translate = tr;
    }

    /// <summary>rotate: an angle, "x", "y" or "z" and an angle, or an axis vector and an angle, in either order.</summary>
    private void ApplyRotate(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        var t = CssParser.Tokens(d.Lower);
        if (d.Lower is "none" or "initial" or "unset") { s.Rotate = null; return; }
        if (d.Inherit) { s.Rotate = parent.Rotate; return; }
        string? fn = null;
        if (t.Count == 1) fn = "rotate(" + t[0] + ")";
        else if (t.Count == 2)
        {
            bool axisFirst = t[0] is "x" or "y" or "z";
            string axis = axisFirst ? t[0] : t[1], angle = axisFirst ? t[1] : t[0];
            if (axis is "x" or "y" or "z") fn = "rotate" + axis + "(" + angle + ")";
        }
        else if (t.Count == 4)
        {
            // The angle is the one token that is not a number.
            bool angleLast = !char.IsDigit(t[3][^1]);
            fn = angleLast ? "rotate3d(" + t[0] + ", " + t[1] + ", " + t[2] + ", " + t[3] + ")"
                : "rotate3d(" + t[1] + ", " + t[2] + ", " + t[3] + ", " + t[0] + ")";
        }
        if (fn != null && CssTransform.TryParse(fn, d.Em, _rootFontSize, _vw, _vh, out var ro)) s.Rotate = ro;
    }

    private void ApplyScale(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        var t = CssParser.Tokens(d.Lower);
        if (d.Lower is "none" or "initial" or "unset") s.Scale = null;
        else if (d.Inherit) s.Scale = parent.Scale;
        else if (t.Count == 3 && CssTransform.TryParse("scale3d(" + t[0] + ", " + t[1] + ", " + t[2] + ")",
                     d.Em, _rootFontSize, _vw, _vh, out var sc3)) s.Scale = sc3;
        else if (t.Count is 1 or 2 && CssTransform.TryParse("scale(" + t[0] + ", " + (t.Count > 1 ? t[1] : t[0]) + ")",
                     d.Em, _rootFontSize, _vw, _vh, out var sc)) s.Scale = sc;
    }

    private void ApplyTransformOrigin(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.TransformOriginX = parent.TransformOriginX; s.TransformOriginY = parent.TransformOriginY; }
        else if (CssTransform.TryParseOrigin(d.Lower, d.Em, _rootFontSize, _vw, _vh, out var ox, out var oy)) { s.TransformOriginX = ox; s.TransformOriginY = oy; }
    }

    /// <summary>perspective: none or a distance; the children's 3D transforms are drawn as seen from that far away.</summary>
    private void ApplyPerspective(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Lower is "none" or "initial" or "unset") s.Perspective = null;
        else if (d.Inherit) s.Perspective = parent.Perspective;
        else if (CssTransform.TryParse("perspective(" + d.Lower + ")", d.Em, _rootFontSize, _vw, _vh, out var p)) s.Perspective = p[0].A;
    }

    private void ApplyPerspectiveOrigin(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.PerspectiveOriginX = parent.PerspectiveOriginX; s.PerspectiveOriginY = parent.PerspectiveOriginY; }
        else if (CssTransform.TryParseOrigin(d.Lower, d.Em, _rootFontSize, _vw, _vh, out var ox, out var oy)) { s.PerspectiveOriginX = ox; s.PerspectiveOriginY = oy; }
    }

    /// <summary>clip-path: a reference to an SVG clipPath, or a basic shape; setting one clears the other.</summary>
    private void ApplyClipPath(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.SvgClipPath = parent.SvgClipPath; s.ClipShape = parent.ClipShape; }
        else if (d.Lower is "none" or "initial" or "unset") { s.SvgClipPath = null; s.ClipShape = null; }
        else if (SvgUrl(d.Value, d.Lower) is { } clipUrl) { s.SvgClipPath = clipUrl; s.ClipShape = null; }
        else if (ClipShape.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } shape) { s.ClipShape = shape; s.SvgClipPath = null; }
    }

    /// <summary>
    /// clip: rect(top, right, bottom, left), the deprecated clip of absolutely positioned boxes, with commas or (as
    /// browsers also accept) without; each side a length or auto. auto and anything else that does not parse clip nothing.
    /// </summary>
    private void ApplyClip(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.ClipRect = parent.ClipRect; return; }
        s.ClipRect = null;
        var v = d.Lower.AsSpan().Trim();
        if (!v.StartsWith("rect(") || v[^1] != ')') return;
        var args = v[5..^1].ToString();
        var parts = args.Contains(',') ? args.Split(',', StringSplitOptions.TrimEntries) : CssParser.Tokens(args).ToArray();
        if (parts.Length != 4) return;
        var sides = new Length[4];
        for (int i = 0; i < 4; i++)
        {
            if (parts[i] == "auto") sides[i] = Length.Auto;
            else if (TryParseLength(parts[i], d.Em, _rootFontSize, out var l, _vw, _vh) && !l.IsAuto && !l.IsPercent) sides[i] = l;
            else return;
        }
        s.ClipRect = sides;
    }

    /// <summary>filter: a reference to an SVG filter, or a list of filter functions; setting one clears the other.</summary>
    private void ApplyFilter(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.SvgFilter = parent.SvgFilter; s.Filters = parent.Filters; }
        else if (d.Lower is "none" or "initial" or "unset") { s.SvgFilter = null; s.Filters = null; }
        else if (SvgUrl(d.Value, d.Lower) is { } filterUrl && CssParser.Tokens(d.Lower).Count == 1) { s.SvgFilter = filterUrl; s.Filters = null; }
        else if (FilterFunction.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } filters) { s.Filters = filters.Length > 0 ? filters : null; s.SvgFilter = null; }
    }

    /// <summary>backdrop-filter: a list of filter functions, run on what is painted behind the element.</summary>
    private void ApplyBackdropFilter(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BackdropFilters = parent.BackdropFilters;
        else if (d.Lower is "none" or "initial" or "unset") s.BackdropFilters = null;
        else if (FilterFunction.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } filters) s.BackdropFilters = filters.Length > 0 ? filters : null;
    }

    /// <summary>
    /// mask-image: layers of images, gradients and references to SVG mask elements. A lone url(#id) is also the mask
    /// reference of an SVG element, as the mask property was before it became a shorthand.
    /// </summary>
    private void ApplyMaskImage(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.MaskImages = parent.MaskImages; s.SvgMask = parent.SvgMask; return; }
        if (d.Lower is "none" or "initial" or "unset") { s.MaskImages = null; s.SvgMask = null; return; }
        var layers = ParseBackgroundImage(d.Value, s);
        if (layers != null && layers.Length > MaxMaskLayers) return;
        s.MaskImages = layers;
        s.SvgMask = layers is [{ Url: ['#', ..] url }] ? url : null;
    }

    // More mask layers than this make the declaration invalid: each layer is a soft mask nested in the ones below it.
    private const int MaxMaskLayers = 16;

    /// <summary>A comma-separated mask longhand: one entry per layer, null for its initial value, or as it was when invalid.</summary>
    private static T[]? MaskList<T>(T[]? current, T[]? inherited, in DeclaredValue d, Func<string, T?> parse) where T : struct
    {
        if (d.Inherit) return inherited;
        if (d.Lower is "initial" or "unset") return null;
        return BackgroundList(d.Lower, parse) ?? current;
    }

    private void ApplyMaskSize(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        float em = d.Em;
        s.MaskSizes = MaskList(s.MaskSizes, parent.MaskSizes, in d, l => CssBackground.Size(l, em, _rootFontSize, _vw, _vh));
    }

    private void ApplyMaskPosition(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.MaskPositionsX = parent.MaskPositionsX; s.MaskPositionsY = parent.MaskPositionsY; return; }
        if (d.Lower is "initial" or "unset") { s.MaskPositionsX = s.MaskPositionsY = null; return; }
        float em = d.Em;
        if (BackgroundList(d.Value, l => CssBackground.Position(l, em, _rootFontSize, _vw, _vh)) is { } positions)
        {
            s.MaskPositionsX = positions.Select(p => p.X).ToArray();
            s.MaskPositionsY = positions.Select(p => p.Y).ToArray();
        }
    }

    private void ApplyMaskPositionAxis(ComputedStyle s, ComputedStyle parent, in DeclaredValue d, bool horizontal)
    {
        float em = d.Em;
        if (horizontal)
            s.MaskPositionsX = MaskList(s.MaskPositionsX, parent.MaskPositionsX, in d, l => CssBackground.Axis(l, true, em, _rootFontSize, _vw, _vh));
        else
            s.MaskPositionsY = MaskList(s.MaskPositionsY, parent.MaskPositionsY, in d, l => CssBackground.Axis(l, false, em, _rootFontSize, _vw, _vh));
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
