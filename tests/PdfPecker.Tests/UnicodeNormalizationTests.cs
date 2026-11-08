using System.Globalization;
using System.Text;
using PdfPecker.Text;
using Xunit.Abstractions;

namespace PdfPecker.Tests;

/// <summary>
/// The library's own Unicode normalization, which shaping, hyphenation and AES-256 passwords use instead of
/// string.Normalize: that one asks ICU, and changes nothing where globalization is invariant (the command-line tool, the
/// Native AOT library of the Python, Node and Rust packages). CI runs this class with invariant globalization too.
/// </summary>
/// <remarks>
/// The Unicode conformance test file is large, so it is not part of the repository: the test downloads
/// NormalizationTest.txt of the Unicode version the tables are generated from once into a cache folder and returns early
/// when that is impossible (offline runs stay green). Set PDFPECKER_NORMALIZATION_TEST to the path of a local copy to use
/// that file instead.
/// </remarks>
public class UnicodeNormalizationTests
{
    private readonly ITestOutputHelper _output;

    public UnicodeNormalizationTests(ITestOutputHelper output) => _output = output;

    private static string Hex(string s) => string.Join(" ", EnumerateCodePoints(s).Select(cp => cp.ToString("X4", CultureInfo.InvariantCulture)));

    private static IEnumerable<int> EnumerateCodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsSurrogatePair(s, i))
            {
                yield return char.ConvertToUtf32(s, i);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }

    [Theory]
    [InlineData("Cafe\u0301", "Caf\u00E9", "Cafe\u0301")]
    [InlineData("\u1EA0\u0306", "\u1EB6", "A\u0323\u0306")] // A with dot below and a breve: a letter above U+0600
    [InlineData("Vie\u0302\u0323t", "Vi\u1EC7t", "Vie\u0323\u0302t")] // the marks are put in canonical order first
    [InlineData("\u1F00\u0301\u0345", "\u1F84", "\u03B1\u0313\u0301\u0345")] // polytonic Greek
    [InlineData("\u212B", "\u00C5", "A\u030A")] // a singleton: the Angstrom sign is a letter A with ring
    [InlineData("a\u0328\u0301", "\u0105\u0301", "a\u0328\u0301")] // a with ogonek has no precomposed form with an acute
    [InlineData("\u0627\u064E\u0653", "\u0622\u064E", "\u0627\u064E\u0653")] // alef and madda after a fatha
    [InlineData("\u1100\u1161\u11A8", "\uAC01", "\u1100\u1161\u11A8")] // Hangul jamo and syllables
    [InlineData("\u0301e", "\u0301e", "\u0301e")] // a mark at the start composes with nothing
    [InlineData("\u0915\u093C", "\u0915\u093C", "\u0915\u093C")] // Devanagari Qa is a composition exclusion
    [InlineData("\U0001109A", "\U0001109A", "\U00011099\U000110BA")] // Kaithi, outside the BMP
    public void Composes_and_decomposes_canonically(string text, string nfc, string nfd)
    {
        Assert.Equal(Hex(nfc), Hex(UnicodeNormalization.Normalize(text, NormalizationForm.FormC)));
        Assert.Equal(Hex(nfd), Hex(UnicodeNormalization.Normalize(text, NormalizationForm.FormD)));
        Assert.Equal(Hex(nfc), Hex(UnicodeNormalization.Normalize(nfd, NormalizationForm.FormC)));
    }

    [Theory]
    [InlineData("\uFB01ve", "five")] // the fi ligature
    [InlineData("\uFF50\uFF41\uFF53\uFF53", "pass")] // fullwidth letters
    [InlineData("x\u00B2\u00A0\u2460", "x2 1")] // superscript, no-break space, circled digit
    [InlineData("\uFDFA", "\u0635\u0644\u0649 \u0627\u0644\u0644\u0647 \u0639\u0644\u064A\u0647 \u0648\u0633\u0644\u0645")]
    [InlineData("\u1E9B\u0323", "\u1E69")] // long s with dot above and dot below: s with both dots
    [InlineData("\u3300", "\u30A2\u30D1\u30FC\u30C8")] // a squared katakana word, recomposed
    public void Compatibility_forms_fold(string text, string nfkc)
    {
        Assert.Equal(Hex(nfkc), Hex(UnicodeNormalization.Normalize(text, NormalizationForm.FormKC)));
        Assert.Equal(Hex(UnicodeNormalization.Normalize(nfkc, NormalizationForm.FormD)), Hex(UnicodeNormalization.Normalize(text, NormalizationForm.FormKD)));
    }

    [Fact]
    public void Text_in_the_form_already_comes_back_unchanged()
    {
        foreach (var text in new[] { "", "plain ASCII", "Caf\u00E9 \u00C5ngstr\u00F6m", "\u0627\u0644\u0639\u0631\u0628\u064A\u0629", "\uAC01\u4E2D\U0001F600" })
            Assert.Same(text, UnicodeNormalization.Normalize(text, NormalizationForm.FormC));
        Assert.Same("plain ASCII", UnicodeNormalization.Normalize("plain ASCII", NormalizationForm.FormKC));
        Assert.Same("e\u0301", UnicodeNormalization.Normalize("e\u0301", NormalizationForm.FormD));
    }

    [Fact]
    public void Unpaired_surrogates_stand_for_themselves()
    {
        Assert.Equal("\uD800\u00E9\uDC00", UnicodeNormalization.Normalize("\uD800e\u0301\uDC00", NormalizationForm.FormC));
        Assert.Equal("\uDC00\u0301", UnicodeNormalization.Normalize("\uDC00\u0301", NormalizationForm.FormKC));
        Assert.Equal(0, UnicodeNormalization.ComposePair(0xD800, 0x0301));
    }

    [Fact]
    public void Long_runs_of_marks_keep_canonical_order()
    {
        // Runs longer than the insertion sort handles take the keyed sort, which must also keep equal classes in order.
        var text = new StringBuilder("a");
        for (int i = 0; i < 5000; i++) text.Append(i % 3 == 0 ? '\u0301' : i % 3 == 1 ? '\u0323' : '\u0300');
        string above = string.Concat(text.ToString().Where(c => c is '\u0301' or '\u0300'));
        Assert.Equal("a" + new string('\u0323', 1667) + above, UnicodeNormalization.Normalize(text.ToString(), NormalizationForm.FormD));
        // The first dot below joins the a; the other marks are blocked by it or by marks of their own class.
        Assert.Equal("\u1EA1" + new string('\u0323', 1666) + above, UnicodeNormalization.Normalize(text.ToString(), NormalizationForm.FormC));
    }

    [Fact]
    public void Pairs_compose_as_their_normalization_does()
    {
        Assert.Equal(0x1EB6, UnicodeNormalization.ComposePair(0x1EA0, 0x0306));
        Assert.Equal(0x01FA, UnicodeNormalization.ComposePair(0x212B, 0x0301)); // the Angstrom sign decomposes first
        Assert.Equal(0x0622, UnicodeNormalization.ComposePair(0x0627, 0x0653));
        Assert.Equal(0, UnicodeNormalization.ComposePair(0x05D0, 0x05B7)); // Hebrew presentation forms are excluded
        Assert.Equal(0, UnicodeNormalization.ComposePair(0x0105, 0x0301));
        Assert.Equal(0, UnicodeNormalization.ComposePair('a', 'b'));
    }

    [Fact]
    public void Decompositions_fit_the_buffers()
    {
        // ComposePair decomposes into a buffer for two canonical decompositions, and Normalize keeps room for the longest.
        int longest = 0, longestCanonical = 0;
        foreach (int cp in UnicodeTables.DecompositionKeys)
        {
            string s = char.ConvertFromUtf32(cp);
            longest = Math.Max(longest, EnumerateCodePoints(UnicodeNormalization.Normalize(s, NormalizationForm.FormKD)).Count());
            longestCanonical = Math.Max(longestCanonical, EnumerateCodePoints(UnicodeNormalization.Normalize(s, NormalizationForm.FormD)).Count());
        }
        Assert.Equal(UnicodeNormalization.MaxDecomposition, longest);
        Assert.Equal(UnicodeNormalization.MaxCanonicalDecomposition, longestCanonical);
    }

    // ICU is missing where globalization is invariant, and then string.Normalize returns its input.
    private static bool HostNormalizes => "e\u0301".Normalize(NormalizationForm.FormC).Length == 1;

    [Fact]
    public void Matches_the_hosts_normalization_in_the_basic_multilingual_plane()
    {
        // Every decomposition in the BMP is older than any ICU .NET runs with, so the host must agree on all of it; the
        // characters that Unicode 15 and 16 added with decompositions are all outside the BMP, which the conformance
        // test below covers.
        if (!HostNormalizes)
        {
            _output.WriteLine("The host has no normalization (invariant globalization), so there is nothing to compare.");
            return;
        }
        var forms = new[] { NormalizationForm.FormC, NormalizationForm.FormD, NormalizationForm.FormKC, NormalizationForm.FormKD };
        var differences = new List<string>();
        for (int cp = 0; cp < 0x10000 && differences.Count < 10; cp++)
        {
            // .NET refuses to normalize surrogates and the noncharacters U+FFFE and U+FFFF.
            if (char.IsSurrogate((char)cp) || cp >= 0xFFFE) continue;
            string s = ((char)cp).ToString();
            foreach (var form in forms)
                if (UnicodeNormalization.Normalize(s, form) != s.Normalize(form)) differences.Add($"{form} of U+{cp:X4}");
        }
        // Each letter that composes or decomposes with each mark that composes, as the shaper asks.
        var letters = new SortedSet<int>();
        var marks = new SortedSet<int>();
        foreach (int cp in UnicodeTables.DecompositionKeys)
        {
            if (cp >= 0x10000 || UnicodeTables.FindDecomposition(cp, compatibility: false) is not (var k and >= 0)) continue;
            letters.Add(cp);
            var (start, end) = UnicodeTables.DecompositionMapping(k);
            for (int i = start; i < end;)
            {
                int part = UnicodeTables.MappingCodePoint(ref i);
                if (part < 0x10000) (UnicodeTables.CombiningClass(part) == 0 ? letters : marks).Add(part);
            }
        }
        foreach (int letter in letters)
            foreach (int mark in marks)
            {
                string pair = string.Concat((char)letter, (char)mark), nfc = pair.Normalize(NormalizationForm.FormC);
                int expected = nfc.Length == 1 ? nfc[0] : 0;
                if (UnicodeNormalization.ComposePair(letter, mark) != expected && differences.Count < 20)
                    differences.Add($"U+{letter:X4} U+{mark:X4}");
                if (UnicodeNormalization.Normalize(pair, NormalizationForm.FormC) != nfc && differences.Count < 20)
                    differences.Add($"NFC of U+{letter:X4} U+{mark:X4}");
            }
        Assert.Empty(differences);
    }

    [Fact]
    public void Conforms_to_the_unicode_normalization_test()
    {
        var lines = LoadConformanceTest();
        if (lines == null)
        {
            // Offline runs skip the check, but in CI a failed download would hide every regression behind a green test.
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                "NormalizationTest.txt could not be downloaded, so the conformance test could not run.");
            _output.WriteLine("NormalizationTest.txt could not be downloaded, so the conformance test was skipped.");
            return;
        }
        var failures = new List<string>();
        var listed = new HashSet<int>();
        bool part1 = false;
        int cases = 0;
        foreach (var raw in lines)
        {
            if (raw.StartsWith("@Part", StringComparison.Ordinal))
            {
                part1 = raw.StartsWith("@Part1", StringComparison.Ordinal);
                continue;
            }
            int hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) continue;
            var c = line.Split(';').Take(5).Select(field => string.Concat(field.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))))).ToArray();
            if (part1) listed.Add(char.ConvertToUtf32(c[0], 0));
            cases++;
            // UAX #15's conformance requirements: NFC c2 == NFC(c1..c3), c4 == NFC(c4, c5), and so on.
            Check(c, NormalizationForm.FormC, [1, 1, 1, 3, 3]);
            Check(c, NormalizationForm.FormD, [2, 2, 2, 4, 4]);
            Check(c, NormalizationForm.FormKC, [3, 3, 3, 3, 3]);
            Check(c, NormalizationForm.FormKD, [4, 4, 4, 4, 4]);
        }
        // Every code point not in part 1 is its own normalization in every form.
        for (int cp = 0; cp <= 0x10FFFF && failures.Count < 20; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF || listed.Contains(cp)) continue;
            string s = char.ConvertFromUtf32(cp);
            foreach (var form in new[] { NormalizationForm.FormC, NormalizationForm.FormD, NormalizationForm.FormKC, NormalizationForm.FormKD })
                if (UnicodeNormalization.Normalize(s, form) != s) failures.Add($"{form} of unlisted U+{cp:X4}");
        }
        _output.WriteLine($"{cases} conformance cases, {listed.Count} characters in part 1.");
        Assert.Empty(failures);

        void Check(string[] c, NormalizationForm form, int[] expected)
        {
            for (int i = 0; i < 5 && failures.Count < 20; i++)
            {
                var actual = UnicodeNormalization.Normalize(c[i], form);
                if (actual != c[expected[i]]) failures.Add($"{form}({Hex(c[i])}) = {Hex(actual)}, expected {Hex(c[expected[i]])}");
            }
        }
    }

    private static string[]? LoadConformanceTest()
    {
        try
        {
            var local = Environment.GetEnvironmentVariable("PDFPECKER_NORMALIZATION_TEST");
            if (!string.IsNullOrEmpty(local)) return File.ReadAllLines(local);
            var cache = Path.Combine(Path.GetTempPath(), "pdfpecker-tests", $"NormalizationTest-{UnicodeTables.Version}.txt");
            if (!File.Exists(cache))
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                string text;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        text = client.GetStringAsync($"https://www.unicode.org/Public/{UnicodeTables.Version}/ucd/NormalizationTest.txt").GetAwaiter().GetResult();
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
                File.WriteAllText(temporary, text);
                File.Move(temporary, cache, overwrite: true);
            }
            var lines = File.ReadAllLines(cache);
            // The file names its version in its first line, so a stale or truncated download is not mistaken for a pass.
            return lines.Length > 0 && lines[0].Contains($"-{UnicodeTables.Version}.txt", StringComparison.Ordinal) ? lines : null;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
