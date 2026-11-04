using System.Reflection;

namespace PdfPecker.Cli;

/// <summary>Finds the command a command line names and runs it, turning failures into exit codes.</summary>
internal static class CliApp
{
    private static readonly string[] CommandNames =
    {
        "convert", "watch", "merge", "split", "extract", "remove", "rotate", "stamp", "compact", "linearize", "sanitize",
        "sign", "verify", "encrypt", "decrypt", "to-image", "to-html", "text", "info",
    };

    public static async Task<int> RunAsync(string[] args, CliIo io, CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(args, io, ct).ConfigureAwait(false);
        }
        catch (UsageException e)
        {
            io.Error.WriteLine("pdfpecker: " + e.Message);
            io.Error.WriteLine("Run 'pdfpecker help' for the commands and options.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception e) when (IsExpectedFailure(e))
        {
            io.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    /// <summary>
    /// The documented ways a run fails: bad input or output files and the library's own exceptions. Anything else is a bug
    /// and is left to surface with its stack trace.
    /// </summary>
    public static bool IsExpectedFailure(Exception e)
        => e is PdfPeckerException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
            or NotSupportedException or InvalidOperationException or System.Security.Cryptography.CryptographicException
            or FormatException or UriFormatException;

    private static Task<int> DispatchAsync(string[] args, CliIo io, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            io.Error.Write(Help.Overview);
            return Task.FromResult(2);
        }

        var first = args[0];
        var rest = args.Skip(1).ToList();
        switch (first)
        {
            case "help" or "--help" or "-h" or "-?" or "/?":
                io.Output.Write(System.Text.Encoding.UTF8.GetBytes(rest.Count > 0 ? Help.ForCommand(rest[0]) : Help.Overview));
                io.Output.Flush();
                return Task.FromResult(0);
            case "version" or "--version" or "-V":
                io.Output.Write(System.Text.Encoding.UTF8.GetBytes(VersionText() + "\n"));
                io.Output.Flush();
                return Task.FromResult(0);
        }

        if (rest.Count > 0 && rest.Any(r => r is "--help" or "-h") && CommandNames.Contains(first))
        {
            io.Output.Write(System.Text.Encoding.UTF8.GetBytes(Help.ForCommand(first)));
            io.Output.Flush();
            return Task.FromResult(0);
        }

        switch (first)
        {
            case "convert": return ConvertCommand.RunAsync(io, rest, wkDefaults: false, watch: false, ct);
            case "watch": return ConvertCommand.RunAsync(io, rest, wkDefaults: false, watch: true, ct);
            case "merge": return Task.FromResult(PageCommands.Merge(io, rest, ct));
            case "split": return Task.FromResult(PageCommands.Split(io, rest, ct));
            case "extract": return Task.FromResult(PageCommands.ExtractOrRemove(io, rest, remove: false, ct));
            case "remove": return Task.FromResult(PageCommands.ExtractOrRemove(io, rest, remove: true, ct));
            case "rotate": return Task.FromResult(PageCommands.Rotate(io, rest, ct));
            case "stamp": return Task.FromResult(PageCommands.Stamp(io, rest, ct));
            case "compact": return Task.FromResult(FileCommands.Compact(io, rest, ct));
            case "linearize": return Task.FromResult(FileCommands.Linearize(io, rest, ct));
            case "sanitize": return Task.FromResult(FileCommands.Sanitize(io, rest, ct));
            case "sign": return Task.FromResult(SecurityCommands.Sign(io, rest, ct));
            case "verify": return Task.FromResult(SecurityCommands.Verify(io, rest, ct));
            case "encrypt": return Task.FromResult(SecurityCommands.Encrypt(io, rest, ct));
            case "decrypt": return Task.FromResult(SecurityCommands.Decrypt(io, rest, ct));
            case "to-image": return Task.FromResult(ExportCommands.ToImage(io, rest, ct));
            case "to-html": return Task.FromResult(ExportCommands.ToHtml(io, rest, ct));
            case "text": return Task.FromResult(ExportCommands.Text(io, rest, ct));
            case "info": return Task.FromResult(ExportCommands.Info(io, rest, ct));
        }

        // Anything else is a wkhtmltopdf-style call: options and files, the last file the PDF to write.
        if (first.StartsWith('-') || first == "-" || first.Contains('.') || first.Contains('/') || first.Contains('\\'))
            return ConvertCommand.RunAsync(io, args, wkDefaults: true, watch: false, ct);

        throw new UsageException($"Unknown command '{first}'.");
    }

    private static string VersionText()
    {
        var info = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CliApp).Assembly.GetName().Version?.ToString() ?? "unknown";
        int plus = info.IndexOf('+');
        return "pdfpecker " + (plus >= 0 ? info.Substring(0, plus) : info);
    }
}
