using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;
using SpeechToSpeech.Llm;
using SpeechToSpeech.Stt;
using SpeechToSpeech.Stt.Whisper;
using SpeechToSpeech.Tts;
using SpeechToSpeech.Vad;

namespace SpeechToSpeech.Pipeline;

/// <summary>
/// A complete VAD → STT → LLM → TTS loop assembled from a <see cref="VoicePipelineConfig"/>.
/// </summary>
/// <remarks>
/// The pipeline owns the stage handlers, the queues between them and the shared runtime state, but
/// no audio devices: callers push PCM in with <see cref="PushAudio(ReadOnlySpan{byte})"/> and read
/// synthesized audio and events off <see cref="AudioOutput"/> and <see cref="TextOutput"/>. That
/// keeps it usable from a console app, a server or a test. <see cref="VoiceLoopHost"/> adds the
/// microphone and speaker on top when a local loop is what is wanted.
/// </remarks>
public sealed class VoicePipeline : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly PipelineRunner _runner;
    private readonly IReadOnlyList<IPipelineHandler> _handlers;
    private readonly CancellationTokenSource _stop;
    private readonly IVadModel _vadModel;

    private readonly Lock _frameGate = new();
    private readonly byte[] _carry;
    private int _carryLength;

    private bool _started;
    private bool _stopped;

    private VoicePipeline(
        VoicePipelineConfig config,
        ILoggerFactory loggerFactory,
        CancellationTokenSource stop,
        IVadModel vadModel,
        RuntimeConfig runtimeConfig,
        PipelineQueue<IPipelineItem> audioInput,
        PipelineQueue<IPipelineItem> audioOutput,
        PipelineQueue<IPipelineItem> textOutput,
        ManualResetEventSlim shouldListen,
        CancelScope cancelScope,
        ChatClientLanguageModel languageModel,
        IReadOnlyList<IPipelineHandler> handlers)
    {
        Config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VoicePipeline>();
        _stop = stop;
        _vadModel = vadModel;
        RuntimeConfig = runtimeConfig;
        AudioInput = audioInput;
        AudioOutput = audioOutput;
        TextOutput = textOutput;
        ShouldListen = shouldListen;
        CancelScope = cancelScope;
        LanguageModel = languageModel;
        _handlers = handlers;
        _runner = new PipelineRunner(handlers, loggerFactory.CreateLogger<PipelineRunner>());

        InputFrameBytes = config.Audio.FrameSamples * 2;
        _carry = new byte[InputFrameBytes];
    }

    public VoicePipelineConfig Config { get; }

    /// <summary>Session state (model, instructions, voice, tools) shared by every stage.</summary>
    public RuntimeConfig RuntimeConfig { get; }

    public Chat Chat => RuntimeConfig.Chat;

    /// <summary>Capture queue. Prefer <see cref="PushAudio(ReadOnlySpan{byte})"/> over writing to it directly.</summary>
    public PipelineQueue<IPipelineItem> AudioInput { get; }

    /// <summary>Synthesized PCM at <see cref="OutputSampleRate"/>, plus response sentinels.</summary>
    public PipelineQueue<IPipelineItem> AudioOutput { get; }

    /// <summary>Transcripts, assistant text, tool calls, token usage and failures.</summary>
    public PipelineQueue<IPipelineItem> TextOutput { get; }

    /// <summary>Cleared while the assistant is speaking so capture does not hear its own playback.</summary>
    public ManualResetEventSlim ShouldListen { get; }

    /// <summary>Shared barge-in scope; cancelling it stops the in-flight response everywhere.</summary>
    public CancelScope CancelScope { get; }

    public ChatClientLanguageModel LanguageModel { get; }

    /// <summary>Bytes per VAD frame. <see cref="PushAudio(ReadOnlySpan{byte})"/> handles any other size.</summary>
    public int InputFrameBytes { get; }

    public int InputSampleRate => Config.Audio.InputSampleRate;

    public int OutputSampleRate => Config.Tts.OutputSampleRate;

    /// <summary>
    /// Loads every model named by <paramref name="config"/> and wires the stages together. Whisper
    /// weights are downloaded on first use, so this can take a while on a cold machine.
    /// </summary>
    public static async Task<VoicePipeline> CreateAsync(
        VoicePipelineConfig config,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var log = factory.CreateLogger<VoicePipeline>();

        var stop = new CancellationTokenSource();
        var shouldListen = new ManualResetEventSlim(true);
        var cancelScope = new CancelScope();
        var speculativeTurns = new SpeculativeTurnTracker(logger: factory.CreateLogger<SpeculativeTurnTracker>());

        var langCode = config.Tts.ResolveLanguageCode(config.Stt.Language);
        var vadOptions = config.Vad.ToOptions();
        var sttOptions = config.Stt.ToOptions();
        var llmOptions = config.Llm.ToOptions();
        var ttsOptions = config.Tts.ToOptions(langCode);

        var runtimeConfig = BuildRuntimeConfig(config, ttsOptions.Voice, factory);

        // ── Queues ────────────────────────────────────────────────────
        var dropLog = factory.CreateLogger("queue");
        var audioQueue = new PipelineQueue<IPipelineItem>(
            config.Queues.AudioCapacity,
            _ => dropLog.LogWarning("Capture queue full; dropped the oldest frame. Is a stage stalled?"));
        var vadQueue = new PipelineQueue<IPipelineItem>(config.Queues.AudioCapacity);
        var sttQueue = new PipelineQueue<IPipelineItem>();
        var templateQueue = new PipelineQueue<IPipelineItem>();
        var llmQueue = new PipelineQueue<IPipelineItem>();
        var lmQueue = new PipelineQueue<IPipelineItem>();
        var ttsQueue = new PipelineQueue<IPipelineItem>();
        var textQueue = new PipelineQueue<IPipelineItem>();
        var audioOutQueue = new PipelineQueue<IPipelineItem>(
            config.Queues.OutputCapacity,
            _ => dropLog.LogWarning("Playback queue full; dropped the oldest block."));

        // ── Models ────────────────────────────────────────────────────
        log.LogInformation("Loading Silero VAD from {Path}", vadOptions.ModelPath);
        IVadModel vadModel = new SileroVadOnnxModel(vadOptions.ModelPath);
        if (config.Vad.LogProbabilities)
        {
            vadModel = new ProbeVadModel(vadModel, factory.CreateLogger("silero"), vadOptions.Threshold);
        }

        var ggmlModelPath = await GgmlModelResolver
            .ResolveAsync(config.Stt.ModelPath, config.Stt.ResolveGgmlType(), log, cancellationToken)
            .ConfigureAwait(false);
        log.LogInformation("Loading Whisper.net from {Path}", ggmlModelPath);

        // ── Handlers ──────────────────────────────────────────────────
        var vad = new VadHandler(
            stop, audioQueue, vadQueue, vadOptions, shouldListen, vadModel,
            textOutputQueue: textQueue,
            speculativeTurns: speculativeTurns,
            logger: factory.CreateLogger<VadHandler>());

        var useTemplate = !string.IsNullOrEmpty(config.Llm.UserTemplate);
        var notifierIn = useTemplate ? templateQueue : sttQueue;

        var stt = new WhisperNetSttHandler(
            stop, vadQueue, sttQueue, sttOptions, ggmlModelPath,
            speculativeTurns: speculativeTurns,
            logger: factory.CreateLogger<WhisperNetSttHandler>());

        var template = useTemplate
            ? new UserTemplateHandler(
                stop, sttQueue, templateQueue, config.Llm.UserTemplate!,
                factory.CreateLogger<UserTemplateHandler>())
            : null;

        var notifier = new TranscriptionNotifier(
            stop, notifierIn, llmQueue,
            textOutputQueue: textQueue,
            runtimeConfig: runtimeConfig,
            shouldListen: shouldListen,
            logger: factory.CreateLogger<TranscriptionNotifier>());

        var llm = new ChatClientLanguageModel(
            stop, llmQueue, lmQueue, llmOptions,
            baseUrl: config.Llm.BaseUrl,
            apiKey: config.Llm.ResolveApiKey(),
            disableThinking: config.Llm.DisableThinking,
            reasoningEffort: config.Llm.ReasoningEffort,
            cancelScope: cancelScope,
            speculativeTurns: speculativeTurns,
            logger: factory.CreateLogger<ChatClientLanguageModel>());

        var lmProcessor = new LmOutputProcessor(
            stop, lmQueue, ttsQueue,
            textOutputQueue: textQueue,
            speculativeTurns: speculativeTurns,
            logger: factory.CreateLogger<LmOutputProcessor>());

        log.LogInformation(
            "Loading Kokoro from {Model} (voice {Voice}, language {Lang})",
            ttsOptions.ModelPath, ttsOptions.Voice, langCode);
        var tts = new KokoroOnnxTtsHandler(
            stop, ttsQueue, audioOutQueue, ttsOptions,
            shouldListen: shouldListen,
            langCode: langCode,
            blockSize: config.Tts.ResolveBlockSize(),
            speculativeTurns: speculativeTurns,
            logger: factory.CreateLogger<KokoroOnnxTtsHandler>());

        // Barge-in propagates through the shared scope, exactly as the realtime service wires it.
        vad.CancelScope = cancelScope;
        lmProcessor.CancelScope = cancelScope;
        tts.CancelScope = cancelScope;

        IPipelineHandler[] handlers = template is null
            ? [vad, stt, notifier, llm, lmProcessor, tts]
            : [vad, stt, template, notifier, llm, lmProcessor, tts];

        if (config.EnableMetrics)
        {
            var metrics = new TurnMetrics(factory.CreateLogger<TurnMetrics>());
            foreach (var handler in handlers)
            {
                handler.Metrics = metrics;
            }
        }

        return new VoicePipeline(
            config, factory, stop, vadModel, runtimeConfig,
            audioQueue, audioOutQueue, textQueue, shouldListen, cancelScope, llm, handlers);
    }

    /// <summary>Issues a cheap LLM request so the first real turn is not paying for model load.</summary>
    public async Task WarmupAsync()
    {
        if (!Config.Llm.Warmup)
        {
            return;
        }

        _logger.LogInformation("Warming up {Model} at {Url}", Config.Llm.Model, Config.Llm.BaseUrl);
        await LanguageModel.WarmupAsync().ConfigureAwait(false);
    }

    /// <summary>Starts every stage on its own long-running thread.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _runner.Start();
    }

    /// <summary>
    /// Splits <paramref name="pcm16"/> into VAD frames and queues them, carrying any remainder over
    /// to the next call. Never blocks, so it is safe to call from an audio device callback.
    /// </summary>
    public void PushAudio(ReadOnlySpan<byte> pcm16)
    {
        var consumed = 0;
        while (consumed < pcm16.Length)
        {
            byte[]? frame = null;

            lock (_frameGate)
            {
                var take = Math.Min(InputFrameBytes - _carryLength, pcm16.Length - consumed);
                pcm16.Slice(consumed, take).CopyTo(_carry.AsSpan(_carryLength));
                _carryLength += take;
                consumed += take;

                if (_carryLength == InputFrameBytes)
                {
                    frame = (byte[])_carry.Clone();
                    _carryLength = 0;
                }
            }

            if (frame is not null)
            {
                AudioInput.Put(new AudioChunk(frame, RuntimeConfig));
            }
        }
    }

    /// <summary>Queues one silent frame, letting the VAD observe an end-of-speech gap.</summary>
    public void PushSilence() => AudioInput.Put(new AudioChunk(new byte[InputFrameBytes], RuntimeConfig));

    /// <summary>
    /// Waits until the response path goes quiet, or <paramref name="timeout"/> elapses. Returns
    /// whether the pipeline actually drained.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (AudioOutput.IsEmpty && TextOutput.IsEmpty)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Sends the end sentinel through the graph and waits for every stage to exit. The sentinel
    /// cascades: each stage forwards it downstream when its own loop ends.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        AudioInput.Put(SentinelMessage.PipelineEnd);
        await _runner.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        foreach (var handler in _handlers.OfType<IDisposable>())
        {
            handler.Dispose();
        }

        _vadModel.Dispose();
        Chat.Dispose();
        ShouldListen.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// Projects the config into the session the stages read at runtime, so a later
    /// <c>session.update</c> can change voice, instructions or tools through the same path.
    /// </summary>
    private static RuntimeConfig BuildRuntimeConfig(
        VoicePipelineConfig config, string voice, ILoggerFactory factory) => new()
        {
            Chat = new Chat(config.Llm.HistorySize, factory.CreateLogger<Chat>()),
            Session = new SessionCreateRequest
            {
                Model = config.Llm.Model,
                Instructions = config.Llm.BuildInstructions(),
                OutputModalities = ["audio"],
                Temperature = config.Llm.Temperature,
                MaxOutputTokens = config.Llm.MaxOutputTokens,
                ToolChoice = config.Llm.ToolChoice,
                Tools = config.Llm.Tools.Count == 0
                    ? null
                    : [.. config.Llm.Tools.Select(tool => tool.ToDefinition())],
                Audio = new AudioConfig
                {
                    Input = new AudioInputConfig
                    {
                        Format = new AudioFormat { Rate = config.Audio.InputSampleRate },
                        TurnDetection = new TurnDetectionConfig
                        {
                            Threshold = config.Vad.Threshold,
                            SilenceDurationMs = config.Vad.MinSilenceMs,
                            PrefixPaddingMs = config.Vad.SpeechPadMs,
                        },
                    },
                    Output = new AudioOutputConfig
                    {
                        Format = new AudioFormat { Rate = config.Tts.OutputSampleRate },
                        Voice = voice,
                        Speed = config.Tts.Speed,
                    },
                },
            },
        };
}
