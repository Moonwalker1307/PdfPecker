namespace PdfPecker.Css;

internal static partial class CssParser
{
    /// <summary>Parses "a: b; c: d !important" and appends the expanded longhand declarations.</summary>
    public static void ParseDeclarations(string body, List<Declaration> output)
    {
        body = StripComments(body);
        foreach (var part in SplitTopLevel(body, ';'))
        {
            if (TryParseDeclaration(part, out string name, out string value, out bool important))
                Expand(name, value, important, output);
        }
    }

    /// <summary>
    /// Splits one declaration into its name, value and importance. Property names are lowercased, except custom
    /// properties, which are case-sensitive and may have an empty value.
    /// </summary>
    private static bool TryParseDeclaration(string text, out string name, out string value, out bool important)
    {
        name = value = "";
        important = false;
        int colon = text.IndexOf(':');
        if (colon <= 0) return false;
        name = text.Substring(0, colon).Trim();
        bool custom = name.StartsWith("--", StringComparison.Ordinal);
        if (!custom) name = name.ToLowerInvariant();
        value = text.Substring(colon + 1).Trim();
        int bang = value.LastIndexOf('!');
        if (bang >= 0 && value.AsSpan(bang + 1).Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
        {
            important = true;
            value = value.Substring(0, bang).TrimEnd();
        }
        // Any other '!' in a property's value, such as Acid2's "! error", makes the declaration invalid.
        if (!custom && HasBang(value)) return false;
        return name.Length > 0 && (value.Length > 0 || custom);
    }

    /// <summary>Whether a '!' appears in the value outside strings, escapes and functions such as url().</summary>
    private static bool HasBang(string value)
    {
        if (value.IndexOf('!') < 0) return false;
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\') i++;
            else if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == '!' && depth == 0) return true;
        }
        return false;
    }

    /// <summary>True when the value references a custom property and can only be expanded once the element's variables are known.</summary>
    public static bool HasVar(string value) => value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>A container-name value: none, or one or more container names.</summary>
    internal static bool ContainerNamesValid(string names)
    {
        var parts = names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        return parts.Length > 0 && Array.TrueForAll(parts, ContainerQuery.IsName);
    }

    internal static bool IsBorderStyle(string s)
        => s is "none" or "hidden" or "solid" or "dashed" or "dotted" or "double" or "groove" or "ridge" or "inset" or "outset";
}
