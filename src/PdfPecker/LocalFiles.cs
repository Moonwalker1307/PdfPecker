namespace PdfPecker;

/// <summary>Decides which local file a document's image or stylesheet reference may read.</summary>
internal static class LocalFiles
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The full path to read for <paramref name="path"/>, or null when the document may not read it. Unless
    /// <paramref name="anywhere"/> is set, only relative paths that stay inside <paramref name="baseDirectory"/> are allowed:
    /// absolute paths, UNC shares, <c>..</c> escapes and symbolic links that point outside it are refused.
    /// </summary>
    public static string? Resolve(string path, string? baseDirectory, bool anywhere)
    {
        if (path.Length == 0 || path.IndexOf('\0') >= 0) return null;
        if (anywhere)
        {
            if (Path.IsPathRooted(path)) return path;
            return baseDirectory == null ? null : Path.Combine(baseDirectory, path);
        }
        if (baseDirectory == null || Path.IsPathRooted(path)) return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!IsInside(full, root)) return null;

        // A link inside the base directory may still point elsewhere: check every link between it and the file.
        for (var p = full; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
        {
            FileSystemInfo info = p == full ? new FileInfo(p) : new DirectoryInfo(p);
            if (info.LinkTarget == null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target == null || !IsInside(Path.GetFullPath(target.FullName), root)) return null;
        }
        return full;
    }

    /// <summary>The largest file a document's reference may read: far more than any real image or stylesheet.</summary>
    internal const long MaxFileSize = 256L * 1024 * 1024;

    /// <summary>
    /// The contents of the regular file at <paramref name="path"/>, or null when it is missing, empty, larger than
    /// <see cref="MaxFileSize"/> or not a regular file.
    /// </summary>
    public static byte[]? Read(string path)
    {
        // Devices such as /dev/zero never end and a named pipe blocks whoever opens it until something writes to it.
        // Both report no length, as do empty files that have nothing to offer anyway, so only files with a length are
        // opened, and no more than that length is read.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxFileSize) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var bytes = new byte[info.Length];
        int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return read == bytes.Length ? bytes : bytes.AsSpan(0, read).ToArray();
    }

    private static bool IsInside(string full, string root)
        => full.Length > root.Length && full.StartsWith(root, PathComparison)
           && (Path.EndsInDirectorySeparator(root) // a drive or file-system root
               || full[root.Length] == Path.DirectorySeparatorChar || full[root.Length] == Path.AltDirectorySeparatorChar);
}
