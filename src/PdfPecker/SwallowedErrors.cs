namespace PdfPecker;

/// <summary>
/// Reports the exceptions that the library catches and carries on from, so that tests and the fuzzer can see them.
/// </summary>
/// <remarks>
/// <para>
/// Damaged input is allowed to fail deep inside a parser or decoder with an <see cref="IndexOutOfRangeException"/>,
/// a <see cref="NullReferenceException"/> or an <see cref="InvalidCastException"/>, and the code around it catches the
/// exception and keeps what it has: the renderer goes on with the next operator, a font falls back, an image is left
/// out. The same catch hides a real bug when the input is well formed, so every such catch calls <see cref="Record"/>.
/// </para>
/// <para>
/// Nothing is recorded unless a sink is set, and the catch sites run only after an exception, so the normal path costs
/// nothing. The sink is an <see cref="AsyncLocal{T}"/> rather than a static field because tests run in parallel and the
/// fuzzer runs one input per thread: each caller sees only the exceptions of its own work, including the work the
/// library hands to other threads, which inherit the execution context.
/// </para>
/// </remarks>
internal static class SwallowedErrors
{
    private static readonly AsyncLocal<Action<Exception>?> s_sink = new();

    /// <summary>
    /// The handler that receives the exceptions caught in the current execution context, or null to record nothing.
    /// It runs inside the catch block, so it must not throw, and it may be called from several threads at once when the
    /// library works in parallel.
    /// </summary>
    internal static Action<Exception>? Sink
    {
        get => s_sink.Value;
        set => s_sink.Value = value;
    }

    /// <summary>
    /// Passes a caught exception to the sink when it is of a type that points at a bug rather than at damaged input.
    /// Documented exceptions and the format errors the parsers throw on purpose are not passed on, because they are the
    /// expected answer to damaged input.
    /// </summary>
    internal static void Record(Exception ex)
    {
        if (ex is IndexOutOfRangeException or NullReferenceException or InvalidCastException) s_sink.Value?.Invoke(ex);
    }
}
