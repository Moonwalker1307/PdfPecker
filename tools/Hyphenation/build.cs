// Regenerates the hyphenation pattern resources in src/PdfPecker/Text/Hyphenation from the hyph-utf8 TeX files.
//
// Usage (from the repository root, .NET 10 SDK):
//   dotnet run tools/Hyphenation/build.cs -- <folder with the .tex files> src/PdfPecker/Text/Hyphenation
//
// The folder needs these files from https://github.com/hyphenation/tex-hyphen (hyph-utf8/tex/generic/hyph-utf8/patterns/tex):
//   hyph-en-us.tex, hyph-da.tex, hyph-de-1996.tex, hyph-no.tex, hyph-nl.tex, hyph-es.tex,
//   hyph-fr.tex, hyph-it.tex, hyph-pt.tex, hyph-pl.tex, hyph-sv.tex
// Each output file is Brotli-compressed UTF-8 text: a line "<lefthyphenmin> <righthyphenmin>", the patterns one per
// line, a line "--", then the exceptions one per line (lower case, with hyphens at their break points).
// Swedish is only available under the LaTeX Project Public License, which allows distributing an unmodified copy but
// attaches conditions to modified ones, so hyph-sv.tex is kept byte for byte as "sv.tex.br" (Brotli only).
using System.IO.Compression;
using System.Text;

var sets = new (string Name, string Tex, int Left, int Right)[]
{
    ("en-us", "hyph-en-us.tex", 2, 3),
    ("da", "hyph-da.tex", 2, 2),
    ("de", "hyph-de-1996.tex", 2, 2),
    ("no", "hyph-no.tex", 2, 2),
    ("nl", "hyph-nl.tex", 2, 2),
    ("es", "hyph-es.tex", 2, 2),
    ("fr", "hyph-fr.tex", 2, 2),
    ("it", "hyph-it.tex", 2, 2),
    ("pt", "hyph-pt.tex", 2, 3),
    ("pl", "hyph-pl.tex", 2, 2),
};
string src = args[0], dst = args[1];
Directory.CreateDirectory(dst);
Compress(File.ReadAllBytes(Path.Combine(src, "hyph-sv.tex")), Path.Combine(dst, "sv.tex.br"), "sv");
foreach (var (name, tex, left, right) in sets)
{
    var text = new StringBuilder();
    foreach (var line in File.ReadAllLines(Path.Combine(src, tex), Encoding.UTF8))
    {
        int pct = line.IndexOf('%');
        text.Append(pct >= 0 ? line[..pct] : line).Append('\n');
    }
    string all = text.ToString();
    var outText = new StringBuilder($"{left} {right}\n");
    foreach (var p in Block(all, "\\patterns{")) outText.Append(p).Append('\n');
    outText.Append("--\n");
    foreach (var h in Block(all, "\\hyphenation{")) outText.Append(h.ToLowerInvariant()).Append('\n');
    Compress(Encoding.UTF8.GetBytes(outText.ToString()), Path.Combine(dst, name + ".hyph.br"), name);
}

static void Compress(byte[] bytes, string path, string name)
{
    using (var fs = File.Create(path))
    using (var br = new BrotliStream(fs, CompressionLevel.SmallestSize))
        br.Write(bytes);
    Console.WriteLine($"{name}: {bytes.Length} bytes -> {new FileInfo(path).Length} bytes");
}

static IEnumerable<string> Block(string text, string start)
{
    int i = text.IndexOf(start, StringComparison.Ordinal);
    if (i < 0) yield break;
    int end = text.IndexOf('}', i + start.Length);
    foreach (var token in text[(i + start.Length)..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        yield return token;
}
