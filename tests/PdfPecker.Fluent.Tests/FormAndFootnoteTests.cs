namespace PdfPecker.Fluent.Tests;

/// <summary>Form fields and footnotes, which PdfPecker supports and QuestPDF does not.</summary>
public sealed class FormAndFootnoteTests
{
    [Fact]
    public void A_text_field_is_an_input_as_wide_as_the_slot_in_the_slots_text_style()
    {
        var document = Html.Page(c => c.TextField("name").Value("Jane").MaxLength(40).Tooltip("Full name").Required());
        Assert.Equal("<main><input type=\"text\" class=\"c0\" name=\"name\" maxlength=\"40\" title=\"Full name\" required value=\"Jane\"></main>",
            Html.Body(document.GenerateHtml()));
        Assert.Contains(".c0{display:block;margin:0;width:100%;box-sizing:border-box;font-family:inherit;font-size:inherit;font-weight:inherit;font-style:inherit;color:inherit}",
            Html.Css(document));
    }

    [Fact]
    public void Fields_write_the_matching_controls()
    {
        Assert.Equal("<input type=\"password\" class=\"c1\" name=\"pw\" readonly>", Html.Item(c => c.TextField("pw").Password().ReadOnly()));
        Assert.Equal("<textarea class=\"c1\" name=\"notes\" rows=\"3\">a\nb</textarea>", Html.Item(c => c.TextField("notes").Multiline().Value("a\nb")));
        Assert.Equal("<textarea class=\"c1\" name=\"n\" rows=\"2\">\n\nx</textarea>", Html.Item(c => c.TextField("n").Multiline(2).Value("\nx")));
        Assert.Equal("<input type=\"checkbox\" class=\"c1\" name=\"terms\" value=\"agree\" checked>", Html.Item(c => c.CheckBox("terms").ExportValue("agree").Checked()));
        Assert.Equal("<input type=\"radio\" class=\"c1\" name=\"size\" value=\"M\">", Html.Item(c => c.RadioButton("size", "M")));
        Assert.Equal("<select class=\"c1\" name=\"c\"><option value=\"dk\">Danmark</option><option selected>Sverige</option></select>",
            Html.Item(c => c.ComboBox("c").Option("Danmark", "dk").Option("Sverige", selected: true)));
        Assert.Equal("<select class=\"c1\" name=\"l\" size=\"2\" multiple><option>a</option></select>", Html.Item(c => c.ListBox("l").Rows(2).MultiSelect().Option("a")));
    }

    [Fact]
    public void Check_boxes_keep_their_own_size_unless_given_one()
    {
        string css = Html.Css(Html.Page(c => c.Column(k => { k.Item().CheckBox("a"); k.Item().RadioButton("b", "x").Size(12); })));
        Assert.Contains(".c1{display:block;margin:0}", css);
        Assert.Contains(".c2{display:block;margin:0;width:12pt;height:12pt}", css);
    }

    [Fact]
    public void Caller_text_in_fields_stays_text()
    {
        const string evil = "\"><x-evil a='";
        string html = Html.Page(c => c.Column(k =>
        {
            k.Item().TextField(evil).Value(evil).Tooltip(evil);
            k.Item().TextField("t").Multiline().Value("</textarea><x-evil>");
            k.Item().CheckBox("c").ExportValue(evil);
            k.Item().ComboBox("s").Option("</option><x-evil>", evil);
        })).GenerateHtml();
        Assert.DoesNotContain("<x-evil", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_field_arguments_throw_argument_exceptions()
    {
        Assert.ThrowsAny<ArgumentException>(() => Html.Of(c => c.TextField(" ")));
        Assert.ThrowsAny<ArgumentException>(() => Html.Of(c => c.RadioButton("g", "")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.TextField("a").MaxLength(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.TextField("a").Multiline(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.ListBox("a").Rows(1001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.CheckBox("a").Size(-1)));
    }

    [Fact]
    public void Fields_become_fillable_pdf_fields()
    {
        byte[] pdf = Html.Page(c => c.Column(k =>
        {
            k.Item().TextField("name").Value("Jane").Tooltip("Full name");
            k.Item().TextField("notes").Multiline().Value("a\nb");
            k.Item().CheckBox("terms").Checked();
            k.Item().RadioButton("size", "S");
            k.Item().RadioButton("size", "M").Checked();
            k.Item().ComboBox("country").Option("Danmark", "dk").Option("Sverige", "se", selected: true);
            k.Item().ListBox("colors").MultiSelect().Option("Red", selected: true).Option("Green").Option("Blue", selected: true);
        })).GeneratePdf();
        var fields = PdfForm.GetFields(pdf).ToDictionary(f => f.Name);
        Assert.Equal(PdfFormFieldType.Text, fields["name"].Type);
        Assert.Equal("Jane", fields["name"].Value);
        Assert.Equal("Full name", fields["name"].ToolTip);
        Assert.True(fields["notes"].Multiline);
        Assert.Equal(PdfFormFieldType.CheckBox, fields["terms"].Type);
        Assert.NotEqual("Off", fields["terms"].Value);
        Assert.Equal(PdfFormFieldType.RadioButton, fields["size"].Type);
        Assert.Equal("M", fields["size"].Value);
        Assert.Equal("se", fields["country"].Value);
        Assert.Equal(new[] { "Red", "Blue" }, fields["colors"].Values);
    }

    [Fact]
    public void A_footnote_is_a_span_that_floats_to_the_footnote_area()
    {
        var document = Html.Page(c => c.Text(t => { t.Span("Text"); t.Footnote("Note").Italic(); t.Footnote("Big").FontSize(12); }));
        Assert.Equal("<main><p>Text<span class=\"c0\">Note</span><span class=\"c1\">Big</span></p></main>", Html.Body(document.GenerateHtml()));
        string css = Html.Css(document);
        Assert.Contains(".c0{font-style:italic;float:footnote;font-size:0.8em}", css);
        Assert.Contains(".c1{font-size:12pt;float:footnote}", css);
        Assert.Contains("@page{@footnote{", css);
    }

    [Fact]
    public void Footnotes_are_numbered_and_set_at_the_bottom_of_their_page()
    {
        byte[] pdf = Document.Create(d => d.Page(p =>
        {
            p.Margin(40);
            p.Content().Column(k =>
            {
                k.Item().Text(t => { t.Span("First claim"); t.Footnote("Source one."); t.Span(" and second"); t.Footnote("Source two."); });
                k.Item().PageBreak();
                k.Item().Text(t => { t.Span("Third claim"); t.Footnote("Source three."); });
            });
        })).GeneratePdf();
        var pages = PdfText.ExtractPages(pdf);
        Assert.Equal(2, pages.Count);
        var note = pages[0].Lines.Single(l => l.Text.Contains("Source one.", StringComparison.Ordinal));
        var claim = pages[0].Lines.Single(l => l.Text.Contains("First claim", StringComparison.Ordinal));
        Assert.True(Math.Abs(note.Y - claim.Y) > pages[0].Height / 2, "the note sits at the bottom of the page");
        Assert.Contains("2", pages[0].Lines.Single(l => l.Text.Contains("Source two.", StringComparison.Ordinal)).Text, StringComparison.Ordinal);
        Assert.Contains("3", pages[1].Lines.Single(l => l.Text.Contains("Source three.", StringComparison.Ordinal)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_footnote_in_a_header_throws()
        => Assert.Throws<InvalidOperationException>(() => Document.Create(d => d.Page(p =>
        {
            p.Header().Text(t => t.Footnote("x"));
            p.Content().Text("y");
        })).GenerateHtml());
}
