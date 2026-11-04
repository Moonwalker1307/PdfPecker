namespace PdfPecker.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Let the running command stop on its own, so a half-written output file is cleaned up.
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        var io = new CliIo(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
        return await CliApp.RunAsync(args, io, cts.Token).ConfigureAwait(false);
    }
}
