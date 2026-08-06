namespace SpeechToSpeech.Core.Pipeline;

/// <summary>Strongly-typed kinds for <see cref="PipelineControlMessage"/>.</summary>
public enum ControlKind
{
    SessionEnd,
}

/// <summary>
/// Soft control message that resets per-session state without stopping a handler thread.
/// </summary>
/// <param name="Kind">The control kind.</param>
/// <param name="SessionId">
/// Session that enqueued the message, when known. Lets the pooled realtime send loop ignore a
/// session end from a force-released session so it cannot satisfy the drain wait of the session
/// that claimed the unit afterwards.
/// </param>
public sealed record PipelineControlMessage(ControlKind Kind, string? SessionId = null) : IPipelineItem
{
    public static readonly PipelineControlMessage SessionEnd = new(ControlKind.SessionEnd);

    public static bool Is(object? message, ControlKind? kind = null) =>
        message is PipelineControlMessage control && (kind is null || control.Kind == kind);
}
