namespace PdfPecker.Css;

/// <summary>Default browser styles, close to the HTML specification's rendering section.</summary>
internal static class UserAgentStyles
{
    public const string Css = @"
html, address, blockquote, body, center, dialog, div, figure, figcaption, footer, form, header, hr, legend,
main, nav, p, pre, section, article, aside, hgroup, details, summary, fieldset, dl, dt, dd, menu, ol, ul,
h1, h2, h3, h4, h5, h6, listing, plaintext, xmp, search { display: block; }
head, script, style, title, meta, link, base, template, noscript, datalist, param, area, map, [hidden] { display: none; }
li { display: list-item; }
table { display: table; border-collapse: separate; border-spacing: 2px; box-sizing: border-box; }
caption { display: table-caption; text-align: center; }
thead { display: table-header-group; vertical-align: middle; }
tbody { display: table-row-group; vertical-align: middle; }
tfoot { display: table-footer-group; vertical-align: middle; }
tr { display: table-row; vertical-align: inherit; }
td, th { display: table-cell; padding: 1px; vertical-align: inherit; }
th { font-weight: bold; text-align: center; }
col { display: table-column; }
colgroup { display: table-column-group; }

body { margin: 8px; }
p, dl { margin: 1em 0; }
blockquote, figure { margin: 1em 40px; }
dd { margin-left: 40px; }
address { font-style: italic; }
center { text-align: center; }
h1 { font-size: 2em; margin: 0.67em 0; font-weight: bold; }
h2 { font-size: 1.5em; margin: 0.83em 0; font-weight: bold; }
h3 { font-size: 1.17em; margin: 1em 0; font-weight: bold; }
h4 { font-size: 1em; margin: 1.33em 0; font-weight: bold; }
h5 { font-size: 0.83em; margin: 1.67em 0; font-weight: bold; }
h6 { font-size: 0.67em; margin: 2.33em 0; font-weight: bold; }
h1, h2, h3, h4, h5, h6 { break-after: avoid; } /* keep a heading on the page of the text it introduces */
ul, ol, menu { margin: 1em 0; padding-left: 40px; }
ul { list-style-type: disc; }
ol { list-style-type: decimal; }
ul ul, ol ul { list-style-type: circle; margin-top: 0; margin-bottom: 0; }
ol ol, ul ol { margin-top: 0; margin-bottom: 0; }
ul ul ul, ol ul ul, ul ol ul, ol ol ul { list-style-type: square; }
pre, listing, xmp, plaintext { font-family: monospace; white-space: pre; margin: 1em 0; }
code, kbd, samp, tt, var { font-family: monospace; }
var { font-style: italic; }
hr { border: 1px inset gray; margin: 0.5em 0; color: gray; }
b, strong { font-weight: bold; }
i, em, cite, dfn { font-style: italic; }
u, ins { text-decoration: underline; }
s, strike, del { text-decoration: line-through; }
a:link { color: #0000EE; text-decoration: underline; }
small { font-size: smaller; }
big { font-size: larger; }
sub { vertical-align: sub; font-size: smaller; }
sup { vertical-align: super; font-size: smaller; }
mark { background-color: yellow; color: black; }
nobr { white-space: nowrap; }
img { display: inline; }
fieldset { margin: 0 2px; padding: 0.35em 0.75em 0.625em; border: 2px groove #c0c0c0; }
legend { padding: 0 2px; }
/* Form controls, drawn as Chrome prints them; their values are laid out in pdf-field-value children. */
input, select, button, textarea {
  display: inline-block; font-family: sans-serif; font-size: 13.333px; font-weight: normal; font-style: normal;
  color: black; line-height: normal; letter-spacing: normal; word-spacing: normal; text-transform: none;
  text-indent: 0; text-align: left; text-shadow: none; white-space: normal;
}
input, select, textarea { background-color: white; border: 1px solid #767676; }
input { padding: 2px 3px; border-radius: 2px; width: 13.275em; }
input[type=hidden], input[type=image], input[type=file], input[type=range], input[type=color] { display: none; }
input[type=checkbox], input[type=radio] { width: 13px; height: 13px; padding: 0; box-sizing: border-box; }
input[type=checkbox] { margin: 3px 3px 3px 4px; border-radius: 2px; }
input[type=radio] { margin: 3px 3px 0 5px; border-radius: 50%; }
input[type=submit], input[type=reset], input[type=button], button {
  width: auto; padding: 2px 7px; border: 1px solid #767676; border-radius: 3px; background-color: #efefef; text-align: center; box-sizing: border-box;
}
textarea { font-family: monospace; padding: 2px; width: 13.275em; height: 2.26em; border-radius: 2px; }
select { padding: 1px 20px 1px 4px; border-radius: 2px; box-sizing: border-box; }
input[disabled], textarea[disabled], select[disabled], button[disabled] { color: #6d6d6d; border-color: #c3c3c3; }
pdf-field-value { display: block; white-space: pre; }
pdf-field-value[placeholder] { color: #757575; }
pdf-field-value[sizer] { visibility: hidden; height: 0; }
pdf-field-value[option] { padding: 1px 2px; }
pdf-field-value[selected] { background-color: #cecece; }
textarea pdf-field-value { white-space: pre-wrap; overflow-wrap: break-word; }
option, optgroup { display: none; }
q::before { content: open-quote; }
q::after { content: close-quote; }
";
}
