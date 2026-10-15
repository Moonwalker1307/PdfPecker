using System.Globalization;

namespace PdfPecker.Css;

/// <summary>Colors are stored as 0xAARRGGBB. Alpha 0 means transparent.</summary>
internal static class CssColor
{
    public const uint Black = 0xFF000000;
    public const uint Transparent = 0;

    private static readonly Dictionary<string, uint> Named = BuildNamed();

    private static Dictionary<string, uint> BuildNamed()
    {
        const string table =
            "aliceblue=F0F8FF antiquewhite=FAEBD7 aqua=00FFFF aquamarine=7FFFD4 azure=F0FFFF beige=F5F5DC bisque=FFE4C4 " +
            "black=000000 blanchedalmond=FFEBCD blue=0000FF blueviolet=8A2BE2 brown=A52A2A burlywood=DEB887 " +
            "cadetblue=5F9EA0 chartreuse=7FFF00 chocolate=D2691E coral=FF7F50 cornflowerblue=6495ED cornsilk=FFF8DC " +
            "crimson=DC143C cyan=00FFFF darkblue=00008B darkcyan=008B8B darkgoldenrod=B8860B darkgray=A9A9A9 " +
            "darkgreen=006400 darkgrey=A9A9A9 darkkhaki=BDB76B darkmagenta=8B008B darkolivegreen=556B2F darkorange=FF8C00 " +
            "darkorchid=9932CC darkred=8B0000 darksalmon=E9967A darkseagreen=8FBC8F darkslateblue=483D8B " +
            "darkslategray=2F4F4F darkslategrey=2F4F4F darkturquoise=00CED1 darkviolet=9400D3 deeppink=FF1493 " +
            "deepskyblue=00BFFF dimgray=696969 dimgrey=696969 dodgerblue=1E90FF firebrick=B22222 floralwhite=FFFAF0 " +
            "forestgreen=228B22 fuchsia=FF00FF gainsboro=DCDCDC ghostwhite=F8F8FF gold=FFD700 goldenrod=DAA520 gray=808080 " +
            "green=008000 greenyellow=ADFF2F grey=808080 honeydew=F0FFF0 hotpink=FF69B4 indianred=CD5C5C indigo=4B0082 " +
            "ivory=FFFFF0 khaki=F0E68C lavender=E6E6FA lavenderblush=FFF0F5 lawngreen=7CFC00 lemonchiffon=FFFACD " +
            "lightblue=ADD8E6 lightcoral=F08080 lightcyan=E0FFFF lightgoldenrodyellow=FAFAD2 lightgray=D3D3D3 " +
            "lightgreen=90EE90 lightgrey=D3D3D3 lightpink=FFB6C1 lightsalmon=FFA07A lightseagreen=20B2AA " +
            "lightskyblue=87CEFA lightslategray=778899 lightslategrey=778899 lightsteelblue=B0C4DE lightyellow=FFFFE0 " +
            "lime=00FF00 limegreen=32CD32 linen=FAF0E6 magenta=FF00FF maroon=800000 mediumaquamarine=66CDAA " +
            "mediumblue=0000CD mediumorchid=BA55D3 mediumpurple=9370DB mediumseagreen=3CB371 mediumslateblue=7B68EE " +
            "mediumspringgreen=00FA9A mediumturquoise=48D1CC mediumvioletred=C71585 midnightblue=191970 mintcream=F5FFFA " +
            "mistyrose=FFE4E1 moccasin=FFE4B5 navajowhite=FFDEAD navy=000080 oldlace=FDF5E6 olive=808000 olivedrab=6B8E23 " +
            "orange=FFA500 orangered=FF4500 orchid=DA70D6 palegoldenrod=EEE8AA palegreen=98FB98 paleturquoise=AFEEEE " +
            "palevioletred=DB7093 papayawhip=FFEFD5 peachpuff=FFDAB9 peru=CD853F pink=FFC0CB plum=DDA0DD powderblue=B0E0E6 " +
            "purple=800080 rebeccapurple=663399 red=FF0000 rosybrown=BC8F8F royalblue=4169E1 saddlebrown=8B4513 " +
            "salmon=FA8072 sandybrown=F4A460 seagreen=2E8B57 seashell=FFF5EE sienna=A0522D silver=C0C0C0 skyblue=87CEEB " +
            "slateblue=6A5ACD slategray=708090 slategrey=708090 snow=FFFAFA springgreen=00FF7F steelblue=4682B4 tan=D2B48C " +
            "teal=008080 thistle=D8BFD8 tomato=FF6347 turquoise=40E0D0 violet=EE82EE wheat=F5DEB3 white=FFFFFF " +
            "whitesmoke=F5F5F5 yellow=FFFF00 yellowgreen=9ACD32";
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in table.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            d[pair.Substring(0, eq)] = 0xFF000000 | uint.Parse(pair.AsSpan(eq + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return d;
    }

    public static bool IsOpaque(uint c) => c >> 24 == 0xFF;

    /// <summary>The colour with its alpha multiplied by <paramref name="opacity"/>.</summary>
    public static uint Fade(uint c, float opacity)
        => opacity >= 1 ? c : (uint)MathF.Round((c >> 24) * Math.Clamp(opacity, 0, 1)) << 24 | (c & 0xFFFFFF);
    public static bool IsVisible(uint c) => c >> 24 != 0;

    /// <summary>Parses a CSS color. <c>currentColor</c> resolves to <paramref name="current"/>.</summary>
    public static bool TryParse(string value, uint current, out uint color)
    {
        color = 0;
        var v = value.AsSpan().Trim();
        if (v.Length == 0) return false;
        if (v[0] == '#') return TryParseHex(v.Slice(1), out color);
        int paren = v.IndexOf('(');
        if (paren > 0)
        {
            var fn = v.Slice(0, paren).Trim();
            int close = v.LastIndexOf(')');
            if (close < paren) close = v.Length;
            var args = v.Slice(paren + 1, close - paren - 1);
            if (close != v.Length - 1) return false;
            return ColorFunctions.TryParse(fn.ToString(), args.ToString(), current, out color);
        }
        var s = v.ToString();
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = Transparent; return true; }
        if (s.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) { color = current; return true; }
        return Named.TryGetValue(s, out color) || System.TryGetValue(s, out color);
    }

    // CSS system colours, with the values Chrome uses in its light colour scheme.
    private static readonly Dictionary<string, uint> System = new(StringComparer.OrdinalIgnoreCase)
    {
        ["canvas"] = 0xFFFFFFFF, ["canvastext"] = Black, ["linktext"] = 0xFF0000EE, ["visitedtext"] = 0xFF551A8B,
        ["activetext"] = 0xFFFF0000, ["buttonface"] = 0xFFEFEFEF, ["buttontext"] = Black, ["buttonborder"] = 0xFF767676,
        ["field"] = 0xFFFFFFFF, ["fieldtext"] = Black, ["highlight"] = 0xFFB5D5FF, ["highlighttext"] = Black,
        ["selecteditem"] = 0xFFB5D5FF, ["selecteditemtext"] = Black, ["mark"] = 0xFFFFFF00, ["marktext"] = Black,
        ["graytext"] = 0xFF808080, ["accentcolor"] = 0xFF0075FF, ["accentcolortext"] = 0xFFFFFFFF,
    };

    private static bool TryParseHex(ReadOnlySpan<char> h, out uint color)
    {
        color = 0;
        if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint n)) return false;
        switch (h.Length)
        {
            case 3:
                color = 0xFF000000 | Dup((n >> 8) & 0xF) << 16 | Dup((n >> 4) & 0xF) << 8 | Dup(n & 0xF);
                return true;
            case 4:
                color = Dup(n & 0xF) << 24 | Dup((n >> 12) & 0xF) << 16 | Dup((n >> 8) & 0xF) << 8 | Dup((n >> 4) & 0xF);
                return true;
            case 6:
                color = 0xFF000000 | n;
                return true;
            case 8:
                color = (n & 0xFF) << 24 | n >> 8;
                return true;
        }
        return false;

        static uint Dup(uint x) => x << 4 | x;
    }

    /// <summary>Blends a translucent color over white (box shadows, whose blur bands are painted over each other).</summary>
    public static uint FlattenOnWhite(uint c)
    {
        uint a = c >> 24;
        if (a == 0xFF || a == 0) return c;
        uint Blend(uint ch) => (ch * a + 255 * (255 - a)) / 255;
        return 0xFF000000 | Blend((c >> 16) & 0xFF) << 16 | Blend((c >> 8) & 0xFF) << 8 | Blend(c & 0xFF);
    }
}
