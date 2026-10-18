// Regenerates the hyphenation pattern resources in src/PdfPecker/Text/Hyphenation from the hyph-utf8 TeX files.
//
// Usage (from the repository root, .NET 10 SDK):
//   dotnet run tools/Hyphenation/build.cs -- <folder with the .tex files> src/PdfPecker/Text/Hyphenation
//
// The folder needs these files from https://github.com/hyphenation/tex-hyphen (hyph-utf8/tex/generic/hyph-utf8/patterns/tex):
//   hyph-en-us.tex, hyph-da.tex, hyph-de-1996.tex, hyph-no.tex, hyph-nl.tex, hyph-es.tex,
//   hyph-fr.tex, hyph-it.tex, hyph-pt.tex, hyph-pl.tex, hyph-sv.tex
// The checked-in resources were built from commit 5684c0f51c0b81133db2efbe60a408b4155a3ff5 (2026-02-24). The tool
// checks each file against the hashes below, so a newer pattern file cannot change the output unnoticed; when moving to
// a newer commit on purpose, update the hashes and the commit here in the same change.
// Each output file is Brotli-compressed UTF-8 text: a line "<lefthyphenmin> <righthyphenmin>", the patterns one per
// line, a line "--", then the exceptions one per line (lower case, with hyphens at their break points).
// Swedish is only available under the LaTeX Project Public License, which allows distributing an unmodified copy but
// attaches conditions to modified ones, so hyph-sv.tex is kept byte for byte as "sv.tex.br" (Brotli only).
using System.IO.Compression;
using System.Security.Cryptography;
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
var pinned = new Dictionary<string, string>
{
    ["hyph-en-us.tex"] = "f4ffcd96c5cbc886bdad23f95dcae8edc3cd3620eae62f7946eceda97c4e68f8",
    ["hyph-da.tex"] = "23e0a50f0c20d632be06f973e700323463961eac737bb14f6c3787675a4f5575",
    ["hyph-de-1996.tex"] = "374ad1ce3263f8a2791ec070ef9b516c532dae64456f3fb656e617e8ae53a9d3",
    ["hyph-no.tex"] = "05a2ac5fbf88bdc78c580e5cb8a158e6aa760d13756604f9523aa99feec3ec65",
    ["hyph-nl.tex"] = "d21499bfbee53e4d50e867ef92a41fd28b65725d7c91c980e4f8f02889bf6b2a",
    ["hyph-es.tex"] = "6a2e5f39a991a23d1cd8a23dbd0f6fe96a9f07561a6695f4f3320f47030e236c",
    ["hyph-fr.tex"] = "526cad6fe8f52fcd3caa3fcaf667cb1740cfc14bca198680e00dc343a84594de",
    ["hyph-it.tex"] = "6ce56ed6ed2ca7c0688838e366e6595e0429f984f4fc6e96ec045cebb1e76a47",
    ["hyph-pt.tex"] = "c110a39e501db372d1d755e1c64464321a2efa7d8e9617f66001000389b941f2",
    ["hyph-pl.tex"] = "83ddbbd21bc06a44bc4f4a4c49c38782e809cff5f026b764c044daa29e818843",
    ["hyph-sv.tex"] = "102253382bd91cdb7535de5c20744254c9ac9aaa4ca12ed420eb835e5004c0b3",
};
string src = args[0], dst = args[1];
foreach (var (file, expected) in pinned)
{
    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(src, file)))).ToLowerInvariant();
    if (actual != expected)
    {
        Console.Error.WriteLine($"{file} is not the pinned version (SHA-256 {actual}); see the comment at the top of build.cs.");
        return 1;
    }
}
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
return 0;

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
