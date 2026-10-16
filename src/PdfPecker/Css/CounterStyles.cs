using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// The counter style algorithms of CSS Counter Styles 3, and those of the predefined styles that no descriptor can
/// express: Chinese, Korean and Hebrew numbers as Chrome writes them, and Ethiopic numbers.
/// </summary>
internal enum CounterSystem : byte { Cyclic, Numeric, Alphabetic, Symbolic, Additive, Fixed, Chinese, Korean, Hebrew, Ethiopic }

/// <summary>
/// A counter style (CSS Counter Styles 3): how a counter value is written, as a predefined style or an
/// <c>@counter-style</c> rule. <see cref="Represent(int)"/> gives the text of counter() and counters();
/// <see cref="MarkerText"/> adds the prefix and suffix for a list marker.
/// </summary>
internal sealed class CounterStyle
{
    public string Name = "";
    public CounterSystem System = CounterSystem.Symbolic;
    public string[] Symbols = Array.Empty<string>();
    public (int Weight, string Symbol)[] AdditiveSymbols = Array.Empty<(int, string)>();
    public int First = 1;                      // the first value of a fixed system
    public string NegativePrefix = "-", NegativeSuffix = "";
    public string Prefix = "", Suffix = ". ";
    public (long Low, long High)[]? Range;     // null is auto: the range the system can represent
    public int PadLength;
    public string PadSymbol = "";
    public string Fallback = "decimal";
    public string[] ChineseMarkers = Array.Empty<string>(); // Chinese and Korean: the marks of 10, 100, 1000, 10^4 and 10^8
    public bool ChineseInformal;               // Chinese and Korean: the informal style, which drops some ones

    /// <summary>The set the fallback style is looked up in; null for the predefined styles, which use each other.</summary>
    internal CounterStyles? Owner;

    // Representations far beyond any real use (a symbolic counter in the millions, a pad of thousands) fall back.
    private const int MaxLength = 120;
    private const int MaxFallbackDepth = 8;

    public CounterStyle Clone() => (CounterStyle)MemberwiseClone();

    /// <summary>The marker text of a list item: the prefix, the counter's representation and the suffix.</summary>
    public string MarkerText(int value) => Prefix + Represent(value) + Suffix;

    /// <summary>The counter's representation, from the fallback style when this style cannot represent it.</summary>
    public string Represent(int value) => Represent(value, 0);

    private string Represent(int value, int depth)
    {
        bool negative = value < 0 && UsesNegative;
        long magnitude = negative ? -(long)value : value;
        if (PadLength <= MaxLength && InRange(value) && TryInitial(magnitude, out var text))
        {
            if (PadLength > 0)
            {
                int length = Graphemes(text) + (negative ? Graphemes(NegativePrefix) + Graphemes(NegativeSuffix) : 0);
                if (length < PadLength && PadSymbol.Length > 0)
                    text = string.Concat(Enumerable.Repeat(PadSymbol, PadLength - length)) + text;
            }
            return negative ? NegativePrefix + text + NegativeSuffix : text;
        }
        if (depth >= MaxFallbackDepth) return value.ToString(CultureInfo.InvariantCulture);
        var fallback = (Owner ?? CounterStyles.Predefined).Get(Fallback);
        return fallback == this ? value.ToString(CultureInfo.InvariantCulture) : fallback.Represent(value, depth + 1);
    }

    private static int Graphemes(string s) => s.Length == 0 ? 0 : new StringInfo(s).LengthInTextElements;

    private bool UsesNegative => System is CounterSystem.Symbolic or CounterSystem.Alphabetic or CounterSystem.Numeric
        or CounterSystem.Additive or CounterSystem.Chinese or CounterSystem.Korean or CounterSystem.Ethiopic;

    private bool InRange(int value)
    {
        if (Range == null)
            return System switch
            {
                CounterSystem.Alphabetic or CounterSystem.Symbolic or CounterSystem.Ethiopic => value >= 1,
                CounterSystem.Additive => value >= 0,
                _ => true,
            };
        foreach (var (low, high) in Range)
            if (value >= low && value <= high) return true;
        return false;
    }

    /// <summary>The representation of <paramref name="v"/> (the magnitude, when the style uses a negative sign) by the system's algorithm.</summary>
    private bool TryInitial(long v, out string text)
    {
        text = "";
        int n = Symbols.Length;
        switch (System)
        {
            case CounterSystem.Cyclic:
                if (n == 0) return false;
                text = Symbols[(int)(((v - 1) % n + n) % n)];
                return true;
            case CounterSystem.Fixed:
            {
                long k = v - First;
                if (k < 0 || k >= n) return false;
                text = Symbols[(int)k];
                return true;
            }
            case CounterSystem.Symbolic:
            {
                if (v < 1 || n == 0) return false;
                long repeat = (v - 1) / n + 1;
                var symbol = Symbols[(int)((v - 1) % n)];
                if (repeat * symbol.Length > MaxLength) return false;
                text = string.Concat(Enumerable.Repeat(symbol, (int)repeat));
                return true;
            }
            case CounterSystem.Alphabetic:
            {
                if (v < 1 || n < 2) return false;
                var parts = new List<string>();
                while (v > 0)
                {
                    v--;
                    parts.Add(Symbols[(int)(v % n)]);
                    v /= n;
                }
                parts.Reverse();
                text = string.Concat(parts);
                return true;
            }
            case CounterSystem.Numeric:
            {
                if (n < 2) return false;
                if (v == 0) { text = Symbols[0]; return true; }
                var parts = new List<string>();
                while (v > 0)
                {
                    parts.Add(Symbols[(int)(v % n)]);
                    v /= n;
                }
                parts.Reverse();
                text = string.Concat(parts);
                return true;
            }
            case CounterSystem.Additive:
                return TryAdditive(v, out text);
            case CounterSystem.Chinese:
                return TryChinese(v, out text);
            case CounterSystem.Korean:
                return TryKorean(v, out text);
            case CounterSystem.Hebrew:
                return TryHebrew(v, out text);
            case CounterSystem.Ethiopic:
                return TryEthiopic(v, out text);
        }
        return false;
    }

    private bool TryAdditive(long v, out string text)
    {
        text = "";
        if (v == 0)
        {
            foreach (var (weight, symbol) in AdditiveSymbols)
                if (weight == 0) { text = symbol; return true; }
            return false;
        }
        var sb = new StringBuilder();
        foreach (var (weight, symbol) in AdditiveSymbols)
        {
            if (weight == 0 || weight > v) continue;
            long repeat = v / weight;
            if (sb.Length + repeat * symbol.Length > MaxLength) return false;
            for (long r = 0; r < repeat; r++) sb.Append(symbol);
            v -= repeat * weight;
            if (v == 0) { text = sb.ToString(); return true; }
        }
        return false;
    }

    /// <summary>
    /// Chinese longhand numbers, as Chrome writes them: groups of four digits with the marks for ten thousand and a
    /// hundred million, each group with its digits' place marks; zeros inside a group, and a group that starts with a
    /// zero, are written as one zero, and the informal styles drop the one of a leading 10 to 19 (十五, 十万).
    /// </summary>
    private bool TryChinese(long v, out string text)
    {
        text = "";
        if (Symbols.Length != 10 || ChineseMarkers.Length != 5) return false;
        if (v == 0) { text = Symbols[0]; return true; }
        var sb = new StringBuilder();
        bool any = false, pendingZero = false;
        for (int k = 2; k >= 0; k--)
        {
            int g = (int)(v / Pow10(4 * k) % 10000);
            if (k == 2 && v / Pow10(8) >= 10000) return false;
            if (g == 0)
            {
                pendingZero |= any;
                continue;
            }
            if (any && (pendingZero || g < 1000)) sb.Append(Symbols[0]);
            ChineseGroup(sb, g, leading: !any);
            if (k > 0) sb.Append(ChineseMarkers[2 + k]);
            any = true;
            pendingZero = false;
        }
        text = sb.ToString();
        return true;
    }

    private void ChineseGroup(StringBuilder sb, int g, bool leading)
    {
        int top = 3;
        while (g / (int)Pow10(top) == 0) top--;
        bool zero = false;
        for (int p = top; p >= 0; p--)
        {
            int d = g / (int)Pow10(p) % 10;
            if (d == 0) { zero = true; continue; }
            if (zero) { sb.Append(Symbols[0]); zero = false; }
            bool dropOne = ChineseInformal && leading && p == 1 && d == 1 && top == 1;
            if (!dropOne) sb.Append(Symbols[d]);
            if (p > 0) sb.Append(ChineseMarkers[p - 1]);
        }
    }

    /// <summary>
    /// Korean numbers, as Chrome writes them: groups of four digits with the marks for ten thousand and a hundred
    /// million, separated by spaces, zeros left out; the informal style drops the one before a mark (百十五, 萬).
    /// </summary>
    private bool TryKorean(long v, out string text)
    {
        text = "";
        if (Symbols.Length != 10 || ChineseMarkers.Length != 5) return false;
        if (v == 0) { text = Symbols[0]; return true; }
        if (v / Pow10(8) >= 10000) return false;
        var sb = new StringBuilder();
        for (int k = 2; k >= 0; k--)
        {
            int g = (int)(v / Pow10(4 * k) % 10000);
            if (g == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            for (int p = 3; p >= 0; p--)
            {
                int d = g / (int)Pow10(p) % 10;
                if (d == 0) continue;
                bool dropOne = ChineseInformal && d == 1 && (p > 0 || k > 0);
                if (!dropOne) sb.Append(Symbols[d]);
                if (p > 0) sb.Append(ChineseMarkers[p - 1]);
            }
            if (k > 0) sb.Append(ChineseMarkers[2 + k]);
        }
        text = sb.ToString();
        return true;
    }

    /// <summary>
    /// Hebrew numbers, as Chrome writes them up to 999999: the thousands as a number of their own followed by a geresh,
    /// then the rest, each from the additive letters for 1 to 999 (with 15 and 16 written as 9 + 6 and 9 + 7).
    /// </summary>
    private bool TryHebrew(long v, out string text)
    {
        text = "";
        if (v == 0) { text = "אפס"; return true; }
        if (v > 999999) return false;
        long thousands = v / 1000, rest = v % 1000;
        string high = "", low = "";
        if (thousands > 0 && !TryAdditive(thousands, out high)) return false;
        if (rest > 0 && !TryAdditive(rest, out low)) return false;
        text = (thousands > 0 ? high + "׳" : "") + low;
        return true;
    }

    /// <summary>
    /// The Ethiopic numbering algorithm (CSS Counter Styles 3, section 7.1.3): pairs of digits, the odd pairs followed
    /// by ፻ (hundred) and the even ones after the first by ፼ (ten thousand), with a one that a mark stands for left out.
    /// </summary>
    private static bool TryEthiopic(long v, out string text)
    {
        text = "";
        if (v < 1) return false;
        if (v == 1) { text = "፩"; return true; }
        var groups = new List<int>();
        for (long x = v; x > 0; x /= 100) groups.Add((int)(x % 100));
        var sb = new StringBuilder();
        for (int k = groups.Count - 1; k >= 0; k--)
        {
            int g = groups[k];
            bool odd = (k & 1) == 1;
            bool drop = g == 0 || g == 1 && (k == groups.Count - 1 || odd);
            if (!drop)
            {
                if (g >= 10) sb.Append((char)(0x1372 + g / 10 - 1));
                if (g % 10 > 0) sb.Append((char)(0x1369 + g % 10 - 1));
            }
            if (odd && g != 0) sb.Append('፻');
            else if (!odd && k > 0) sb.Append('፼');
        }
        text = sb.ToString();
        return true;
    }

    internal static long Pow10(int n)
    {
        long r = 1;
        for (int i = 0; i < n; i++) r *= 10;
        return r;
    }
}

/// <summary>
/// The counter styles a document can use: the predefined ones of CSS Counter Styles 3 and its own
/// <c>@counter-style</c> rules, which can replace predefined styles other than decimal and the bullets.
/// </summary>
internal sealed class CounterStyles
{
    private readonly Dictionary<string, CounterStyle> _styles;

    private CounterStyles(Dictionary<string, CounterStyle> styles) => _styles = styles;

    /// <summary>The predefined styles alone.</summary>
    public static readonly CounterStyles Predefined = new(BuildPredefined());

    /// <summary>The predefined styles and the document's rules; rules are given in source order, so a later one with a name wins.</summary>
    public static CounterStyles From(IReadOnlyList<CounterStyleRule> rules)
    {
        if (rules.Count == 0) return Predefined;
        var byName = new Dictionary<string, CounterStyleRule>(StringComparer.Ordinal);
        foreach (var r in rules) byName[r.Name] = r;
        var set = new CounterStyles(new Dictionary<string, CounterStyle>(Predefined._styles, StringComparer.Ordinal));
        var built = new Dictionary<string, CounterStyle?>(StringComparer.Ordinal);
        foreach (var name in byName.Keys) set.Define(name, byName, built);
        return set;
    }

    /// <summary>
    /// Builds the style a rule defines, after the style it extends. Rules that extend each other in a loop extend
    /// decimal instead, and so does a rule that extends a style that does not exist.
    /// </summary>
    private CounterStyle? Define(string name, Dictionary<string, CounterStyleRule> rules, Dictionary<string, CounterStyle?> built)
    {
        if (built.TryGetValue(name, out var done)) return done;
        var rule = rules[name];
        CounterStyle? style;
        if (rule.Extends is { } baseName)
        {
            var baseStyle = InLoop(name, rules) ? null
                : (rules.ContainsKey(baseName) ? Define(baseName, rules, built) : null) ?? FindStyle(baseName);
            style = rule.Apply((baseStyle ?? Predefined.Get("decimal")).Clone());
        }
        else style = rule.Apply(new CounterStyle());
        built[name] = style;
        if (style == null) return null;
        style.Name = name;
        style.Owner = this;
        _styles[name] = style;
        return style;
    }

    /// <summary>True when following the extends of the rules from <paramref name="name"/> comes back to it.</summary>
    private static bool InLoop(string name, Dictionary<string, CounterStyleRule> rules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = rules[name].Extends;
        while (current != null && rules.TryGetValue(current, out var next))
        {
            if (current == name) return true;
            if (!seen.Add(current)) return false;
            current = next.Extends;
        }
        return false;
    }

    private CounterStyle? FindStyle(string name)
        => _styles.TryGetValue(name, out var s) ? s : _styles.TryGetValue(name.ToLowerInvariant(), out s) && IsPredefinedName(name) ? s : null;

    private static bool IsPredefinedName(string name) => Predefined._styles.ContainsKey(name.ToLowerInvariant());

    /// <summary>Whether this set has a style of exactly this name.</summary>
    public bool IsDefined(string name) => _styles.ContainsKey(name);

    /// <summary>The style with this name; decimal when there is none, as the spec says for an unknown name.</summary>
    public CounterStyle Get(string name)
    {
        if (TryGet(name, out var style)) return style;
        return _styles["decimal"];
    }

    /// <summary>
    /// The style a list-style-type or counter() value names: a name (unknown names are decimal) or a symbols()
    /// function. False when the value is neither.
    /// </summary>
    public bool TryGet(string value, out CounterStyle style)
    {
        style = null!;
        var v = value.Trim();
        if (v.StartsWith("symbols(", StringComparison.OrdinalIgnoreCase) && v.EndsWith(")", StringComparison.Ordinal))
        {
            // Built on each use, not cached: the predefined set is shared by every conversion, on any thread.
            if (Anonymous(v.Substring(8, v.Length - 9)) is not { } anon) return false;
            style = anon;
            return true;
        }
        if (!IsIdent(v)) return false;
        style = FindStyle(v) ?? _styles["decimal"];
        return true;
    }

    /// <summary>symbols(type? string+): an anonymous style with a space as suffix.</summary>
    private static CounterStyle? Anonymous(string args)
    {
        var tokens = CssParser.Tokens(args);
        var system = CounterSystem.Symbolic;
        int i = 0;
        if (tokens.Count > 0 && tokens[0].Length > 0 && tokens[0][0] is not ('"' or '\''))
        {
            switch (tokens[0].ToLowerInvariant())
            {
                case "cyclic": system = CounterSystem.Cyclic; break;
                case "numeric": system = CounterSystem.Numeric; break;
                case "alphabetic": system = CounterSystem.Alphabetic; break;
                case "symbolic": system = CounterSystem.Symbolic; break;
                case "fixed": system = CounterSystem.Fixed; break;
                default: return null;
            }
            i = 1;
        }
        var symbols = new List<string>();
        for (; i < tokens.Count; i++)
        {
            if (GeneratedContent.UnquoteString(tokens[i]) is not { } s) return null;
            symbols.Add(s);
        }
        if (symbols.Count == 0 || system is CounterSystem.Numeric or CounterSystem.Alphabetic && symbols.Count < 2) return null;
        return new CounterStyle { Name = "", System = system, Symbols = symbols.ToArray(), Suffix = " " };
    }

    internal static bool IsIdent(string s)
    {
        if (s.Length == 0) return false;
        int start = s[0] == '-' ? 1 : 0;
        if (start >= s.Length || !(char.IsLetter(s[start]) || s[start] is '_' or '-' || s[start] > 0x7F)) return false;
        foreach (char c in s)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' || c > 0x7F)) return false;
        return true;
    }

    // ---------------------------------------------------------------- the predefined styles

    private static Dictionary<string, CounterStyle> BuildPredefined()
    {
        var d = new Dictionary<string, CounterStyle>(StringComparer.Ordinal);
        void Add(CounterStyle s) => d[s.Name] = s;
        static string[] Chars(string s)
        {
            var list = new List<string>();
            var e = StringInfo.GetTextElementEnumerator(s);
            while (e.MoveNext()) list.Add((string)e.Current);
            return list.ToArray();
        }
        static string[] Run(int first, int count) => Enumerable.Range(first, count).Select(c => ((char)c).ToString()).ToArray();
        CounterStyle Numeric(string name, int zero, string suffix = ". ") => new() { Name = name, System = CounterSystem.Numeric, Symbols = Run(zero, 10), Suffix = suffix };
        CounterStyle Alphabetic(string name, string[] symbols, string suffix = ". ") => new() { Name = name, System = CounterSystem.Alphabetic, Symbols = symbols, Suffix = suffix };
        CounterStyle Bullet(string name, string symbol) => new() { Name = name, System = CounterSystem.Cyclic, Symbols = new[] { symbol }, Suffix = " " };

        Add(Numeric("decimal", '0'));
        Add(new CounterStyle { Name = "decimal-leading-zero", System = CounterSystem.Numeric, Symbols = Run('0', 10), PadLength = 2, PadSymbol = "0" });
        Add(Numeric("arabic-indic", 0x660));
        Add(Numeric("persian", 0x6F0));
        Add(Numeric("devanagari", 0x966));
        Add(Numeric("bengali", 0x9E6));
        Add(Numeric("gurmukhi", 0xA66));
        Add(Numeric("gujarati", 0xAE6));
        Add(Numeric("oriya", 0xB66));
        Add(Numeric("tamil", 0xBE6));
        Add(Numeric("telugu", 0xC66));
        Add(Numeric("kannada", 0xCE6));
        Add(Numeric("malayalam", 0xD66));
        Add(Numeric("thai", 0xE50));
        Add(Numeric("lao", 0xED0));
        Add(Numeric("tibetan", 0xF20));
        Add(Numeric("myanmar", 0x1040));
        Add(Numeric("khmer", 0x17E0));
        Add(Numeric("cambodian", 0x17E0));
        Add(Numeric("mongolian", 0x1810));
        var cjkDecimal = new CounterStyle
        {
            Name = "cjk-decimal", System = CounterSystem.Numeric, Symbols = Chars("〇一二三四五六七八九"), Suffix = "、",
            Range = new[] { (0L, (long)int.MaxValue) },
        };
        Add(cjkDecimal);

        var latin = Run('a', 26);
        var upperLatin = Run('A', 26);
        Add(Alphabetic("lower-alpha", latin));
        Add(Alphabetic("lower-latin", latin));
        Add(Alphabetic("upper-alpha", upperLatin));
        Add(Alphabetic("upper-latin", upperLatin));
        Add(Alphabetic("lower-greek", Chars("αβγδεζηθικλμνξοπρστυφχψω")));
        Add(Alphabetic("hiragana", Chars("あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわゐゑをん"), "、"));
        Add(Alphabetic("hiragana-iroha", Chars("いろはにほへとちりぬるをわかよたれそつねならむうゐのおくやまけふこえてあさきゆめみしゑひもせす"), "、"));
        Add(Alphabetic("katakana", Chars("アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヰヱヲン"), "、"));
        Add(Alphabetic("katakana-iroha", Chars("イロハニホヘトチリヌルヲワカヨタレソツネナラムウヰノオクヤマケフコエテアサキユメミシヱヒモセス"), "、"));

        Add(Bullet("disc", "•"));
        Add(Bullet("circle", "◦"));
        Add(Bullet("square", "■")); // Chrome's square, U+25A0, rather than the spec's U+25AA
        Add(Bullet("disclosure-open", "▾"));
        Add(Bullet("disclosure-closed", "▸"));

        Add(new CounterStyle
        {
            Name = "cjk-earthly-branch", System = CounterSystem.Fixed, Symbols = Chars("子丑寅卯辰巳午未申酉戌亥"), Suffix = "、", Fallback = "cjk-decimal",
        });
        Add(new CounterStyle
        {
            Name = "cjk-heavenly-stem", System = CounterSystem.Fixed, Symbols = Chars("甲乙丙丁戊己庚辛壬癸"), Suffix = "、", Fallback = "cjk-decimal",
        });

        // Roman numerals, 1 to 3999.
        (int, string)[] roman = { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        Add(Additive("upper-roman", roman, 1, 3999));
        Add(Additive("lower-roman", roman.Select(r => (r.Item1, r.Item2.ToLowerInvariant())).ToArray(), 1, 3999));

        // Armenian: letters for 1 to 9, the tens, hundreds and thousands, in alphabet order from U+0531 (lower case
        // U+0561); with a combining circumflex they are worth ten thousand times as much, as Chrome writes them.
        (int, string)[] Armenian(int first)
        {
            var list = new List<(int, string)>();
            for (int k = 71; k >= 0; k--)
            {
                int place = k / 9, digit = k % 9 + 1;
                list.Add(((int)CounterStyle.Pow10(place) * digit, ((char)(first + k % 36)).ToString() + (k >= 36 ? "\u0302" : "")));
            }
            return list.ToArray();
        }
        Add(Additive("armenian", Armenian(0x531), 1, 99999999));
        Add(Additive("upper-armenian", Armenian(0x531), 1, 99999999));
        Add(Additive("lower-armenian", Armenian(0x561), 1, 99999999));

        Add(Additive("georgian", Pairs(
            "10000 ჵ 9000 ჰ 8000 ჯ 7000 ჴ 6000 ხ 5000 ჭ 4000 წ 3000 ძ 2000 ც 1000 ჩ 900 შ 800 ყ 700 ღ 600 ქ " +
            "500 ფ 400 ჳ 300 ტ 200 ს 100 რ 90 ჟ 80 პ 70 ო 60 ჲ 50 ნ 40 მ 30 ლ 20 კ 10 ი 9 თ 8 ჱ 7 ზ 6 ვ 5 ე " +
            "4 დ 3 გ 2 ბ 1 ა"), 1, 19999));
        Add(new CounterStyle
        {
            Name = "hebrew", System = CounterSystem.Hebrew, Range = new[] { (0L, 999999L) },
            AdditiveSymbols = Pairs("400 ת 300 ש 200 ר 100 ק 90 צ 80 פ 70 ע 60 ס 50 נ 40 מ 30 ל 20 כ 19 יט 18 יח 17 יז 16 טז 15 טו " +
                                    "10 י 9 ט 8 ח 7 ז 6 ו 5 ה 4 ד 3 ג 2 ב 1 א"),
        });
        Add(new CounterStyle { Name = "ethiopic-numeric", System = CounterSystem.Ethiopic, Suffix = "/ " });

        // Japanese: additive, -9999 to 9999, each digit with its place's mark.
        const string minus = "マイナス";
        Add(Longhand("japanese-informal", "一二三四五六七八九", "十百千", "〇", informalOne: true, minus, "、"));
        Add(Longhand("japanese-formal", "壱弐参四伍六七八九", "拾百阡", "零", informalOne: false, minus, "、"));

        Add(Grouped("korean-hangul-formal", CounterSystem.Korean, "영일이삼사오육칠팔구", "십백천만억", false, "마이너스 ", ", "));
        Add(Grouped("korean-hanja-informal", CounterSystem.Korean, "零一二三四五六七八九", "十百千萬億", true, "마이너스 ", ", "));
        Add(Grouped("korean-hanja-formal", CounterSystem.Korean, "零壹貳參四五六七八九", "拾百仟萬億", false, "마이너스 ", ", "));
        Add(Grouped("simp-chinese-informal", CounterSystem.Chinese, "零一二三四五六七八九", "十百千万亿", true, "负", "、"));
        Add(Grouped("simp-chinese-formal", CounterSystem.Chinese, "零壹贰叁肆伍陆柒捌玖", "拾佰仟万亿", false, "负", "、"));
        Add(Grouped("trad-chinese-informal", CounterSystem.Chinese, "零一二三四五六七八九", "十百千萬億", true, "負", "、"));
        Add(Grouped("trad-chinese-formal", CounterSystem.Chinese, "零壹貳參肆伍陸柒捌玖", "拾佰仟萬億", false, "負", "、"));
        Add(Grouped("cjk-ideographic", CounterSystem.Chinese, "零一二三四五六七八九", "十百千萬億", true, "負", "、"));
        return d;

        static CounterStyle Additive(string name, (int, string)[] symbols, long low, long high)
            => new() { Name = name, System = CounterSystem.Additive, AdditiveSymbols = symbols, Range = new[] { (low, high) } };

        static (int, string)[] Pairs(string list)
        {
            var parts = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new (int, string)[parts.Length / 2];
            for (int k = 0; k < result.Length; k++) result[k] = (int.Parse(parts[2 * k], CultureInfo.InvariantCulture), parts[2 * k + 1]);
            return result;
        }

        static CounterStyle Longhand(string name, string digits, string marks, string zero, bool informalOne, string negative, string suffix)
        {
            var list = new List<(int, string)>();
            for (int place = 2; place >= 0; place--)
            {
                int unit = (int)CounterStyle.Pow10(place + 1);
                for (int digit = 9; digit >= 1; digit--)
                    list.Add((digit * unit, (digit == 1 && informalOne ? "" : digits[digit - 1].ToString()) + marks[place]));
            }
            for (int digit = 9; digit >= 1; digit--) list.Add((digit, digits[digit - 1].ToString()));
            list.Add((0, zero));
            return new CounterStyle
            {
                Name = name, System = CounterSystem.Additive, AdditiveSymbols = list.ToArray(), Range = new[] { (-9999L, 9999L) },
                NegativePrefix = negative, Suffix = suffix, Fallback = "cjk-decimal",
            };
        }

        static CounterStyle Grouped(string name, CounterSystem system, string digits, string marks, bool informal, string negative, string suffix)
            => new()
            {
                Name = name, System = system, Symbols = digits.Select(c => c.ToString()).ToArray(),
                ChineseMarkers = marks.Select(c => c.ToString()).ToArray(), ChineseInformal = informal,
                NegativePrefix = negative, Suffix = suffix, Fallback = "cjk-decimal",
            };
    }
}

/// <summary>
/// An <c>@counter-style</c> rule's descriptors, checked as the spec requires: a rule with an invalid system, symbols
/// its system cannot use, or additive weights out of order defines no style. Invalid descriptors other than those are
/// ignored.
/// </summary>
internal sealed class CounterStyleRule
{
    public string Name = "";
    public string? Extends;
    private readonly Dictionary<string, string> _descriptors = new(StringComparer.Ordinal);

    // Names that @counter-style cannot define: the bullets, decimal, none and the CSS-wide keywords.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "decimal", "disc", "square", "circle", "disclosure-open", "disclosure-closed", "none",
        "inherit", "initial", "unset", "default", "revert", "revert-layer",
    };

    /// <summary>The rule from its prelude (the name) and body, or null when it is invalid.</summary>
    public static CounterStyleRule? Parse(string prelude, string body)
    {
        var name = prelude.Trim();
        if (!CounterStyles.IsIdent(name) || Reserved.Contains(name.ToLowerInvariant())) return null;
        // Names are case-sensitive, but those of the predefined styles match in any case and are lower case.
        if (CounterStyles.Predefined.IsDefined(name.ToLowerInvariant())) name = name.ToLowerInvariant();
        var rule = new CounterStyleRule { Name = name };
        foreach (var part in CssParser.SplitTopLevel(CssParser.StripComments(body), ';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var key = part.Substring(0, colon).Trim().ToLowerInvariant();
            var value = part.Substring(colon + 1).Trim();
            if (value.Length > 0) rule._descriptors[key] = value;
        }
        if (rule._descriptors.TryGetValue("system", out var system))
        {
            var t = CssParser.Tokens(system);
            if (t.Count == 2 && t[0].Equals("extends", StringComparison.OrdinalIgnoreCase))
            {
                if (!CounterStyles.IsIdent(t[1]) || rule._descriptors.ContainsKey("symbols") || rule._descriptors.ContainsKey("additive-symbols")) return null;
                rule.Extends = t[1];
                if (CounterStyles.Predefined.IsDefined(t[1].ToLowerInvariant())) rule.Extends = t[1].ToLowerInvariant();
            }
        }
        return rule;
    }

    /// <summary>Applies the descriptors to a style (a new one, or a copy of the style extended); null when the rule is invalid.</summary>
    internal CounterStyle? Apply(CounterStyle s)
    {
        if (Extends == null)
        {
            s.System = CounterSystem.Symbolic;
            if (_descriptors.TryGetValue("system", out var system) && !TrySystem(system, s)) return null;
            if (s.System == CounterSystem.Additive)
            {
                if (!_descriptors.TryGetValue("additive-symbols", out var additive) || TryAdditive(additive) is not { } pairs) return null;
                s.AdditiveSymbols = pairs;
            }
            else
            {
                if (!_descriptors.TryGetValue("symbols", out var symbols) || TrySymbols(symbols) is not { } list) return null;
                if (s.System is CounterSystem.Alphabetic or CounterSystem.Numeric && list.Length < 2) return null;
                s.Symbols = list;
            }
        }
        foreach (var (key, value) in _descriptors)
        {
            switch (key)
            {
                case "prefix":
                    if (Symbol(value) is { } prefix) s.Prefix = prefix;
                    break;
                case "suffix":
                    if (Symbol(value) is { } suffix) s.Suffix = suffix;
                    break;
                case "negative":
                {
                    var t = CssParser.Tokens(value);
                    if (t.Count is 1 or 2 && Symbol(t[0]) is { } before && (t.Count == 1 || Symbol(t[1]) != null))
                    {
                        s.NegativePrefix = before;
                        s.NegativeSuffix = t.Count == 2 ? Symbol(t[1])! : "";
                    }
                    break;
                }
                case "range":
                    if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) s.Range = null;
                    else if (TryRange(value) is { } range) s.Range = range;
                    break;
                case "pad":
                {
                    var t = CssParser.Tokens(value);
                    if (t.Count != 2) break;
                    int k = int.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? 0 : 1;
                    if (int.TryParse(t[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) && length >= 0 && Symbol(t[1 - k]) is { } pad)
                    {
                        s.PadLength = length;
                        s.PadSymbol = pad;
                    }
                    break;
                }
                case "fallback":
                    if (CounterStyles.IsIdent(value)) s.Fallback = value;
                    break;
            }
        }
        return s;
    }

    private static bool TrySystem(string value, CounterStyle s)
    {
        var t = CssParser.Tokens(value.ToLowerInvariant());
        if (t.Count == 0) return false;
        switch (t[0])
        {
            case "cyclic": s.System = CounterSystem.Cyclic; break;
            case "numeric": s.System = CounterSystem.Numeric; break;
            case "alphabetic": s.System = CounterSystem.Alphabetic; break;
            case "symbolic": s.System = CounterSystem.Symbolic; break;
            case "additive": s.System = CounterSystem.Additive; break;
            case "fixed":
                s.System = CounterSystem.Fixed;
                s.First = 1;
                if (t.Count == 2)
                {
                    if (!int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first)) return false;
                    s.First = first;
                }
                return t.Count <= 2;
            default: return false;
        }
        return t.Count == 1;
    }

    private static string[]? TrySymbols(string value)
    {
        var list = new List<string>();
        foreach (var tok in CssParser.Tokens(value))
        {
            if (Symbol(tok) is not { } symbol) return null;
            list.Add(symbol);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static (int, string)[]? TryAdditive(string value)
    {
        var list = new List<(int, string)>();
        foreach (var part in CssParser.SplitTopLevel(value, ','))
        {
            var t = CssParser.Tokens(part.Trim());
            if (t.Count != 2) return null;
            int k = int.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? 0 : 1;
            if (!int.TryParse(t[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) || weight < 0) return null;
            if (Symbol(t[1 - k]) is not { } symbol) return null;
            // Weights must be in strictly descending order.
            if (list.Count > 0 && weight >= list[^1].Item1) return null;
            list.Add((weight, symbol));
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static (long, long)[]? TryRange(string value)
    {
        var list = new List<(long, long)>();
        foreach (var part in CssParser.SplitTopLevel(value, ','))
        {
            var t = CssParser.Tokens(part.Trim().ToLowerInvariant());
            if (t.Count != 2) return null;
            long low, high;
            if (t[0] == "infinite") low = long.MinValue;
            else if (!long.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out low)) return null;
            if (t[1] == "infinite") high = long.MaxValue;
            else if (!long.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out high)) return null;
            if (low > high) return null;
            list.Add((low, high));
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    /// <summary>A symbol: a quoted string or an identifier, with CSS escapes decoded. Images are not supported.</summary>
    private static string? Symbol(string token)
    {
        var t = token.Trim();
        if (t.Length == 0) return null;
        if (t[0] is '"' or '\'') return GeneratedContent.UnquoteString(t);
        if (t.Contains('(') || t.Contains('"') || t.Contains('\'')) return null;
        return t.Contains('\\') ? GeneratedContent.UnquoteString("\"" + t + "\"") : t;
    }
}
