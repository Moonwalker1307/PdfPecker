using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Text;

namespace PdfPecker.Tests;

/// <summary>Line breaking in Chinese, Japanese and Korean text without spaces (UAX #14 and CSS line-break).</summary>
public class CjkLineBreakTests
{
    private const float PageHeight = 800;

    private static List<string> LayoutLines(string html, float width)
    {
        var doc = HtmlParser.Parse(html);
        var root = new ComputedStyle { Display = Display.Block };
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, root);
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return Flatten(engine.Ops).OfType<TextOp>().Select(t => t.Text).ToList();
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    // Every ideograph and kana has the same advance in the fallback font, so a box a few characters wide holds exactly
    // that many.
    private static float Chars(int n)
    {
        var style = new ComputedStyle();
        return style.Font.MeasureUnits("漢", 0, 1) * style.FontSize / 1000f * n + 0.01f;
    }

    private static List<string> Segments(string s, bool keepAll = false, LineBreakStrictness strictness = LineBreakStrictness.Strict)
    {
        var list = new List<string>();
        int from = 0;
        while (from < s.Length)
        {
            int next = LineBreaker.NextBreak(s, from, keepAll, strictness);
            list.Add(s[from..next]);
            from = next;
        }
        return list;
    }

    [Fact]
    public void Chinese_breaks_between_ideographs_without_adding_spaces()
    {
        var lines = LayoutLines("<p>我们今天去公园散步</p>", Chars(4));
        Assert.Equal(["我们今天", "去公园散", "步"], lines);
    }

    [Fact]
    public void Japanese_breaks_between_kanji_and_kana()
    {
        var lines = LayoutLines("<p>日本語の文章を書きます</p>", Chars(5));
        Assert.Equal(["日本語の文", "章を書きま", "す"], lines);
    }

    [Fact]
    public void Closing_punctuation_never_starts_a_line()
    {
        // 三 cannot end the first line, as 。 may not start the next, so both move down.
        Assert.Equal(["一二", "三。四", "五"], LayoutLines("<p>一二三。四五</p>", Chars(3)));
        Assert.Equal(["一二", "三、四", "五"], LayoutLines("<p>一二三、四五</p>", Chars(3)));
        Assert.Equal(["一二", "三」四"], LayoutLines("<p>一二三」四</p>", Chars(3)));
    }

    [Fact]
    public void Opening_brackets_never_end_a_line()
    {
        Assert.Equal(["一二", "「三", "四」"], LayoutLines("<p>一二「三四」</p>", Chars(3)));
        Assert.Equal(["一二", "（三", "四）"], LayoutLines("<p>一二（三四）</p>", Chars(3)));
    }

    [Fact]
    public void Small_kana_and_the_prolonged_sound_mark_follow_line_break()
    {
        // auto and strict keep small kana and ー off the start of a line, as Chrome does; normal lets them start one.
        Assert.Equal(["あい", "うぇお"], LayoutLines("<p>あいうぇお</p>", Chars(3)));
        Assert.Equal(["あい", "うぇお"], LayoutLines("<p style='line-break: strict'>あいうぇお</p>", Chars(3)));
        Assert.Equal(["あいう", "ぇお"], LayoutLines("<p style='line-break: normal'>あいうぇお</p>", Chars(3)));
        Assert.Equal(["コー", "ヒー"], LayoutLines("<p>コーヒー</p>", Chars(3)));
        Assert.Equal(["コーヒ", "ー"], LayoutLines("<p style='line-break: loose'>コーヒー</p>", Chars(3)));
    }

    [Fact]
    public void Iteration_marks_break_only_under_loose()
    {
        Assert.Equal(["人々", "人"], Segments("人々人"));
        Assert.Equal(["人", "々", "人"], Segments("人々人", strictness: LineBreakStrictness.Loose));
    }

    [Fact]
    public void Keep_all_breaks_only_at_spaces_and_punctuation()
    {
        Assert.Equal(["我们今天", "去公园"], LayoutLines("<p style='word-break: keep-all'>我们今天 去公园</p>", Chars(3)));
        Assert.Equal(["一二、", "三四"], LayoutLines("<p style='word-break: keep-all'>一二、三四</p>", Chars(3)));
        Assert.Equal(["안녕하세요"], Segments("안녕하세요", keepAll: true));
        Assert.Equal(["안", "녕", "하", "세", "요"], Segments("안녕하세요"));
    }

    [Fact]
    public void Line_break_anywhere_ignores_the_punctuation_rules()
    {
        Assert.Equal(["一二三", "。"], LayoutLines("<p style='line-break: anywhere'>一二三。</p>", Chars(3)));
    }

    [Fact]
    public void A_break_between_elements_is_found_too()
    {
        // The coloured text is a run of its own, so the first line shows as two runs.
        Assert.Equal(["一二", "三", "四"], LayoutLines("<p>一二<span style='color: red'>三四</span></p>", Chars(3)));
    }

    [Fact]
    public void Mixed_latin_and_cjk_breaks_only_next_to_the_cjk()
    {
        Assert.Equal(["PDF", "文", "件", "abc"], Segments("PDF文件abc"));
        Assert.Equal(["100％", "漢"], Segments("100％漢"));
        Assert.Equal(["2024", "年"], Segments("2024年"));
        // Fullwidth digits and letters are ideographs to UAX #14.
        Assert.Equal(["２", "０", "年"], Segments("２０年"));
        Assert.Equal(["文", "“文”", "文"], Segments("文“文”文"));
        // Emoji sequences stay whole.
        Assert.Equal(["漢", "\U0001F468\u200D\U0001F469\u200D\U0001F467"], Segments("漢\U0001F468\u200D\U0001F469\u200D\U0001F467"));
        Assert.Equal(["漢", "\U0001F44D\U0001F3FD"], Segments("漢\U0001F44D\U0001F3FD"));
        // CJK Unified Ideographs Extension B, outside the BMP.
        Assert.Equal(["\U00020000", "\U00020001"], Segments("\U00020000\U00020001"));
    }

    [Theory]
    [InlineData("hello-world")]
    [InlineData("foo/bar!baz")]
    [InlineData("naïve")]
    [InlineData("שלום")]
    public void Text_without_cjk_is_left_alone(string word)
    {
        Assert.False(LineBreaker.HasEastAsian(word));
        Assert.Equal([word], Segments(word));
    }

    [Fact]
    public void Latin_words_in_cjk_text_stay_whole_on_their_line()
    {
        var lines = LayoutLines("<p>漢字abcdefgh漢字</p>", Chars(3));
        Assert.Equal("漢字abcdefgh漢字", string.Concat(lines));
        Assert.Contains("abcdefgh", lines.Single(l => l.Contains('a')));
    }

    [Fact]
    public void Nowrap_keeps_cjk_on_one_line()
    {
        Assert.Equal(["我们今天去公园"], LayoutLines("<p style='white-space: nowrap'>我们今天去公园</p>", Chars(3)));
    }
}
