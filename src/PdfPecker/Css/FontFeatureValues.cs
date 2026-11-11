using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

/// <summary>The kinds of named feature values an @font-feature-values rule gives, and the functions of font-variant-alternates that use them.</summary>
internal enum AlternateKind : byte { Stylistic, Styleset, CharacterVariant, Swash, Ornaments, Annotation }

/// <summary>
/// The @font-feature-values rules of a document: for each family, the numbers its names stand for in each kind of
/// block. Rules for the same family add up, and a later name replaces an earlier one, as in Chrome.
/// </summary>
internal sealed class FontFeatureValues
{
    // A document's rules give at most this many names in all; real ones give a handful.
    private const int MaxNames = 4096;

    private readonly Dictionary<string, Dictionary<(AlternateKind, string), int[]>> _families = new(StringComparer.OrdinalIgnoreCase);
    private int _names;

    /// <summary>
    /// Adds an @font-feature-values rule from its prelude (the family list) and body (the feature value blocks). A
    /// block of an unknown kind, a name with values its kind does not allow and a rule without families are left out.
    /// </summary>
    public void Add(string prelude, string body)
    {
        var families = CssParser.SplitTopLevel(prelude, ',').Select(f => Unquote(f.Trim())).Where(f => f.Length > 0).ToList();
        if (families.Count == 0) return;
        var values = new List<(AlternateKind, string, int[])>();
        var reader = body.AsSpan();
        while (true)
        {
            int at = reader.IndexOf('@');
            if (at < 0) break;
            int open = reader.Slice(at).IndexOf('{');
            if (open < 0) break;
            open += at;
            int close = reader.Slice(open).IndexOf('}');
            if (close < 0) break;
            close += open;
            var kind = reader.Slice(at + 1, open - at - 1).Trim().ToString().ToLowerInvariant() switch
            {
                "stylistic" => AlternateKind.Stylistic,
                "styleset" => AlternateKind.Styleset,
                "character-variant" => AlternateKind.CharacterVariant,
                "swash" => AlternateKind.Swash,
                "ornaments" => AlternateKind.Ornaments,
                "annotation" => AlternateKind.Annotation,
                _ => (AlternateKind?)null,
            };
            if (kind is { } k)
                foreach (var declaration in reader.Slice(open + 1, close - open - 1).ToString().Split(';'))
                {
                    int colon = declaration.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = declaration.Substring(0, colon).Trim();
                    if (ParseNumbers(k, declaration.Substring(colon + 1)) is { } numbers && IsIdent(name)) values.Add((k, name, numbers));
                }
            reader = reader.Slice(close + 1);
        }
        foreach (var family in families)
        {
            if (!_families.TryGetValue(family, out var names)) _families[family] = names = new();
            foreach (var (k, name, numbers) in values)
            {
                if (_names >= MaxNames) return;
                if (names.TryAdd((k, name), numbers)) _names++;
                else names[(k, name)] = numbers;
            }
        }
    }

    /// <summary>Whether no rule gave any name.</summary>
    public bool IsEmpty => _names == 0;

    /// <summary>The numbers a name of a kind stands for in the first of the families that has it, or null.</summary>
    public int[]? Find(IReadOnlyList<string> families, AlternateKind kind, string name)
    {
        foreach (var family in families)
            if (_families.TryGetValue(family, out var names) && names.TryGetValue((kind, name), out var numbers)) return numbers;
        return null;
    }

    /// <summary>
    /// The numbers of a feature value: one for most kinds, one or more from 1 to 20 for a styleset, and a variant from
    /// 1 to 99 and an optional value for a character variant. Null when they are not valid for the kind.
    /// </summary>
    private static int[]? ParseNumbers(AlternateKind kind, string text)
    {
        var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 20) return null;
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return kind switch
        {
            AlternateKind.Styleset => numbers.All(n => n is >= 1 and <= 20) ? numbers : null,
            AlternateKind.CharacterVariant => numbers.Length <= 2 && numbers[0] is >= 1 and <= 99 ? numbers : null,
            _ => numbers.Length == 1 ? numbers : null,
        };
    }

    internal static bool IsIdent(string name)
        => name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > 0x7F);

    internal static string Unquote(string s)
        => s.Length >= 2 && s[0] is '"' or '\'' && s[^1] == s[0] ? s.Substring(1, s.Length - 2) : s;
}

/// <summary>
/// A font-variant-alternates value: historical-forms and the functions that name feature values, which become
/// OpenType features once the font's family is known. The names resolve against the families of font-family in order,
/// so a fallback font takes the features of the family that named them.
/// </summary>
internal sealed class FontAlternates
{
    private readonly bool _historical;
    private readonly (AlternateKind Kind, string[] Names)[] _functions;
    private readonly FontFeatureValues? _values;
    // The features of the last family list and font-feature-settings asked for: a style asks again for every run.
    private (string? Families, string? Settings, string? Result)? _last;
    private readonly object _lock = new();

    private FontAlternates(bool historical, (AlternateKind, string[])[] functions, FontFeatureValues? values)
    {
        _historical = historical;
        _functions = functions;
        _values = values;
    }

    /// <summary>The value as written, parsed; null for normal, or when it is not valid (then the declaration is ignored).</summary>
    public static FontAlternates? Parse(string value, FontFeatureValues? values, out bool valid)
    {
        valid = false;
        bool historical = false;
        var functions = new List<(AlternateKind, string[])>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = CssParser.Tokens(value.Trim());
        if (tokens.Count == 1 && tokens[0].Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            valid = true;
            return null;
        }
        foreach (var token in tokens)
        {
            int open = token.IndexOf('(');
            string name = (open < 0 ? token : token.Substring(0, open)).ToLowerInvariant();
            if (!seen.Add(name)) return null; // each part at most once
            if (open < 0)
            {
                if (name != "historical-forms") return null;
                historical = true;
                continue;
            }
            if (!token.EndsWith(')')) return null;
            AlternateKind? kind = name switch
            {
                "stylistic" => AlternateKind.Stylistic,
                "styleset" => AlternateKind.Styleset,
                "character-variant" => AlternateKind.CharacterVariant,
                "swash" => AlternateKind.Swash,
                "ornaments" => AlternateKind.Ornaments,
                "annotation" => AlternateKind.Annotation,
                _ => null,
            };
            if (kind is not { } k) return null;
            var names = token.Substring(open + 1, token.Length - open - 2).Split(',').Select(n => n.Trim()).ToArray();
            bool list = k is AlternateKind.Styleset or AlternateKind.CharacterVariant;
            if (names.Length == 0 || !list && names.Length > 1 || names.Any(n => !FontFeatureValues.IsIdent(n))) return null;
            functions.Add((k, names));
        }
        if (tokens.Count == 0) return null;
        valid = true;
        return new FontAlternates(historical, functions.ToArray(), values);
    }

    /// <summary>
    /// font-feature-settings with the features of these alternates before it, so that its own settings win, as the
    /// spec orders them; <paramref name="settings"/> itself when the names stand for nothing in these families.
    /// </summary>
    public string? Settings(string? familyList, string? settings)
    {
        lock (_lock)
        {
            if (_last is { } last && last.Families == familyList && last.Settings == settings) return last.Result;
        }
        string? result = Build(familyList, settings);
        lock (_lock) _last = (familyList, settings, result);
        return result;
    }

    private string? Build(string? familyList, string? settings)
    {
        var features = new StringBuilder();
        if (_historical) Append(features, "hist", 1);
        if (_functions.Length > 0 && _values is { IsEmpty: false })
        {
            var families = familyList == null ? [] : CssParser.SplitTopLevel(familyList, ',').Select(f => FontFeatureValues.Unquote(f.Trim())).ToList();
            foreach (var (kind, names) in _functions)
                foreach (var name in names)
                {
                    if (_values.Find(families, kind, name) is not { } n) continue;
                    switch (kind)
                    {
                        case AlternateKind.Stylistic: Append(features, "salt", n[0]); break;
                        case AlternateKind.Swash: Append(features, "swsh", n[0]); Append(features, "cswh", n[0]); break;
                        case AlternateKind.Ornaments: Append(features, "ornm", n[0]); break;
                        case AlternateKind.Annotation: Append(features, "nalt", n[0]); break;
                        case AlternateKind.Styleset:
                            foreach (int set in n) Append(features, "ss" + set.ToString("00", CultureInfo.InvariantCulture), 1);
                            break;
                        case AlternateKind.CharacterVariant:
                            Append(features, "cv" + n[0].ToString("00", CultureInfo.InvariantCulture), n.Length > 1 ? n[1] : 1);
                            break;
                    }
                }
        }
        if (features.Length == 0) return settings;
        if (settings != null) features.Append(", ").Append(settings);
        return features.ToString();
    }

    private static void Append(StringBuilder features, string tag, int value)
    {
        if (features.Length > 0) features.Append(", ");
        features.Append('"').Append(tag).Append("\" ").Append(value.ToString(CultureInfo.InvariantCulture));
    }
}
