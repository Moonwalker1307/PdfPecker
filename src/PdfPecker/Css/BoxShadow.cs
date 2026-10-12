namespace PdfPecker.Css;

/// <summary>One box-shadow: offsets, blur radius and spread in points, and its colour (null: currentColor).</summary>
internal readonly struct BoxShadow
{
    public readonly float X, Y, Blur, Spread;
    public readonly uint? Color;
    public readonly bool Inset;

    public BoxShadow(float x, float y, float blur, float spread, uint? color, bool inset)
    {
        X = x; Y = y; Blur = blur; Spread = spread; Color = color; Inset = inset;
    }

    /// <summary>
    /// Parses a box-shadow value: a comma-separated list of [inset] offset-x offset-y [blur [spread]] [color].
    /// Returns an empty array for "none" and null when the value is invalid.
    /// </summary>
    public static BoxShadow[]? Parse(string value, float em, float rem, float vw, float vh)
    {
        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return Array.Empty<BoxShadow>();
        var list = new List<BoxShadow>();
        foreach (var part in CssParser.SplitTopLevel(value, ','))
        {
            bool inset = false;
            uint? color = null;
            var lengths = new List<float>(4);
            foreach (var token in CssParser.Tokens(part.Trim()))
            {
                if (token.Equals("inset", StringComparison.OrdinalIgnoreCase)) { inset = true; continue; }
                if (StyleResolver.TryParseLength(token.ToLowerInvariant(), em, rem, out var len, vw, vh) && !len.IsAuto && !len.IsPercent)
                {
                    if (lengths.Count == 4) return null;
                    lengths.Add(len.Value);
                    continue;
                }
                if (color == null && !token.Equals("currentcolor", StringComparison.OrdinalIgnoreCase) && CssColor.TryParse(token, 0xFF000000, out uint c))
                {
                    color = c;
                    continue;
                }
                if (token.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) continue;
                return null;
            }
            if (lengths.Count < 2) return null;
            float blur = lengths.Count > 2 ? lengths[2] : 0;
            if (blur < 0) return null;
            list.Add(new BoxShadow(lengths[0], lengths[1], blur, lengths.Count > 3 ? lengths[3] : 0, color, inset));
        }
        return list.ToArray();
    }
}
