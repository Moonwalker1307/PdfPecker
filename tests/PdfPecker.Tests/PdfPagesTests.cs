using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Taking, reordering, repeating, removing, turning and splitting the pages of existing PDFs.</summary>
public class PdfPagesTests
{
    private static readonly string[] Colors = { "#ff0000", "#00ff00", "#0000ff", "#ffff00", "#ff00ff" };

    /// <summary>Five pages, each filled with its own colour and headed "Page n" (a bookmark each).</summary>
    private static byte[] FivePages(PdfOptions? options = null) => HtmlToPdf.Convert(FivePagesHtml, options);

    private static string FivePagesHtml => "<html lang='en'><head><title>Five</title></head><body style='margin:0'>" + string.Concat(Colors.Select((c, i) =>
        $"<h1 id='p{i + 1}'{(i > 0 ? " style='page-break-before:always'" : "")}>Page {i + 1}</h1>"
        + (i == 0 ? "<p><a href='#p2'>to two</a> <a href='#p3'>to three</a></p>" : "")
        + $"<div style='background:{c};width:300pt;height:300pt'></div>")) + "</body></html>";

    private static (int R, int G, int B) Color(byte[] pdf, int page)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 20 });
        int o = ((int)(200 * 20 / 72.0) * img.Width + (int)(100 * 20 / 72.0)) * 4; // 200pt down, 100pt in: inside the box
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2]);
    }

    /// <summary>The source page (1-based) each page of <paramref name="pdf"/> shows, by its colour.</summary>
    private static int[] SourcePages(byte[] pdf)
    {
        int n = PdfRenderer.Open(pdf).PageCount;
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            var (r, g, b) = Color(pdf, i);
            string hex = $"#{(r > 128 ? "ff" : "00")}{(g > 128 ? "ff" : "00")}{(b > 128 ? "ff" : "00")}";
            result[i] = Array.IndexOf(Colors, hex) + 1;
        }
        return result;
    }

    private static List<PdfDict> Pages(PdfFile file)
        => file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items.Select(k => (PdfDict)file.Resolve(k)!).ToList();

    private static List<(string Title, PdfObject? Page)> Bookmarks(PdfFile file)
    {
        var result = new List<(string, PdfObject?)>();
        var item = file.GetDict(file.GetDict(file.Catalog, "Outlines"), "First");
        for (int guard = 0; item != null && guard < 100; guard++)
        {
            var dest = file.Resolve(item["Dest"]) as PdfArray;
            result.Add((((PdfString)file.Resolve(item["Title"])!).Text, dest?.Items[0]));
            item = file.GetDict(item, "Next");
        }
        return result;
    }

    private static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------ page ranges

    [Theory]
    [InlineData("1-3,5,8-", new[] { 1, 2, 3, 5, 8, 9, 10 })]
    [InlineData("3,1,2", new[] { 3, 1, 2 })]
    [InlineData("1,1, 2", new[] { 1, 1, 2 })]
    [InlineData("5-3", new[] { 5, 4, 3 })]
    [InlineData("-2", new[] { 1, 2 })]
    [InlineData("last", new[] { 10 })]
    [InlineData("last-8", new[] { 10, 9, 8 })]
    public void Page_ranges_parse_into_page_numbers(string text, int[] expected)
    {
        Assert.Equal(expected, PdfPageRange.Parse(text).ToPageNumbers(10));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,,2")]
    [InlineData("0")]
    [InlineData("a-3")]
    [InlineData("-")]
    [InlineData("1.5")]
    public void Bad_page_ranges_are_refused(string text)
    {
        Assert.Throws<FormatException>(() => PdfPageRange.Parse(text));
        Assert.False(PdfPageRange.TryParse(text, out _));
    }

    [Fact]
    public void Page_ranges_round_trip_and_check_the_page_count()
    {
        Assert.Equal("1-3,5,8-last", PdfPageRange.Parse("1-3, 5, 8-").ToString());
        Assert.Equal(new[] { 2, 4 }, PdfPageRange.FromPages(new[] { 2, 4 }).ToPageNumbers(4));
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRange.Parse("2,9").ToPageNumbers(5));
        Assert.Contains("Page 9 is out of range: the PDF has 5 pages.", ex.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRange.FromPages(new[] { 0 }));
        Assert.Throws<ArgumentException>(() => PdfPageRange.FromPages(Array.Empty<int>()));
    }

    // ------------------------------------------------------------------ extracting

    [Fact]
    public void Extract_takes_a_range()
    {
        var pdf = FivePages();
        var part = PdfPages.Extract(pdf, "2-3,5");
        PdfTestHelpers.AssertStructurallyValid(part);
        Assert.Equal(new[] { 2, 3, 5 }, SourcePages(part));
        Assert.Equal(3, PdfPages.Count(part));
        Assert.Contains("/Title (Five)", PdfTestHelpers.Latin1(part));
    }

    [Fact]
    public void Extract_reorders_pages()
    {
        var part = PdfPages.Extract(FivePages(), new[] { 3, 1, 2 });
        PdfTestHelpers.AssertStructurallyValid(part);
        Assert.Equal(new[] { 3, 1, 2 }, SourcePages(part));
    }

    [Fact]
    public void Extract_repeats_pages_as_copies_with_their_own_annotations()
    {
        var part = PdfPages.Extract(FivePages(), "1,2,1");
        PdfTestHelpers.AssertStructurallyValid(part);
        Assert.Equal(new[] { 1, 2, 1 }, SourcePages(part));
        var file = PdfFile.Open(part, null);
        var pages = Pages(file);
        var first = file.GetArray(pages[0], "Annots")!.Items.Cast<PdfRef>().Select(r => r.Num).ToList();
        var copy = file.GetArray(pages[2], "Annots")!.Items.Cast<PdfRef>().Select(r => r.Num).ToList();
        Assert.Equal(first.Count, copy.Count);
        Assert.Empty(first.Intersect(copy));
        // The copy's links still go to the (one) copy of page 2; the link to page 3 is gone with page 3.
        var pageRefs = file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items.Cast<PdfRef>().ToList();
        foreach (var n in copy)
        {
            var annot = (PdfDict)file.GetObject(n)!;
            Assert.Null(annot["P"]);
            var dest = (PdfArray)file.Resolve(annot["Dest"] ?? file.GetDict(annot, "A")!["D"])!;
            Assert.Equal(pageRefs[1].Num, ((PdfRef)dest.Items[0]).Num);
        }
        Assert.Single(copy);
    }

    [Fact]
    public void Out_of_range_pages_throw_with_a_clear_message()
    {
        var pdf = FivePages();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfPages.Extract(pdf, "1,7"));
        Assert.Contains("Page 7 is out of range: the PDF has 5 pages.", ex.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPages.Extract(pdf, "6-"));
        Assert.Throws<FormatException>(() => PdfPages.Extract(pdf, "one"));
        var merged = Assert.Throws<ArgumentOutOfRangeException>(() => PdfMerger.Merge(new[] { new PdfMergeInput(pdf), new PdfMergeInput(pdf, "9") }));
        Assert.Contains("Page 9 is out of range: PDF 2 has 5 pages.", merged.Message);
        Assert.Throws<ArgumentException>(() => PdfPages.Remove(pdf, "1-last"));
    }

    [Fact]
    public void Remove_leaves_pages_out_and_Rotate_turns_them()
    {
        var pdf = FivePages();
        var rest = PdfPages.Remove(pdf, "2,4-");
        PdfTestHelpers.AssertStructurallyValid(rest);
        Assert.Equal(new[] { 1, 3 }, SourcePages(rest));

        var turned = PdfPages.Rotate(pdf, 90, "2");
        PdfTestHelpers.AssertStructurallyValid(turned);
        var file = PdfFile.Open(turned, null);
        var rotations = Pages(file).Select(p => file.GetInt(p, "Rotate", 0)).ToArray();
        Assert.Equal(new[] { 0, 90, 0, 0, 0 }, rotations);
        var back = PdfFile.Open(PdfPages.Rotate(turned, -180), null);
        Assert.Equal(new[] { 180, 270, 180, 180, 180 }, Pages(back).Select(p => back.GetInt(p, "Rotate", 0)).ToArray());
        Assert.Throws<ArgumentException>(() => PdfPages.Rotate(pdf, 45));
    }

    [Fact]
    public void Encrypted_input_needs_its_password()
    {
        var secret = FivePages(new PdfOptions { Security = new PdfSecurity { UserPassword = "pw", OwnerPassword = "owner" } });
        Assert.Throws<PdfPasswordException>(() => PdfPages.Extract(secret, "1"));
        Assert.Throws<PdfPasswordException>(() => PdfPages.Extract(secret, "1", new PdfPagesOptions { Password = "wrong" }));
        var part = PdfPages.Extract(secret, "4", new PdfPagesOptions { Password = "pw", Title = "Fourth" });
        Assert.DoesNotContain("/Encrypt", PdfTestHelpers.Latin1(part));
        Assert.Equal(new[] { 4 }, SourcePages(part));
        Assert.Contains("/Title (Fourth)", PdfTestHelpers.Latin1(part));
    }

    // ------------------------------------------------------------------ splitting

    [Fact]
    public void Split_makes_parts_of_n_pages()
    {
        var parts = PdfPages.Split(FivePages(), 2);
        Assert.Equal(3, parts.Length);
        foreach (var p in parts) PdfTestHelpers.AssertStructurallyValid(p);
        Assert.Equal(new[] { 1, 2 }, SourcePages(parts[0]));
        Assert.Equal(new[] { 3, 4 }, SourcePages(parts[1]));
        Assert.Equal(new[] { 5 }, SourcePages(parts[2]));
        // Each part has the bookmarks of its own pages.
        Assert.Equal(new[] { "Page 3", "Page 4" }, Bookmarks(PdfFile.Open(parts[1], null)).Select(b => b.Title));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPages.Split(FivePages(), 0));
    }

    [Fact]
    public void Split_by_ranges()
    {
        var parts = PdfPages.Split(FivePages(), "1-2", "4-", "3,3");
        Assert.Equal(3, parts.Length);
        foreach (var p in parts) PdfTestHelpers.AssertStructurallyValid(p);
        Assert.Equal(new[] { 1, 2 }, SourcePages(parts[0]));
        Assert.Equal(new[] { 4, 5 }, SourcePages(parts[1]));
        Assert.Equal(new[] { 3, 3 }, SourcePages(parts[2]));
        Assert.Throws<ArgumentException>(() => PdfPages.Split(FivePages(), Array.Empty<string>()));
    }

    [Fact]
    public void File_and_stream_overloads()
    {
        var dir = Directory.CreateTempSubdirectory("pdfpages");
        try
        {
            var input = Path.Combine(dir.FullName, "five.pdf");
            File.WriteAllBytes(input, FivePages());
            var output = Path.Combine(dir.FullName, "two.pdf");
            PdfPages.ExtractFile(input, output, "2,1");
            Assert.Equal(new[] { 2, 1 }, SourcePages(File.ReadAllBytes(output)));
            // The output may be the input.
            PdfPages.ExtractFile(input, input, "5");
            Assert.Equal(new[] { 5 }, SourcePages(File.ReadAllBytes(input)));

            File.WriteAllBytes(input, FivePages());
            int count = PdfPages.SplitFile(input, 3, n => Path.Combine(dir.FullName, $"part-{n}.pdf"));
            Assert.Equal(2, count);
            Assert.Equal(new[] { 4, 5 }, SourcePages(File.ReadAllBytes(Path.Combine(dir.FullName, "part-2.pdf"))));
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp"));

            var stream = new MemoryStream();
            PdfPages.Extract(new MemoryStream(FivePages()), stream, PdfPageRange.Parse("3"));
            Assert.Equal(new[] { 3 }, SourcePages(stream.ToArray()));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // ------------------------------------------------------------------ merging selected pages

    [Fact]
    public void Merge_takes_selected_pages_of_each_input()
    {
        var pdf = FivePages();
        var merged = PdfMerger.Merge(new[] { new PdfMergeInput(pdf, "5,1"), new PdfMergeInput(pdf), new PdfMergeInput(pdf, PdfPageRange.Span(3, 2)) });
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal(new[] { 5, 1, 1, 2, 3, 4, 5, 3, 2 }, SourcePages(merged));
    }

    [Fact]
    public void StreamingPdfMerger_takes_selected_pages()
    {
        var pdf = FivePages();
        var output = new MemoryStream();
        var merger = new StreamingPdfMerger(output);
        merger.Add(pdf, PdfPageRange.Parse("2"));
        merger.Add(pdf, PdfPageRange.Parse("4"));
        merger.Complete();
        PdfTestHelpers.AssertStructurallyValid(output.ToArray());
        Assert.Equal(new[] { 2, 4 }, SourcePages(output.ToArray()));
    }

    // ------------------------------------------------------------------ what comes along

    [Fact]
    public void Bookmarks_and_links_follow_the_pages_kept()
    {
        var part = PdfPages.Extract(FivePages(), "3,1");
        var file = PdfFile.Open(part, null);
        var pageRefs = file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items.Cast<PdfRef>().ToList();
        // Bookmarks to pages left out are gone; the others go to the new pages.
        var marks = Bookmarks(file);
        Assert.Equal(new[] { "Page 1", "Page 3" }, marks.Select(b => b.Title));
        Assert.Equal(pageRefs[1].Num, ((PdfRef)marks[0].Page!).Num);
        Assert.Equal(pageRefs[0].Num, ((PdfRef)marks[1].Page!).Num);
        // Page 1's link to page 2 is dropped; its link to page 3 now goes to the new first page.
        var annots = file.GetArray(Pages(file)[1], "Annots")!.Items.Select(a => (PdfDict)file.Resolve(a)!).ToList();
        var link = Assert.Single(annots);
        var dest = (PdfArray)file.Resolve(link["Dest"] ?? file.GetDict(link, "A")!["D"])!;
        Assert.Equal(pageRefs[0].Num, ((PdfRef)dest.Items[0]).Num);
        Assert.Single(Bookmarks(PdfFile.Open(PdfPages.Extract(FivePages(), "2"), null)));
        Assert.DoesNotContain("/Outlines", PdfTestHelpers.Latin1(PdfPages.Extract(FivePages(), "2", new PdfPagesOptions { Bookmarks = false })));
    }

    [Fact]
    public void Nested_bookmarks_keep_a_parent_of_a_kept_child()
    {
        // Outline: "Part" (to page 1) > "A" (page 1), "B" (page 2); "End" (page 2).
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Outlines /First 6 0 R /Last 9 0 R /Count 4 >>",
            "<< /Title (Part) /Parent 5 0 R /Next 9 0 R /First 7 0 R /Last 8 0 R /Count 2 /Dest [3 0 R /Fit] >>",
            "<< /Title (A) /Parent 6 0 R /Next 8 0 R /Dest [3 0 R /Fit] >>",
            "<< /Title (B) /Parent 6 0 R /Prev 7 0 R /Dest [4 0 R /Fit] >>",
            "<< /Title (End) /Parent 5 0 R /Prev 6 0 R /Dest [4 0 R /Fit] >>");
        var part = PdfPages.Extract(pdf, "2");
        PdfTestHelpers.AssertStructurallyValid(part);
        var file = PdfFile.Open(part, null);
        var top = Bookmarks(file);
        Assert.Equal(new[] { "Part", "End" }, top.Select(b => b.Title));
        Assert.Null(top[0].Page); // "Part" went to page 1: it stays as a heading, without a destination
        var partItem = file.GetDict(file.GetDict(file.Catalog, "Outlines"), "First")!;
        Assert.Null(partItem["Dest"]);
        var b = file.GetDict(partItem, "First")!;
        Assert.Same(b, file.GetDict(partItem, "Last"));
        Assert.Equal("B", ((PdfString)file.Resolve(b["Title"])!).Text);
        Assert.Null(b["Prev"]);
        Assert.Null(b["Next"]);
        Assert.Equal(1, file.GetInt(partItem, "Count", 0));
    }

    [Fact]
    public void Form_fields_on_kept_pages_stay()
    {
        var html = "<html><body><p><input type='text' name='first' value='one'></p>"
            + "<p><input type='radio' name='pick' value='a' checked> A</p>"
            + "<div style='page-break-before:always'><input type='text' name='second' value='two'>"
            + "<input type='radio' name='pick' value='b'> B</div></body></html>";
        var pdf = HtmlToPdf.Convert(html);
        var part = PdfPages.Extract(pdf, "2");
        PdfTestHelpers.AssertStructurallyValid(part);
        var file = PdfFile.Open(part, null);
        var fields = file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")!.Items.Select(f => (PdfDict)file.Resolve(f)!).ToList();
        var names = fields.Select(f => ((PdfString)file.Resolve(f["T"])!).Text).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "pick", "second" }, names);
        // The radio group keeps only its button on the page kept, and that button is in the page's annotations.
        var pick = fields.Single(f => ((PdfString)file.Resolve(f["T"])!).Text == "pick");
        var kid = Assert.Single(file.GetArray(pick, "Kids")!.Items);
        var annots = file.GetArray(Pages(file)[0], "Annots")!.Items.Cast<PdfRef>().Select(r => r.Num).ToList();
        Assert.Contains(((PdfRef)kid).Num, annots);
        // Extracting page 1 instead keeps "first" and the other button.
        var other = PdfFile.Open(PdfPages.Extract(pdf, "1"), null);
        var otherNames = file.GetArray(other.GetDict(other.Catalog, "AcroForm"), "Fields")!.Items
            .Select(f => ((PdfString)other.Resolve(((PdfDict)other.Resolve(f)!)["T"])!).Text).OrderBy(n => n);
        Assert.Equal(new[] { "first", "pick" }, otherNames);
    }

    [Fact]
    public void Page_labels_are_kept()
    {
        // Labels: i, ii, then 1, 2.
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /r >> 2 << /S /D /P (A-) >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>");
        var text = PdfTestHelpers.Latin1(PdfPages.Extract(pdf, "2-4"));
        // ii; A-1, A-2
        Assert.Contains("/PageLabels << /Nums [0 << /S /r /St 2 >> 1 << /S /D /P <412d> >> ] >>", text);
        Assert.DoesNotContain("/PageLabels", PdfTestHelpers.Latin1(PdfPages.Extract(FivePages(), "2")));
    }

    // ------------------------------------------------------------------ tagged input

    [Fact]
    public void Tagged_input_stays_tagged_with_only_the_kept_content()
    {
        var pdf = FivePages(new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 });
        var part = PdfPages.Extract(pdf, "3,1");
        PdfTestHelpers.AssertStructurallyValid(part);
        var file = PdfFile.Open(part, null);
        var catalog = file.Catalog!;
        Assert.Equal("en", ((PdfString)file.Resolve(catalog["Lang"])!).Text);
        var root = file.GetDict(catalog, "StructTreeRoot")!;
        var nums = file.GetArray(file.GetDict(root, "ParentTree"), "Nums")!;
        var keys = new HashSet<int>();
        for (int i = 0; i < nums.Count; i += 2) keys.Add(((PdfNumber)nums[i]).IntValue);
        var pages = Pages(file);
        foreach (var page in pages)
        {
            Assert.Contains(file.GetInt(page, "StructParents", -1), keys);
            foreach (var a in file.GetArray(page, "Annots")?.Items ?? new List<PdfObject>())
                if (file.Resolve(a) is PdfDict ad && ad["StructParent"] != null) Assert.Contains(file.GetInt(ad, "StructParent", -1), keys);
        }
        // Every page an element points at is a page of the result, and only kept headings remain.
        var pageNums = file.GetArray(file.GetDict(catalog, "Pages"), "Kids")!.Items.Cast<PdfRef>().Select(r => r.Num).ToHashSet();
        var headings = new List<string>();
        Walk(root["K"], 0);
        Assert.Equal(2, headings.Count);
        // The result still claims PDF/UA-1.
        var xmp = Encoding.UTF8.GetString(file.DecodeStream((PdfStream)file.Resolve(catalog["Metadata"])!));
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", xmp);

        void Walk(PdfObject? k, int depth)
        {
            Assert.True(depth < 50);
            switch (file.Resolve(k))
            {
                case PdfArray a:
                    foreach (var i in a.Items) Walk(i, depth + 1);
                    break;
                case PdfDict d when d["S"] != null:
                    if (d["Pg"] is PdfRef pg) Assert.Contains(pg.Num, pageNums);
                    if (file.GetName(d, "S") == "H1") headings.Add("H1");
                    Walk(d["K"], depth + 1);
                    break;
                case PdfDict d when file.GetName(d, "Type") == "OBJR":
                    Assert.NotNull(file.Resolve(d["Obj"]));
                    break;
            }
        }
    }

    [Fact]
    public void Repeating_a_tagged_page_drops_the_PdfUA_claim_but_stays_tagged()
    {
        var pdf = FivePages(new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 });
        var part = PdfPages.Extract(pdf, "2,2");
        PdfTestHelpers.AssertStructurallyValid(part);
        var file = PdfFile.Open(part, null);
        Assert.NotNull(file.GetDict(file.Catalog, "StructTreeRoot"));
        Assert.Null(file.Catalog!["Metadata"]);
        var pages = Pages(file);
        Assert.NotNull(pages[0]["StructParents"]);
        Assert.Null(pages[1]["StructParents"]);
    }

    [Fact]
    public void Untagged_input_stays_untagged()
    {
        var text = PdfTestHelpers.Latin1(PdfPages.Extract(FivePages(), "1"));
        Assert.DoesNotContain("/StructTreeRoot", text);
    }
}
