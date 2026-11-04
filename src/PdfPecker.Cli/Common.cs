using System.Globalization;

namespace PdfPecker.Cli;

/// <summary>What the PDF commands share: reading a PDF, reading a password and writing the result.</summary>
internal static class Common
{
    public static OptionSet WithPassword(OptionSet set)
        => set.Value("password").Value("password-env").Flag("quiet|q").Value("output|o");

    public static byte[] ReadPdf(CliIo io, string path)
    {
        if (path == "-") return io.ReadAllInput();
        return File.ReadAllBytes(path);
    }

    /// <summary>The password given with --password, or read from the environment variable --password-env names.</summary>
    public static string? Password(ParsedArgs a, string passwordOption = "password", string envOption = "password-env", string? defaultEnv = null)
    {
        var password = a.Get(passwordOption);
        if (password != null) return password;
        var env = a.Get(envOption) ?? defaultEnv;
        if (env == null) return null;
        var value = Environment.GetEnvironmentVariable(env);
        if (value == null && a.Get(envOption) != null)
            throw new UsageException($"The environment variable {env} named by --{envOption} is not set.");
        return value;
    }

    /// <summary>The positional arguments, checked against what the command takes.</summary>
    public static List<string> Positionals(ParsedArgs a, int min, int max, string usage)
    {
        if (a.Positionals.Count < min || a.Positionals.Count > max) throw new UsageException($"Usage: pdfpecker {usage}");
        return a.Positionals;
    }

    /// <summary>The output path: the last positional argument, or --output. The input must not be the output.</summary>
    public static string Output(ParsedArgs a, List<string> positionals, int inputs, string usage, string? input)
    {
        string? output = a.Get("output");
        if (output == null)
        {
            if (positionals.Count <= inputs) throw new UsageException($"Usage: pdfpecker {usage}");
            output = positionals[inputs];
        }

        if (input != null && input != "-" && output != "-"
            && string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
            throw new UsageException($"The output '{output}' is also the input; choose another output file.");
        return output;
    }

    public static void Report(CliIo io, string output, byte[] pdf, string verb = "Wrote")
    {
        if (output == "-") return;
        io.Info($"{verb} {output} ({FormatSize(pdf.Length)})");
    }

    public static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";

    public static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
