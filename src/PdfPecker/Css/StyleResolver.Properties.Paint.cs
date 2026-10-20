namespace PdfPecker.Css;

/// <summary>Paint properties: colour, opacity, backgrounds, border images, outlines and shadows.</summary>
internal sealed partial class StyleResolver
{
    private static void ApplyColor(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.Color = parent.Color;
        else if (CssColor.TryParse(d.Value, parent.Color, out var col)) s.Color = col;
    }

    private static void ApplyOpacity(ComputedStyle s, in DeclaredValue d)
    {
        if (TryParseNumberOrPercent(d.Lower, out float op)) s.Opacity = Math.Clamp(op, 0, 1);
    }

    private static void ApplyBackgroundColor(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BackgroundColor = parent.BackgroundColor;
        else if (CssColor.TryParse(d.Value, s.Color, out var bg)) s.BackgroundColor = bg;
    }

    // The background longhands take one value per layer; a list with an invalid layer leaves the property as it was.

    private static void ApplyBackgroundRepeat(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.BackgroundRepeats = d.Inherit ? parent.BackgroundRepeats : BackgroundList(d.Value, CssBackground.Repeat) ?? s.BackgroundRepeats;

    private void ApplyBackgroundSize(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.BackgroundSizes = parent.BackgroundSizes; return; }
        float em = d.Em;
        s.BackgroundSizes = BackgroundList(d.Value, l => CssBackground.Size(l, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundSizes;
    }

    private void ApplyBackgroundPosition(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.BackgroundPositionsX = parent.BackgroundPositionsX; s.BackgroundPositionsY = parent.BackgroundPositionsY; return; }
        float em = d.Em;
        if (BackgroundList(d.Value, l => CssBackground.Position(l, em, _rootFontSize, _vw, _vh)) is { } positions)
        {
            s.BackgroundPositionsX = positions.Select(p => p.X).ToArray();
            s.BackgroundPositionsY = positions.Select(p => p.Y).ToArray();
        }
    }

    private void ApplyBackgroundPositionX(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.BackgroundPositionsX = parent.BackgroundPositionsX; return; }
        float em = d.Em;
        s.BackgroundPositionsX = BackgroundList(d.Value, l => CssBackground.Axis(l, true, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundPositionsX;
    }

    private void ApplyBackgroundPositionY(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.BackgroundPositionsY = parent.BackgroundPositionsY; return; }
        float em = d.Em;
        s.BackgroundPositionsY = BackgroundList(d.Value, l => CssBackground.Axis(l, false, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundPositionsY;
    }

    private static void ApplyBackgroundOrigin(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.BackgroundOrigins = d.Inherit ? parent.BackgroundOrigins : BackgroundList(d.Value, l => CssBackground.Box(l.Trim().ToLowerInvariant())) ?? s.BackgroundOrigins;

    private static void ApplyBackgroundClip(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.BackgroundClips = d.Inherit ? parent.BackgroundClips : BackgroundList(d.Value, l => CssBackground.Box(l.Trim().ToLowerInvariant())) ?? s.BackgroundClips;

    private void ApplyBorderImageSource(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BorderImageSource = parent.BorderImageSource;
        else if (d.Lower is "none" or "initial" or "unset") s.BorderImageSource = null;
        else if (ParseBackgroundImage(d.Value, s) is [{ } source]) s.BorderImageSource = source;
    }

    private static void ApplyBorderImageSlice(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.BorderImageSlice = parent.BorderImageSlice; s.BorderImageFill = parent.BorderImageFill; }
        else if (d.Lower is "initial" or "unset") { s.BorderImageSlice = null; s.BorderImageFill = false; }
        else if (CssBorderImage.Slice(d.Lower) is { } slice) { s.BorderImageSlice = slice.Slice; s.BorderImageFill = slice.Fill; }
    }

    private void ApplyBorderImageWidth(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BorderImageWidths = parent.BorderImageWidths;
        else if (d.Lower is "initial" or "unset") s.BorderImageWidths = null;
        else s.BorderImageWidths = CssBorderImage.Widths(d.Lower, false, d.Em, _rootFontSize, _vw, _vh) ?? s.BorderImageWidths;
    }

    private void ApplyBorderImageOutset(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BorderImageOutsets = parent.BorderImageOutsets;
        else if (d.Lower is "initial" or "unset") s.BorderImageOutsets = null;
        else s.BorderImageOutsets = CssBorderImage.Widths(d.Lower, true, d.Em, _rootFontSize, _vw, _vh) ?? s.BorderImageOutsets;
    }

    private static void ApplyBorderImageRepeat(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) (s.BorderImageRepeatX, s.BorderImageRepeatY) = (parent.BorderImageRepeatX, parent.BorderImageRepeatY);
        else if (d.Lower is "initial" or "unset") s.BorderImageRepeatX = s.BorderImageRepeatY = BorderImageRepeat.Stretch;
        else if (CssBorderImage.Repeat(d.Lower) is { } rep) (s.BorderImageRepeatX, s.BorderImageRepeatY) = rep;
    }

    private void ApplyOutlineOffset(ComputedStyle s, in DeclaredValue d)
    {
        if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var oo, _vw, _vh) && !oo.IsAuto && !oo.IsPercent) s.OutlineOffset = oo.Value;
    }

    private void ApplyBoxShadow(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.BoxShadows = parent.BoxShadows;
        else if (BoxShadow.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } shadows) s.BoxShadows = shadows.Length > 0 ? shadows : null;
    }

    private void ApplyTextStrokeWidth(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.TextStrokeWidth = parent.TextStrokeWidth;
        else if (d.Lower == "initial") s.TextStrokeWidth = 0;
        else if (d.Lower is "thin" or "medium" or "thick") s.TextStrokeWidth = d.Lower == "thin" ? 0.75f : d.Lower == "medium" ? 2.25f : 3.75f;
        else if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var w, _vw, _vh) && !w.IsAuto && !w.IsPercent && w.Value >= 0) s.TextStrokeWidth = w.Value;
    }

    private static void ApplyTextStrokeColor(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.TextStrokeColor = parent.TextStrokeColor;
        else if (d.Lower is "currentcolor" or "initial") s.TextStrokeColor = null;
        else if (CssColor.TryParse(d.Value, s.Color, out var c)) s.TextStrokeColor = c;
    }

    /// <summary>paint-order on HTML text: only whether the stroke comes before the fill matters, as for the other orders the stroke is on top.</summary>
    private static void ApplyPaintOrder(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.PaintStrokeFirst = parent.PaintStrokeFirst; return; }
        var words = d.Lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        s.PaintStrokeFirst = words[0] == "stroke" || words[0] == "markers" && words.Length > 1 && words[1] == "stroke";
    }

    /// <summary>text-shadow takes the box-shadow syntax without inset and spread, which make the whole value invalid.</summary>
    private void ApplyTextShadow(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.TextShadows = parent.TextShadows;
        else if (d.Lower == "initial") s.TextShadows = null;
        else if (BoxShadow.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh) is { } ts && !ts.Any(t => t.Inset || t.Spread != 0))
            s.TextShadows = ts.Length > 0 ? ts : null;
    }
}
