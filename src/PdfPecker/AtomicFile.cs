using System.Text;

namespace PdfPecker;

/// <summary>
/// Writes output files through a temporary file in the same folder that replaces the target only once the whole file is
/// written, so a failure, a cancellation or a crash part-way never leaves a truncated file at the output path and an
/// existing file there stays as it was.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Writes a file through a temporary one that is moved into place once <paramref name="write"/> succeeds.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        var full = Path.GetFullPath(path);
        // The temporary file sits next to the target so the final move is a rename on the same volume, not a copy.
        var temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                write(output);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> through a temporary file.</summary>
    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, output => output.Write(bytes, 0, bytes.Length));

    /// <summary>
    /// Writes <paramref name="text"/> as UTF-8 without a byte order mark, the encoding <see cref="File.WriteAllText(string, string)"/>
    /// uses, through a temporary file.
    /// </summary>
    public static void WriteAllText(string path, string text)
        => WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
}
