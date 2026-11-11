using PdfPecker.Fonts;

namespace PdfPecker.Css;

/// <summary>
/// An @font-palette-values rule: the families it applies to, the font palette it starts from and the colours it
/// changes in it.
/// </summary>
internal sealed class FontPaletteValuesRule
{
    /// <summary>base-palette: light or dark, or no base palette (the font's first).</summary>
    internal const int LightBase = -1, DarkBase = -2, NoBase = -3;

    // A rule overrides at most this many colours; real ones change a handful.
    private const int MaxOverrides = 1024;

    public readonly string Name;
    public readonly string[] Families;
    /// <summary>The index of the base palette, or <see cref="LightBase"/>, <see cref="DarkBase"/> or <see cref="NoBase"/>.</summary>
    public readonly int BasePalette;
    public readonly (int Index, uint Color)[] Overrides;

    private FontPaletteValuesRule(string name, string[] families, int basePalette, (int, uint)[] overrides)
    {
        Name = name;
        Families = families;
        BasePalette = basePalette;
        Overrides = overrides;
    }

    /// <summary>
    /// Parses an @font-palette-values rule from its prelude (the dashed name) and body; null when the name is not a
    /// dashed ident. A descriptor that is not valid is left out, and an override colour that is not valid drops the
    /// whole override-colors descriptor, as in Chrome.
    /// </summary>
    public static FontPaletteValuesRule? Parse(string prelude, string body)
    {
        var name = prelude.Trim();
        if (name.Length < 3 || !name.StartsWith("--", StringComparison.Ordinal) || name.AsSpan().ContainsAny(" \t\n,;")) return null;
        string[] families = [];
        int basePalette = NoBase;
        (int, uint)[] overrides = [];
        var declarations = new List<Declaration>();
        CssParser.ParseDeclarations(body, declarations);
        foreach (var d in declarations)
        {
            switch (d.Property)
            {
                case "font-family":
                    families = CssParser.SplitTopLevel(d.Value, ',').Select(f => Unquote(f.Trim())).Where(f => f.Length > 0).ToArray();
                    break;
                case "base-palette":
                    var v = d.Value.Trim().ToLowerInvariant();
                    if (v == "light") basePalette = LightBase;
                    else if (v == "dark") basePalette = DarkBase;
                    else if (Invariant.TryParseInt(v, out int index) && index >= 0) basePalette = index;
                    break;
                case "override-colors":
                    if (ParseOverrides(d.Value) is { } parsed) overrides = parsed;
                    break;
            }
        }
        return new FontPaletteValuesRule(name, families, basePalette, overrides);
    }

    private static (int, uint)[]? ParseOverrides(string value)
    {
        var parts = CssParser.SplitTopLevel(value, ',');
        if (parts.Count > MaxOverrides) return null;
        var result = new (int, uint)[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i].Trim();
            int space = part.IndexOfAny([' ', '\t', '\n']);
            if (space < 0 || !Invariant.TryParseInt(part.AsSpan(0, space), out int index) || index < 0) return null;
            var color = part.Substring(space + 1).Trim();
            // currentcolor has no meaning in a palette: the spec leaves it out of override-colors.
            if (color.Equals("currentcolor", StringComparison.OrdinalIgnoreCase) || !CssColor.TryParse(color, 0, out uint c)) return null;
            result[i] = (index, c);
        }
        return result;
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] is '"' or '\'' && s[^1] == s[0] ? s.Substring(1, s.Length - 2) : s;

    /// <summary>Whether the rule applies to a font of <paramref name="family"/>.</summary>
    public bool AppliesTo(string family)
    {
        foreach (var f in Families)
            if (f.Equals(family, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// The font-palette of text: light, dark, or the palette an @font-palette-values rule makes. normal is null, so that
/// text without the property draws as it always has.
/// </summary>
internal sealed class FontPalette
{
    public static readonly FontPalette Light = new(null, dark: false), Dark = new(null, dark: true);

    private readonly FontPaletteValuesRule? _rule;
    private readonly bool _dark;

    private FontPalette(FontPaletteValuesRule? rule, bool dark)
    {
        _rule = rule;
        _dark = dark;
    }

    /// <summary>The palette of an @font-palette-values rule.</summary>
    public static FontPalette Of(FontPaletteValuesRule rule) => new(rule, false);

    /// <summary>
    /// The colour glyphs of a font of <paramref name="family"/> (or of the family names in the font itself) drawn in
    /// this palette. A rule for other families leaves the font's own palette, as in Chrome.
    /// </summary>
    public ColorGlyphs Apply(ColorGlyphs glyphs, string? family, FontProgram program)
    {
        if (_rule == null) return glyphs.WithPalette(glyphs.PaletteColors(Math.Max(0, glyphs.PaletteOfType(_dark))));
        if (!(family != null && _rule.AppliesTo(family) || _rule.AppliesTo(program.FamilyName)
            || program.TypographicFamily != null && _rule.AppliesTo(program.TypographicFamily)))
            return glyphs;
        int index = _rule.BasePalette switch
        {
            FontPaletteValuesRule.LightBase => Math.Max(0, glyphs.PaletteOfType(false)),
            FontPaletteValuesRule.DarkBase => Math.Max(0, glyphs.PaletteOfType(true)),
            FontPaletteValuesRule.NoBase => 0,
            var i => i,
        };
        var colors = glyphs.PaletteColors(index);
        foreach (var (i, color) in _rule.Overrides)
            if (i < colors.Length) colors[i] = color;
        return glyphs.WithPalette(colors);
    }
}
