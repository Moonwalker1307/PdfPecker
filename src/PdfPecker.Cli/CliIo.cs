using System.Text;

namespace PdfPecker.Cli;

/// <summary>
/// The streams a run reads and writes. Documents travel on <see cref="Output"/> so a PDF can be piped, and everything a
/// person reads (progress, warnings, errors) goes to <see cref="Error"/>.
/// </summary>
internal sealed class CliIo
{
    public CliIo(Stream input, Stream output, TextWriter error)
    {
        Input = input;
        Output = output;
        Error = error;
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public TextWriter Error { get; }

    /// <summary>Set by -q / --quiet: progress and warnings are not printed.</summary>
    public bool Quiet { get; set; }

    public void Info(string message)
    {
        if (!Quiet) Error.WriteLine(message);
    }

    public void Warn(string message)
    {
        if (!Quiet) Error.WriteLine("warning: " + message);
    }

    public byte[] ReadAllInput()
    {
        using var ms = new MemoryStream();
        Input.CopyTo(ms);
        return ms.ToArray();
    }

    public string ReadAllInputText() => new UTF8Encoding(false, false).GetString(ReadAllInput());

    /// <summary>Writes bytes to a file, or to standard output for "-". A file appears only once it is complete.</summary>
    public void WriteOutput(string path, byte[] bytes)
    {
        if (path == "-")
        {
            Output.Write(bytes, 0, bytes.Length);
            Output.Flush();
            return;
        }

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = TemporaryPath(full);
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The file a complete output is written to before it replaces <paramref name="fullPath"/>.</summary>
    public static string TemporaryPath(string fullPath)
        => fullPath + ".pdfpecker-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";

    public void WriteOutput(string path, string text) => WriteOutput(path, new UTF8Encoding(false).GetBytes(text));
}
