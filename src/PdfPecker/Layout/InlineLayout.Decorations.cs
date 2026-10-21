using PdfPecker.Css;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>Text decorations (underline, overline, line-through in each style) and text shadows of a run of text.</summary>
internal sealed partial class LayoutEngine
{
    // A decoration line is 5.5% of the font size thick, at least half a point, unless text-decoration-thickness says
    // otherwise (then at least a tenth); the underline sits a tenth of the size below the baseline and the
    // line-through three tenths above it.
    private const float DecorationThicknessRatio = 0.055f, MinDecorationThickness = 0.5f, MinSetThickness = 0.1f;
    private const float UnderlineOffsetRatio = 0.1f, LineThroughRaiseRatio = 0.3f;

    // A wavy line is drawn as short straight segments: about three thicknesses high, a wave six thicknesses long (at
    // least 3pt), eight segments per wave and at most this many in all.
    private const float WavyAmplitudeRatio = 1.5f, MinWavyAmplitude = 0.75f, WavyLengthRatio = 6, MinWavyLength = 3f;
    private const int SegmentsPerWave = 8, MaxWavySegments = 4000;

    // A text-shadow blur under half a CSS pixel is drawn sharp; larger blurs are capped, which keeps the image a
    // reasonable size, and reach about three deviations from the text.
    private const float SharpBlurLimit = 0.375f, MaxTextShadowBlur = 150, ShadowReachSigmas = 3;

    // A blur several pixels wide loses nothing at a lower resolution: about three pixels per deviation is enough.
    private const float PixelsPerSigma = 3, MinRasterDensity = 1, MaxRasterDensity = 2;

    /// <summary>The decorations of a run, in their own colour or in <paramref name="shadow"/>, the colour of a text-shadow.</summary>
    private void Decorate(ComputedStyle s, float x, float w, float baseline, uint? shadow = null)
    {
        float size = s.TextSize;
        float thickness = s.DecorationThickness is { } dt ? Math.Max(dt.Resolve(size), MinSetThickness) : Math.Max(size * DecorationThicknessRatio, MinDecorationThickness);
        uint color = shadow ?? CssColor.Fade(s.DecorationColor ?? s.Color, s.InlineFade);
        if ((s.TextDecoration & TextDecoration.Underline) != 0)
            DecorationLine(s.DecorationStyle, x, baseline + (s.UnderlineOffset is { } uo ? uo.Resolve(size) : size * UnderlineOffsetRatio), w, thickness, color, below: true);
        if ((s.TextDecoration & TextDecoration.LineThrough) != 0)
            DecorationLine(s.DecorationStyle, x, baseline - size * LineThroughRaiseRatio, w, thickness, color, below: true);
        if ((s.TextDecoration & TextDecoration.Overline) != 0)
            DecorationLine(s.DecorationStyle, x, baseline - s.Font.Ascent * size, w, thickness, color, below: false);
    }

    /// <summary>One decoration line with its top edge at <paramref name="y"/>, in the given text-decoration-style.</summary>
    private void DecorationLine(DecorationStyle style, float x, float y, float w, float t, uint color, bool below)
    {
        if (w <= 0) return;
        switch (style)
        {
            case DecorationStyle.Double:
            {
                // Two lines a thickness apart; the second goes away from the text.
                float second = below ? y + 2 * t : y - 2 * t;
                _ops.Add(new RectOp(x, y, w, t, color) { IsDecoration = true });
                _ops.Add(new RectOp(x, second, w, t, color) { IsDecoration = true });
                break;
            }
            case DecorationStyle.Dotted:
            case DecorationStyle.Dashed:
                _ops.Add(new LineOp(x, y + t / 2, x + w, y + t / 2, t, color, dotted: style == DecorationStyle.Dotted));
                break;
            case DecorationStyle.Wavy:
                WavyLine(x, y, w, t, color, below);
                break;
            default:
                _ops.Add(new RectOp(x, y, w, t, color) { IsDecoration = true });
                break;
        }
    }

    /// <summary>A wave of short straight segments with round joins, about three thicknesses high.</summary>
    private void WavyLine(float x, float y, float w, float t, uint color, bool below)
    {
        float amplitude = Math.Max(t * WavyAmplitudeRatio, MinWavyAmplitude), wavelength = Math.Max(t * WavyLengthRatio, MinWavyLength);
        float mid = y + (below ? amplitude : -amplitude) + t / 2;
        int segments = Math.Clamp((int)MathF.Ceiling(w / wavelength * SegmentsPerWave), 1, MaxWavySegments);
        float px = x, py = mid;
        for (int k = 1; k <= segments; k++)
        {
            float nx = x + w * k / segments;
            float ny = mid - amplitude * MathF.Sin((nx - x) / wavelength * 2 * MathF.PI);
            _ops.Add(new LineOp(px, py, nx, ny, t, color, dotted: false) { Solid = true });
            px = nx; py = ny;
        }
    }

    // ------------------------------------------------------------------ text shadows

    /// <summary>
    /// Paints the text-shadows of a run under it, the first shadow on top, each with the shadows of the run's
    /// decorations. A shadow without blur is a copy of the text in the shadow's colour; blurred shadows are the text's
    /// pixels through a Gaussian blur, as browsers draw them, with consecutive blurred shadows made in one image.
    /// </summary>
    private void TextShadows(ComputedStyle style, float x, float w, float baseline, float lineTop, string text, float wordSpacing, float letterSpacing, bool rtl)
    {
        var shadows = style.TextShadows!;
        List<(BoxShadow Shadow, uint Color)>? blurred = null;
        for (int i = shadows.Length - 1; i >= 0; i--)
        {
            var sh = shadows[i];
            uint color = CssColor.Fade(sh.Color ?? style.Color, style.InlineFade);
            if (!CssColor.IsVisible(color)) continue;
            if (sh.Blur > SharpBlurLimit)
            {
                (blurred ??= new()).Add((sh, color));
                continue;
            }
            if (blurred != null) { BlurredTextShadows(style, x, w, baseline, lineTop, text, wordSpacing, letterSpacing, rtl, blurred); blurred = null; }
            float sx = x + sh.X, sy = baseline + sh.Y;
            _ops.Add(new TextOp(sx, sy, lineTop, style.Font, style.TextSize, color, text, wordSpacing, letterSpacing) { Rtl = rtl });
            if (style.TextDecoration != TextDecoration.None) Decorate(style, sx, w, sy, color);
        }
        if (blurred != null) BlurredTextShadows(style, x, w, baseline, lineTop, text, wordSpacing, letterSpacing, rtl, blurred);
    }

    /// <summary>
    /// Blurred text-shadows, bottom one first: a layer holding the run and its decorations, whose filter makes the
    /// shadows from the shape of what it holds and draws them in place of it.
    /// </summary>
    private void BlurredTextShadows(ComputedStyle style, float x, float w, float baseline, float lineTop, string text, float wordSpacing, float letterSpacing, bool rtl,
        List<(BoxShadow Shadow, uint Color)> shadows)
    {
        // The shadow takes its colour from the shadow alone: the text's own colour, even transparent, only gives the shape.
        var layer = new LayerOp { RasterReplaces = true, TextShadow = true };
        layer.Ops.Add(new TextOp(x, baseline, lineTop, style.Font, style.TextSize, CssColor.Black, text, wordSpacing, letterSpacing) { Rtl = rtl });
        if (style.TextDecoration != TextDecoration.None)
        {
            // The decorations are drawn into the main list and moved into the layer.
            int before = _ops.Count;
            Decorate(style, x, w, baseline, CssColor.Black);
            for (int k = before; k < _ops.Count; k++) layer.Ops.Add(_ops[k]);
            _ops.RemoveRange(before, _ops.Count - before);
        }
        layer.Raster = TextShadowFilter(shadows, out float minSigma);
        layer.RasterDensity = Math.Clamp(PixelsPerSigma / minSigma, MinRasterDensity, MaxRasterDensity);
        _ops.Add(layer);
    }

    /// <summary>
    /// The filter of blurred text-shadows: for each, the alpha of the text blurred (the CSS blur radius is twice the
    /// deviation, as for box-shadow), offset and filled with the shadow's colour, merged bottom first.
    /// </summary>
    private static SvgFilter TextShadowFilter(List<(BoxShadow Shadow, uint Color)> shadows, out float minSigma)
    {
        var f = new SvgFilter { TextShadows = true };
        var merged = new List<string?>();
        float reachX = 0, reachY = 0;
        minSigma = float.MaxValue;
        for (int k = 0; k < shadows.Count; k++)
        {
            var (sh, color) = shadows[k];
            float sigma = Math.Min(sh.Blur, MaxTextShadowBlur) / 2;
            minSigma = Math.Min(minSigma, sigma);
            string result = "s" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.Blur, In = "SourceAlpha", Result = "b", Sx = sigma, Sy = sigma, Linear = false });
            f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.Offset, In = "b", Result = "b", Dx = sh.X, Dy = sh.Y, Linear = false });
            f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.Flood, Result = "f", Color = color, Linear = false });
            f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.Composite, In = "f", In2 = "b", Operator = "in", Result = result, Linear = false });
            merged.Add(result);
            reachX = Math.Max(reachX, Math.Abs(sh.X) + ShadowReachSigmas * sigma);
            reachY = Math.Max(reachY, Math.Abs(sh.Y) + ShadowReachSigmas * sigma);
        }
        f.Primitives.Add(new SvgFilter.Primitive { Kind = SvgFilter.Kind.Merge, Inputs = merged, Linear = false });
        f.X = -reachX;
        f.Y = -reachY;
        f.W = 2 * reachX;
        f.H = 2 * reachY;
        return f;
    }
}
