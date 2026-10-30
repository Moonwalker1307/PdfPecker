using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>Text decorations (underline, overline, line-through in each style) and text shadows of a run of text.</summary>
internal sealed partial class InlineLayout
{
    // A decoration line is 5.5% of the font size thick, at least half a point, unless text-decoration-thickness says
    // otherwise (then at least a tenth); the underline sits a tenth of the size below the baseline and the
    // line-through three tenths above it.
    private const float DecorationThicknessRatio = 0.055f, MinDecorationThickness = 0.5f, MinSetThickness = 0.1f;
    private const float UnderlineOffsetRatio = 0.1f, LineThroughRaiseRatio = 0.3f;

    // Dashed, dotted, double and wavy lines follow Chrome, which is thicker than the solid line: a tenth of the font
    // size, at least one CSS pixel. Its dashes, dots and double lines are whole pixels, a wavy underline starts about
    // two pixels below the baseline, and a wave is 1.5 thicknesses and 1.4px high and 4 thicknesses and 3px long. The
    // wave is drawn as short straight segments, eight per wave and at most this many in all, and a dashed line is
    // drawn as at most this many dashes before it goes solid.
    private const float StyledThicknessRatio = 0.1f, Pixel = 0.75f, WavyTopBelowBaseline = 1.4f;
    private const float WavyHeightPerThickness = 1.5f, WavyHeightExtra = 1.4f * Pixel, WavyLengthPerThickness = 4, WavyLengthExtra = 3 * Pixel;
    private const int SegmentsPerWave = 8, MaxWavySegments = 4000, MaxDashes = 4000;

    // A text-shadow blur under half a CSS pixel is drawn sharp; larger blurs are capped, which keeps the image a
    // reasonable size, and reach about three deviations from the text.
    private const float SharpBlurLimit = 0.375f, MaxTextShadowBlur = 150, ShadowReachSigmas = 3;

    // A blur several pixels wide loses nothing at a lower resolution: about three pixels per deviation is enough.
    private const float PixelsPerSigma = 3, MinRasterDensity = 1, MaxRasterDensity = 2;

    /// <summary>
    /// The decorations of a run, in their own colour or in <paramref name="shadow"/>, the colour of a text-shadow. The
    /// underline and overline leave gaps for the glyphs of <paramref name="text"/>, the run as drawn, they cross.
    /// </summary>
    private void Decorate(ComputedStyle s, float x, float w, float baseline, uint? shadow = null, TextOp? text = null)
    {
        float size = s.TextSize;
        float thickness = s.DecorationThickness is { } dt ? Math.Max(dt.Resolve(size), MinSetThickness) : Math.Max(size * DecorationThicknessRatio, MinDecorationThickness);
        uint color = shadow ?? CssColor.Fade(s.DecorationColor ?? s.Color, s.InlineFade);
        var style = s.DecorationStyle;
        // The thickness the lines of this style are drawn with; solid lines keep their own.
        float styled = style == DecorationStyle.Solid || s.DecorationThickness != null ? thickness : Math.Max(size * StyledThicknessRatio, Pixel);
        if ((s.TextDecoration & TextDecoration.Underline) != 0)
        {
            float y = UnderlineTop(s, style, baseline, size, styled);
            var (top, bottom) = DecorationBand(style, y, styled, below: true);
            DecorationLine(style, x, y, w, styled, color, below: true, InkGaps(s, text, top, bottom, styled));
        }
        if ((s.TextDecoration & TextDecoration.LineThrough) != 0)
        {
            // The middle of a dashed, dotted, double or wavy line is three tenths of the size above the baseline, as in
            // Chrome; a solid one hangs from there.
            float middle = baseline - size * LineThroughRaiseRatio;
            float y = style switch
            {
                DecorationStyle.Solid => middle,
                DecorationStyle.Wavy => middle - DecorationBand(style, 0, styled, below: true).Bottom / 2,
                DecorationStyle.Double => middle - DoubleLine(styled).Thickness / 2,
                _ => middle - styled / 2,
            };
            DecorationLine(style, x, y, w, styled, color, below: true);
        }
        if ((s.TextDecoration & TextDecoration.Overline) != 0)
        {
            // A dashed, dotted, double or wavy line ends at the ascent, as in Chrome, and a solid one hangs from there.
            float y = baseline - s.Font.Ascent * size;
            if (style != DecorationStyle.Solid) y -= style == DecorationStyle.Double ? DoubleLine(styled).Thickness : styled;
            var (top, bottom) = DecorationBand(style, y, styled, below: false);
            DecorationLine(style, x, y, w, styled, color, below: false, InkGaps(s, text, top, bottom, styled));
        }
    }

    /// <summary>
    /// The top of an underline. from-font and under follow Chrome, in whole CSS pixels: from-font at the top of the
    /// font's own underline, rounded, and under one pixel below the bottom of the em box, rounded down, so that it
    /// clears the descenders. text-underline-offset moves either; a font without an underline position draws auto.
    /// </summary>
    private static float UnderlineTop(ComputedStyle s, DecorationStyle style, float baseline, float size, float styled)
    {
        float offset = s.UnderlineOffset is { } uo ? uo.Resolve(size) : 0;
        switch (s.UnderlinePosition)
        {
            case UnderlinePosition.Under:
                return baseline + (MathF.Floor((s.Font.Metric(FontMetric.EmDescent) * size + offset) / Pixel) + 1) * Pixel;
            case UnderlinePosition.FromFont when s.Font.Metric(FontMetric.UnderlinePosition) is not 0 and var position:
                return baseline + MathF.Round((position * size + offset) / Pixel, MidpointRounding.AwayFromZero) * Pixel;
            default:
                return s.UnderlineOffset != null ? baseline + offset : AutoUnderlineTop(style, baseline, size, styled);
        }
    }

    /// <summary>
    /// The top of an underline when text-underline-position and text-underline-offset are auto. A solid one hangs a tenth of the size below the
    /// baseline; the middle of a dashed, dotted or double one is there, as in Chrome, and a wave starts a fixed way below.
    /// </summary>
    private static float AutoUnderlineTop(DecorationStyle style, float baseline, float size, float styled) => style switch
    {
        DecorationStyle.Wavy => baseline + WavyTopBelowBaseline * Pixel,
        DecorationStyle.Double => baseline + size * UnderlineOffsetRatio - DoubleLine(styled).Thickness / 2,
        DecorationStyle.Solid => baseline + size * UnderlineOffsetRatio,
        _ => baseline + size * UnderlineOffsetRatio - styled / 2,
    };

    /// <summary>A length in points as whole CSS pixels, at least one, rounded half up (the small margin keeps 1.5px from falling short).</summary>
    private static float WholePixels(float length, bool floor = false) =>
        Math.Max(1, MathF.Floor(length / Pixel + (floor ? 0.001f : 0.501f)));

    /// <summary>The thickness of each line of a double decoration, and the distance from one line's top to the next's, in points.</summary>
    private static (float Thickness, float Step) DoubleLine(float t) =>
        (WholePixels(t, floor: true) * Pixel, MathF.Floor(t / Pixel + 1.501f) * Pixel);

    /// <summary>The height and length of one wave of a wavy line of thickness <paramref name="t"/>: the middle of the line's height.</summary>
    private static (float Height, float Length) WavyGeometry(float t) =>
        (t * WavyHeightPerThickness + WavyHeightExtra, t * WavyLengthPerThickness + WavyLengthExtra);

    /// <summary>The top and bottom of a decoration line with its top edge at <paramref name="y"/>, as it is drawn.</summary>
    private static (float Top, float Bottom) DecorationBand(DecorationStyle style, float y, float t, bool below)
    {
        switch (style)
        {
            case DecorationStyle.Double:
            {
                var (line, step) = DoubleLine(t);
                return below ? (y, y + step + line) : (y - step, y + line);
            }
            case DecorationStyle.Wavy:
            {
                float amplitude = WavyGeometry(t).Height / 2;
                float mid = y + (below ? amplitude : -amplitude) + t / 2;
                return (mid - amplitude - t / 2, mid + amplitude + t / 2);
            }
            default:
                return (y, y + t);
        }
    }

    /// <summary>
    /// One decoration line with its top edge at <paramref name="y"/>, in the given text-decoration-style, drawn between
    /// the <paramref name="gaps"/> it leaves for glyphs.
    /// </summary>
    private void DecorationLine(DecorationStyle style, float x, float y, float w, float t, uint color, bool below, List<(float Left, float Right)>? gaps = null)
    {
        if (gaps == null)
        {
            DecorationPiece(style, x, x, y, w, t, color, below);
            return;
        }
        float from = x, end = x + w;
        foreach (var (left, right) in gaps)
        {
            if (left > from) DecorationPiece(style, x, from, y, Math.Min(left, end) - from, t, color, below);
            from = Math.Max(from, right);
            if (from >= end) return;
        }
        DecorationPiece(style, x, from, y, end - from, t, color, below);
    }

    /// <summary>The part of a decoration line from <paramref name="x"/>; a wave keeps the phase of the whole line, which starts at <paramref name="origin"/>.</summary>
    private void DecorationPiece(DecorationStyle style, float origin, float x, float y, float w, float t, uint color, bool below)
    {
        if (w <= 0) return;
        switch (style)
        {
            case DecorationStyle.Double:
            {
                // Two whole-pixel lines; the second goes away from the text.
                var (line, step) = DoubleLine(t);
                float second = below ? y + step : y - step;
                _engine.Ops.Add(new RectOp(x, y, w, line, color) { IsDecoration = true });
                _engine.Ops.Add(new RectOp(x, second, w, line, color) { IsDecoration = true });
                break;
            }
            case DecorationStyle.Dotted:
            case DecorationStyle.Dashed:
            {
                var dashed = new LineOp(x, y + t / 2, x + w, y + t / 2, t, color, dotted: style == DecorationStyle.Dotted);
                SetDashes(dashed, style == DecorationStyle.Dotted, t, w);
                _engine.Ops.Add(dashed);
                break;
            }
            case DecorationStyle.Wavy:
                WavyLine(origin, x, y, w, t, color, below);
                break;
            default:
                _engine.Ops.Add(new RectOp(x, y, w, t, color) { IsDecoration = true });
                break;
        }
    }

    /// <summary>
    /// Gives a dashed or dotted line the pattern Chrome draws: whole-pixel dashes of three thicknesses (two from three
    /// pixels up) with gaps of two (one), or square dots, with the gap spread so that the line starts and ends on a dash.
    /// </summary>
    private static void SetDashes(LineOp line, bool dotted, float t, float length)
    {
        float whole = WholePixels(t) * Pixel;
        bool thick = t >= 3 * Pixel;
        float dash = dotted ? whole : whole * (thick ? 2 : 3), gap = dotted ? whole : whole * (thick ? 1 : 2);
        if (length / (dash + gap) > MaxDashes) return;
        float available = length + gap, fewest = MathF.Floor(available / (dash + gap));
        if (fewest >= 2)
        {
            // The number of dashes that leaves the gap closest to the wanted one.
            float fewGap = (length - fewest * dash) / (fewest - 1), manyGap = (length - (fewest + 1) * dash) / fewest;
            gap = manyGap <= 0 || MathF.Abs(fewGap - gap) < MathF.Abs(manyGap - gap) ? fewGap : manyGap;
        }
        line.DashLength = dash;
        line.DashGap = gap;
    }

    /// <summary>
    /// A wave of short straight segments with round joins, about three thicknesses high, from <paramref name="x"/>; its
    /// phase starts at <paramref name="origin"/>.
    /// </summary>
    private void WavyLine(float origin, float x, float y, float w, float t, uint color, bool below)
    {
        var (height, wavelength) = WavyGeometry(t);
        float amplitude = height / 2;
        float mid = y + (below ? amplitude : -amplitude) + t / 2;
        int segments = Math.Clamp((int)MathF.Ceiling(w / wavelength * SegmentsPerWave), 1, MaxWavySegments);
        float px = x, py = mid - amplitude * MathF.Sin((x - origin) / wavelength * 2 * MathF.PI);
        for (int k = 1; k <= segments; k++)
        {
            float nx = x + w * k / segments;
            float ny = mid - amplitude * MathF.Sin((nx - origin) / wavelength * 2 * MathF.PI);
            _engine.Ops.Add(new LineOp(px, py, nx, ny, t, color, dotted: false) { Solid = true });
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
            var copy = new TextOp(sx, sy, lineTop, style.Font, style.TextSize, color, text, wordSpacing, letterSpacing) { Rtl = rtl };
            _engine.Ops.Add(copy);
            if (style.TextDecoration != TextDecoration.None) Decorate(style, sx, w, sy, color, copy);
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
        var shape = new TextOp(x, baseline, lineTop, style.Font, style.TextSize, CssColor.Black, text, wordSpacing, letterSpacing) { Rtl = rtl };
        layer.Ops.Add(shape);
        if (style.TextDecoration != TextDecoration.None)
        {
            // The decorations are drawn into the main list and moved into the layer.
            int before = _engine.Ops.Count;
            Decorate(style, x, w, baseline, CssColor.Black, shape);
            for (int k = before; k < _engine.Ops.Count; k++) layer.Ops.Add(_engine.Ops[k]);
            _engine.Ops.RemoveRange(before, _engine.Ops.Count - before);
        }
        layer.Raster = TextShadowFilter(shadows, out float minSigma);
        layer.RasterDensity = Math.Clamp(PixelsPerSigma / minSigma, MinRasterDensity, MaxRasterDensity);
        _engine.Ops.Add(layer);
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
