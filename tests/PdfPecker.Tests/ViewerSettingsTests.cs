using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

public class ViewerSettingsTests
{
    private const string ThreePages =
        "<html><head><title>Report</title></head><body><h1>One</h1><h1 style='break-before:page'>Two</h1><h1 style='break-before:page'>Three</h1></body></html>";

    private static string Catalog(byte[] pdf)
        => Regex.Match(PdfTestHelpers.Latin1(pdf), @"<< /Type /Catalog .*?(?=\nendobj)", RegexOptions.Singleline).Value;

    private static List<PdfObject> Kids(PdfFile file) => file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items;

    [Fact]
    public void Default_options_write_no_viewer_entries()
    {
        var catalog = Catalog(PdfTestHelpers.ConvertUncompressed(ThreePages));
        Assert.DoesNotContain("/PageLayout", catalog);
        Assert.DoesNotContain("/PageMode", catalog);
        Assert.DoesNotContain("/OpenAction", catalog);
        Assert.DoesNotContain("/ViewerPreferences", catalog);
        Assert.DoesNotContain("/PageLabels", catalog);
    }

    [Fact]
    public void Viewer_settings_go_into_the_catalog()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions
        {
            Viewer = new PdfViewerSettings
            {
                PageLayout = PdfPageLayout.OneColumn,
                PageMode = PdfPageMode.Bookmarks,
                StartPage = 2,
                Zoom = PdfZoom.FitWidth,
                DisplayDocTitle = true,
                FitWindow = true,
                CenterWindow = true,
                HideToolbar = true,
                HideMenuBar = true,
                HideWindowControls = true,
            },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        Assert.StartsWith("%PDF-1.4", PdfTestHelpers.Latin1(pdf));
        var catalog = Catalog(pdf);
        Assert.Contains("/PageLayout /OneColumn /PageMode /UseOutlines", catalog);
        Assert.Contains(" /FitH null]", catalog);
        Assert.Contains("/ViewerPreferences << /HideToolbar true /HideMenubar true /HideWindowUI true /FitWindow true /CenterWindow true /DisplayDocTitle true >>", catalog);

        // The open action goes to the second page.
        var file = PdfFile.Open(pdf, null);
        var action = (PdfArray)file.Resolve(file.Catalog!["OpenAction"])!;
        Assert.Equal(Kids(file)[1], action[0]);
    }

    [Theory]
    [InlineData(PdfZoom.Default, "/XYZ null null null]")]
    [InlineData(PdfZoom.FitPage, "/Fit]")]
    [InlineData(PdfZoom.ActualSize, "/XYZ null null 1]")]
    public void Zoom_is_the_open_action_destination(PdfZoom zoom, string destination)
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions { Viewer = new PdfViewerSettings { StartPage = 3, Zoom = zoom } });
        Assert.Contains(destination, Catalog(pdf));
    }

    [Fact]
    public void A_start_page_past_the_end_opens_the_last_page()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions { Viewer = new PdfViewerSettings { StartPage = 99 } });
        var file = PdfFile.Open(pdf, null);
        var action = (PdfArray)file.Resolve(file.Catalog!["OpenAction"])!;
        Assert.Equal(Kids(file)[2], action[0]);
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfViewerSettings { StartPage = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageLabel(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageLabel(1, firstNumber: 0));
        var options = new PdfOptions { PageLabels = { null! } };
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", options));
    }

    [Fact]
    public void Print_settings_and_two_page_layouts_need_PDF_1_7()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions
        {
            Viewer = new PdfViewerSettings
            {
                PageLayout = PdfPageLayout.TwoPageRight,
                PageMode = PdfPageMode.Attachments,
                DisablePrintScaling = true,
                Duplex = PdfDuplex.FlipLongEdge,
            },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        Assert.StartsWith("%PDF-1.7", PdfTestHelpers.Latin1(pdf));
        var catalog = Catalog(pdf);
        Assert.Contains("/PageLayout /TwoPageRight /PageMode /UseAttachments", catalog);
        Assert.Contains("/ViewerPreferences << /PrintScaling /None /Duplex /DuplexFlipLongEdge >>", catalog);
    }

    [Fact]
    public void PDF_UA_keeps_the_title_in_the_title_bar()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions
        {
            Accessibility = PdfAccessibility.PdfUA1,
            Viewer = new PdfViewerSettings { PageMode = PdfPageMode.Thumbnails, DisplayDocTitle = false },
        });
        var catalog = Catalog(pdf);
        Assert.Contains("/PageMode /UseThumbs", catalog);
        Assert.Contains("/ViewerPreferences << /DisplayDocTitle true >>", catalog);
    }

    [Fact]
    public void PDF_UA_2_opens_at_a_structure_destination()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions
        {
            Conformance = PdfConformance.PdfA4,
            Accessibility = PdfAccessibility.PdfUA2,
            Viewer = new PdfViewerSettings { StartPage = 2, Zoom = PdfZoom.FitPage },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var file = PdfFile.Open(pdf, null);
        Assert.IsType<PdfRef>(file.Catalog!["OpenAction"]);
        var action = file.GetDict(file.Catalog, "OpenAction")!;
        Assert.Equal("GoTo", file.GetName(action, "S"));
        Assert.Equal(Kids(file)[1], file.GetArray(action, "D")![0]);
        // The structure destination is the second page's heading.
        var element = (PdfDict)file.Resolve(file.GetArray(action, "SD")![0])!;
        Assert.Equal("H1", file.GetName(element, "S"));
        var mcr = (PdfDict)file.Resolve(file.GetArray(element, "K")![0])!;
        Assert.Equal(Kids(file)[1], mcr["Pg"]);
    }

    [Fact]
    public void Page_labels_are_sorted_and_cover_the_first_page()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions
        {
            PageLabels =
            {
                new PdfPageLabel(3, PdfPageNumberStyle.Decimal, "A-", 5),
                new PdfPageLabel(2, PdfPageNumberStyle.LowerRoman),
                new PdfPageLabel(9, PdfPageNumberStyle.UpperLetters), // past the last page
            },
        });
        PdfTestHelpers.AssertStructurallyValid(pdf);
        Assert.Contains("/PageLabels << /Nums [0 << /S /D >> 1 << /S /r >> 2 << /S /D /P (A-) /St 5 >> ] >>", Catalog(pdf));
        Assert.Equal(new[] { "1", "i", "A-5" }, Labels(pdf));
    }

    [Fact]
    public void Page_labels_take_the_last_range_at_a_page_and_unicode_prefixes()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(ThreePages, new PdfOptions
        {
            PageLabels =
            {
                new PdfPageLabel(1, PdfPageNumberStyle.UpperRoman),
                new PdfPageLabel(1, PdfPageNumberStyle.None, "Forside"),
                new PdfPageLabel(2, PdfPageNumberStyle.Decimal, "Bilag ø-"),
            },
        });
        var hex = "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("Bilag ø-"));
        Assert.Contains("/PageLabels << /Nums [0 << /P (Forside) >> 1 << /S /D /P <" + hex + "> >> ] >>", Catalog(pdf));
        Assert.Equal(new[] { "Forside", "Bilag ø-1", "Bilag ø-2" }, Labels(pdf));
    }

    [Fact]
    public void Page_labels_and_settings_work_in_encrypted_and_PDF_A_files()
    {
        var options = new PdfOptions
        {
            Viewer = new PdfViewerSettings { PageMode = PdfPageMode.Bookmarks, StartPage = 2 },
            PageLabels = { new PdfPageLabel(1, PdfPageNumberStyle.LowerRoman, "p. ") },
        };
        var encrypted = HtmlToPdf.Convert(ThreePages, new PdfOptions
        {
            Viewer = options.Viewer,
            PageLabels = options.PageLabels,
            Security = new PdfSecurity { UserPassword = "pw" },
        });
        Assert.Equal(new[] { "p. i", "p. ii", "p. iii" }, Labels(encrypted, "pw"));

        var archival = HtmlToPdf.Convert(ThreePages, new PdfOptions
        {
            Viewer = options.Viewer,
            PageLabels = options.PageLabels,
            Conformance = PdfConformance.PdfA2b,
        });
        PdfTestHelpers.AssertStructurallyValid(archival);
        Assert.Equal(new[] { "p. i", "p. ii", "p. iii" }, Labels(archival));
    }

    [Fact]
    public void Merge_options_set_viewer_settings_and_replace_the_inputs_page_labels()
    {
        var labelled = HtmlToPdf.Convert(ThreePages, new PdfOptions { PageLabels = { new PdfPageLabel(1, PdfPageNumberStyle.LowerRoman) } });
        var plain = HtmlToPdf.Convert("<p>x</p>");

        Assert.Equal(new[] { "i", "ii", "iii", "1" }, Labels(PdfMerger.Merge(labelled, plain)));

        var merged = PdfMerger.Merge(new[] { labelled, plain }, new PdfMergeOptions
        {
            Viewer = new PdfViewerSettings { PageLayout = PdfPageLayout.SinglePage, StartPage = 4, Zoom = PdfZoom.FitPage },
            PageLabels = { new PdfPageLabel(4, PdfPageNumberStyle.Decimal, "Appendix ") },
        });
        PdfTestHelpers.AssertStructurallyValid(merged);
        Assert.Equal(new[] { "1", "2", "3", "Appendix 1" }, Labels(merged));
        var file = PdfFile.Open(merged, null);
        Assert.Equal("SinglePage", file.GetName(file.Catalog, "PageLayout"));
        var action = (PdfArray)file.Resolve(file.Catalog!["OpenAction"])!;
        Assert.Equal(Kids(file)[3], action[0]);
        Assert.Equal("Fit", ((PdfName)action[1]).Value);
    }

    /// <summary>The label a viewer shows for each page, worked out from the file's /PageLabels as the PDF spec says.</summary>
    private static string[] Labels(byte[] pdf, string? password = null)
    {
        var file = PdfFile.Open(pdf, password);
        int count = Kids(file).Count;
        var nums = file.GetArray(file.GetDict(file.Catalog, "PageLabels"), "Nums");
        var labels = new string[count];
        for (int page = 0; page < count; page++)
        {
            if (nums == null) { labels[page] = (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); continue; }
            int at = 0;
            for (int i = 0; i < nums.Count; i += 2)
                if (((PdfNumber)nums[i]).IntValue <= page) at = i;
            int start = ((PdfNumber)nums[at]).IntValue;
            var range = (PdfDict)file.Resolve(nums[at + 1])!;
            int n = page - start + file.GetInt(range, "St", 1);
            string prefix = (file.Resolve(range["P"]) as PdfString)?.Text ?? "";
            labels[page] = prefix + file.GetName(range, "S") switch
            {
                "D" => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "r" => Roman(n).ToLowerInvariant(),
                "R" => Roman(n),
                "A" => new string((char)('A' + (n - 1) % 26), (n - 1) / 26 + 1),
                "a" => new string((char)('a' + (n - 1) % 26), (n - 1) / 26 + 1),
                _ => "",
            };
        }
        return labels;
    }

    private static string Roman(int n)
    {
        var sb = new StringBuilder();
        foreach (var (value, symbol) in new[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            for (; n >= value; n -= value) sb.Append(symbol);
        return sb.ToString();
    }
}
