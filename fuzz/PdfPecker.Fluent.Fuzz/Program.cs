using System.Diagnostics;
using System.Globalization;
using PdfPecker;
using PdfPecker.Fluent;

// A generative fuzzer: each case is a random document built from random fluent calls with hostile strings and edge
// values, reproducible from its seed. A case fails when the package throws an exception it does not document, when
// caller text turns into markup, or when generating takes too long.
//
// Usage: PdfPecker.Fluent.Fuzz [--seconds 60] [--seed 1] [--pdf-every 8] [--case N]
int seconds = 60, seed = 1, pdfEvery = 8;
int? only = null;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    int value = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
    switch (args[i])
    {
        case "--seconds": seconds = value; break;
        case "--seed": seed = value; break;
        case "--pdf-every": pdfEvery = value; break;
        case "--case": only = value; break;
        default: throw new ArgumentException("Unknown option " + args[i]);
    }
}

var watch = Stopwatch.StartNew();
int cases = 0, pdfs = 0, failures = 0, rejected = 0;
for (int n = only ?? 0; only == null ? watch.Elapsed.TotalSeconds < seconds : n == only; n++)
{
    cases++;
    // Each composition gets a fresh generator, so the HTML and the PDF see the same document.
    int caseSeed = unchecked(seed * 1_000_003 + n);
    var document = Document.Create(d => Fuzz.Compose(d, new Random(caseSeed)));
    bool pdf = only != null || n % pdfEvery == 0;
    string? failure = Fuzz.Check(document, pdf, ref rejected);
    if (pdf) pdfs++;
    if (failure != null && only != null)
        File.WriteAllText("fuzz-case.html", string.Join("\n<!-- next page set -->\n", DocumentGenerator.Html(document, false, out _)));
    if (failure != null)
    {
        failures++;
        Console.WriteLine($"FAIL seed {seed} case {n}: {failure}");
        if (failures >= 20) break;
    }
}
Console.WriteLine($"{cases} cases ({pdfs} with a PDF, {rejected} rejected with documented exceptions), {failures} failures, {watch.Elapsed.TotalSeconds:F0} s.");
return failures == 0 ? 0 : 1;

internal static class Fuzz
{
    // Css() and Html() take trusted markup on purpose, so a case that uses them only checks exceptions and time.
    [ThreadStatic]
    private static bool Raw;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private static readonly string[] Pieces =
    [
        "", " ", "x", "Hello", "<x-evil>", "</p>", "</main>", "</style>", "<script>", "&", "&amp;", "&#0;", "\"", "'",
        "{", "}", "{page}", "{total-pages}", "{pages}", "\n", "\r\n", "\t", "\0", "\u0001", "\u007f", "\u0085", "\ud800",
        "\udc00", "￾", "￿", "‮", "​", "مرحبا", "שלום", "日本語", "😀", "é", "a{color:red}",
        "url(x)", "\\", "/*", "*/", "<!--", "-->", "javascript:", "#", "%", ";", ":", "@page", " ",
    ];

    private static readonly string[] Colors = ["#000", "#fff", "#12345678", "#abc", "#abcd", "#00000000", "#ff000080", "#123456"];

    private static readonly string[] BadColors = ["red", "", "#", "#ggg", "#1234567", "rgb(1,2,3)", "#fff;x"];

    public static string? Check(IDocument document, bool pdf, ref int rejected)
    {
        try
        {
            var task = Task.Run(() =>
            {
                string page = DocumentGenerator.Html(document, false, out _);
                if (!Raw)
                {
                    if (page.Contains("<x-evil", StringComparison.Ordinal) || page.Contains("<script", StringComparison.Ordinal))
                        return "caller text became markup";
                    // One main element per page set, all in one document.
                    if (Count(page, "</style>") != 1 || Count(page, "</main>") < 1 || Count(page, "</main>") != Count(page, "<main")
                        || Count(page, "<body") != 1)
                        return "the document structure was broken";
                    if (page.Contains('\0')) return "a NUL character reached the HTML";
                }
                if (pdf) document.GeneratePdf();
                return null;
            });
            if (!task.Wait(Bound)) return $"took longer than {Bound.TotalSeconds} s";
            return task.Result;
        }
        catch (AggregateException e) when (Documented(e.InnerException))
        {
            rejected++;
            if (Environment.GetEnvironmentVariable("FUZZ_REJECTIONS") == "1") Console.WriteLine("rejected: " + e.InnerException!.Message);
            return null;
        }
        catch (AggregateException e)
        {
            return e.InnerException!.GetType().Name + ": " + e.InnerException.Message + "\n" + e.InnerException.StackTrace;
        }
    }

    // The exceptions the package documents: invalid arguments, invalid structure, and PdfPecker's own limits.
    private static bool Documented(Exception? e) => e is ArgumentException or InvalidOperationException or PdfPeckerException;

    private static int Count(string text, string value) => text.Split(value).Length - 1;

    private static string Text(Random r)
    {
        int parts = r.Next(0, 5);
        var s = new System.Text.StringBuilder();
        for (int i = 0; i < parts; i++)
        {
            if (r.Next(8) == 0) s.Append((char)r.Next(0, 0x10000));
            else s.Append(Pieces[r.Next(Pieces.Length)]);
        }
        if (r.Next(50) == 0) s.Append('y', r.Next(1, 20_000));
        return s.ToString();
    }

    // Invalid values are rare, so most cases get past argument checks and reach the writer and the renderer.
    private static string Name(Random r) => r.Next(20) == 0 ? Text(r) : "s" + r.Next(5) + Text(r);

    private static float Number(Random r) => r.Next(60) switch
    {
        0 => -1,
        1 => float.NaN,
        2 => float.PositiveInfinity,
        3 => 1e7f,
        4 or 5 or 6 => 0,
        7 or 8 => 0.0001f,
        9 or 10 => 5000,
        _ => (float)(r.NextDouble() * 120),
    };

    private static uint Index(Random r) => r.Next(10) switch { 0 => 0, 1 => 100_001, 2 => (uint)r.Next(1, 100_000), _ => (uint)r.Next(1, 5) };

    private static Color Color(Random r) => r.Next(40) == 0 ? BadColors[r.Next(BadColors.Length)] : Colors[r.Next(Colors.Length)];

    private static Unit Unit(Random r) => (Unit)(r.Next(40) == 0 ? 7 : r.Next(0, 7));

    public static void Compose(IDocumentContainer document, Random r)
    {
        Raw = false;
        if (r.Next(10) == 0)
        {
            document.Css(Text(r));
            Raw = true;
        }
        int pages = r.Next(0, 3);
        for (int p = 0; p < pages; p++)
        {
            document.Page(page =>
            {
                if (r.Next(3) == 0) page.Size(r.Next(30) == 0 ? Number(r) : r.Next(20, 1500), r.Next(30) == 0 ? Number(r) : r.Next(20, 1500));
                if (r.Next(2) == 0) page.Margin(Number(r), Unit(r));
                if (r.Next(4) == 0) page.PageColor(Color(r));
                if (r.Next(4) == 0) page.DefaultTextStyle(s => Style(s, r));
                if (r.Next(6) == 0) page.ContentFromRightToLeft();
                if (r.Next(2) == 0) Content(page.Header(), r, 2);
                Content(page.Content(), r, 0);
                if (r.Next(2) == 0) Content(page.Footer(), r, 2);
            });
        }
    }

    private static TextStyle Style(TextStyle s, Random r)
    {
        for (int i = r.Next(4); i > 0; i--)
        {
            s = r.Next(18) switch
            {
                16 => r.Next(2) == 0
                    ? s.EnableFontFeature(r.Next(10) == 0 ? Text(r) : FontFeatures.SmallCapitals)
                    : s.DisableFontFeature(r.Next(10) == 0 ? Text(r) : FontFeatures.StandardLigatures),
                12 => s.DecorationWavy().DecorationColor(Color(r)),
                13 => s.DecorationDashed().DecorationThickness(r.Next(20) == 0 ? Number(r) : 1 + r.Next(4)),
                14 => s.DecorationDouble().DecorationDotted(),
                15 => s.WordSpacing(r.Next(20) == 0 ? Number(r) : (float)(r.NextDouble() - 0.3)),
                0 => s.FontSize(Number(r)),
                1 => s.FontColor(Color(r)),
                2 => s.BackgroundColor(Color(r)),
                3 => s.FontFamily(Text(r), "sans-serif"),
                4 => s.FontWeight((FontWeight)(r.Next(20) == 0 ? r.Next(0, 1200) : r.Next(1, 11) * 100)),
                5 => s.Italic(r.Next(2) == 0),
                6 => s.Underline().Strikethrough(r.Next(2) == 0),
                7 => s.LineHeight(Number(r)),
                8 => s.LetterSpacing(r.Next(20) == 0 ? Number(r) : (float)(r.NextDouble() - 0.5)),
                9 => s.Superscript(),
                10 => s.Subscript(),
                _ => s.Bold(),
            };
        }
        return s;
    }

    private static void Content(IContainer c, Random r, int depth)
    {
        for (int i = r.Next(4); i > 0; i--) c = Decorate(c, r);
        if (depth > 12 && r.Next(2) == 0)
        {
            c.Text(Text(r));
            return;
        }
        switch (r.Next(depth > 6 ? 10 : 20))
        {
            case 18:
                c.Decoration(d =>
                {
                    if (r.Next(3) != 0) Content(d.Before(), r, depth + 1);
                    if (r.Next(10) != 0) Content(d.Content(), r, depth + 1);
                    if (r.Next(3) != 0) Content(d.After(), r, depth + 1);
                });
                break;
            case 19:
                c.List(l =>
                {
                    switch (r.Next(4))
                    {
                        case 0: l.Numbered((ListNumbering)r.Next(0, r.Next(20) == 0 ? 9 : 5), r.Next(20) == 0 ? r.Next() : r.Next(-3, 20)); break;
                        case 1: l.Marker(r.Next(5) == 0 ? Text(r) : "–"); break;
                    }
                    if (r.Next(2) == 0) l.Spacing(Number(r));
                    if (r.Next(3) == 0) l.Indent(Number(r));
                    for (int i = r.Next(6); i > 0; i--) Content(l.Item(), r, depth + 1);
                });
                break;
            case 14:
                c.Layers(l =>
                {
                    for (int i = r.Next(3); i > 0; i--) Content(l.Layer(), r, depth + 1);
                    if (r.Next(20) != 0) Content(l.PrimaryLayer(), r, depth + 1);
                    for (int i = r.Next(3); i > 0; i--) Content(l.Layer(), r, depth + 1);
                });
                break;
            case 15:
                c.Inlined(i =>
                {
                    if (r.Next(2) == 0) i.Spacing(Number(r));
                    if (r.Next(3) == 0) i.AlignJustify();
                    if (r.Next(3) == 0) i.BaselineMiddle();
                    for (int n = r.Next(8); n > 0; n--) Content(i.Item(), r, depth + 1);
                });
                break;
            case 16:
                c.MultiColumn(m =>
                {
                    m.Columns(r.Next(20) == 0 ? r.Next(-5, 200) : r.Next(1, 5));
                    if (r.Next(2) == 0) m.Spacing(Number(r));
                    m.BalanceHeight(r.Next(2) == 0);
                    Content(m.Content(), r, depth + 1);
                });
                break;
            case 17:
                c.Placeholder(r.Next(2) == 0 ? null : Text(r));
                break;
            case 0:
                c.Text(Text(r)).FontSize(Number(r));
                break;
            case 1:
                var block = c.Text(Text(r));
                if (r.Next(2) == 0) block.ClampLines(r.Next(20) == 0 ? r.Next(-2, 20_000) : r.Next(1, 5));
                if (r.Next(2) == 0) block.ParagraphFirstLineIndentation(Number(r));
                break;
            case 2:
                c.Text(t =>
                {
                    if (r.Next(3) == 0) t.ParagraphSpacing(Number(r));
                    for (int i = r.Next(6); i > 0; i--)
                    {
                        if (r.Next(8) == 0)
                        {
                            Content(t.Element((TextInjectedElementAlignment)r.Next(0, r.Next(20) == 0 ? 8 : 5)), r, depth + 1);
                            continue;
                        }
                        _ = r.Next(9) switch
                        {
                            0 => t.CurrentPageNumber(),
                            1 => t.TotalPages(),
                            2 => t.Hyperlink(Text(r), r.Next(10) != 0 ? "https://example.com/" + Uri.EscapeDataString(Text(r)) : Text(r)),
                            3 => t.SectionLink(Text(r), Name(r)),
                            4 => t.Line(Text(r)),
                            5 => t.BeginPageNumberOfSection(Name(r)),
                            _ => t.Span(Text(r)).Style(Style(TextStyle.Default, r)),
                        };
                    }
                    if (r.Next(3) == 0) t.Justify();
                });
                break;
            case 3:
                c.Image(Image(r)).AlternativeText(Text(r));
                break;
            case 4:
                c.Svg(r.Next(2) == 0 ? "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='5' height='5'/></svg>" : Text(r));
                break;
            case 5:
                if (r.Next(2) == 0) c.QrCode(Text(r));
                else c.Ean13(r.Next(10) == 0 ? Text(r) : r.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture));
                break;
            case 6:
                var line = c.LineHorizontal(Number(r)).LineColor(Color(r));
                if (r.Next(2) == 0) line.LineDashPattern(r.Next(20) == 0 ? [] : [Number(r), Number(r)]);
                break;
            case 7:
                c.PageBreak();
                break;
            case 8:
                if (r.Next(4) == 0)
                {
                    c.Html(Text(r));
                    Raw = true;
                }
                else c.LineVertical(Number(r));
                break;
            case 9:
                break; // an empty slot
            case 10:
            case 11:
                c.Column(col =>
                {
                    if (r.Next(2) == 0) col.Spacing(Number(r));
                    for (int i = r.Next(6); i > 0; i--) Content(col.Item(), r, depth + 1);
                });
                break;
            case 12:
                c.Row(row =>
                {
                    if (r.Next(2) == 0) row.Spacing(Number(r));
                    for (int i = r.Next(5); i > 0; i--)
                    {
                        var item = r.Next(3) switch { 0 => row.RelativeItem(Number(r)), 1 => row.ConstantItem(Number(r), Unit(r)), _ => row.AutoItem() };
                        Content(item, r, depth + 1);
                    }
                });
                break;
            default:
                c.Table(t =>
                {
                    if (r.Next(8) != 0)
                        t.ColumnsDefinition(d =>
                        {
                            for (int i = r.Next(1, 6); i > 0; i--)
                                if (r.Next(2) == 0) d.RelativeColumn(Number(r));
                                else d.ConstantColumn(Number(r));
                        });
                    if (r.Next(3) == 0) t.Header(h => Cell(h.Cell(), r, depth));
                    for (int i = r.Next(10); i > 0; i--) Cell(t.Cell(), r, depth);
                    if (r.Next(4) == 0) t.Footer(f => Cell(f.Cell(), r, depth));
                });
                break;
        }
    }

    private static void Cell(ITableCellContainer cell, Random r, int depth)
    {
        if (r.Next(4) == 0) cell.ColumnSpan(Index(r));
        if (r.Next(5) == 0) cell.RowSpan(Index(r));
        if (r.Next(6) == 0) cell.Row(Index(r));
        if (r.Next(6) == 0) cell.Column(Index(r));
        Content(cell, r, depth + 1);
    }

    private static float Signed(Random r) => r.Next(2) == 0 ? Number(r) : -Number(r);

    private static IContainer Decorate(IContainer c, Random r) => r.Next(48) switch
    {
        46 => c.ScaleToFit(),
        47 => r.Next(2) == 0 ? c.ShowOnce() : c.SkipOnce(),
        41 => c.ShrinkHorizontal(),
        42 => r.Next(2) == 0 ? c.Shrink() : c.ShrinkVertical(),
        43 => r.Next(6) switch
        {
            0 => c.SemanticSection(),
            1 => c.SemanticBlockQuotation(),
            2 => c.SemanticCaption(),
            3 => c.SemanticList(),
            4 => c.SemanticListItem(),
            _ => c.SemanticArticle(),
        },
        44 => r.Next(2) == 0 ? c.SemanticIgnore() : c.SemanticImage(Text(r)),
        24 => c.Rotate(Signed(r) * 3),
        25 => c.RotateRight(),
        26 => c.RotateLeft(),
        27 => c.Scale(r.Next(20) == 0 ? Signed(r) * 100 : (float)(r.NextDouble() * 3 - 1)),
        28 => c.FlipHorizontal(),
        29 => c.FlipOver(),
        30 => c.TranslateX(Signed(r), Unit(r)),
        31 => c.TranslateY(Signed(r)),
        32 => c.Unconstrained(),
        33 => c.ZIndex(r.Next(-5, 5)),
        34 => c.AspectRatio(r.Next(20) == 0 ? Signed(r) : (float)(r.NextDouble() * 3), (AspectRatioOption)r.Next(0, 4)),
        35 => c.EnsureSpace(Number(r) * 5),
        36 => c.Shadow(new BoxShadowStyle { Blur = Number(r), Spread = Signed(r), OffsetX = Signed(r), OffsetY = Signed(r), Color = Color(r) }),
        37 => c.CornerRadiusTopLeft(Number(r)),
        38 => c.CornerRadiusBottomRight(Number(r)),
        39 => c.DebugArea(r.Next(2) == 0 ? null : Text(r), Color(r)),
        40 => c.ScaleVertical(r.Next(20) == 0 ? 0 : 0.5f),
        0 => c.Padding(Number(r), Unit(r)),
        1 => c.PaddingLeft(Number(r)),
        2 => c.Background(Color(r)),
        3 => c.Border(Number(r)),
        4 => c.BorderBottom(Number(r)).BorderColor(Color(r)),
        5 => c.CornerRadius(Number(r)),
        6 => c.Width(Number(r)),
        7 => c.Height(Number(r)),
        8 => c.MinHeight(Number(r)),
        9 => c.MaxWidth(Number(r)),
        10 => c.AlignLeft(),
        11 => c.AlignCenter(),
        12 => c.AlignRight(),
        13 => c.AlignMiddle(),
        14 => c.AlignBottom(),
        15 => c.ExtendHorizontal(),
        16 => c.PreventPageBreak(),
        17 => c.ShowIf(r.Next(4) != 0),
        18 => c.DefaultTextStyle(s => Style(s, r)),
        19 => c.Heading(r.Next(0, 8)),
        20 => c.Hyperlink(r.Next(10) != 0 ? "mailto:a@example.com?subject=" + Uri.EscapeDataString(Text(r)) : Text(r)),
        21 => c.Section(Name(r)),
        22 => c.ContentFromRightToLeft(),
        23 => r.Next(20) == 0 ? c.BorderColor(Color(r)) : c.Border(Number(r)).BorderColor(Color(r)),
        _ => c.ScaleHorizontal(-1),
    };

    private static byte[] Image(Random r)
    {
        // A tiny valid PNG, or random bytes: a damaged image must not fail the document.
        if (r.Next(2) == 0) return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var bytes = new byte[r.Next(0, 64)];
        r.NextBytes(bytes);
        return bytes;
    }
}
