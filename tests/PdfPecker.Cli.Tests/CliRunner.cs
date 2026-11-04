using System.Text;
using PdfPecker.Cli;

namespace PdfPecker.Cli.Tests;

/// <summary>Runs the tool in process with in-memory streams, in a folder of its own.</summary>
public sealed class CliRunner : IDisposable
{
    public CliRunner()
    {
        Dir = Path.Combine(Path.GetTempPath(), "pdfpecker-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
    }

    public string Dir { get; }

    public string Path_(string name) => Path.Combine(Dir, name);

    public string Write(string name, string content)
    {
        var path = Path_(name);
        File.WriteAllText(path, content);
        return path;
    }

    public string Html(string name, string body, string title = "Doc")
        => Write(name, $"<html><head><title>{title}</title></head><body>{body}</body></html>");

    public sealed record Result(int Exit, byte[] Out, string Err)
    {
        public string OutText => Encoding.UTF8.GetString(Out);
    }

    public Result Run(params string[] args) => RunWithInput(null, CancellationToken.None, args);

    public Result RunWithInput(byte[]? stdin, CancellationToken ct, params string[] args)
    {
        var output = new MemoryStream();
        var error = new StringWriter();
        var io = new CliIo(new MemoryStream(stdin ?? Array.Empty<byte>()), output, error);
        int exit = CliApp.RunAsync(args, io, ct).GetAwaiter().GetResult();
        return new Result(exit, output.ToArray(), error.ToString());
    }

    public Result Ok(params string[] args)
    {
        var r = Run(args);
        Assert.True(r.Exit == 0, $"pdfpecker {string.Join(' ', args)} exited {r.Exit}: {r.Err}");
        return r;
    }

    public static string Text(string pdfPath) => PdfText.Extract(File.ReadAllBytes(pdfPath));

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
