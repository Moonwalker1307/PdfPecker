namespace PdfPecker;

/// <summary>Thrown when a PDF is encrypted and the given password (or the empty password) does not open it.</summary>
public sealed class PdfPasswordException : PdfPeckerException
{
    /// <summary>Creates the exception with a message.</summary>
    public PdfPasswordException(string message) : base(message) { }
}
