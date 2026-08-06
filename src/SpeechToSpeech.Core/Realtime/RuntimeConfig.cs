using SpeechToSpeech.Core.Conversation;

namespace SpeechToSpeech.Core.Realtime;

/// <summary>
/// Shared realtime configuration written by the realtime service on <c>session.update</c> and read
/// by pipeline handlers (VAD, LLM, TTS) during processing. The canonical state lives in
/// <see cref="Session"/>.
/// </summary>
/// <remarks>
/// Updates are copy-on-write: <see cref="ApplySessionUpdate"/> builds a fully-formed clone under a
/// lock and publishes it with a single reference store. Readers take the reference once and see a
/// snapshot that never changes underneath them. The previous design mutated the live object field
/// by field, so a VAD or TTS thread reading mid-update could observe a new threshold with an old
/// voice — a state the client never asked for and which is close to impossible to reproduce.
/// </remarks>
public sealed class RuntimeConfig
{
    private readonly Lock _writeGate = new();
    private SessionCreateRequest _session = EnsureAudioStructure(new SessionCreateRequest());

    public Chat Chat { get; init; } = new(10);

    /// <summary>
    /// The current session snapshot. Each read returns an internally consistent instance; treat it
    /// as read-only and re-read it rather than caching across a turn.
    /// </summary>
    public SessionCreateRequest Session
    {
        get => Volatile.Read(ref _session);
        set
        {
            lock (_writeGate)
            {
                Volatile.Write(ref _session, EnsureAudioStructure(value.Clone()));
            }
        }
    }

    /// <summary>
    /// Whether barge-in should cancel an active response. Reads
    /// <c>audio.input.turn_detection.interrupt_response</c>, defaulting to <c>true</c> like the
    /// OpenAI API.
    /// </summary>
    public bool InterruptResponseEnabled =>
        Session.Audio?.Input?.TurnDetection?.InterruptResponse ?? true;

    /// <summary>
    /// Merges the fields the client actually sent from <paramref name="update"/> into the current
    /// session, preserving fields the update omitted, and publishes the result atomically.
    /// </summary>
    public void ApplySessionUpdate(SessionCreateRequest update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_writeGate)
        {
            var session = EnsureAudioStructure(_session.Clone());

            session.Model = update.Model ?? session.Model;
            session.Instructions = update.Instructions ?? session.Instructions;
            session.OutputModalities = update.OutputModalities ?? session.OutputModalities;
            session.Tools = update.Tools ?? session.Tools;
            session.ToolChoice = update.ToolChoice ?? session.ToolChoice;
            session.Temperature = update.Temperature ?? session.Temperature;
            session.MaxOutputTokens = update.MaxOutputTokens ?? session.MaxOutputTokens;

            if (update.Audio?.Input is { } input)
            {
                var current = session.Audio!.Input!;
                current.Format = input.Format ?? current.Format;
                current.Transcription = input.Transcription ?? current.Transcription;
                if (input.TurnDetection is { } turnDetection)
                {
                    current.TurnDetection ??= new TurnDetectionConfig();
                    current.TurnDetection.Type = turnDetection.Type;
                    current.TurnDetection.Threshold = turnDetection.Threshold ?? current.TurnDetection.Threshold;
                    current.TurnDetection.PrefixPaddingMs =
                        turnDetection.PrefixPaddingMs ?? current.TurnDetection.PrefixPaddingMs;
                    current.TurnDetection.SilenceDurationMs =
                        turnDetection.SilenceDurationMs ?? current.TurnDetection.SilenceDurationMs;
                    current.TurnDetection.CreateResponse =
                        turnDetection.CreateResponse ?? current.TurnDetection.CreateResponse;
                    current.TurnDetection.InterruptResponse =
                        turnDetection.InterruptResponse ?? current.TurnDetection.InterruptResponse;
                }
            }

            if (update.Audio?.Output is { } output)
            {
                var current = session.Audio!.Output!;
                current.Format = output.Format ?? current.Format;
                current.Voice = output.Voice ?? current.Voice;
                current.Speed = output.Speed ?? current.Speed;
            }

            // Single publishing store: readers see either the whole update or none of it.
            Volatile.Write(ref _session, session);
        }
    }

    /// <summary>Guarantees <c>audio.input</c> and <c>audio.output</c> are never null.</summary>
    private static SessionCreateRequest EnsureAudioStructure(SessionCreateRequest session)
    {
        session.Audio ??= new AudioConfig();
        session.Audio.Input ??= new AudioInputConfig();
        session.Audio.Output ??= new AudioOutputConfig();
        return session;
    }
}
