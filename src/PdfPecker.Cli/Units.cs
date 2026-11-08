using System.Globalization;

namespace PdfPecker.Cli;

/// <summary>Lengths and paper sizes as the command line writes them.</summary>
internal static class Units
{
    private const float PointsPerMillimeter = 72f / 25.4f;

    /// <summary>
    /// Parses a length such as <c>10mm</c>, <c>1.5cm</c>, <c>0.5in</c>, <c>36pt</c> or <c>96px</c> into points. A number
    /// without a unit is in millimetres, as in wkhtmltopdf.
    /// </summary>
    public static float ParseLength(string text, string optionName)
    {
        var s = text.Trim();
        float factor = PointsPerMillimeter;
        foreach (var (suffix, f) in Suffixes)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - suffix.Length);
                factor = f;
                break;
            }
        }

        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value) || value < 0)
            throw new UsageException($"'{text}' is not a length like 10mm, 1cm, 0.5in or 36pt (option --{optionName}).");
        return value * factor;
    }

    private static readonly (string, float)[] Suffixes =
    {
        ("mm", PointsPerMillimeter),
        ("cm", PointsPerMillimeter * 10),
        ("in", 72f),
        ("pt", 1f),
        ("px", 0.75f),
    };

    /// <summary>
    /// Parses a paper size: a name such as <c>A4</c>, <c>Letter</c> or <c>B5</c>, or <c>WIDTHxHEIGHT</c> with a length
    /// each, such as <c>100x150mm</c> or <c>4inx6in</c>.
    /// </summary>
    public static PageSize ParsePageSize(string text)
    {
        var s = text.Trim();
        switch (s.ToUpperInvariant())
        {
            case "A3": return PageSize.A3;
            case "A4": return PageSize.A4;
            case "A5": return PageSize.A5;
            case "LETTER": return PageSize.Letter;
            case "LEGAL": return PageSize.Legal;
        }

        if (Named.TryGetValue(s, out var mm)) return PageSize.FromMillimeters(mm.Item1, mm.Item2);

        // The x between the numbers is the one a number follows: in 800pxx600px the first x belongs to px.
        int x = s.IndexOf('x');
        for (int i = x; i >= 0 && i < s.Length - 1; i = s.IndexOf('x', i + 1))
        {
            if (char.IsDigit(s[i + 1]) || s[i + 1] == '.')
            {
                x = i;
                break;
            }
        }

        if (x > 0)
        {
            var w = s.Substring(0, x);
            var h = s.Substring(x + 1);
            // A unit written once, as in 100x150mm, applies to both numbers.
            w = HasUnit(w) ? w : w + UnitOf(h);
            float width = ParseLength(w, "page-size");
            float height = ParseLength(h, "page-size");
            if (width > 0 && height > 0) return new PageSize(width, height);
        }

        throw new UsageException($"'{text}' is not a paper size. Use a name like A4 or Letter, or a size like 100x150mm.");
    }

    private static bool HasUnit(string s) => s.Length > 0 && !(char.IsDigit(s[s.Length - 1]) || s[s.Length - 1] == '.');

    private static string UnitOf(string s)
    {
        int i = s.Length;
        while (i > 0 && !(char.IsDigit(s[i - 1]) || s[i - 1] == '.')) i--;
        return s.Substring(i);
    }

    // ISO and North American sizes in millimetres; the five the library names itself are handled first.
    private static readonly Dictionary<string, (float, float)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A0"] = (841, 1189), ["A1"] = (594, 841), ["A2"] = (420, 594), ["A6"] = (105, 148), ["A7"] = (74, 105),
        ["A8"] = (52, 74), ["A9"] = (37, 52),
        ["B0"] = (1000, 1414), ["B1"] = (707, 1000), ["B2"] = (500, 707), ["B3"] = (353, 500), ["B4"] = (250, 353),
        ["B5"] = (176, 250), ["B6"] = (125, 176), ["B7"] = (88, 125), ["B8"] = (62, 88), ["B9"] = (44, 62),
        ["B10"] = (31, 44),
        ["C5E"] = (163, 229), ["Comm10E"] = (105, 241), ["DLE"] = (110, 220),
        ["Executive"] = (184.15f, 266.7f), ["Folio"] = (210, 330), ["Ledger"] = (431.8f, 279.4f),
        ["Tabloid"] = (279.4f, 431.8f),
    };
}
