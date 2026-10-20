using System.Globalization;
using PdfPecker.Fonts;

namespace PdfPecker.Css;

/// <summary>
/// font-size-adjust: a number that says how large one metric of the font (the height of a lowercase x, by default)
/// should be compared with the font size. Text is then drawn in the size that gives the first font that proportion, so
/// that a fallback font with a small x-height does not look small next to the font it stands in for. none and
/// from-font (which holds the first font's own proportion, so changes nothing for it) are the default.
/// </summary>
internal readonly record struct FontSizeAdjust(FontMetric Metric, float Value)
{
    // A ratio far from 1 only comes from a font with a bogus metric or a stylesheet gone wrong; it would draw text far
    // larger or smaller than the box it sits in.
    private const float MinRatio = 0.05f, MaxRatio = 20f;

    public bool IsNone => Value <= 0;

    /// <summary>The size to draw text of a font size in: the size that gives the font's metric the declared proportion.</summary>
    public float Apply(float fontSize, TextFont font)
    {
        float natural = IsNone ? 0 : font.Metric(Metric);
        return natural > 0 ? fontSize * Math.Clamp(Value / natural, MinRatio, MaxRatio) : fontSize;
    }

    /// <summary>Parses <c>none | from-font | [ex-height | cap-height | ch-width | ic-width | ic-height]? [from-font | &lt;number&gt;]</c>; null when invalid.</summary>
    public static FontSizeAdjust? Parse(string lower)
    {
        var tokens = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 2) return null;
        if (tokens.Length == 1 && tokens[0] is "none" or "from-font") return new FontSizeAdjust();
        FontMetric metric = FontMetric.XHeight;
        string number = tokens[^1];
        if (tokens.Length == 2)
        {
            switch (tokens[0])
            {
                case "ex-height": metric = FontMetric.XHeight; break;
                case "cap-height": metric = FontMetric.CapHeight; break;
                case "ch-width": metric = FontMetric.ZeroWidth; break;
                case "ic-width" or "ic-height": metric = FontMetric.IdeographWidth; break;
                default: return null;
            }
            if (number == "from-font") return new FontSizeAdjust();
        }
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) && value >= 0
            ? new FontSizeAdjust(metric, value) : null;
    }
}
