using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

public class AccessibilityTests
{
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    private static string Tagged(string html, PdfAccessibility mode = PdfAccessibility.Tagged, PdfOptions? options = null)
    {
        options ??= new PdfOptions();
        options.Accessibility = mode;
        var pdf = PdfTestHelpers.ConvertUncompressed(html, options);
        PdfTestHelpers.AssertStructurallyValid(pdf);
        return PdfTestHelpers.Latin1(pdf);
    }

    private static string Content(string pdf) => PdfTestHelpers.AllContent(Encoding.Latin1.GetBytes(pdf));

    /// <summary>The structure element types in document order, e.g. "Document P Link".</summary>
    private static string Types(string pdf)
        => string.Join(" ", Regex.Matches(pdf, @"/Type /StructElem /S /(\w+)").Select(m => m.Groups[1].Value));

    /// <summary>Checks that every painting operator of a content stream is inside marked content, and that the sequences nest.</summary>
    private static void AssertAllContentMarked(string content)
    {
        int depth = 0;
        bool inText = false;
        int textDepth = 0;
        foreach (var line in content.Split('\n'))
        {
            foreach (Match m in Regex.Matches(line, @"(?<![\w/])(BDC|BMC|EMC|BT|ET|Tj|TJ|Do|f|S|B|f\*|sh)(?![\w*])"))
            {
                switch (m.Value)
                {
                    case "BDC": case "BMC": depth++; break;
                    case "EMC":
                        Assert.True(depth > 0, "EMC without a sequence");
                        Assert.False(inText && depth == textDepth, "A text object spans the end of a marked-content sequence");
                        depth--;
                        break;
                    case "BT": inText = true; textDepth = depth; break;
                    case "ET": Assert.Equal(textDepth, depth); inText = false; break;
                    // A transparency group's form marks its own content.
                    case "Do" when line.Contains("/Fx"): break;
                    default: Assert.True(depth > 0, $"Unmarked content: {line}"); break;
                }
            }
        }
        Assert.Equal(0, depth);
    }

    [Fact]
    public void Fieldset_legend_on_the_border_stays_marked()
    {
        // The border behind the legend is cut out with a clip, which must stay outside the tagged content.
        var pdf = Tagged("<title>Form</title><fieldset><legend>Customer</legend><p>Name: Jane</p></fieldset>", PdfAccessibility.PdfUA1);
        var content = Content(pdf);
        Assert.Contains(" re W* n", content);
        AssertAllContentMarked(content);
        Assert.Equal("Document P P", Types(pdf)); // the legend is a paragraph, as before
    }

    [Fact]
    public void Untagged_by_default()
    {
        var pdf = PdfTestHelpers.Latin1(PdfTestHelpers.ConvertUncompressed("<h1>Title</h1><p>Text</p>"));
        Assert.DoesNotContain("/StructTreeRoot", pdf);
        Assert.DoesNotContain("/MarkInfo", pdf);
        Assert.DoesNotContain("BDC", PdfTestHelpers.AllContent(PdfTestHelpers.ConvertUncompressed("<p>Text</p>")));
    }

    [Fact]
    public void Structure_follows_the_html()
    {
        var pdf = Tagged("""
            <html lang="da"><body>
            <h1>Lønseddel</h1>
            <p>Hello <b>bold</b> <a href="https://pdfpecker.net">link</a> end.</p>
            <ol><li>One</li><li>Two</li></ol>
            <blockquote><p>Quote</p></blockquote>
            </body></html>
            """);
        Assert.Equal("Document H1 P Link L LI Lbl LBody LI Lbl LBody BlockQuote P", Types(pdf));
        Assert.Contains("/MarkInfo << /Marked true >> /StructTreeRoot", pdf);
        Assert.Contains("/Lang (da)", pdf);
        Assert.Matches(@"/Type /StructTreeRoot /K \d+ 0 R /ParentTree \d+ 0 R /ParentTreeNextKey \d+", pdf);
        Assert.Contains("/H1 <</MCID 0>> BDC", pdf);
        Assert.Contains("/StructParents 0 /Tabs /S", pdf);
        AssertAllContentMarked(Content(pdf));
    }

    [Fact]
    public void Text_outside_a_paragraph_gets_one()
    {
        var pdf = Tagged("<div>Loose text <span>and a span</span><p>Para</p>more loose</div><ul><li><p>In item</p></li></ul>");
        Assert.Equal("Document P P P L LI LBody P", Types(pdf));
    }

    [Fact]
    public void Links_are_tagged_with_their_annotations()
    {
        var pdf = Tagged("<p>See <a href='https://example.com/x'>the site</a>.</p><a href='#p' style='display:block'><div>Block</div></a><p id='p'>Target</p>");
        Assert.Equal(2, Regex.Matches(pdf, @"/Type /OBJR /Pg \d+ 0 R /Obj \d+ 0 R").Count);
        Assert.Matches(@"/Subtype /Link [^\n]*/StructParent 1 /Contents \(the site\)", pdf);
        Assert.Matches(@"/Subtype /Link [^\n]*/StructParent 2 /Contents \(Block\)", pdf);
    }

    [Fact]
    public void Images_need_alt_text_to_be_figures()
    {
        var pdf = Tagged($"""
            <p><img src="{Pixel}" alt="Company logo" width="20" height="20"></p>
            <p><img src="{Pixel}" alt="" width="20" height="20"><img src="{Pixel}" width="20" height="20"></p>
            <svg width="20" height="20"><title>Blue dot</title><circle cx="10" cy="10" r="8" fill="blue"/></svg>
            <svg width="20" height="20"><circle cx="10" cy="10" r="8" fill="red"/></svg>
            """);
        Assert.Equal("Document P Figure P Figure", Types(pdf));
        Assert.Contains("/S /Figure /P", pdf);
        Assert.Contains("/Alt (Company logo)", pdf);
        Assert.Contains("/Alt (Blue dot)", pdf);
        var content = Content(pdf);
        Assert.Equal(2, Regex.Matches(content, @"/Figure <</MCID \d+>> BDC").Count);
        Assert.Equal(3, Regex.Matches(content, @"/Im1 Do").Count);
        AssertAllContentMarked(content);
    }

    [Fact]
    public void Heading_levels_do_not_skip()
    {
        var pdf = Tagged("<h2>A</h2><h4>B</h4><h3>C</h3><h1>D</h1><h3>E</h3>");
        Assert.Equal("Document H1 H2 H3 H1 H2", Types(pdf));
    }

    [Fact]
    public void Tables_get_header_scopes_and_spans()
    {
        var pdf = Tagged("""
            <table>
              <thead><tr><th>Item</th><th>Amount</th></tr></thead>
              <tbody><tr><th>Apples</th><td>3</td></tr><tr><td colspan="2">Total</td></tr><tr><td></td><td>x</td></tr></tbody>
            </table>
            """);
        Assert.Equal("Document Table THead TR TH TH TBody TR TH TD TR TD TR TD TD", Types(pdf));
        Assert.Equal(2, Regex.Matches(pdf, @"/A << /O /Table /Scope /Column >>").Count);
        Assert.Single(Regex.Matches(pdf, @"/A << /O /Table /Scope /Row >>"));
        Assert.Contains("/A << /O /Table /ColSpan 2 >>", pdf);
    }

    [Fact]
    public void Repeated_table_headers_are_artifacts()
    {
        var rows = string.Concat(Enumerable.Range(1, 120).Select(i => $"<tr><td>Row {i}</td><td>{i}</td></tr>"));
        var pdf = Tagged($"<table><thead><tr><th>Name</th><th>Value</th></tr></thead><tbody>{rows}</tbody></table>");
        Assert.True(PdfTestHelpers.PageCount(Encoding.Latin1.GetBytes(pdf)) > 1);
        var content = Content(pdf);
        Assert.True(Regex.Matches(content, @"\(Name\) Tj").Count > 1);
        Assert.Equal(2, Regex.Matches(content, @"/TH <</MCID").Count);
        AssertAllContentMarked(content);
    }

    [Fact]
    public void Language_changes_are_tagged()
    {
        var pdf = Tagged("<html lang='en'><p>Hello <span lang='da'>hej</span></p><p lang='en'>Same</p><div lang='de'>Hallo</div></html>");
        Assert.Equal("Document P Span P Div P", Types(pdf));
        Assert.Contains("/S /Span /P", pdf);
        Assert.Single(Regex.Matches(pdf, @"/StructElem [^\n]*/Lang \(da\)"));
        Assert.Single(Regex.Matches(pdf, @"/StructElem [^\n]*/Lang \(de\)"));
        Assert.Matches(@"/Type /Catalog [^\n]*/Lang \(en\)", pdf);
    }

    [Fact]
    public void Decorations_headers_and_hidden_content_are_artifacts()
    {
        var html = """
            <div data-sticky-pdf-header height="12mm">Running header {page}</div>
            <div style="background:#eef;border:1px solid #99c;text-shadow:1px 1px 1px #888"><p>Body</p></div>
            <p aria-hidden="true">Hidden</p>
            <div style="position:fixed;bottom:0">Fixed</div>
            """;
        var pdf = Tagged(html, options: new PdfOptions { FooterText = "Page {page}", Watermark = new PdfWatermark { Text = "DRAFT" } });
        Assert.Equal("Document P", Types(pdf));
        var content = Content(pdf);
        AssertAllContentMarked(content);
        Assert.Single(Regex.Matches(content, @"<</MCID \d+>> BDC"));
    }

    [Fact]
    public void Translucent_layers_are_tagged_in_their_own_stream()
    {
        var pdf = Tagged("<p>Before</p><div style='opacity:0.5'><p>Faded</p></div><div style='overflow:hidden;height:20px'><p>Clipped</p></div>");
        Assert.Equal("Document P P P", Types(pdf));
        Assert.Matches(@"/Subtype /Form [^\n]*/StructParents 1", pdf);
        Assert.Matches(@"/Type /MCR /Pg \d+ 0 R /MCID 0 /Stm \d+ 0 R", pdf);
        Assert.Contains("q /Artifact BMC ", Content(pdf)); // the clip path
        AssertAllContentMarked(Content(pdf));
    }

    [Fact]
    public void Tagging_does_not_change_how_pages_look()
    {
        var html = $"""
            <h1 style="color:#336">Report</h1>
            <p style="background:#ffd">Text with <a href="https://x.dk">a link</a> and <img src="{Pixel}" alt="pixel" width="30" height="30">.</p>
            <div style="opacity:0.5;background:#00f;color:#fff;padding:8px"><p>Faded</p></div>
            <div style="overflow:hidden;height:24px;border-radius:6px;background:#eee"><p>Clipped one</p><p>Clipped two</p></div>
            <ul><li>Item</li></ul>
            """;
        var plain = PdfRenderer.Open(HtmlToPdf.Convert(html)).RenderPage(0, new ImageOptions { Dpi = 50 });
        var tagged = PdfRenderer.Open(HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.Tagged })).RenderPage(0, new ImageOptions { Dpi = 50 });
        Assert.Equal(plain.Rgba, tagged.Rgba);
    }

    [Fact]
    public void PdfUA_claims_conformance_and_embeds_fonts()
    {
        var pdf = Tagged("<html><head><title>Payslip</title></head><p>Hello</p></html>", PdfAccessibility.PdfUA1);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", pdf);
        Assert.DoesNotContain("pdfaid:part", pdf);
        Assert.Contains("/ViewerPreferences << /DisplayDocTitle true >>", pdf);
        Assert.Contains("/Lang (en)", pdf);
        Assert.Contains("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Payslip</rdf:li>", pdf);
        Assert.DoesNotContain("/Subtype /Type1", pdf);
        Assert.Contains("/FontFile2", pdf);
        Assert.StartsWith("%PDF-1.7", pdf);
    }

    [Fact]
    public void PdfUA_with_PdfA_describes_the_pdfua_schema()
    {
        var pdf = Tagged("<title>Invoice</title><p>Hello</p>", PdfAccessibility.PdfUA1, new PdfOptions { Conformance = PdfConformance.PdfA2b });
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", pdf);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", pdf);
        Assert.Contains("<pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>", pdf);
        Assert.Contains("<pdfaProperty:valueType>Integer</pdfaProperty:valueType><pdfaProperty:category>internal</pdfaProperty:category>", pdf);
    }

    [Fact]
    public void PdfUA_title_falls_back_to_the_first_heading()
    {
        var pdf = Tagged("<h2>Annual   report</h2><p>Hello</p>", PdfAccessibility.PdfUA1, new PdfOptions { Language = "en-GB" });
        Assert.Contains("/Title (Annual report)", pdf);
        Assert.Contains("/Lang (en-GB)", pdf);
        var error = Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>No title</p>", new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 }));
        Assert.Contains("title", error.Message);
    }

    [Fact]
    public void PdfUA_encryption_must_allow_screen_readers()
    {
        var options = new PdfOptions
        {
            Accessibility = PdfAccessibility.PdfUA1, Title = "T",
            Security = new PdfSecurity { UserPassword = "x", Permissions = PdfPermissions.Print },
        };
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", options));
        options.Security.Permissions |= PdfPermissions.ExtractForAccessibility;
        Assert.NotEmpty(HtmlToPdf.Convert("<p>x</p>", options));
    }

    [Fact]
    public void Markdown_can_be_PdfUA()
    {
        var pdf = PdfTestHelpers.Latin1(MarkdownToPdf.Convert("# Guide\n\nSome *text* and a [link](https://x.dk).\n\n- one\n- two\n",
            new PdfOptions { Accessibility = PdfAccessibility.PdfUA1, Compress = false }));
        Assert.Equal("Document H1 P Link L LI LBody LI LBody", Types(pdf));
        Assert.Contains("/Title (Guide)", pdf);
    }
}
