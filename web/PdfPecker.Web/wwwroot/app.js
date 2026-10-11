const examples = {
  invoice: `<!DOCTYPE html>
<html>
<head>
<title>Invoice 2026-0042</title>
<style>
  body { font-family: Helvetica, Arial, sans-serif; font-size: 10pt; color: #222; margin: 0; }
  .brand { font-size: 20pt; font-weight: bold; color: #2b6cb0; }
  .header { border-bottom: 3px solid #2b6cb0; padding-bottom: 8px; margin-bottom: 16px; }
  .muted { color: #718096; }
  table.items { width: 100%; border-collapse: collapse; margin-top: 16px; }
  table.items th { background: #2b6cb0; color: white; text-align: left; padding: 6px 8px; }
  table.items td { padding: 6px 8px; border-bottom: 1px solid #e2e8f0; }
  table.items tr:nth-child(even) td { background-color: #f7fafc; }
  .num { text-align: right; }
  .total td { font-weight: bold; border-top: 2px solid #2b6cb0; }
  .note { margin-top: 24px; padding: 10px 12px; background: #fffbea; border-left: 4px solid #d69e2e; }
</style>
</head>
<body>
  <div class="header"><span class="brand">Acme Supplies</span></div>
  <h1 style="font-size:16pt;margin:0">Invoice #2026-0042</h1>
  <div class="muted">Issued 10 October 2026 &middot; Due 24 October 2026</div>
  <table class="items">
    <thead><tr><th>Description</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Amount</th></tr></thead>
    <tbody>
      <tr><td>Widgets, large</td><td class="num">4</td><td class="num">&euro;25.00</td><td class="num">&euro;100.00</td></tr>
      <tr><td>Widgets, small</td><td class="num">10</td><td class="num">&euro;7.50</td><td class="num">&euro;75.00</td></tr>
      <tr><td>Installation</td><td class="num">1</td><td class="num">&euro;120.00</td><td class="num">&euro;120.00</td></tr>
      <tr class="total"><td colspan="3">Total</td><td class="num">&euro;295.00</td></tr>
    </tbody>
  </table>
  <div class="note"><strong>Thank you!</strong> Please pay within 14 days.</div>
</body>
</html>`,
  article: `<!DOCTYPE html>
<html>
<head>
<title>A short article</title>
<style>
  body { font-family: Georgia, serif; font-size: 11pt; line-height: 1.5; }
  h1 { font-family: Helvetica, sans-serif; color: #2c5282; }
  blockquote { border-left: 4px solid #a0aec0; margin-left: 0; padding-left: 12px; color: #4a5568; font-style: italic; }
  p { text-align: justify; }
</style>
</head>
<body>
  <h1>Why plain HTML makes great PDFs</h1>
  <p>HTML and CSS are a comfortable way to describe documents: headings, paragraphs, <b>bold</b>, <i>italic</i>,
  <a href="https://example.com">links</a>, lists and tables all come for free.</p>
  <blockquote>Write the document once, render it anywhere.</blockquote>
  <ul><li>Invoices and receipts</li><li>Reports and statements</li><li>Tickets and labels</li></ul>
  <ol><li>Write HTML</li><li>Convert</li><li>Done</li></ol>
  <h2 style="page-break-before: always">Second page</h2>
  <p>This heading uses <code>page-break-before: always</code>.</p>
</body>
</html>`,
  readme: `# Project Falcon

Falcon turns **Markdown** into tidy PDFs: write plain text, get a document you can send.

## Features

- Headings, *emphasis*, **bold**, ~~strikethrough~~ and \`inline code\`
- [Links](https://example.com) and bare URLs like https://example.com
- Tables, task lists, block quotes and code blocks

> Write the document once, render it anywhere.

## Status

| Milestone      | Owner | Done |
|:---------------|:-----:|-----:|
| Parser         | Ana   |  yes |
| PDF output     | Ben   |  yes |
| Footnotes      | Cleo  |   no |

### Next steps

- [x] Ship the first version
- [ ] Collect feedback

\`\`\`csharp
var pdf = MarkdownToPdf.Convert(File.ReadAllText("README.md"));
\`\`\`
`,
  blank: `<!DOCTYPE html>
<html>
<body>
  <h1>Hello</h1>
  <p>Start typing HTML here.</p>
</body>
</html>`,
};

const $ = id => document.getElementById(id);
const html = $("html"), statusEl = $("status"), convertBtn = $("convert");
let currentUrl = null, fileName = "document";
const isMarkdown = () => $("inputFormat").value === "markdown";

function setInputFormat(format) {
  $("inputFormat").value = format;
  const md = format === "markdown";
  $("inputLabel").textContent = md ? "Markdown" : "HTML";
  // HTML output only makes sense for Markdown input.
  $("output").querySelector("option[value=html]").disabled = !md;
  if (!md) $("output").value = "pdf";
  updateOutputLabel();
}

function updateOutputLabel() {
  const pdf = $("output").value === "pdf";
  $("outputLabel").textContent = pdf ? "PDF" : "HTML";
  $("empty").textContent = pdf ? "Your PDF will appear here." : "Your HTML will appear here.";
}

function setStatus(text, isError) {
  statusEl.textContent = text;
  statusEl.classList.toggle("error", !!isError);
}

function loadExample(name) {
  html.value = examples[name];
  fileName = name === "blank" ? "document" : name;
  setInputFormat(name === "readme" ? "markdown" : "html");
}

async function convert() {
  convertBtn.disabled = true;
  setStatus("Converting…");
  const started = performance.now();
  try {
    const output = $("output").value;
    const res = await fetch("api/convert", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        [isMarkdown() ? "markdown" : "html"]: html.value,
        output,
        pageSize: $("pageSize").value,
        landscape: $("landscape").checked,
        marginMm: parseFloat($("margin").value) || 0,
        pageNumbers: $("pageNumbers").checked,
        watermark: $("watermark").value,
        password: $("password").value,
        pdfa: $("pdfa").checked,
        fileName,
      }),
    });
    if (!res.ok) {
      let message = res.status === 429 ? "Too many requests, wait a moment." : "Conversion failed (" + res.status + ").";
      try { const p = await res.json(); if (p.detail) message = p.detail; } catch {}
      throw new Error(message);
    }
    const blob = await res.blob();
    const serverMs = parseFloat(res.headers.get("X-Conversion-Ms"));
    if (currentUrl) URL.revokeObjectURL(currentUrl);
    currentUrl = URL.createObjectURL(blob);
    const preview = $("preview");
    // HTML previews are sandboxed: any script in the document stays inert.
    if (output === "html") preview.setAttribute("sandbox", "");
    else preview.removeAttribute("sandbox");
    preview.style.background = output === "html" ? "#fff" : "";
    preview.src = currentUrl;
    preview.hidden = false;
    $("empty").hidden = true;
    const dl = $("download");
    dl.href = currentUrl;
    dl.download = fileName + "." + output;
    dl.hidden = false;
    const kb = (blob.size / 1024).toFixed(1);
    setStatus(isNaN(serverMs)
      ? `${kb} KB`
      : `${kb} KB · converted in ${serverMs.toFixed(1)} ms (round trip ${Math.round(performance.now() - started)} ms)`);
  } catch (e) {
    setStatus(e.message, true);
  } finally {
    convertBtn.disabled = false;
  }
}

$("example").addEventListener("change", e => { loadExample(e.target.value); convert(); });
convertBtn.addEventListener("click", convert);
html.addEventListener("keydown", e => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); convert(); }
  if (e.key === "Tab") {
    e.preventDefault();
    const s = html.selectionStart;
    html.setRangeText("  ", s, html.selectionEnd, "end");
  }
});
async function openFile(f) {
  if (!f) return;
  html.value = await f.text();
  setInputFormat(/\.(md|markdown|mdown|mkdn?)$/i.test(f.name) ? "markdown" : "html");
  fileName = f.name.replace(/\.[^.]+$/, "") || "document";
  convert();
}
$("file").addEventListener("change", async e => { await openFile(e.target.files[0]); e.target.value = ""; });
// Drop an .html or .md file anywhere on the page.
document.addEventListener("dragover", e => { e.preventDefault(); document.body.classList.add("dragging"); });
document.addEventListener("dragleave", e => { if (!e.relatedTarget) document.body.classList.remove("dragging"); });
document.addEventListener("drop", e => {
  e.preventDefault();
  document.body.classList.remove("dragging");
  openFile(e.dataTransfer.files[0]);
});
for (const id of ["pageSize", "landscape", "pageNumbers", "watermark", "password", "pdfa"]) $(id).addEventListener("change", convert);
$("inputFormat").addEventListener("change", e => { setInputFormat(e.target.value); convert(); });
$("output").addEventListener("change", () => { updateOutputLabel(); convert(); });
$("margin").addEventListener("change", convert);

loadExample("invoice");
convert();
