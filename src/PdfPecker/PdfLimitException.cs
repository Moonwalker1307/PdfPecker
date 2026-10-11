namespace PdfPecker;

/// <summary>
/// Thrown when a document goes over a configured limit, such as <see cref="PdfOptions.MaxPages"/>, instead of using
/// unbounded time or memory.
/// </summary>
public sealed class PdfLimitException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfLimitException(string message) : base(message) { }
}
