using System.Security.Cryptography;
using System.Text;

namespace PdfPecker.Fuzz;

/// <summary>
/// The findings folder of one target: a minimised input and a .txt with the reason and stack trace per distinct
/// signature. Signatures seen in earlier runs are loaded from the .txt files so a restart does not report them again.
/// </summary>
internal sealed class FindingStore
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public string Directory { get; }

    public FindingStore(string outRoot, string targetName)
    {
        Directory = Path.Combine(outRoot, targetName);
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var txt in System.IO.Directory.EnumerateFiles(Directory, "*.txt"))
        {
            var first = File.ReadLines(txt).FirstOrDefault(l => l.StartsWith("signature: ", StringComparison.Ordinal));
            if (first != null) _seen[first["signature: ".Length..]] = 1;
        }
    }

    public string CurrentInputPath => Path.Combine(Directory, ".current");

    /// <summary>How many seeds the workers of this run have started, so a restarted worker can skip them.</summary>
    public int SeedsDone
    {
        get => File.Exists(SeedsPath) && int.TryParse(File.ReadAllText(SeedsPath), out var n) ? n : 0;
        set { if (value > 0) File.WriteAllText(SeedsPath, value.ToString()); else File.Delete(SeedsPath); }
    }

    private string SeedsPath => Path.Combine(Directory, ".seeds-done");

    public int Count => System.IO.Directory.EnumerateFiles(Directory, "*.txt").Count();

    /// <summary>Counts a repeat of a known signature; false when the signature is new.</summary>
    public bool Repeat(string signature)
    {
        if (!_seen.ContainsKey(signature)) return false;
        _seen[signature]++;
        return true;
    }

    public string Save(Target target, Verdict verdict, byte[] input, long originalSize, long seed, long executions)
    {
        _seen[verdict.Signature] = 1;
        var id = verdict.Kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verdict.Signature)))[..8].ToLowerInvariant();
        var inputName = id + target.Extension;
        File.WriteAllBytes(Path.Combine(Directory, inputName), input);
        var sb = new StringBuilder();
        sb.Append("signature: ").Append(verdict.Signature).Append('\n');
        sb.Append("target: ").Append(target.Name).Append('\n');
        sb.Append("kind: ").Append(verdict.Kind).Append('\n');
        sb.Append("input: ").Append(inputName).Append(" (").Append(input.Length).Append(" bytes");
        if (originalSize != input.Length) sb.Append(", minimised from ").Append(originalSize).Append(" bytes");
        sb.Append(")\n");
        sb.Append("seed: ").Append(seed).Append(", found after ").Append(executions).Append(" executions\n\n");
        sb.Append(verdict.Detail).Append('\n');
        File.WriteAllText(Path.Combine(Directory, id + ".txt"), sb.ToString());
        return inputName;
    }
}
