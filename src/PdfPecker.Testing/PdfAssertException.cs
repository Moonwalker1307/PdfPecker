namespace PdfPecker.Testing;

/// <summary>
/// Thrown when a <see cref="PdfAssert"/> check fails. Test frameworks report an unexpected exception as a failed test,
/// so the checks work with xUnit, NUnit, MSTest and others without depending on any of them.
/// </summary>
public sealed class PdfAssertException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfAssertException() : this("A PDF check failed.") { }

    /// <summary>Creates the exception with a message.</summary>
    public PdfAssertException(string message) : base(message) => Files = [];

    /// <summary>Creates the exception with a message and the error that caused it.</summary>
    public PdfAssertException(string message, Exception? innerException) : base(message, innerException) => Files = [];

    internal PdfAssertException(string message, IReadOnlyList<string> files) : base(message) => Files = files;

    /// <summary>
    /// The files the failed check wrote, with full paths: the received PDF and the expected, actual and diff images of
    /// the pages that differ. Empty when it wrote none.
    /// </summary>
    public IReadOnlyList<string> Files { get; }
}
