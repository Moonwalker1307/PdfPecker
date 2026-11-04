using System.Globalization;

namespace PdfPecker.Cli;

/// <summary>A command line the caller got wrong: it ends the run with exit code 2 and a pointer to the help.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>One option a command accepts: its names (the first is the canonical one) and how many values follow it.</summary>
internal sealed class OptionSpec
{
    public OptionSpec(string names, int arity)
    {
        Names = names.Split('|');
        Arity = arity;
    }

    public string[] Names { get; }
    public int Arity { get; }
    public string Name => Names[0];
}

/// <summary>
/// The options of a command. Option names are written without dashes: a name of one letter is a short option
/// (<c>-o</c>), longer names are long options (<c>--output</c>, also as <c>--output=file</c>).
/// </summary>
internal sealed class OptionSet
{
    private readonly Dictionary<string, OptionSpec> _byName = new(StringComparer.Ordinal);

    public OptionSet Flag(string names) => Add(names, 0);

    public OptionSet Value(string names) => Add(names, 1);

    public OptionSet Pair(string names) => Add(names, 2);

    public OptionSet Add(string names, int arity)
    {
        var spec = new OptionSpec(names, arity);
        foreach (var name in spec.Names) _byName[name] = spec;
        return this;
    }

    /// <summary>Adds the options of another set, for commands that share a group of settings.</summary>
    public OptionSet AddAll(OptionSet other)
    {
        foreach (var pair in other._byName) _byName[pair.Key] = pair.Value;
        return this;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public ParsedArgs Parse(IReadOnlyList<string> args)
    {
        var parsed = new ParsedArgs();
        bool optionsEnded = false;
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsEnded || arg.Length < 2 || arg[0] != '-')
            {
                parsed.Positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            string name;
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                name = arg.Substring(2);
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inline = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
            }
            else
            {
                name = arg.Substring(1);
            }

            if (!_byName.TryGetValue(name, out var spec))
                throw new UsageException($"Unknown option '{arg}'.");

            var values = new List<string>(spec.Arity);
            if (inline != null)
            {
                if (spec.Arity != 1) throw new UsageException($"Option '--{name}' does not take a value with '='.");
                values.Add(inline);
            }
            else
            {
                if (i + spec.Arity > args.Count - 1)
                    throw new UsageException($"Option '{arg}' needs {(spec.Arity == 1 ? "a value" : spec.Arity + " values")}.");
                for (int k = 0; k < spec.Arity; k++) values.Add(args[++i]);
            }

            parsed.Add(spec, values);
        }

        return parsed;
    }
}

/// <summary>The positional arguments and the option values found on a command line.</summary>
internal sealed class ParsedArgs
{
    private readonly Dictionary<string, List<string[]>> _values = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = new();

    /// <summary>Option names in the order they appeared, for options that take effect in order.</summary>
    public List<string> Seen { get; } = new();

    public void Add(OptionSpec spec, List<string> values)
    {
        if (!_values.TryGetValue(spec.Name, out var list)) _values[spec.Name] = list = new List<string[]>();
        list.Add(values.ToArray());
        Seen.Add(spec.Name);
    }

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>The value of a single-value option; the last one wins when it is repeated.</summary>
    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[list.Count - 1][0] : null;

    public IEnumerable<string> GetAll(string name)
        => _values.TryGetValue(name, out var list) ? list.Select(v => v[0]) : Enumerable.Empty<string>();

    public IEnumerable<string[]> GetPairs(string name)
        => _values.TryGetValue(name, out var list) ? list : Enumerable.Empty<string[]>();

    public float? GetFloat(string name)
    {
        var s = Get(name);
        if (s == null) return null;
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && float.IsFinite(f)
            ? f
            : throw new UsageException($"'{s}' is not a number (option --{name}).");
    }

    public int? GetInt(string name)
    {
        var s = Get(name);
        if (s == null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new UsageException($"'{s}' is not a whole number (option --{name}).");
    }

    public string Require(string name)
        => Get(name) ?? throw new UsageException($"Option --{name} is required.");
}
