namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Per-pipeline logging context. Each isolated pipeline unit sets the value at thread or task
/// entry so every log record emitted from that thread is tagged with a <c>[pipeline N] </c>
/// prefix. <see cref="AsyncLocal{T}"/> flows across both threads and async continuations, matching
/// <c>contextvars</c> original.
/// </summary>
public static class PipelineLogContext
{
    private static readonly AsyncLocal<int?> Current = new();

    public static int? PipelineIndex
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string Prefix => Current.Value is { } index ? $"[pipeline {index}] " : string.Empty;
}
