using System.Globalization;
using System.Text;

namespace PdfPecker.Fluent;

/// <summary>
/// Made-up text, numbers, dates, colours and images for trying out a layout before the real data is there, as in
/// QuestPDF. The values come from a fixed sequence on each thread, so a program that builds the same document the same
/// way gets the same PDF every time.
/// </summary>
public static class Placeholders
{
    private static readonly string[] Words =
    [
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor",
        "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim", "ad", "minim", "veniam", "quis",
        "nostrud", "exercitation", "ullamco", "laboris", "nisi", "aliquip", "ex", "ea", "commodo", "consequat", "duis",
        "aute", "irure", "in", "reprehenderit", "voluptate", "velit", "esse", "cillum", "fugiat", "nulla", "pariatur",
        "excepteur", "sint", "occaecat", "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt",
        "mollit", "anim", "id", "est", "laborum",
    ];

    private static readonly string[] FirstNames =
        ["Anna", "Ben", "Clara", "David", "Emma", "Felix", "Grace", "Hugo", "Ida", "Jonas", "Karen", "Lukas", "Maja", "Noah"];

    private static readonly string[] LastNames =
        ["Andersen", "Berg", "Carter", "Dahl", "Evans", "Fischer", "Hansen", "Jensen", "Keller", "Larsen", "Meyer", "Nielsen"];

    private static readonly string[] Domains = ["example.com", "example.org", "example.net"];

    private static readonly Color[] BackgroundColors =
    [
        Colors.Red.Lighten3, Colors.Pink.Lighten3, Colors.Purple.Lighten3, Colors.Indigo.Lighten3, Colors.Blue.Lighten3,
        Colors.Cyan.Lighten3, Colors.Teal.Lighten3, Colors.Green.Lighten3, Colors.Lime.Lighten3, Colors.Amber.Lighten3,
        Colors.Orange.Lighten3, Colors.Brown.Lighten3, Colors.Grey.Lighten3,
    ];

    private static readonly Color[] StrongColors =
    [
        Colors.Red.Medium, Colors.Pink.Medium, Colors.Purple.Medium, Colors.Indigo.Medium, Colors.Blue.Medium,
        Colors.Cyan.Medium, Colors.Teal.Medium, Colors.Green.Medium, Colors.Lime.Medium, Colors.Amber.Medium,
        Colors.Orange.Medium, Colors.Brown.Medium, Colors.Grey.Medium,
    ];

    [ThreadStatic] private static Random? _random;

    private static Random Random => _random ??= new Random(1307);

    private static string Word() => Words[Random.Next(Words.Length)];

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string RandomWords(int min, int max)
    {
        int count = Random.Next(min, max + 1);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Word());
        }
        return sb.ToString();
    }

    /// <summary>The classic lorem ipsum paragraph.</summary>
    public static string LoremIpsum()
        => "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore "
           + "magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo "
           + "consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla "
           + "pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id "
           + "est laborum.";

    /// <summary>A label of one to three words.</summary>
    public static string Label() => Capitalized(RandomWords(1, 3));

    /// <summary>A sentence of six to twelve words.</summary>
    public static string Sentence() => Capitalized(RandomWords(6, 12)) + ".";

    /// <summary>A question of four to eight words.</summary>
    public static string Question() => Capitalized(RandomWords(4, 8)) + "?";

    /// <summary>A paragraph of three to six sentences.</summary>
    public static string Paragraph()
    {
        int count = Random.Next(3, 7);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Sentence());
        }
        return sb.ToString();
    }

    /// <summary>Two to four paragraphs, separated by line breaks.</summary>
    public static string Paragraphs()
    {
        int count = Random.Next(2, 5);
        var parts = new string[count];
        for (int i = 0; i < count; i++) parts[i] = Paragraph();
        return string.Join("\n", parts);
    }

    /// <summary>A person's name.</summary>
    public static string Name() => FirstNames[Random.Next(FirstNames.Length)] + " " + LastNames[Random.Next(LastNames.Length)];

    /// <summary>An email address at an example domain.</summary>
    public static string Email()
        => Word() + "." + Word() + "@" + Domains[Random.Next(Domains.Length)];

    /// <summary>A phone number.</summary>
    public static string PhoneNumber()
        => string.Create(CultureInfo.InvariantCulture, $"+45 {Random.Next(10, 100)} {Random.Next(10, 100)} {Random.Next(10, 100)} {Random.Next(10, 100)}");

    /// <summary>A whole number from 0 to 9,999.</summary>
    public static string Integer() => Random.Next(0, 10_000).ToString(CultureInfo.InvariantCulture);

    /// <summary>A number from 0 to 9,999 with two decimals.</summary>
    public static string Decimal() => (Random.Next(0, 1_000_000) / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A percentage from 0 to 100, with one decimal.</summary>
    public static string Percent() => (Random.Next(0, 1001) / 10m).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static DateTime Date() => new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(Random.Next(0, 6 * 365 * 24 * 60));

    /// <summary>A time of day, such as 14:05.</summary>
    public static string Time() => Date().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A date, such as 2024-03-17.</summary>
    public static string ShortDate() => Date().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A date written out, such as 17 March 2024.</summary>
    public static string LongDate() => Date().ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A date and time, such as 2024-03-17 14:05.</summary>
    public static string DateTime() => Date().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A light colour, for backgrounds.</summary>
    public static Color BackgroundColor() => BackgroundColors[Random.Next(BackgroundColors.Length)];

    /// <summary>A strong colour, for text, lines and accents.</summary>
    public static Color Color() => StrongColors[Random.Next(StrongColors.Length)];

    /// <summary>An image of the given size in pixels: a soft two-colour gradient, as an SVG image.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is not 1 to 10,000.</exception>
    public static byte[] Image(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 10_000);
        var from = BackgroundColor();
        var to = Color();
        string w = width.ToString(CultureInfo.InvariantCulture), h = height.ToString(CultureInfo.InvariantCulture);
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"" + w + "\" height=\"" + h + "\" viewBox=\"0 0 " + w + " " + h + "\">"
            + "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">"
            + "<stop offset=\"0\" stop-color=\"" + from + "\"/><stop offset=\"1\" stop-color=\"" + to + "\"/></linearGradient></defs>"
            + "<rect width=\"" + w + "\" height=\"" + h + "\" fill=\"url(#g)\"/></svg>";
        return Encoding.UTF8.GetBytes(svg);
    }
}
