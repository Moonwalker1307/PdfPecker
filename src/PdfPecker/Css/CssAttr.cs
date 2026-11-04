using System.Globalization;
using System.Text;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// The attr() function of CSS Values 5 in any property: <c>attr(name type, fallback)</c>, where the type is
/// <c>type(&lt;syntax&gt;)</c>, <c>raw-string</c>, <c>number</c> or a unit, and no type means a string. Like var(), it
/// is substituted per element before the declaration is parsed (see <see cref="CssVariables"/>), and as in Chrome a value
/// that came from an attribute may not name a resource: an attr() result in or around url(), image-set() and the other
/// functions that load something makes the declaration invalid.
/// </summary>
internal static class CssAttr
{
    /// <summary>
    /// The longest attribute value attr() reads. Attribute values are untrusted text, and nothing a property takes needs
    /// more; a longer value is treated as one that does not parse, so the fallback applies.
    /// </summary>
    internal const int MaxValueLength = 1 << 16;

    /// <summary>
    /// How many attr() references one declaration may resolve. Attribute values can refer to other attributes, through
    /// both their values and their fallbacks, which without a budget is exponential in the number of attributes.
    /// </summary>
    internal const int MaxLookups = 1024;

    /// <summary>What an attr() call parses its attribute as.</summary>
    internal enum AttrType : byte { String, RawString, Number, Unit, Syntax }

    /// <summary>One attr() call: the attribute's name, its type and the fallback (null when there is no comma).</summary>
    internal readonly record struct Call(string Name, AttrType Type, string? Argument, string? Fallback)
    {
        /// <summary>
        /// Whether generated text reads the call itself: no type, or the older <c>attr(href url)</c> of the target
        /// functions (target-counter() and the like), whose url type CSS Values 5 dropped.
        /// </summary>
        public bool IsGeneratedText => Type == AttrType.String || Type == AttrType.Unit && Argument == "url";
    }

    /// <summary>
    /// The per-declaration state of a substitution: the element whose attributes are read (a pseudo-element's
    /// originating element), the attributes being substituted, for cycles, and the lookup budget.
    /// </summary>
    internal sealed class Context
    {
        public Element? Host;
        /// <summary>
        /// Whether attr() calls without a type stay in the value: content, string-set and bookmark-label read them when
        /// the generated text is made, as they always have.
        /// </summary>
        public bool KeepUntyped;
        public int Budget;
        /// <summary>The attribute on whose reference cycle the substitution failed, until that attribute's own call is left.</summary>
        public string? Cycle;
        public readonly List<string> Active = [];

        public void Reset(Element? host, bool keepUntyped)
        {
            Host = host;
            KeepUntyped = keepUntyped;
            Budget = MaxLookups;
            Cycle = null;
            Active.Clear();
        }

        /// <summary>The attribute's value, matched as written and then in lower case, as HTML attribute names are.</summary>
        public string? Attribute(string name)
        {
            if (Host == null) return null;
            return Host.GetAttribute(name) ?? Host.GetAttribute(name.ToLowerInvariant());
        }
    }

    /// <summary>A cheap test for an attr( that starts a token, made before any other work on a value.</summary>
    public static bool Has(string value)
    {
        int i = value.IndexOf("attr(", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            if (i == 0 || !IsNameChar(value[i - 1])) return true;
            i = value.IndexOf("attr(", i + 5, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>The properties whose generated text reads attr() itself (see <see cref="GeneratedContent"/>).</summary>
    public static bool IsGeneratedTextProperty(string property) => property is "content" or "string-set" or "bookmark-label";

    /// <summary>
    /// Whether the style resolver substitutes attr() in a declaration: any attr() outside the generated-text
    /// properties, and a typed one inside them. Declarations without attr( cost one substring search.
    /// </summary>
    public static bool Used(string property, string value)
    {
        if (!Has(value)) return false;
        if (property.StartsWith("--", StringComparison.Ordinal) || !IsGeneratedTextProperty(property)) return true;
        foreach (var call in Calls(value))
            if (call is { IsGeneratedText: false }) return true;
        return false;
    }

    /// <summary>Whether every attr() in the value is well formed; Chrome drops a declaration with a malformed one when it parses it.</summary>
    public static bool SyntaxValid(string value)
    {
        foreach (var call in Calls(value))
            if (call == null) return false;
        return true;
    }

    /// <summary>The attr() calls of a value, outside strings, with null for a malformed one.</summary>
    private static IEnumerable<Call?> Calls(string value)
    {
        int i = 0;
        while ((i = Next(value, i)) >= 0)
        {
            int close = MatchingParen(value, i + 4);
            if (close < 0)
            {
                yield return null;
                yield break;
            }
            yield return TryParse(value.Substring(i + 5, close - i - 5), out var call) ? call : null;
            i += 5;
        }
    }

    /// <summary>The index of the next attr( from <paramref name="from"/> that starts a token outside a string; -1 when none.</summary>
    internal static int Next(string value, int from)
    {
        char quote = '\0';
        for (int i = from; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c is 'a' or 'A' && i + 5 <= value.Length && value.AsSpan(i, 5).Equals("attr(", StringComparison.OrdinalIgnoreCase)
                     && (i == 0 || !IsNameChar(value[i - 1]))) return i;
        }
        return -1;
    }

    /// <summary>
    /// Parses the arguments of attr(): a name, an optional type and an optional fallback after a comma. A type that is an
    /// identifier other than raw-string and number is taken as a unit here and checked when it is used, as Chrome does.
    /// </summary>
    public static bool TryParse(string inner, out Call call)
    {
        call = default;
        int i = SkipSpace(inner, 0);
        int start = i;
        while (i < inner.Length && IsNameChar(inner[i])) i++;
        if (i == start || char.IsAsciiDigit(inner[start]) || inner[start] == '-' && i - start > 1 && char.IsAsciiDigit(inner[start + 1])) return false;
        string name = inner.Substring(start, i - start);
        i = SkipSpace(inner, i);
        var type = AttrType.String;
        string? argument = null;
        if (i < inner.Length && inner[i] != ',')
        {
            int typeStart = i;
            if (inner[i] == '%') i++;
            else while (i < inner.Length && IsNameChar(inner[i])) i++;
            if (i == typeStart) return false;
            string word = inner.Substring(typeStart, i - typeStart);
            if (i < inner.Length && inner[i] == '(')
            {
                if (!word.Equals("type", StringComparison.OrdinalIgnoreCase)) return false;
                int close = MatchingParen(inner, i);
                if (close < 0) return false;
                argument = inner.Substring(i + 1, close - i - 1).Trim();
                if (!CssPropertySyntax.IsValid(argument, allowUrl: false)) return false;
                type = AttrType.Syntax;
                i = close + 1;
            }
            else if (word.Equals("raw-string", StringComparison.OrdinalIgnoreCase)) type = AttrType.RawString;
            else if (word.Equals("number", StringComparison.OrdinalIgnoreCase)) type = AttrType.Number;
            else
            {
                type = AttrType.Unit;
                argument = word.ToLowerInvariant();
            }
            i = SkipSpace(inner, i);
        }
        string? fallback = null;
        if (i < inner.Length)
        {
            if (inner[i] != ',') return false;
            fallback = inner.Substring(i + 1).Trim();
        }
        call = new Call(name, type, argument, fallback);
        return true;
    }

    /// <summary>
    /// The attribute's value as the call's type makes it, before any syntax check of a type(): a string, a number with
    /// its unit, or the trimmed text for type(). Null when the value does not parse as the type.
    /// </summary>
    public static string? Typed(in Call call, string raw)
    {
        if (raw.Length > MaxValueLength) return null;
        switch (call.Type)
        {
            case AttrType.String:
            case AttrType.RawString:
                return Quote(raw);
            case AttrType.Number:
                return NumberToken(raw) is { } n ? Format(n) : null;
            case AttrType.Unit:
                return IsUnit(call.Argument!) && NumberToken(raw) is { } u ? Format(u) + call.Argument : null;
            default:
                return raw.Trim();
        }
    }

    /// <summary>
    /// Whether a type() result can stand in a declaration: brackets and strings balanced, and no ";" or "!" outside
    /// them, as a declaration value must be. Anything else could end the declaration early or add another.
    /// </summary>
    public static bool IsDeclarationValue(string value)
    {
        Span<char> stack = stackalloc char[64];
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c is '\n' or '\r' or '\f') return false;
                continue;
            }
            switch (c)
            {
                case '"' or '\'': quote = c; break;
                case '(' or '[' or '{':
                    if (depth == stack.Length) return false;
                    stack[depth++] = c == '(' ? ')' : c == '[' ? ']' : '}';
                    break;
                case ')' or ']' or '}':
                    if (depth == 0 || stack[--depth] != c) return false;
                    break;
                case ';' or '!' when depth == 0: return false;
            }
        }
        return quote == '\0' && depth == 0;
    }

    /// <summary>The functions whose arguments name a resource, which no value from an attribute may reach.</summary>
    private static readonly string[] ResourceFunctions = ["url", "src", "image-set", "-webkit-image-set", "image", "cross-fade", "-webkit-cross-fade"];

    /// <summary>Whether a value calls url() or another function that loads a resource, outside strings.</summary>
    public static bool ContainsResource(string value)
    {
        if (value.IndexOf('(') < 0) return false;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(' && IsResourceFunction(value, i)) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="at"/> lies inside the arguments of url() or another function that loads a resource.</summary>
    public static bool InsideResource(string value, int at)
    {
        Span<bool> stack = stackalloc bool[64];
        int depth = 0, resources = 0;
        char quote = '\0';
        for (int i = 0; i < at && i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(')
            {
                bool resource = IsResourceFunction(value, i);
                if (resource) resources++;
                // Deeper nesting than the stack holds counts as inside, which errs on the side of refusing.
                if (depth < stack.Length) stack[depth] = resource;
                else resources++;
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
                if (depth >= stack.Length || stack[depth]) resources--;
            }
        }
        return resources > 0;
    }

    private static bool IsResourceFunction(string value, int paren)
    {
        int start = paren;
        while (start > 0 && IsNameChar(value[start - 1])) start--;
        var name = value.AsSpan(start, paren - start);
        foreach (var f in ResourceFunctions)
            if (name.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The value as a CSS string, with quotes, backslashes and control characters escaped.</summary>
    internal static string Quote(string raw)
    {
        var sb = new StringBuilder(raw.Length + 2).Append('"');
        foreach (char c in raw)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c == '\0') sb.Append('�');
            else if (c < 0x20 || c == 0x7F) sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The number when the whole value is one CSS number token, without spaces around it, as Chrome requires.</summary>
    private static double? NumberToken(string raw)
    {
        int i = 0, n = raw.Length;
        if (i < n && raw[i] is '+' or '-') i++;
        int digits = 0;
        while (i < n && char.IsAsciiDigit(raw[i])) { i++; digits++; }
        if (i < n && raw[i] == '.')
        {
            i++;
            int fraction = 0;
            while (i < n && char.IsAsciiDigit(raw[i])) { i++; fraction++; }
            if (fraction == 0) return null;
            digits += fraction;
        }
        if (digits == 0) return null;
        if (i < n && raw[i] is 'e' or 'E')
        {
            int k = i + 1;
            if (k < n && raw[k] is '+' or '-') k++;
            int exponent = 0;
            while (k < n && char.IsAsciiDigit(raw[k])) { k++; exponent++; }
            if (exponent > 0) i = k;
        }
        if (i != n) return null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    }

    private static string Format(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    /// <summary>The units an attr() may give a number: every CSS dimension and the percentage.</summary>
    private static bool IsUnit(string unit) => unit is "%" or "px" or "cm" or "mm" or "q" or "in" or "pt" or "pc"
        or "em" or "rem" or "ex" or "rex" or "ch" or "rch" or "cap" or "rcap" or "ic" or "ric" or "lh" or "rlh"
        or "vw" or "vh" or "vi" or "vb" or "vmin" or "vmax" or "svw" or "svh" or "svi" or "svb" or "svmin" or "svmax"
        or "lvw" or "lvh" or "lvi" or "lvb" or "lvmin" or "lvmax" or "dvw" or "dvh" or "dvi" or "dvb" or "dvmin" or "dvmax"
        or "cqw" or "cqh" or "cqi" or "cqb" or "cqmin" or "cqmax" or "deg" or "grad" or "rad" or "turn" or "s" or "ms"
        or "hz" or "khz" or "dpi" or "dpcm" or "dppx" or "x" or "fr";

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c >= 0x80;

    private static int SkipSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int MatchingParen(string s, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }
}
