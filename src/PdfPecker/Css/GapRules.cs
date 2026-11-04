namespace PdfPecker.Css;

/// <summary>column-rule-break and row-rule-break: where the rule in a gap starts and ends at the gaps that cross it.</summary>
internal enum RuleBreak : byte { Normal, None, Intersection }

/// <summary>column-rule-visibility-items and row-rule-visibility-items: which parts of a gap next to empty cells get a rule.</summary>
internal enum RuleVisibility : byte { Normal, All, Around, Between }

/// <summary>One rule-inset longhand: a length, a percentage of the crossing gap, or overlap-join.</summary>
internal readonly record struct RuleInset(Length Length, bool OverlapJoin)
{
    public static readonly RuleInset Zero = new(Length.Zero, false);
}

/// <summary>The four rule-inset longhands of one direction: at the ends of a gap (caps) and where gaps cross (junctions).</summary>
internal readonly record struct RuleInsets(RuleInset CapStart, RuleInset CapEnd, RuleInset JunctionStart, RuleInset JunctionEnd)
{
    public static readonly RuleInsets Zero = new(RuleInset.Zero, RuleInset.Zero, RuleInset.Zero, RuleInset.Zero);
}

/// <summary>
/// The values a list of gap decoration values gives a container's gaps (CSS Gaps §4.6): the values before an auto
/// repeat go to the first gaps, those after it to the last gaps, and the auto repeat fills the gaps between. Without an
/// auto repeat the list starts again from its first value as often as there are gaps. Integer repeats are expanded when
/// the list is parsed.
/// </summary>
internal sealed class GapValues<T>
{
    /// <summary>The most values a list keeps once its integer repeats are expanded; a longer list is not valid.</summary>
    public const int MaxValues = 1000;

    private readonly T[] _leading, _trailing;
    private readonly T[]? _auto;

    public GapValues(T[] leading, T[]? auto, T[] trailing)
    {
        _leading = leading;
        _auto = auto;
        _trailing = trailing;
    }

    /// <summary>A list of one value.</summary>
    public GapValues(T value) : this([value], null, []) { }

    /// <summary>The value of the first gap when there are several, which the multi-column properties keep.</summary>
    public T First => _leading.Length > 0 ? _leading[0] : _auto is { Length: > 0 } a ? a[0] : _trailing[0];

    /// <summary>Whether the list holds a single value that every gap takes.</summary>
    public bool IsSingle => _auto == null && _leading.Length == 1;

    /// <summary>The value of gap <paramref name="gap"/> (from 0, in placement order) of <paramref name="count"/>.</summary>
    public T For(int gap, int count)
    {
        if (_auto == null) return _leading[gap % _leading.Length];
        int leading = Math.Min(_leading.Length, count);
        if (gap < leading) return _leading[gap];
        int trailing = Math.Min(_trailing.Length, count - leading);
        int firstTrailing = count - trailing;
        if (gap >= firstTrailing) return _trailing[gap - firstTrailing];
        return _auto[(gap - leading) % _auto.Length];
    }
}

/// <summary>Parses a gap decoration value: a single value, false when it is not one.</summary>
internal delegate bool GapValueParser<T>(string text, out T value);

/// <summary>
/// The gap decoration properties of a grid, flex or multi-column container (CSS Gaps 1) other than a single column
/// rule colour, style and width, which the multi-column properties keep. Few boxes set any of them, so a style holds
/// none until one is set, and a style that changes one copies the object first, since styles share it.
/// </summary>
internal sealed class GapRules
{
    /// <summary>Lists of column rule values; null while the property holds one value, which the multi-column properties keep.</summary>
    public GapValues<BorderStyle>? ColumnStyles;
    public GapValues<float>? ColumnWidths;
    public GapValues<uint?>? ColumnColors;     // null colours are currentcolor
    /// <summary>The row rule values; null for the initial none, medium and currentcolor.</summary>
    public GapValues<BorderStyle>? RowStyles;
    public GapValues<float>? RowWidths;
    public GapValues<uint?>? RowColors;
    public RuleBreak ColumnBreak, RowBreak;
    public RuleVisibility ColumnVisibility, RowVisibility;
    public bool ColumnOverRow;                 // rule-overlap: column-over-row
    public RuleInsets ColumnInsets = RuleInsets.Zero, RowInsets = RuleInsets.Zero;

    public GapRules Copy() => (GapRules)MemberwiseClone();

    /// <summary>
    /// Parses a comma-separated list of values with integer and auto repeat() notations. Returns null when it is not
    /// valid: a value that does not parse, more than one auto repeat, or more than <see cref="GapValues{T}.MaxValues"/>
    /// values once the integer repeats are expanded.
    /// </summary>
    public static GapValues<T>? ParseList<T>(string value, GapValueParser<T> parse)
    {
        var leading = new List<T>();
        var trailing = new List<T>();
        T[]? auto = null;
        foreach (var raw in CssParser.SplitTopLevel(value, ','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) return null;
            var into = auto == null ? leading : trailing;
            if (!entry.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase))
            {
                if (!parse(entry, out var single)) return null;
                into.Add(single);
            }
            else
            {
                if (!entry.EndsWith(')')) return null;
                var args = CssParser.SplitTopLevel(entry.Substring(7, entry.Length - 8), ',');
                if (args.Count < 2) return null;
                var count = args[0].Trim();
                var items = new T[args.Count - 1];
                for (int i = 1; i < args.Count; i++)
                    if (!parse(args[i].Trim(), out items[i - 1])) return null;
                if (count.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    if (auto != null) return null;
                    auto = items;
                    if (leading.Count + items.Length > GapValues<T>.MaxValues) return null;
                    continue;
                }
                if (!Invariant.TryParseInt(count, out int times) || times < 1 || (long)times * items.Length + into.Count > GapValues<T>.MaxValues)
                    return null;
                for (int t = 0; t < times; t++) into.AddRange(items);
            }
            if (leading.Count + trailing.Count + (auto?.Length ?? 0) > GapValues<T>.MaxValues) return null;
        }
        if (leading.Count == 0 && auto == null) return null;
        return new GapValues<T>(leading.ToArray(), auto, trailing.ToArray());
    }

    /// <summary>Whether a value is a list rather than one value: it has a comma or a repeat() outside any other function.</summary>
    public static bool IsList(string lower)
        => CssParser.SplitTopLevel(lower, ',').Count > 1 || lower.TrimStart().StartsWith("repeat(", StringComparison.Ordinal);

    /// <summary>A break keyword, or null.</summary>
    public static RuleBreak? ParseBreak(string lower) => lower switch
    {
        "normal" => RuleBreak.Normal,
        "none" => RuleBreak.None,
        "intersection" => RuleBreak.Intersection,
        _ => null,
    };

    /// <summary>A visibility keyword, or null.</summary>
    public static RuleVisibility? ParseVisibility(string lower) => lower switch
    {
        "normal" => RuleVisibility.Normal,
        "all" => RuleVisibility.All,
        "around" => RuleVisibility.Around,
        "between" => RuleVisibility.Between,
        _ => null,
    };
}
