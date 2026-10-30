using System.Buffers;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// The prelude of an <c>@container</c> rule: an optional container name and a condition on the container's size or on
/// its custom properties (CSS Containment 3). Conditions are parsed once with the stylesheet; their lengths are
/// resolved when evaluated, because em in a query refers to the container's font size.
/// </summary>
internal sealed class ContainerQuery
{
    // The characters that make a feature a range comparison such as (width > 400px) rather than (width: 400px).
    private static readonly SearchValues<char> RangeOperators = SearchValues.Create("<>=");

    private const int MaxDepth = 16;

    /// <summary>The container name the query asks for, or null for the nearest container of a fitting type.</summary>
    public readonly string? Name;

    private readonly Node _condition;

    /// <summary>The query tests the inline axis (width, inline-size) or the block axis (height, block-size).</summary>
    public readonly bool NeedsWidth, NeedsHeight;

    /// <summary>The query tests a size, so only a size container can answer it; style() alone any element can.</summary>
    public bool NeedsSize => NeedsWidth || NeedsHeight;

    private ContainerQuery(string? name, Node condition)
    {
        Name = name;
        _condition = condition;
        condition.Axes(ref NeedsWidth, ref NeedsHeight);
    }

    /// <summary>Parses the prelude of an @container rule; null when it is not a valid container query.</summary>
    public static ContainerQuery? Parse(string prelude)
    {
        var s = prelude.Trim();
        if (s.Length == 0) return null;
        string? name = null;
        if (s[0] != '(')
        {
            int end = 0;
            while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '(') end++;
            var word = s.Substring(0, end);
            // "not" and style() start a condition; the other keywords are not valid names. A name without a condition
            // is not a valid query in Chrome.
            if (!word.Equals("not", StringComparison.OrdinalIgnoreCase) && !(end < s.Length && s[end] == '('))
            {
                if (!IsName(word)) return null;
                name = word;
                s = s.Substring(end).Trim();
            }
        }
        var condition = ParseCondition(s, 0, false);
        return condition == null ? null : new ContainerQuery(name, condition);
    }

    /// <summary>A container name: a custom identifier other than the keywords that container queries reserve.</summary>
    internal static bool IsName(string word)
    {
        if (word.Length == 0 || char.IsDigit(word[0]) || word.Length > 1 && word[0] == '-' && char.IsDigit(word[1])) return false;
        foreach (char c in word)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' || c > 0x7F)) return false;
        return word.ToLowerInvariant() is not ("none" or "and" or "or" or "not" or "initial" or "inherit" or "unset" or "default" or "revert" or "revert-layer");
    }

    /// <summary>
    /// Whether <paramref name="container"/> can answer this query: it has the name asked for, and a container type that
    /// covers every axis the condition tests. The nearest ancestor that can is the query container.
    /// </summary>
    public bool Accepts(ComputedStyle container)
    {
        if (Name != null && (container.ContainerNames == null || Array.IndexOf(container.ContainerNames, Name) < 0)) return false;
        if (NeedsHeight && container.ContainerType != ContainerType.Size) return false;
        if (NeedsWidth && container.ContainerType == ContainerType.Normal) return false;
        return true;
    }

    /// <summary>
    /// Evaluates the condition against a container's content box and custom properties. False also stands for
    /// unknown (a size that is not known before the first layout), as an unknown query does not apply its rules.
    /// </summary>
    public bool Matches(Element container, float rootFontSize)
    {
        var style = container.Style;
        var context = new Context(container.ContainerWidth, container.ContainerHeight, style.FontSize, rootFontSize, style.CustomProperties);
        return _condition.Evaluate(context) == true;
    }

    private readonly record struct Context(float Width, float Height, float Em, float Rem, CssVarScope? Vars);

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// "not (q)", "(q) and (q) ...", "(q) or (q) ..."; mixing and with or needs parentheses. Inside style() the parts
    /// are style features rather than size features.
    /// </summary>
    private static Node? ParseCondition(string s, int depth, bool style)
    {
        s = s.Trim();
        if (s.Length == 0 || depth > MaxDepth) return null;
        if (StartsWithNot(s))
        {
            // not applies to one part: (q) or a function such as style(...).
            var rest = s.Substring(3).Trim();
            int open = rest.IndexOf('(');
            if (open < 0 || MediaQuery.MatchingParen(rest, open) != rest.Length - 1) return null;
            var inner = ParseCondition(rest, depth + 1, style);
            return inner == null ? null : new Not(inner);
        }
        var parts = new List<Node>();
        string? op = null;
        int i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;
            int open = i;
            // A function: style(). Others (scroll-state()) make the query invalid, as features Chrome does not know do.
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-')) i++;
            if (i >= s.Length || s[i] != '(') return null;
            int close = MediaQuery.MatchingParen(s, i);
            if (close < 0) return null;
            Node? part;
            if (open == i) part = ParseInParens(s.Substring(i, close - i + 1), depth + 1, style);
            else if (!style && s.Substring(open, i - open).Equals("style", StringComparison.OrdinalIgnoreCase))
                part = ParseStyleQuery(s.Substring(i + 1, close - i - 1).Trim(), depth + 1);
            else part = null;
            if (part == null) return null;
            parts.Add(part);
            i = close + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            int start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var word = s.Substring(start, i - start).ToLowerInvariant();
            if (word is not ("and" or "or") || op != null && op != word) return null;
            op = word;
        }
        return parts.Count == 1 ? parts[0] : new Junction(parts.ToArray(), op == "or");
    }

    private static bool StartsWithNot(string s)
        => s.StartsWith("not", StringComparison.OrdinalIgnoreCase) && s.Length > 3 && (char.IsWhiteSpace(s[3]) || s[3] == '(');

    /// <summary>"( ... )": a nested condition, a size feature or a range (or inside style(), a style feature).</summary>
    private static Node? ParseInParens(string s, int depth, bool style)
    {
        var inner = s.Substring(1, s.Length - 2).Trim();
        if (inner.Length == 0) return null;
        if (inner[0] == '(' || StartsWithNot(inner)) return ParseCondition(inner, depth, style);
        if (style) return ParseStyleFeature(inner);
        inner = inner.ToLowerInvariant();
        int colon = inner.IndexOf(':');
        if (colon >= 0)
        {
            var name = inner.Substring(0, colon).Trim();
            var value = inner.Substring(colon + 1).Trim();
            if (value.Length == 0) return null;
            string op = "=";
            if (name.StartsWith("min-", StringComparison.Ordinal)) { op = ">="; name = name.Substring(4); }
            else if (name.StartsWith("max-", StringComparison.Ordinal)) { op = "<="; name = name.Substring(4); }
            var feature = FeatureOf(name);
            // min- and max- go with range features only.
            if (op != "=" && feature == Feature.Orientation) return null;
            return Comparison.Create(feature, op, value);
        }
        if (inner.AsSpan().IndexOfAny(RangeOperators) >= 0) return ParseRange(inner);
        var boolean = FeatureOf(inner);
        return boolean == Feature.None ? null : new BooleanFeature(boolean);
    }

    /// <summary>The inside of style(): one style feature, or a condition of them in parentheses.</summary>
    private static Node? ParseStyleQuery(string s, int depth)
    {
        if (s.Length == 0) return null;
        return s[0] == '(' || StartsWithNot(s) ? ParseCondition(s, depth, true) : ParseStyleFeature(s);
    }

    /// <summary>"--name: value" or "--name". Chrome queries custom properties only; other properties make the query invalid.</summary>
    private static StyleFeature? ParseStyleFeature(string s)
    {
        int colon = s.IndexOf(':');
        var name = (colon < 0 ? s : s.Substring(0, colon)).Trim();
        if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length == 2) return null;
        if (colon < 0) return new StyleFeature(name, null);
        var value = s.Substring(colon + 1).Trim();
        return value.Length == 0 ? null : new StyleFeature(name, NormalizeSpace(value));
    }

    /// <summary>Collapses runs of white space to one space, as values are compared token by token.</summary>
    private static string NormalizeSpace(string v)
    {
        var sb = new System.Text.StringBuilder(v.Length);
        bool space = false;
        foreach (char c in v.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"width &gt;= 400px", "400px &lt;= width", "200px &lt; width &lt;= 600px".</summary>
    private static Node? ParseRange(string s)
    {
        var parts = new List<string>();
        var ops = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is not ('<' or '>' or '=')) continue;
            parts.Add(s.Substring(start, i - start).Trim());
            string op = c.ToString();
            if (c != '=' && i + 1 < s.Length && s[i + 1] == '=') { op += "="; i++; }
            ops.Add(op);
            start = i + 1;
        }
        parts.Add(s.Substring(start).Trim());
        if (parts.Exists(p => p.Length == 0)) return null;
        if (parts.Count == 2)
        {
            var left = FeatureOf(parts[0]);
            if (left is not (Feature.None or Feature.Orientation)) return Comparison.Create(left, ops[0], parts[1]);
            var right = FeatureOf(parts[1]);
            if (right is not (Feature.None or Feature.Orientation)) return Comparison.Create(right, Flip(ops[0]), parts[0]);
            return null;
        }
        if (parts.Count == 3 && ops[0][0] == ops[1][0] && ops[0][0] != '=')
        {
            var middle = FeatureOf(parts[1]);
            if (middle is Feature.None or Feature.Orientation) return null;
            var low = Comparison.Create(middle, Flip(ops[0]), parts[0]);
            var high = Comparison.Create(middle, ops[1], parts[2]);
            return low == null || high == null ? null : new Junction(new Node[] { low, high }, false);
        }
        return null;

        static string Flip(string op) => op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };
    }

    private enum Feature : byte { None, Width, Height, AspectRatio, Orientation }

    private static Feature FeatureOf(string name) => name.Trim() switch
    {
        // Without vertical writing modes the inline axis is always horizontal.
        "width" or "inline-size" => Feature.Width,
        "height" or "block-size" => Feature.Height,
        "aspect-ratio" => Feature.AspectRatio,
        "orientation" => Feature.Orientation,
        _ => Feature.None,
    };

    // ---------------------------------------------------------------- the condition tree

    private abstract class Node
    {
        /// <summary>True, false, or null for unknown.</summary>
        public abstract bool? Evaluate(in Context c);

        public virtual void Axes(ref bool width, ref bool height) { }

        protected static void FeatureAxes(Feature f, ref bool width, ref bool height)
        {
            if (f is Feature.Width or Feature.AspectRatio or Feature.Orientation) width = true;
            if (f is Feature.Height or Feature.AspectRatio or Feature.Orientation) height = true;
        }
    }

    private sealed class Not : Node
    {
        private readonly Node _inner;
        public Not(Node inner) => _inner = inner;
        public override bool? Evaluate(in Context c) => _inner.Evaluate(c) is bool b ? !b : null;
        public override void Axes(ref bool width, ref bool height) => _inner.Axes(ref width, ref height);
    }

    /// <summary>Parts joined by and (false wins over unknown) or by or (true wins over unknown).</summary>
    private sealed class Junction : Node
    {
        private readonly Node[] _parts;
        private readonly bool _or;
        public Junction(Node[] parts, bool or) { _parts = parts; _or = or; }

        public override bool? Evaluate(in Context c)
        {
            bool unknown = false;
            foreach (var p in _parts)
            {
                var v = p.Evaluate(c);
                if (v == null) unknown = true;
                else if (v.Value == _or) return _or;
            }
            return unknown ? null : !_or;
        }

        public override void Axes(ref bool width, ref bool height)
        {
            foreach (var p in _parts) p.Axes(ref width, ref height);
        }
    }

    /// <summary>style(--name: value), true when the container's custom property has that value; style(--name), when it has one.</summary>
    private sealed class StyleFeature : Node
    {
        private readonly string _name;
        private readonly string? _value;
        public StyleFeature(string name, string? value) { _name = name; _value = value; }

        public override bool? Evaluate(in Context c)
        {
            string? actual = c.Vars != null && c.Vars.TryGetValue(_name, out var v) && v.Trim().Length > 0 ? v : null;
            if (_value == null) return actual != null;
            return actual != null && NormalizeSpace(actual) == _value;
        }
    }

    /// <summary>A feature in a boolean context, such as (width): true when it is not zero.</summary>
    private sealed class BooleanFeature : Node
    {
        private readonly Feature _feature;
        public BooleanFeature(Feature feature) => _feature = feature;

        public override bool? Evaluate(in Context c)
        {
            if (float.IsNaN(c.Width) && _feature != Feature.Height || float.IsNaN(c.Height) && _feature != Feature.Width) return null;
            return _feature switch
            {
                Feature.Width => c.Width != 0,
                Feature.Height => c.Height != 0,
                Feature.AspectRatio => c.Width != 0 && c.Height != 0,
                _ => true,
            };
        }

        public override void Axes(ref bool width, ref bool height) => FeatureAxes(_feature, ref width, ref height);
    }

    /// <summary>feature op value, such as width &gt;= 400px or orientation = portrait.</summary>
    private sealed class Comparison : Node
    {
        private readonly Feature _feature;
        private readonly string _op, _value;

        private Comparison(Feature feature, string op, string value)
        {
            _feature = feature;
            _op = op;
            _value = value;
        }

        /// <summary>
        /// The comparison, or null when the value does not fit the feature: a length (not a percentage) for a size,
        /// a ratio for aspect-ratio, portrait or landscape for orientation.
        /// </summary>
        public static Comparison? Create(Feature feature, string op, string value)
        {
            bool valid = feature switch
            {
                Feature.Width or Feature.Height => StyleResolver.TryParseLength(value, 16, 16, out var len) && !len.IsAuto && !len.IsPercent,
                Feature.AspectRatio => MediaQuery.TryRatio(value, out _),
                Feature.Orientation => op == "=" && value is "portrait" or "landscape",
                _ => false,
            };
            return valid ? new Comparison(feature, op, value) : null;
        }

        public override void Axes(ref bool width, ref bool height) => FeatureAxes(_feature, ref width, ref height);

        public override bool? Evaluate(in Context c)
        {
            double actual, wanted, eps = 1e-6;
            switch (_feature)
            {
                case Feature.Width:
                case Feature.Height:
                {
                    float size = _feature == Feature.Width ? c.Width : c.Height;
                    if (float.IsNaN(size)) return null;
                    if (!StyleResolver.TryParseLength(_value, c.Em, c.Rem, out var len) || len.IsAuto || len.IsPercent) return null;
                    // In CSS pixels, the box size rounded to Chrome's layout unit (1/64 px) to drop float noise.
                    actual = Math.Round(size / 0.75 * 64) / 64;
                    wanted = len.Value / 0.75;
                    eps = 1 / 64.0;
                    break;
                }
                case Feature.AspectRatio:
                    if (float.IsNaN(c.Width) || float.IsNaN(c.Height) || c.Height <= 0) return null;
                    if (!MediaQuery.TryRatio(_value, out wanted)) return null;
                    actual = c.Width / (double)c.Height;
                    break;
                case Feature.Orientation:
                    if (float.IsNaN(c.Width) || float.IsNaN(c.Height)) return null;
                    return _value == (c.Height >= c.Width ? "portrait" : "landscape");
                default:
                    return null;
            }
            // As in Chrome, equality allows for a layout unit and the strict comparisons do not.
            bool equal = Math.Abs(actual - wanted) < eps;
            return _op switch
            {
                "=" => equal,
                "<" => actual < wanted,
                "<=" => actual < wanted || equal,
                ">" => actual > wanted,
                ">=" => actual > wanted || equal,
                _ => null,
            };
        }
    }

    // ---------------------------------------------------------------- container query length units

    // The sizes that cq units refer to while the style resolver computes an element: 1% of the nearest container's
    // content box, NaN without one. Lengths are parsed by static helpers all over the CSS code, so the sizes are kept
    // per thread for the duration of a style pass rather than held in the ConversionContext and passed through every
    // one of them.
    [ThreadStatic] private static float t_cqWidth, t_cqHeight;
    [ThreadStatic] private static bool t_cqSet;

    /// <summary>Sets the sizes cq units refer to and returns the previous ones, for <see cref="RestoreUnits"/>.</summary>
    internal static (bool, float, float) SetUnits(float width, float height)
    {
        var saved = (t_cqSet, t_cqWidth, t_cqHeight);
        t_cqSet = true;
        t_cqWidth = width;
        t_cqHeight = height;
        return saved;
    }

    internal static void RestoreUnits((bool Set, float Width, float Height) saved)
    {
        t_cqSet = saved.Set;
        t_cqWidth = saved.Width;
        t_cqHeight = saved.Height;
    }

    /// <summary>
    /// The value in points of one cq unit (cqw, cqh, cqi, cqb, cqmin, cqmax), or null for another unit. Without a
    /// container on an axis the unit falls back to the viewport unit of that axis, as the spec says.
    /// </summary>
    internal static float? UnitValue(string unit, float vw, float vh)
    {
        if (unit.Length < 3 || unit[0] != 'c' || unit[1] != 'q') return null;
        float w = t_cqSet && !float.IsNaN(t_cqWidth) ? t_cqWidth : vw;
        float h = t_cqSet && !float.IsNaN(t_cqHeight) ? t_cqHeight : vh;
        return unit switch
        {
            "cqw" or "cqi" => w,
            "cqh" or "cqb" => h,
            "cqmin" => Math.Min(w, h),
            "cqmax" => Math.Max(w, h),
            _ => null,
        };
    }

    /// <summary>Whether a value contains a cq unit, such as 10cqw or calc(50cqi - 1em).</summary>
    internal static bool HasContainerUnit(string value)
    {
        for (int i = value.IndexOf("cq", StringComparison.OrdinalIgnoreCase); i > 0; i = value.IndexOf("cq", i + 2, StringComparison.OrdinalIgnoreCase))
        {
            char before = value[i - 1];
            if (!char.IsDigit(before) && before != '.') continue;
            if (i + 2 < value.Length && char.ToLowerInvariant(value[i + 2]) is 'w' or 'h' or 'i' or 'b' or 'm') return true;
        }
        return false;
    }
}
