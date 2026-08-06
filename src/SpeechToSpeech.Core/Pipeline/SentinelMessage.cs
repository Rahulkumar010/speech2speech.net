namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Marker for everything that may travel on a pipeline queue: payload messages, control
/// messages and binary sentinels.
/// </summary>
public interface IPipelineItem;

/// <summary>Binary sentinel carried on audio/output queues.</summary>
public sealed class SentinelMessage : IPipelineItem
{
    /// <summary>Stops a handler thread and avoids a queue deadlock.</summary>
    public static readonly SentinelMessage PipelineEnd = new("END");

    /// <summary>Marks the end of the audio for one response.</summary>
    public static readonly SentinelMessage AudioResponseDone = new("__RESPONSE_DONE__");

    private SentinelMessage(string name) => Name = name;

    public string Name { get; }

    public override string ToString() => Name;
}
