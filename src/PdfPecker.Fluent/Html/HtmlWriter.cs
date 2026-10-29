using System.Globalization;
using System.Text;
using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent.Html;

/// <summary>
/// Turns one page set into an HTML document. This is the only place markup is written: caller text goes through
/// <see cref="WriteText"/> and <see cref="WriteAttribute"/>, and only <see cref="HtmlElement"/> is written as given.
/// </summary>
internal sealed class HtmlWriter
{
    /// <summary>
    /// The deepest nesting of elements the writer opens. PdfPecker's HTML parser stops nesting at 256, so deeper
    /// documents would be laid out differently from what the code describes; failing loudly is better.
    /// </summary>
    public const int MaxDepth = 200;

    /// <summary>The most table cells (rows times columns) a table can place.</summary>
    public const int MaxTableGrid = 4_000_000;

    // Every element starts from these rules, so the fluent calls alone decide spacing, as in QuestPDF: no default
    // margins, text keeps its spaces and line breaks, tables have fixed column widths and no cell spacing. Long words
    // break where they would overflow.
    private const string BaseCss =
        "*{box-sizing:border-box}" +
        "html,body{margin:0;padding:0}" +
        "body{font-family:sans-serif;font-size:12pt;color:#000;overflow-wrap:break-word}" +
        "p,h1,h2,h3,h4,h5,h6{margin:0;font-size:inherit;font-weight:inherit;white-space:pre-wrap}" +
        "a{color:inherit;text-decoration:inherit}" +
        "table{width:100%;table-layout:fixed;border-collapse:separate;border-spacing:0}" +
        "th,td{padding:0;vertical-align:top;text-align:inherit;font-weight:inherit}" +
        "img{display:block}";

    private readonly StringBuilder _html = new();
    private readonly Dictionary<string, string> _classes = new(StringComparer.Ordinal);
    private readonly StringBuilder _rules = new();
    private readonly Stack<string> _open = new();
    private readonly ImageTable _images;
    private Band _band;

    /// <summary>Whether the band being written has content for the first page only or for the other pages only.</summary>
    private bool _bandHasOnce;

    /// <summary>The text alternative that SemanticImage gives the images inside it.</summary>
    private string? _alternativeText;

    /// <summary>Whether some text shows a section's page number, which needs the rule that shows it.</summary>
    private bool _sectionPages;

    /// <summary>Whether some text has a footnote, which needs the rule that sets out the footnote area.</summary>
    private bool _footnotes;

    private HtmlWriter(ImageTable images) => _images = images;

    /// <summary>
    /// The complete HTML document. Several page sets each become a named page of their own (page: p0, p1, ...), with
    /// their own size, margins, header and footer, so page numbers run on through the whole document.
    /// </summary>
    public static string Write(IReadOnlyList<PageSet> pages, IReadOnlyList<string> css, DocumentMetadata metadata, ImageTable images)
    {
        var writer = new HtmlWriter(images);
        return writer.Document(pages, css, metadata);
    }

    private string Document(IReadOnlyList<PageSet> pages, IReadOnlyList<string> css, DocumentMetadata metadata)
    {
        string? bodyClass = null;
        if (pages.Count == 1)
        {
            var bodyDeclarations = new List<(string, string)>();
            CssValue.AddTextStyle(bodyDeclarations, pages[0].Style, forBox: true);
            bodyClass = bodyDeclarations.Count > 0 ? Class(bodyDeclarations) : null;
            RenderPageSet(pages[0], null);
        }
        else
        {
            for (int i = 0; i < pages.Count; i++)
            {
                // The page set's text style and direction go on an element around its bands and content, which puts
                // them all on its named page.
                var page = pages[i];
                string name = PageName(i);
                var declarations = new List<(string, string)> { ("page", name) };
                CssValue.AddTextStyle(declarations, page.Style, forBox: true);
                _html.Append("<div");
                WriteAttribute("class", Class(declarations));
                if (page.RightToLeft) WriteAttribute("dir", "rtl");
                _html.Append('>');
                RenderPageSet(page, name);
                _html.Append("</div>");
            }
        }
        string body = _html.ToString();

        _html.Clear();
        _html.Append("<!DOCTYPE html>\n<html");
        if (!string.IsNullOrWhiteSpace(metadata.Language)) WriteAttribute("lang", metadata.Language);
        _html.Append("><head><meta charset=\"utf-8\">");
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            _html.Append("<title>");
            WriteText(metadata.Title);
            _html.Append("</title>");
        }
        _html.Append("<style>").Append(BaseCss);
        PageRule(pages[0], null);
        if (pages.Count > 1)
            for (int i = 0; i < pages.Count; i++) PageRule(pages[i], PageName(i));
        _html.Append(_rules);
        if (_sectionPages) _html.Append("[data-section-page]::after{content:target-counter(attr(data-section-page),page)}");
        if (_footnotes) _html.Append("@page{@footnote{margin-top:8pt;padding-top:4pt;border-top:0.5pt solid currentColor}}");
        foreach (var rule in css) _html.Append('\n').Append(rule);
        _html.Append("</style></head>\n<body");
        if (bodyClass != null) WriteAttribute("class", bodyClass);
        if (pages.Count == 1 && pages[0].RightToLeft) WriteAttribute("dir", "rtl");
        _html.Append('>').Append(body).Append("</body></html>\n");
        return _html.ToString();
    }

    private static string PageName(int index) => "p" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The @page rule of a page set: the document's when <paramref name="name"/> is null, else its named page's.</summary>
    private void PageRule(PageSet page, string? name)
    {
        _html.Append("@page");
        if (name != null) _html.Append(' ').Append(name);
        _html.Append("{size:").Append(CssValue.Pt(page.Size.Width)).Append(' ').Append(CssValue.Pt(page.Size.Height))
            .Append(";margin:").Append(CssValue.Pt(page.MarginTop)).Append(' ').Append(CssValue.Pt(page.MarginRight)).Append(' ')
            .Append(CssValue.Pt(page.MarginBottom)).Append(' ').Append(CssValue.Pt(page.MarginLeft));
        if (page.Background is { } background) _html.Append(";background:").Append(CssValue.Color(background));
        _html.Append('}');
    }

    /// <summary>The header, content and footer of a page set; its bands name its page when there are several.</summary>
    private void RenderPageSet(PageSet page, string? name)
    {
        if (page.Header.Child != null) RenderBand(page.Header, "header", name);
        // The footer comes after the content, so a browser that does not repeat it (Chrome) still shows it last.
        RenderContainer(page.Content, Box.Slot("main"));
        if (page.Footer.Child != null) RenderBand(page.Footer, "footer", name);
    }

    /// <summary>
    /// Writes a header or footer, which PdfPecker repeats on every page. When some of its content uses ShowOnce or
    /// SkipOnce, the band is written a second time for the first page, which PdfPecker then uses there instead.
    /// </summary>
    private void RenderBand(Container band, string tag, string? pageName)
    {
        _band = Band.Running;
        _bandHasOnce = false;
        RenderContainer(band, BandSlot(tag, "data-sticky-pdf-" + tag, pageName));
        if (_bandHasOnce)
        {
            _band = Band.First;
            RenderContainer(band, BandSlot(tag, "data-sticky-pdf-first-" + tag, pageName));
        }
        _band = Band.None;
    }

    private static Box BandSlot(string tag, string attribute, string? pageName)
        => pageName == null ? Box.Slot(tag, (attribute, "")) : Box.Slot(tag, (attribute, ""), ("data-sticky-pdf-page", pageName));

    // Slots and decorators

    /// <summary>
    /// Writes a slot: its decorators merge into as few elements as the CSS box model allows, and the content then
    /// either becomes the last of those elements or goes inside it.
    /// </summary>
    private void RenderContainer(Container slot, Box box)
    {
        int depth = _open.Count;
        int? heading = null;
        string? outerAlternativeText = _alternativeText;
        Element? element = slot.Child;
        while (element is Decorator decorator)
        {
            if (decorator.Kind == DecoratorKind.Hidden)
            {
                element = null;
                break;
            }
            if (decorator.Kind is DecoratorKind.ShowOnce or DecoratorKind.SkipOnce)
            {
                // Content outside the bands appears once, on its first page, so ShowOnce keeps it and SkipOnce
                // leaves it out, as QuestPDF does.
                _bandHasOnce |= _band != Band.None;
                if (decorator.Kind == DecoratorKind.ShowOnce ? _band == Band.Running : _band != Band.Running)
                {
                    element = null;
                    break;
                }
                element = decorator.Inner.Child;
                continue;
            }
            if (decorator.Kind == DecoratorKind.Heading) heading = decorator.Level;
            else if (decorator.Kind == DecoratorKind.AlternativeText) _alternativeText = decorator.Text;
            else if (decorator.Kind == DecoratorKind.EnsureSpace) box = EnsureSpace(box, decorator.Value);
            else if (!box.TryApply(decorator))
            {
                bool fill = box.FillsWith(decorator);
                // A percentage height resolves against a table cell only when the cell has a height of its own, as in
                // Chrome, so the cell gets one too small to matter and grows with its row.
                if (fill) box.Declarations.Add(("height", "1px"));
                Open(box);
                box = box.Child();
                if (fill) box.Declarations.Add(("height", "100%"));
                if (!box.TryApply(decorator))
                {
                    Open(box);
                    box = box.Child();
                    box.TryApply(decorator); // a box without alignment takes any decorator
                }
            }
            element = decorator.Inner.Child;
        }

        switch (element)
        {
            case null:
                if (box.MustEmit) Open(box);
                break;
            case TextElement text:
                RenderText(text, box, heading);
                break;
            case ColumnElement column:
                RenderColumn(column, box);
                break;
            case RowElement row:
                RenderRow(row, box);
                break;
            case TableElement table:
                RenderTable(table, box);
                break;
            case ImageElement image:
                RenderImage(image, box);
                break;
            case LineElement line:
                RenderLine(line, box);
                break;
            case PageBreakElement:
                box = Absorbing(box);
                box.Declarations.Add(("break-before", "page"));
                Open(box);
                break;
            case LayersElement layers:
                RenderLayers(layers, box);
                break;
            case InlinedElement inlined:
                RenderInlined(inlined, box);
                break;
            case MultiColumnElement multiColumn:
                RenderMultiColumn(multiColumn, box);
                break;
            case DecorationElement decoration:
                RenderDecoration(decoration, box);
                break;
            case ListElement list:
                RenderList(list, box);
                break;
            case FormFieldElement field:
                RenderFormField(field, box);
                break;
            case HtmlElement html:
                if (box.MustEmit) Open(box);
                _html.Append(html.Html);
                break;
        }
        CloseTo(depth);
        _alternativeText = outerAlternativeText;
    }

    /// <summary>The box itself if content can become it (a plain block), or a new box inside it.</summary>
    private Box Absorbing(Box box)
    {
        if (box.CanBecomeBlock || box.CanBecomeLayout) return box;
        Open(box);
        return box.Child();
    }

    /// <summary>
    /// An unbreakable empty block as tall as the space wanted, pulled back up by a negative margin: it moves to the next
    /// page when the space is not left, and the content after it moves with it. The box is written first, so the block
    /// is not an item of a column's flex layout, where it would add a gap.
    /// </summary>
    private Box EnsureSpace(Box box, float height)
    {
        Open(box);
        string h = CssValue.Pt(height);
        _html.Append("<div");
        WriteAttribute("class", Class([("break-inside", "avoid"), ("height", h), ("margin-bottom", "-" + h)]));
        _html.Append("></div>");
        return box.Child();
    }

    /// <summary>The box itself if it can take the given properties as a plain block, or a new box inside it.</summary>
    private Box Fresh(Box box, params string[] properties)
    {
        bool free = box.CanBecomeBlock;
        foreach (var property in properties)
            if (box.Has(property)) free = false;
        if (free) return box;
        Open(box);
        return box.Child();
    }

    private void RenderLayers(LayersElement layers, Box box)
    {
        // Every layer is positioned, so they paint in document order above the in-flow content around them, and the
        // stack's own z-index keeps them together in one stacking context.
        box = Fresh(box, "position", "z-index");
        box.Declarations.Add(("position", "relative"));
        box.Declarations.Add(("z-index", "0"));
        Open(box);
        foreach (var (primary, slot) in layers.Layers)
        {
            var layer = Box.Slot("div");
            layer.Declarations.Add(("position", primary ? "relative" : "absolute"));
            if (!primary)
            {
                layer.Declarations.Add(("top", "0"));
                layer.Declarations.Add(("right", "0"));
                layer.Declarations.Add(("bottom", "0"));
                layer.Declarations.Add(("left", "0"));
            }
            RenderContainer(slot, layer);
        }
    }

    private void RenderInlined(InlinedElement inlined, Box box)
    {
        box = Absorbing(box);
        box.Declarations.Add(("display", "flex"));
        box.Declarations.Add(("flex-wrap", "wrap"));
        if (inlined.VerticalSpacing > 0) box.Declarations.Add(("row-gap", CssValue.Pt(inlined.VerticalSpacing)));
        if (inlined.HorizontalSpacing > 0) box.Declarations.Add(("column-gap", CssValue.Pt(inlined.HorizontalSpacing)));
        if (inlined.Justify != "flex-start") box.Declarations.Add(("justify-content", inlined.Justify));
        box.Declarations.Add(("align-items", inlined.Align));
        Open(box);
        foreach (var slot in inlined.Items)
        {
            var item = Box.Slot("div");
            item.Declarations.Add(("flex", "0 0 auto"));
            item.Declarations.Add(("max-width", "100%"));
            RenderContainer(slot, item);
        }
    }

    private void RenderMultiColumn(MultiColumnElement multiColumn, Box box)
    {
        box = Fresh(box, "column-count", "column-gap");
        box.Declarations.Add(("column-count", multiColumn.Columns.ToString(CultureInfo.InvariantCulture)));
        box.Declarations.Add(("column-gap", CssValue.Pt(multiColumn.Spacing)));
        if (!multiColumn.Balance) box.Declarations.Add(("column-fill", "auto"));
        Open(box);
        RenderContainer(multiColumn.Content, new Box());
    }

    /// <summary>
    /// A one-column table: PdfPecker repeats a table's head and foot on every page the table spans, which is what a
    /// decoration's before and after parts do. It repeats them where the table breaks between rows, not inside a row,
    /// so the items of a column content become rows of their own.
    /// </summary>
    private void RenderDecoration(DecorationElement decoration, Box box)
    {
        if (box.MustEmit) Open(box);
        _html.Append("<table>");
        if (decoration.Before.Child != null)
        {
            _html.Append("<thead><tr>");
            RenderContainer(decoration.Before, Cell());
            _html.Append("</tr></thead>");
        }
        _html.Append("<tbody>");
        if (decoration.Content.Child is ColumnElement column)
        {
            for (int i = 0; i < column.Items.Count; i++)
            {
                var cell = Cell();
                if (i > 0 && column.Spacing > 0) cell.Declarations.Add(("padding-top", CssValue.Pt(column.Spacing)));
                _html.Append("<tr>");
                RenderContainer(column.Items[i], cell);
                _html.Append("</tr>");
            }
        }
        else
        {
            _html.Append("<tr>");
            RenderContainer(decoration.Content, Cell());
            _html.Append("</tr>");
        }
        _html.Append("</tbody>");
        if (decoration.After.Child != null)
        {
            _html.Append("<tfoot><tr>");
            RenderContainer(decoration.After, Cell());
            _html.Append("</tr></tfoot>");
        }
        _html.Append("</table>");

        static Box Cell()
        {
            var cell = Box.Slot("td");
            cell.IsCell = true;
            cell.SizeLocked = true;
            return cell;
        }
    }

    private void RenderList(ListElement list, Box box)
    {
        if (box.MustEmit) Open(box);
        var listBox = Box.Slot(list.Ordered ? "ol" : "ul");
        listBox.Declarations.Add(("margin", "0"));
        listBox.Declarations.Add(("padding", "0 0 0 " + CssValue.Pt(list.Indent)));
        listBox.Declarations.Add(("list-style-type", list.MarkerStyle));
        if (list.Ordered && list.Start != 1) listBox.Attributes.Add(("start", list.Start.ToString(CultureInfo.InvariantCulture)));
        Open(listBox);
        bool first = true;
        foreach (var slot in list.Items)
        {
            var item = Box.Slot("li");
            if (!first && list.Spacing > 0) item.Declarations.Add(("margin-top", CssValue.Pt(list.Spacing)));
            first = false;
            RenderContainer(slot, item);
        }
    }

    private void RenderColumn(ColumnElement column, Box box)
    {
        box = Absorbing(box);
        box.Declarations.Add(("display", "flex"));
        box.Declarations.Add(("flex-direction", "column"));
        if (column.Spacing > 0) box.Declarations.Add(("row-gap", CssValue.Pt(column.Spacing)));
        Open(box);
        foreach (var item in column.Items) RenderContainer(item, new Box());
    }

    private void RenderRow(RowElement row, Box box)
    {
        box = Absorbing(box);
        box.Declarations.Add(("display", "flex"));
        if (row.Spacing > 0) box.Declarations.Add(("column-gap", CssValue.Pt(row.Spacing)));
        Open(box);
        foreach (var (kind, size, slot) in row.Items)
        {
            var item = Box.Slot("div");
            item.SizeLocked = true;
            switch (kind)
            {
                case RowItemKind.Relative:
                    item.Declarations.Add(("flex-grow", CssValue.Number(size)));
                    item.Declarations.Add(("flex-shrink", "1"));
                    item.Declarations.Add(("flex-basis", "0"));
                    item.Declarations.Add(("min-width", "0"));
                    break;
                case RowItemKind.Constant:
                    item.Declarations.Add(("flex-grow", "0"));
                    item.Declarations.Add(("flex-shrink", "0"));
                    item.Declarations.Add(("width", CssValue.Pt(size)));
                    break;
                default:
                    item.Declarations.Add(("flex-grow", "0"));
                    item.Declarations.Add(("flex-shrink", "0"));
                    break;
            }
            RenderContainer(slot, item);
        }
    }

    private void RenderImage(ImageElement image, Box box)
    {
        if (box.MustEmit) Open(box);
        var declarations = new List<(string, string)>();
        switch (image.Scaling)
        {
            case ImageScaling.FitHeight:
                declarations.Add(("height", "100%"));
                declarations.Add(("width", "auto"));
                declarations.Add(("max-width", "100%"));
                break;
            case ImageScaling.FitArea:
                declarations.Add(("width", "100%"));
                declarations.Add(("height", "100%"));
                declarations.Add(("object-fit", "contain"));
                break;
            case ImageScaling.FitUnproportionally:
                declarations.Add(("width", "100%"));
                declarations.Add(("height", "100%"));
                break;
            default:
                declarations.Add(("width", "100%"));
                declarations.Add(("height", "auto"));
                break;
        }
        _html.Append("<img");
        WriteAttribute("class", Class(declarations));
        WriteAttribute("src", _images.Source(image.Data));
        WriteAttribute("alt", image.AlternativeText.Length > 0 ? image.AlternativeText : _alternativeText ?? "");
        _html.Append('>');
    }

    /// <summary>
    /// Writes a form control, which PdfPecker turns into a fillable field. Its text follows the slot's text style
    /// rather than the browser's form font, and text fields and lists are as wide as the slot.
    /// </summary>
    private void RenderFormField(FormFieldElement field, Box box)
    {
        if (box.MustEmit) Open(box);
        var declarations = new List<(string, string)> { ("display", "block"), ("margin", "0") };
        if (field.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton)
        {
            if (field.Size is { } size)
            {
                declarations.Add(("width", CssValue.Pt(size)));
                declarations.Add(("height", CssValue.Pt(size)));
            }
        }
        else
        {
            declarations.Add(("width", "100%"));
            declarations.Add(("box-sizing", "border-box"));
            declarations.Add(("font-family", "inherit"));
            declarations.Add(("font-size", "inherit"));
            declarations.Add(("font-weight", "inherit"));
            declarations.Add(("font-style", "inherit"));
            declarations.Add(("color", "inherit"));
        }
        string cls = Class(declarations);

        switch (field.Kind)
        {
            case FormFieldKind.Text when field.Rows > 0:
                _html.Append("<textarea");
                FieldAttributes(field, cls);
                WriteAttribute("rows", field.Rows.ToString(CultureInfo.InvariantCulture));
                _html.Append('>');
                // A textarea drops a line break straight after its start tag, so one more keeps a value that starts with one.
                if (field.Value?.StartsWith('\n') == true) _html.Append('\n');
                WriteText(field.Value ?? "");
                _html.Append("</textarea>");
                break;
            case FormFieldKind.Text:
                _html.Append("<input");
                WriteAttribute("type", field.Password ? "password" : "text");
                FieldAttributes(field, cls);
                if (field.Value != null) WriteAttribute("value", field.Value);
                _html.Append('>');
                break;
            case FormFieldKind.CheckBox:
            case FormFieldKind.RadioButton:
                _html.Append("<input");
                WriteAttribute("type", field.Kind == FormFieldKind.CheckBox ? "checkbox" : "radio");
                FieldAttributes(field, cls);
                if (field.Value != null) WriteAttribute("value", field.Value);
                if (field.Checked) _html.Append(" checked");
                _html.Append('>');
                break;
            default:
                _html.Append("<select");
                FieldAttributes(field, cls);
                if (field.Kind == FormFieldKind.ListBox)
                {
                    WriteAttribute("size", field.Rows.ToString(CultureInfo.InvariantCulture));
                    if (field.MultiSelect) _html.Append(" multiple");
                }
                _html.Append('>');
                foreach (var (text, value, selected) in field.Options)
                {
                    _html.Append("<option");
                    if (value != null) WriteAttribute("value", value);
                    if (selected) _html.Append(" selected");
                    _html.Append('>');
                    WriteText(text);
                    _html.Append("</option>");
                }
                _html.Append("</select>");
                break;
        }
    }

    private void FieldAttributes(FormFieldElement field, string cls)
    {
        WriteAttribute("class", cls);
        WriteAttribute("name", field.Name);
        if (field.MaxLength > 0) WriteAttribute("maxlength", field.MaxLength.ToString(CultureInfo.InvariantCulture));
        if (field.Tooltip != null) WriteAttribute("title", field.Tooltip);
        if (field.ReadOnly) _html.Append(" readonly");
        if (field.Required) _html.Append(" required");
    }

    private void RenderLine(LineElement line, Box box)
    {
        bool absorbed = box.CanBecomeBlock;
        box = Absorbing(box);
        string side = line.Vertical ? "left" : "top";
        box.Declarations.Add(("border-" + side + "-width", CssValue.Pt(line.Thickness)));
        box.Declarations.Add(("border-" + side + "-style", line.Style));
        box.Declarations.Add(("border-" + side + "-color", CssValue.Color(line.Color)));
        if (line.Vertical && !absorbed) box.Declarations.Add(("height", "100%"));
        Open(box);
    }

    // Text

    private void RenderText(TextElement text, Box box, int? heading)
    {
        // A div inside a paragraph would end it, so text holding elements, or split into paragraphs, is a div.
        bool hasElements = text.Runs.Exists(r => r.Kind == RunKind.Element);
        bool paragraphs = text.ParagraphSpacing != null;
        string tag = heading is { } level ? "h" + level.ToString(CultureInfo.InvariantCulture) : hasElements || paragraphs ? "div" : "p";
        if (box.CanBecomeBlock) box.Tag = tag;
        else
        {
            Open(box);
            box = new Box { Tag = tag };
        }
        // A paragraph with a single plain span (container.Text("...")) carries the span's font itself, so its line
        // height follows that font, as in QuestPDF, instead of the strut of an inherited larger font.
        bool single = text.Runs.Count == 1 && text.Runs[0].Kind == RunKind.Text;
        var paragraph = single ? text.Style.Override(text.Runs[0].Style) : text.Style;
        CssValue.AddTextStyle(box.Declarations, paragraph, forBox: true);
        if (text.Alignment is { } alignment)
        {
            box.Set("text-align", alignment switch
            {
                TextAlignment.Left => "left",
                TextAlignment.Center => "center",
                TextAlignment.Right => "right",
                TextAlignment.Start => "start",
                TextAlignment.End => "end",
                _ => "justify",
            });
        }
        if (tag == "div") box.Set("white-space", "pre-wrap");
        if (text.FirstLineIndent is { } indent) box.Set("text-indent", CssValue.Pt(indent));
        if (text.MaxLines is { } lines)
        {
            box.Set("display", "-webkit-box");
            box.Set("-webkit-box-orient", "vertical");
            box.Set("-webkit-line-clamp", lines.ToString(CultureInfo.InvariantCulture));
            box.Set("overflow", "hidden");
        }
        Open(box);

        // With paragraph spacing each paragraph is an element of its own, opened when its first content comes, so a
        // line break that ends the text adds no empty paragraph.
        string paragraphTag = hasElements ? "div" : "p";
        string? spacingClass = text.ParagraphSpacing is { } spacing ? Class([("margin-top", CssValue.Pt(spacing))]) : null;
        int paragraphCount = 0;
        bool paragraphOpen = false, paragraphEmpty = true;
        void StartContent()
        {
            if (!paragraphs || paragraphOpen) return;
            _html.Append('<').Append(paragraphTag);
            if (paragraphCount++ > 0) WriteAttribute("class", spacingClass!);
            _html.Append('>');
            paragraphOpen = true;
            paragraphEmpty = true;
        }
        void EndParagraph()
        {
            if (!paragraphs) return;
            StartContent();
            // An empty paragraph keeps the height of a line, as an empty line does in QuestPDF.
            if (paragraphEmpty) _html.Append("<br>");
            _html.Append("</").Append(paragraphTag).Append('>');
            paragraphOpen = false;
        }

        foreach (var run in text.Runs)
        {
            var declarations = new List<(string, string)>();
            // The paragraph's background and position belong to its spans, since a box only inherits font properties.
            if (single) CssValue.AddSpanOnlyStyle(declarations, paragraph);
            else
            {
                var style = run.Style;
                if (text.Style.Background is { } background && style.Background == null) style = style.BackgroundColor(background);
                if (text.Style.Position is { } position && style.Position == null)
                    style = position == TextPosition.Superscript ? style.Superscript() : position == TextPosition.Subscript ? style.Subscript() : style.NormalPosition();
                CssValue.AddTextStyle(declarations, style, forBox: false);
            }
            // A footnote writes a class of its own from these declarations.
            string? cls = declarations.Count > 0 && run.Kind != RunKind.Footnote ? Class(declarations) : null;

            if (run.Kind == RunKind.Text && paragraphs)
            {
                var pieces = run.Text.Split('\n');
                for (int i = 0; i < pieces.Length; i++)
                {
                    if (i > 0) EndParagraph();
                    if (pieces[i].Length == 0) continue;
                    StartContent();
                    paragraphEmpty = false;
                    if (cls != null) OpenInline("span", cls, null);
                    WriteText(pieces[i]);
                    if (cls != null) _html.Append("</span>");
                }
                continue;
            }
            StartContent();
            paragraphEmpty = false;

            switch (run.Kind)
            {
                case RunKind.Element:
                {
                    var inline = Box.Slot("div");
                    inline.Declarations.Add(("display", "inline-block"));
                    inline.Declarations.Add(("vertical-align", run.VerticalAlign));
                    inline.Declarations.Add(("max-width", "100%"));
                    inline.Declarations.Add(("text-indent", "0"));
                    RenderContainer(run.Slot!, inline);
                    break;
                }
                case RunKind.SectionPageNumber:
                    _sectionPages = true;
                    _html.Append("<span");
                    if (cls != null) WriteAttribute("class", cls);
                    WriteAttribute("data-section-page", "#" + run.Target);
                    _html.Append("></span>");
                    break;
                case RunKind.Footnote:
                {
                    if (_band != Band.None) throw new InvalidOperationException("Footnote works in the page content, not in a header or footer.");
                    _footnotes = true;
                    // The note keeps the span's own style; the paragraph's comes with it from where it is called.
                    var note = new List<(string, string)>(declarations) { ("float", "footnote") };
                    if (run.Style.Size == null) note.Add(("font-size", "0.8em"));
                    OpenInline("span", Class(note), null);
                    WriteText(run.Text);
                    _html.Append("</span>");
                    break;
                }
                case RunKind.PageNumber:
                case RunKind.TotalPages:
                    if (_band == Band.None)
                        throw new InvalidOperationException("CurrentPageNumber and TotalPages work only in a page header or footer.");
                    OpenInline("span", cls, null);
                    _html.Append(run.Kind == RunKind.PageNumber ? "{page}" : "{total-pages}");
                    _html.Append("</span>");
                    break;
                case RunKind.Hyperlink:
                case RunKind.SectionLink:
                    OpenInline("a", cls, run.Kind == RunKind.Hyperlink ? run.Target : "#" + run.Target);
                    WriteText(run.Text);
                    _html.Append("</a>");
                    break;
                default:
                    if (cls == null) WriteText(run.Text);
                    else
                    {
                        OpenInline("span", cls, null);
                        WriteText(run.Text);
                        _html.Append("</span>");
                    }
                    break;
            }
        }
        if (paragraphOpen) _html.Append("</").Append(paragraphTag).Append('>');
    }

    private void OpenInline(string tag, string? cls, string? href)
    {
        _html.Append('<').Append(tag);
        if (cls != null) WriteAttribute("class", cls);
        if (href != null) WriteAttribute("href", href);
        _html.Append('>');
    }

    // Tables

    private void RenderTable(TableElement table, Box box)
    {
        int columns = table.Columns.Count;
        if (columns == 0) throw new InvalidOperationException("A table needs a ColumnsDefinition with at least one column.");
        if (box.MustEmit) Open(box);

        _html.Append("<table><colgroup>");
        float constant = 0, relative = 0;
        foreach (var (isRelative, size) in table.Columns)
        {
            if (isRelative) relative += size;
            else constant += size;
        }
        foreach (var (isRelative, size) in table.Columns)
        {
            string width = !isRelative ? CssValue.Pt(size)
                : constant == 0 ? CssValue.Number(size / relative * 100) + "%"
                : "calc((100% - " + CssValue.Pt(constant) + ") * " + CssValue.Number(size / relative) + ")";
            _html.Append("<col");
            WriteAttribute("class", Class([("width", width)]));
            _html.Append('>');
        }
        _html.Append("</colgroup>");

        if (table.Header.Count > 0)
        {
            _html.Append("<thead>");
            RenderRows(table.Header, columns, "th");
            _html.Append("</thead>");
        }
        _html.Append("<tbody>");
        RenderRows(table.Body, columns, "td");
        _html.Append("</tbody>");
        if (table.Footer.Count > 0)
        {
            _html.Append("<tfoot>");
            RenderRows(table.Footer, columns, "td");
            _html.Append("</tfoot>");
        }
        _html.Append("</table>");
    }

    private void RenderRows(List<TableCell> cells, int columns, string tag)
    {
        var placed = TableLayout.Place(cells, columns, out int rows, out var covered);
        for (int r = 0; r < rows; r++)
        {
            _html.Append("<tr>");
            for (int c = 0; c < columns; c++)
            {
                if (placed.TryGetValue((r, c), out var cell))
                {
                    var box = Box.Slot(tag);
                    box.IsCell = true;
                    box.SizeLocked = true;
                    box.InGrid = true;
                    if (tag == "th") box.Attributes.Add(("scope", "col"));
                    if (cell.ColumnSpan > 1) box.Attributes.Add(("colspan", cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)));
                    int rowSpan = Math.Min(cell.RowSpan, rows - r);
                    if (rowSpan > 1) box.Attributes.Add(("rowspan", rowSpan.ToString(CultureInfo.InvariantCulture)));
                    RenderContainer(cell, box);
                }
                else if (!covered(r, c))
                {
                    _html.Append('<').Append(tag).Append("></").Append(tag).Append('>');
                }
            }
            _html.Append("</tr>");
        }
    }

    // Elements

    private void Open(Box box)
    {
        if (_open.Count >= MaxDepth)
            throw new InvalidOperationException("The document nests more than 200 elements deep, more than PdfPecker lays out.");
        _html.Append('<').Append(box.Tag);
        if (box.Declarations.Count > 0) WriteAttribute("class", Class(box.Declarations));
        if (box.Id != null) WriteAttribute("id", box.Id);
        if (box.Direction != null) WriteAttribute("dir", box.Direction);
        if (box.Href != null) WriteAttribute("href", box.Href);
        foreach (var (name, value) in box.Attributes) WriteAttribute(name, value);
        _html.Append('>');
        _open.Push(box.Tag);
    }

    private void CloseTo(int depth)
    {
        while (_open.Count > depth) _html.Append("</").Append(_open.Pop()).Append('>');
    }

    /// <summary>The class for a set of declarations: each distinct set becomes one rule, shared by every element that has it.</summary>
    private string Class(List<(string Property, string Value)> declarations)
    {
        var key = new StringBuilder();
        foreach (var (property, value) in declarations)
        {
            if (key.Length > 0) key.Append(';');
            key.Append(property).Append(':').Append(value);
        }
        string text = key.ToString();
        if (!_classes.TryGetValue(text, out var name))
        {
            name = "c" + _classes.Count.ToString(CultureInfo.InvariantCulture);
            _classes.Add(text, name);
            _rules.Append('.').Append(name).Append('{').Append(text).Append('}');
        }
        return name;
    }

    // Escaping

    private void WriteAttribute(string name, string value)
    {
        _html.Append(' ').Append(name).Append("=\"");
        foreach (char c in Clean(value))
        {
            switch (c)
            {
                case '&': _html.Append("&amp;"); break;
                case '<': _html.Append("&lt;"); break;
                case '>': _html.Append("&gt;"); break;
                case '"': _html.Append("&quot;"); break;
                case '\'': _html.Append("&#39;"); break;
                case '\n': _html.Append("&#10;"); break;
                case '\t': _html.Append("&#9;"); break;
                default: _html.Append(c); break;
            }
        }
        _html.Append('"');
    }

    /// <summary>
    /// Writes caller text. In a header or footer, a brace is followed by an empty span, so text that happens to read
    /// "{page}" stays literal: PdfPecker only replaces page numbers within one text node.
    /// </summary>
    private void WriteText(string text)
    {
        foreach (char c in Clean(text))
        {
            switch (c)
            {
                case '&': _html.Append("&amp;"); break;
                case '<': _html.Append("&lt;"); break;
                case '>': _html.Append("&gt;"); break;
                case '{' when _band != Band.None: _html.Append("{<span></span>"); break;
                default: _html.Append(c); break;
            }
        }
    }

    /// <summary>
    /// Text with line breaks normalised to \n, control characters other than tab and line break removed, and lone
    /// surrogates replaced, so no input can produce characters an HTML parser or PDF string treats specially.
    /// </summary>
    internal static string Clean(string text)
    {
        bool clean = true;
        for (int i = 0; i < text.Length && clean; i++)
        {
            char c = text[i];
            if ((c < 0x20 && c != '\n' && c != '\t') || (c >= 0x7F && c <= 0x9F) || char.IsSurrogate(c) || c is '￾' or '￿') clean = false;
        }
        if (clean) return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c)) sb.Append('�');
            else if ((c < 0x20 && c != '\n' && c != '\t') || (c >= 0x7F && c <= 0x9F) || c is '￾' or '￿') continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// An element being collected from a chain of decorators. Declarations merge into it while the CSS box model allows:
    /// sizes, then paint, then padding. A decorator that would need to sit outside what is already there starts a
    /// nested box instead.
    /// </summary>
    private enum Band { None, Running, First }

    private sealed class Box
    {
        private const string ScaleToFitProperty = "-pdfpecker-scale-to-fit";

        public string Tag { get; set; } = "div";

        /// <summary>Written even without content or styles: page slots, row items, table cells.</summary>
        public bool Required { get; private init; }

        public bool IsCell { get; set; }

        /// <summary>
        /// The element's size belongs to its parent's layout (a row item's flex width, a table cell's column), so a
        /// Width or Height inside it gets an element of its own.
        /// </summary>
        public bool SizeLocked { get; set; }

        /// <summary>A cell of a fluent Table, whose content fills the row's height as in QuestPDF.</summary>
        public bool InGrid { get; set; }

        /// <summary>A cell's horizontal alignment, which its non-text content gets from a flex wrapper inside it.</summary>
        private HorizontalAlignment? _cellAlignment;
        public List<(string Property, string Value)> Declarations { get; } = [];
        public List<(string Name, string Value)> Attributes { get; } = [];
        public string? Id { get; private set; }
        public string? Direction { get; private set; }
        public string? Href { get; private set; }

        private int _rank = Rank.Any;
        private bool _aligned;
        private bool _alignedHorizontally, _alignedVertically;

        public static Box Slot(string tag, params (string, string)[] attributes)
        {
            var box = new Box { Tag = tag, Required = true };
            box.Attributes.AddRange(attributes);
            return box;
        }

        public bool MustEmit => Required || Declarations.Count > 0 || Id != null || Direction != null || Href != null
            || Tag != "div" || Attributes.Count > 0;

        /// <summary>Whether block content (a paragraph, column, row, line) can be this element instead of a child of it.</summary>
        public bool CanBecomeBlock => Tag is "div" or "a" && !_aligned && !Has("display") && (!Required || Tag == "div");

        /// <summary>
        /// Whether a column, row or other layout can be this semantic element, so that a list's items stay its
        /// children. Text cannot, because it would make the element a paragraph.
        /// </summary>
        public bool CanBecomeLayout
            => Tag is "section" or "article" or "nav" or "ul" or "li" or "blockquote" or "figcaption" && !_aligned && !Has("display");

        public bool Has(string property)
        {
            foreach (var (p, _) in Declarations)
                if (p == property) return true;
            return false;
        }

        public void Set(string property, string value)
        {
            for (int i = 0; i < Declarations.Count; i++)
            {
                if (Declarations[i].Property == property)
                {
                    Declarations[i] = (property, value);
                    return;
                }
            }
            Declarations.Add((property, value));
        }

        public bool TryApply(Decorator decorator)
        {
            switch (decorator.Kind)
            {
                case DecoratorKind.Style:
                    return TryAdd(decorator.Rank, decorator.Declarations);
                case DecoratorKind.Border:
                    return TryAdd(Rank.Paint, BorderDeclarations(decorator));
                case DecoratorKind.Align:
                    return TryAlign(decorator);
                case DecoratorKind.Hyperlink:
                    if (Href != null || IsCell || Tag is not ("div" or "a")) return false;
                    Tag = "a";
                    Href = decorator.Text;
                    if (!Has("display")) Declarations.Add(("display", "block"));
                    return true;
                case DecoratorKind.Section:
                    if (Id != null) return false;
                    Id = decorator.Text;
                    return true;
                case DecoratorKind.Semantic:
                    if (Tag != "div" || Href != null || IsCell || _aligned || Declarations.Count > 0) return false;
                    Tag = decorator.Text!;
                    // The user agent's margins, padding and markers of these elements would add space the fluent
                    // calls did not ask for.
                    if (Tag is "ul" or "li") Declarations.Add(("list-style-type", "none"));
                    if (Tag is "ul" or "blockquote" or "figcaption")
                    {
                        Declarations.Add(("margin", "0"));
                        Declarations.Add(("padding", "0"));
                    }
                    return true;
                case DecoratorKind.Artifact:
                    Attributes.Add(("aria-hidden", "true"));
                    return true;
                case DecoratorKind.Direction:
                    if (Direction != null) return false;
                    Direction = decorator.Text;
                    return true;
                case DecoratorKind.ScaleToFit:
                    // PdfPecker scales a box's content into the box's height or max-height; without one, the box takes
                    // the height of the slot around it as its largest.
                    return Has("height") || Has("max-height")
                        ? TryAdd(Rank.Size, [(ScaleToFitProperty, "shrink")])
                        : TryAdd(Rank.Size, [(ScaleToFitProperty, "shrink"), ("max-height", "100%")]);
                case DecoratorKind.TextStyle:
                    // Inherited properties: an inner style simply wins over an outer one on the same element.
                    var declarations = new List<(string, string)>();
                    CssValue.AddTextStyle(declarations, decorator.Style!, forBox: true);
                    foreach (var (property, value) in declarations) Set(property, value);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether the box inside this table cell must fill it: it paints a background, border or shadow, which QuestPDF
        /// draws over the whole height of the row. Aligned content keeps its own size.
        /// </summary>
        public bool FillsWith(Decorator decorator)
            => InGrid && _cellAlignment == null && !Has("vertical-align")
               && (decorator.Kind == DecoratorKind.Border || decorator.Kind == DecoratorKind.Style && decorator.Rank == Rank.Paint);

        /// <summary>The box inside this one: plain, or aligned when this is a cell with a horizontal alignment.</summary>
        public Box Child()
        {
            var child = new Box();
            if (_cellAlignment is { } alignment) child.TryAlign(new Decorator(DecoratorKind.Align) { Horizontal = alignment });
            return child;
        }

        private bool TryAdd(int rank, (string Property, string Value)[] declarations)
        {
            if (rank != Rank.Any && (_aligned || rank < _rank || (rank == Rank.Size && SizeLocked) || _cellAlignment != null)) return false;
            foreach (var (property, _) in declarations)
                if (Has(property)) return false;
            Declarations.AddRange(declarations);
            if (rank != Rank.Any) _rank = Math.Max(_rank, rank);
            return true;
        }

        private static (string, string)[] BorderDeclarations(Decorator border)
        {
            var list = new List<(string, string)>();
            Add("top", border.Top);
            Add("right", border.Right);
            Add("bottom", border.Bottom);
            Add("left", border.Left);
            return [.. list];

            void Add(string side, float width)
            {
                if (float.IsNaN(width)) return;
                list.Add(("border-" + side + "-width", CssValue.Pt(width)));
                list.Add(("border-" + side + "-style", "solid"));
                list.Add(("border-" + side + "-color", CssValue.Color(border.BorderColor)));
            }
        }

        private bool TryAlign(Decorator align)
        {
            if (IsCell)
            {
                // A cell cannot be a flex container, so it aligns with the table properties instead.
                if (align.Vertical is { } v)
                {
                    if (Has("vertical-align")) return false;
                    Declarations.Add(("vertical-align", v switch { VerticalAlignment.Top => "top", VerticalAlignment.Middle => "middle", _ => "bottom" }));
                }
                if (align.Horizontal is { } h)
                {
                    if (Has("text-align")) return false;
                    Declarations.Add(("text-align", HorizontalText(h)));
                    _cellAlignment = h;
                }
                return true;
            }

            if (Tag is not ("div" or "a")) return false;
            if (align.Horizontal != null && _alignedHorizontally || align.Vertical != null && _alignedVertically) return false;
            if (!_aligned)
            {
                if (Has("display")) return false;
                Declarations.Add(("display", "flex"));
                Declarations.Add(("flex-direction", "column"));
                _aligned = true;
            }
            if (align.Horizontal is { } horizontal)
            {
                Declarations.Add(("align-items", horizontal switch { HorizontalAlignment.Left => "flex-start", HorizontalAlignment.Center => "center", _ => "flex-end" }));
                Declarations.Add(("text-align", HorizontalText(horizontal)));
                _alignedHorizontally = true;
            }
            if (align.Vertical is { } vertical)
            {
                Declarations.Add(("justify-content", vertical switch { VerticalAlignment.Top => "flex-start", VerticalAlignment.Middle => "center", _ => "flex-end" }));
                _alignedVertically = true;
            }
            return true;
        }

        private static string HorizontalText(HorizontalAlignment alignment)
            => alignment switch { HorizontalAlignment.Left => "left", HorizontalAlignment.Center => "center", _ => "right" };
    }
}
