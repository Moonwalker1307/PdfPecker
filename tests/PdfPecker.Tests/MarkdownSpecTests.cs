using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace PdfPecker.Tests;

/// <summary>
/// Runs the parser against the CommonMark 0.31.2 specification's examples. The parser does not pass all of them, so
/// <c>Fixtures/commonmark-passing.txt</c> lists the examples that pass today, and an example from that list that fails
/// is a regression.
/// </summary>
/// <remarks>
/// The spec is CC-BY-SA, so it is not part of the repository: the test downloads spec.json once into a cache folder and
/// returns early when that is impossible (offline runs stay green). Set PDFPECKER_COMMONMARK_SPEC to the path of a local
/// spec.json to use that file instead. After an improvement, record the new passing examples with
/// <c>PDFPECKER_UPDATE_COMMONMARK=1 dotnet test --filter MarkdownSpecTests -f net10.0</c>.
/// </remarks>
public class MarkdownSpecTests
{
    private const string SpecUrl = "https://spec.commonmark.org/0.31.2/spec.json";

    private static readonly string PassingFile = Path.Combine(SourceDirectory(), "Fixtures", "commonmark-passing.txt");

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private readonly ITestOutputHelper _output;

    public MarkdownSpecTests(ITestOutputHelper output) => _output = output;

    private sealed record Example(int Number, string Section, string Markdown, string Html);

    [Fact]
    public void Previously_passing_examples_still_pass()
    {
        var examples = LoadSpec();
        if (examples == null)
        {
            // Offline runs skip the check, but in CI a failed download would hide every regression behind a green test.
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                "The CommonMark spec could not be downloaded, so the spec examples could not be checked.");
            _output.WriteLine("The CommonMark spec could not be downloaded, so the spec examples were skipped.");
            return;
        }

        var options = new MarkdownOptions { GitHubExtensions = false, HeadingIds = false };
        var passing = new HashSet<int>();
        var perSection = new SortedDictionary<string, (int Passed, int Total)>(StringComparer.Ordinal);
        foreach (var example in examples)
        {
            string actual;
            try { actual = MarkdownToHtml.Convert(example.Markdown, options); }
            catch (Exception ex) { actual = "exception: " + ex.GetType().Name; }
            var ok = Normalize(actual) == Normalize(example.Html);
            if (ok) passing.Add(example.Number);
            perSection.TryGetValue(example.Section, out var counts);
            perSection[example.Section] = (counts.Passed + (ok ? 1 : 0), counts.Total + 1);
        }

        _output.WriteLine($"CommonMark 0.31.2: {passing.Count} of {examples.Count} examples pass ({100.0 * passing.Count / examples.Count:F1}%).");
        foreach (var (section, counts) in perSection)
            _output.WriteLine($"  {section}: {counts.Passed}/{counts.Total}");

        if (Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_COMMONMARK") == "1")
        {
            var lines = new List<string>
            {
                "# CommonMark 0.31.2 spec examples (numbers as in spec.json) that MarkdownToHtml renders correctly.",
                "# MarkdownSpecTests fails when one of them stops passing. Regenerate with PDFPECKER_UPDATE_COMMONMARK=1.",
            };
            lines.AddRange(passing.Order().Select(n => n.ToString()));
            File.WriteAllLines(PassingFile, lines);
            return;
        }

        var recorded = ReadPassing();
        var regressions = recorded.Where(n => !passing.Contains(n)).Order().ToList();
        var improvements = passing.Where(n => !recorded.Contains(n)).Order().ToList();
        if (improvements.Count > 0)
            _output.WriteLine("Newly passing examples, add them to commonmark-passing.txt: " + string.Join(", ", improvements));
        if (regressions.Count == 0) return;

        var byNumber = examples.ToDictionary(e => e.Number);
        var message = new StringBuilder($"{regressions.Count} CommonMark example(s) that passed before now fail:\n");
        foreach (var n in regressions.Take(10))
        {
            var e = byNumber[n];
            message.Append($"\nExample {n} ({e.Section})\n--- markdown\n{e.Markdown}--- expected\n{e.Html}--- actual\n{MarkdownToHtml.Convert(e.Markdown, options)}");
        }
        Assert.Fail(message.ToString());
    }

    [Fact]
    public void Normalization_ignores_whitespace_attribute_order_and_self_closing_slashes()
    {
        Assert.Equal(Normalize("<ul>\n<li>a</li>\n</ul>\n"), Normalize("<ul><li>a</li></ul>"));
        Assert.Equal(Normalize("<img src=\"a\" alt=\"b\" />"), Normalize("<img alt='b' src=a>"));
        Assert.Equal(Normalize("<p>say &quot;hi&quot;</p>"), Normalize("<p>say \"hi\"</p>"));
        Assert.Equal(Normalize("<p>a\n  b</p>"), Normalize("<p>a b</p>"));
        Assert.NotEqual(Normalize("<pre><code>a  b</code></pre>"), Normalize("<pre><code>a b</code></pre>"));
        Assert.NotEqual(Normalize("<em>a</em>"), Normalize("<strong>a</strong>"));
    }

    private static HashSet<int> ReadPassing()
    {
        var numbers = new HashSet<int>();
        if (!File.Exists(PassingFile)) return numbers;
        foreach (var line in File.ReadAllLines(PassingFile))
            if (int.TryParse(line.Trim(), out var n)) numbers.Add(n);
        return numbers;
    }

    private static List<Example>? LoadSpec()
    {
        try
        {
            var json = ReadSpecJson();
            if (json == null) return null;
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Select(e => new Example(
                e.GetProperty("example").GetInt32(), e.GetProperty("section").GetString()!,
                e.GetProperty("markdown").GetString()!, e.GetProperty("html").GetString()!)).ToList();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadSpecJson()
    {
        var local = Environment.GetEnvironmentVariable("PDFPECKER_COMMONMARK_SPEC");
        if (!string.IsNullOrEmpty(local)) return File.ReadAllText(local);

        var cache = Path.Combine(Path.GetTempPath(), "pdfpecker-tests", "commonmark-0.31.2-spec.json");
        if (File.Exists(cache)) return File.ReadAllText(cache);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string json;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                json = client.GetStringAsync(SpecUrl).GetAwaiter().GetResult();
                break;
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        // Written to a temporary name first so a parallel run never reads half a file.
        var temporary = cache + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, json);
        File.Move(temporary, cache, overwrite: true);
        return json;
    }

    // ---- normalization, a small version of commonmark's test/normalize.py ----

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center", "col", "colgroup",
        "dd", "details", "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "frame",
        "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html", "iframe", "legend", "li", "link",
        "main", "menu", "menuitem", "nav", "noframes", "ol", "optgroup", "option", "p", "param", "pre", "section",
        "source", "summary", "table", "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track", "ul",
    };


    private static readonly Regex Tokens = new(@"<!--.*?-->|<![^>]*>|<\?.*?\?>|<(/?)([A-Za-z][A-Za-z0-9-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>|[^<]+|<",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Attributes = new(@"([^\s=/>]+)(?:\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+))?", RegexOptions.Compiled);

    private static string Normalize(string html)
    {
        // Each token is (text, isTag, isBlockTag); whitespace next to a block tag is not significant.
        var tokens = new List<(string Text, bool Tag, bool Block)>();
        var preDepth = 0;
        foreach (Match match in Tokens.Matches(html))
        {
            if (!match.Groups[2].Success)
            {
                var value = match.Value;
                if (value.StartsWith("<!", StringComparison.Ordinal) || value.StartsWith("<?", StringComparison.Ordinal))
                    tokens.Add((value, true, false));
                else
                    tokens.Add((preDepth > 0 ? Escape(WebUtility.HtmlDecode(value)) : Collapse(Escape(WebUtility.HtmlDecode(value))), false, false));
                continue;
            }

            var closing = match.Groups[1].Length > 0;
            var name = match.Groups[2].Value.ToLowerInvariant();
            if (name == "pre") preDepth = Math.Max(0, preDepth + (closing ? -1 : 1));
            var builder = new StringBuilder("<").Append(closing ? "/" : "").Append(name);
            if (!closing)
            {
                var attributes = Attributes.Matches(match.Groups[3].Value)
                    .Select(a => (Name: a.Groups[1].Value.ToLowerInvariant(), Value: Unquote(a.Groups[2].Value)))
                    .OrderBy(a => a.Name, StringComparer.Ordinal);
                foreach (var (attributeName, value) in attributes)
                    builder.Append(' ').Append(attributeName).Append("=\"").Append(Escape(WebUtility.HtmlDecode(value)).Replace("\"", "&quot;", StringComparison.Ordinal)).Append('"');
            }
            tokens.Add((builder.Append('>').ToString(), true, BlockTags.Contains(name)));
        }

        var result = new StringBuilder();
        var inPre = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var (text, tag, block) = tokens[i];
            if (tag)
            {
                if (text.StartsWith("<pre", StringComparison.Ordinal) && (text.Length == 5 || text[4] == ' ')) inPre++;
                else if (text == "</pre>") inPre = Math.Max(0, inPre - 1);
                result.Append(text);
                continue;
            }
            if (inPre == 0)
            {
                if (i > 0 && tokens[i - 1].Block) text = text.TrimStart();
                if (i + 1 < tokens.Count && tokens[i + 1].Block) text = text.TrimEnd();
            }
            result.Append(text);
        }
        return inPre == 0 ? result.ToString().Trim() : result.ToString();
    }

    private static string Unquote(string value)
        => value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    private static string Collapse(string text) => Regex.Replace(text, @"[ \t\r\n\f]+", " ");

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
