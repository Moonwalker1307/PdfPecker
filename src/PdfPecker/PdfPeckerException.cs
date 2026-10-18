namespace PdfPecker;

/// <summary>
/// The base class of the exceptions PdfPecker throws for its own errors (<see cref="PdfLimitException"/>,
/// <see cref="PdfPasswordException"/> and <see cref="PdfSignatureException"/>), so one catch handles them all. Argument
/// errors, unreadable input (<see cref="InvalidDataException"/>), I/O errors and cancellation keep their standard .NET
/// exception types.
/// </summary>
public class PdfPeckerException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfPeckerException() { }

    /// <summary>Creates the exception with a message.</summary>
    public PdfPeckerException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the error that caused it.</summary>
    public PdfPeckerException(string message, Exception? innerException) : base(message, innerException) { }
}
