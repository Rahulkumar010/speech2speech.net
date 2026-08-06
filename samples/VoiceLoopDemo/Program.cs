using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Configuration;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;
using SpeechToSpeech.Llm;
using SpeechToSpeech.Stt;
using SpeechToSpeech.Stt.Whisper;
using SpeechToSpeech.Vad;
using NAudio.Wave;
using SpeechToSpeech.Tts;
using SpeechToSpeech.Tts.Kokoro;
using Whisper.net.Ggml;

var arguments = ParseArguments(args);

var vadModelPath = arguments.GetValueOrDefault("vad") ?? "models/silero_vad.onnx";
var whisperDirectory = arguments.GetValueOrDefault("whisper") ?? "models/whisper";
var ggmlSize = arguments.GetValueOrDefault("whisper-size") ?? "base";
var llmBaseUrl = arguments.GetValueOrDefault("llm-url") ?? "http://localhost:65466/v1";
var modelName = arguments.GetValueOrDefault("model") ?? "qwen2.5-1.5b-instruct-openvino-npu:5";
var demoTools = arguments.ContainsKey("demo-tools");
var metrics = arguments.ContainsKey("metrics");
var verbose = arguments.ContainsKey("verbose");

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

var log = loggerFactory.CreateLogger("VoiceLoopDemo");

// ── Configuration ─────────────────────────────────────────────────────
var vadOptions = new VadOptions
{
    ModelPath = vadModelPath,
    Threshold = double.TryParse(arguments.GetValueOrDefault("vad-threshold"), out var t) ? t : 0.6,
    SampleRate = 16000,
    MinSilenceMs = 300,
    MinSpeechMs = 384,
    SpeechPadMs = 30,
    EnableRealtimeTranscription = true,
};

var sttOptions = new SttOptions
{
    ModelPath = whisperDirectory,
    Language = "auto",
    FinalRevisionSettleSeconds = 0.15,
};

var llmOptions = new LanguageModelHandlerOptions
{
    ModelName = modelName,
    Stream = true,
    StreamBatchSentences = 2,
    EnableLanguagePrompt = true,
    RequestTimeout = TimeSpan.FromSeconds(60),
};

var ttsOptions = new TtsOptions
{
    ModelPath = arguments.GetValueOrDefault("kokoro") ?? "models/kokoro-v1.0.onnx",
    VoicesPath = arguments.GetValueOrDefault("voices") ?? string.Empty,
    Voice = arguments.GetValueOrDefault("voice") ?? "bm_fable",
    Speed = 1.0,
    SampleRate = KokoroOnnxModel.SampleRate,
    OutputSampleRate = KokoroOnnxModel.SampleRate,
};

// ── Shared pipeline state ─────────────────────────────────────────────
using var stop = new CancellationTokenSource();
using var shouldListen = new ManualResetEventSlim(true);
var speculativeTurns = new SpeculativeTurnTracker(logger: loggerFactory.CreateLogger<SpeculativeTurnTracker>());
var cancelScope = new CancelScope();

var runtimeConfig = new RuntimeConfig
{
    Chat = new Chat(size: 10, loggerFactory.CreateLogger<Chat>()),
    Session = new SessionCreateRequest
    {
        Model = modelName,
        Instructions = "You are a concise, friendly voice assistant. Answer in under 20 words.",
        OutputModalities = ["audio"],

        // The demo has no host to execute a tool, so the call is only logged. Its purpose is to
        // exercise the capability probe and the tool-call path, which stay dormant without tools.
        Tools = demoTools
            ?
            [
                new FunctionToolDefinition
                {
                    Name = "get_current_time",
                    Description = "Get the current local time.",
                    Parameters = JsonNode.Parse("""{"type":"object","properties":{}}"""),
                },
                new FunctionToolDefinition
                {
                    Name = "get_weather",
                    Description = "Get the current weather for a city.",
                    Parameters = JsonNode.Parse(
                        """
                        {"type":"object","properties":{"city":{"type":"string","description":"City name"}},"required":["city"]}
                        """),
                },
            ]
            : null,
    },
};

// ── Queues ────────────────────────────────────────────────────────────
// Audio-carrying queues are bounded. The capture callback runs on the WinMM thread and must never
// block, so a full queue evicts its oldest frame: dropping the stale audio a slow stage will discard
// anyway is strictly better than growing the heap until the process dies. Control-plane queues stay
// unbounded — their traffic is one message per turn.
const int AudioQueueCapacity = 512;    // ~16 s of 32 ms capture frames
const int OutputQueueCapacity = 2048;  // ~100 s of 50 ms playback blocks

var dropLog = loggerFactory.CreateLogger("queue");
var audioQueue = new PipelineQueue<IPipelineItem>(
    AudioQueueCapacity,
    _ => dropLog.LogWarning("Capture queue full; dropped the oldest frame. Is a stage stalled?"));
var vadQueue = new PipelineQueue<IPipelineItem>(AudioQueueCapacity);
var sttQueue = new PipelineQueue<IPipelineItem>();
var llmQueue = new PipelineQueue<IPipelineItem>();
var lmQueue = new PipelineQueue<IPipelineItem>();
var ttsQueue = new PipelineQueue<IPipelineItem>();
var textQueue = new PipelineQueue<IPipelineItem>();
var audioOutQueue = new PipelineQueue<IPipelineItem>(
    OutputQueueCapacity,
    _ => dropLog.LogWarning("Playback queue full; dropped the oldest block."));

if (verbose)
{
    using var probe = new Microsoft.ML.OnnxRuntime.InferenceSession(vadModelPath);
    foreach (var (name, meta) in probe.InputMetadata)
    {
        log.LogInformation("VAD input  {Name}: {Type} [{Dims}]",
            name, meta.ElementType, string.Join(",", meta.Dimensions));
    }

    foreach (var (name, meta) in probe.OutputMetadata)
    {
        log.LogInformation("VAD output {Name}: {Type} [{Dims}]",
            name, meta.ElementType, string.Join(",", meta.Dimensions));
    }
}

// ── Models ────────────────────────────────────────────────────────────
log.LogInformation("Loading Silero VAD from {Path}", vadModelPath);
IVadModel vadModel = new SileroVadOnnxModel(vadModelPath);
if (verbose)
{
    vadModel = new ProbeVadModel(vadModel, loggerFactory.CreateLogger("silero"), vadOptions.Threshold);
}

if (!Enum.TryParse<GgmlType>(ggmlSize, ignoreCase: true, out var ggmlType))
{
    log.LogWarning("Unknown --whisper-size {Size}; falling back to base", ggmlSize);
    ggmlType = GgmlType.Base;
}

var ggmlModelPath = await GgmlModelResolver
    .ResolveAsync(whisperDirectory, ggmlType, log, stop.Token)
    .ConfigureAwait(false);
log.LogInformation("Loading Whisper.net from {Path}", ggmlModelPath);

// ── Handlers ──────────────────────────────────────────────────────────
var vad = new VadHandler(
    stop, audioQueue, vadQueue, vadOptions, shouldListen, vadModel,
    textOutputQueue: textQueue,
    speculativeTurns: speculativeTurns,
    logger: loggerFactory.CreateLogger<VadHandler>());

var stt = new WhisperNetSttHandler(
    stop, vadQueue, sttQueue, sttOptions, ggmlModelPath,
    speculativeTurns: speculativeTurns,
    logger: loggerFactory.CreateLogger<WhisperNetSttHandler>());

var notifier = new TranscriptionNotifier(
    stop, sttQueue, llmQueue,
    textOutputQueue: textQueue,
    runtimeConfig: runtimeConfig,
    shouldListen: shouldListen,
    logger: loggerFactory.CreateLogger<TranscriptionNotifier>());

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

var llm = new ChatClientLanguageModel(
    stop, llmQueue, lmQueue, llmOptions,
    baseUrl: llmBaseUrl,
    apiKey: apiKey,
    cancelScope: cancelScope,
    speculativeTurns: speculativeTurns,
    logger: loggerFactory.CreateLogger<ChatClientLanguageModel>());

var lmProcessor = new LmOutputProcessor(
    stop, lmQueue, ttsQueue,
    textOutputQueue: textQueue,
    speculativeTurns: speculativeTurns,
    logger: loggerFactory.CreateLogger<LmOutputProcessor>());

log.LogInformation("Loading Kokoro from {Model} / {Voices}", ttsOptions.ModelPath, ttsOptions.VoicesPath);
var tts = new KokoroOnnxTtsHandler(
    stop, ttsQueue, audioOutQueue, ttsOptions,
    shouldListen: shouldListen,
    langCode: "b",
    blockSize: 1200,               // 50 ms at 24 kHz
    speculativeTurns: speculativeTurns,
    logger: loggerFactory.CreateLogger<KokoroOnnxTtsHandler>());

// Barge-in propagates through the shared scope, exactly as the realtime service wires it.
vad.CancelScope = cancelScope;
lmProcessor.CancelScope = cancelScope;
tts.CancelScope = cancelScope;     // enables the per-chunk barge-in check in ToChunks

log.LogInformation("Warming up {Model} at {Url}", modelName, llmBaseUrl);
await llm.WarmupAsync().ConfigureAwait(false);

// ── Start ─────────────────────────────────────────────────────────────
IPipelineHandler[] handlers = [vad, stt, notifier, llm, lmProcessor, tts];

if (metrics)
{
    var turnMetrics = new TurnMetrics(loggerFactory.CreateLogger<TurnMetrics>());
    foreach (var handler in handlers)
    {
        handler.Metrics = turnMetrics;
    }
}

var runner = new PipelineRunner(handlers, loggerFactory.CreateLogger<PipelineRunner>());

runner.Start();

var speakerBuffer = new BufferedWaveProvider(new WaveFormat(ttsOptions.OutputSampleRate, 16, 1))
{
    BufferDuration = TimeSpan.FromSeconds(30),
    DiscardOnBufferOverflow = true,
};

using var speaker = new WaveOutEvent { DesiredLatency = 120 };
speaker.Init(speakerBuffer);
speaker.Play();

var printer = new Thread(() => PrintOutputs(audioOutQueue, textQueue)) { Name = "OutputPrinter" };
printer.Start();

// ── Capture from the microphone ───────────────────────────────────────
const int FrameSamples = 512;              // Silero v5 window at 16 kHz
const int FrameBytes = FrameSamples * 2;

using var microphone = new WaveInEvent
{
    DeviceNumber = int.TryParse(arguments.GetValueOrDefault("device"), out var device) ? device : 0,
    WaveFormat = new WaveFormat(16000, 16, 1),
    BufferMilliseconds = 32,               // 512 samples per callback
    NumberOfBuffers = 3,
};

// WinMM callbacks are not guaranteed to land on exact frame boundaries, so carry the remainder.
var carry = new byte[FrameBytes];
var carryLength = 0;
var levelLogged = 0.0;

microphone.DataAvailable += (_, e) =>
{
    var consumed = 0;
    while (consumed < e.BytesRecorded)
    {
        var take = Math.Min(FrameBytes - carryLength, e.BytesRecorded - consumed);
        Buffer.BlockCopy(e.Buffer, consumed, carry, carryLength, take);
        carryLength += take;
        consumed += take;

        if (carryLength < FrameBytes)
        {
            continue;
        }

        var frame = (byte[])carry.Clone();
        carryLength = 0;

        // Proves capture is live before VAD ever sees the audio.
        var now = Clock.NowSeconds;
        if (now - levelLogged >= 0.25)
        {
            levelLogged = now;
            var samples = AudioConvert.Int16BytesToFloat(frame);
            var rms = MathF.Sqrt(samples.Sum(s => s * s) / samples.Length);
            var db = 20 * MathF.Log10(MathF.Max(rms, 1e-6f));
            var bars = Math.Clamp((int)((db + 60) / 3), 0, 20);
            Write(ConsoleColor.DarkYellow, $"[mic ] {db,6:F1} dBFS {new string('█', bars)}");
        }

        audioQueue.Put(new AudioChunk(frame, runtimeConfig));
    }
};

microphone.RecordingStopped += (_, e) =>
{
    if (e.Exception is not null)
    {
        log.LogError(e.Exception, "Microphone capture stopped with an error");
    }
};

var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                       // let the pipeline drain instead of killing the process
    finished.TrySetResult();
};

LogCaptureDevices(log);
log.LogInformation("Listening on '{Device}'. Speak, then press Ctrl+C to stop.",
    WaveInEvent.GetCapabilities(microphone.DeviceNumber).ProductName);

microphone.StartRecording();
await finished.Task.ConfigureAwait(false);

// ── Drain and shut down ───────────────────────────────────────────────
log.LogInformation("Stopping capture; waiting for the in-flight response to finish");
microphone.StopRecording();

// Trailing silence lets VAD observe the end-of-speech gap and release the last segment.
for (var i = 0; i < 40; i++)
{
    audioQueue.Put(new AudioChunk(new byte[FrameBytes], runtimeConfig));
    await Task.Delay(32).ConfigureAwait(false);
}

// Give the in-flight response time to finish speaking, but stop waiting as soon as the audio path
// goes quiet rather than always burning a fixed delay.
var drainDeadline = DateTime.UtcNow.AddSeconds(15);
while (DateTime.UtcNow < drainDeadline && !(audioOutQueue.IsEmpty && ttsQueue.IsEmpty && lmQueue.IsEmpty))
{
    await Task.Delay(100).ConfigureAwait(false);
}

// The sentinel cascades: each stage forwards it downstream when its own loop exits.
audioQueue.Put(SentinelMessage.PipelineEnd);
printer.Join(TimeSpan.FromSeconds(15));
await runner.StopAsync().ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine("── Final conversation history ──");
foreach (var item in runtimeConfig.Chat.Buffer)
{
    Console.WriteLine($"  [{item.Role}] {item.TextContent()}");
}

return 0;

// ── Helpers ───────────────────────────────────────────────────────────

void LogCaptureDevices(ILogger logger)
{
    for (var i = 0; i < WaveInEvent.DeviceCount; i++)
    {
        var capabilities = WaveInEvent.GetCapabilities(i);
        logger.LogInformation("  capture device {Index}: {Name} ({Channels}ch)",
            i, capabilities.ProductName, capabilities.Channels);
    }
}

void PrintOutputs(PipelineQueue<IPipelineItem> audio, PipelineQueue<IPipelineItem> text)
{
    while (true)
    {
        var idle = true;
        var spokenBytes = 0;

        while (text.TryTakeNow(out var item))
        {
            idle = false;
            switch (item)
            {
                case SpeechStartedEvent:
                    Write(ConsoleColor.DarkGray, "[vad] speech started");
                    break;
                case SpeechStoppedEvent:
                    Write(ConsoleColor.DarkGray, "[vad] speech stopped");
                    break;
                case PartialTranscriptionEvent partial:
                    Write(ConsoleColor.DarkCyan, $"[stt~] {partial.Delta}");
                    break;
                case TranscriptionCompletedEvent completed:
                    Write(ConsoleColor.Cyan, $"[stt ] ({completed.LanguageCode ?? "?"}) {completed.Transcript}");
                    break;
                case AssistantTextEvent assistant when assistant.Tools.Count > 0:
                    Write(ConsoleColor.Magenta,
                        $"[tool] {string.Join(", ", assistant.Tools.Select(t => $"{t.Name}{t.Arguments}"))}");
                    break;
                case AssistantTextEvent assistant:
                    Write(ConsoleColor.White, $"[llm ] {assistant.Text}");
                    break;
                case TokenUsageEvent usage:
                    Write(ConsoleColor.DarkGray, $"[cost] in={usage.InputTokens} out={usage.OutputTokens}");
                    break;
                case ResponseFailedEvent failed:
                    Write(ConsoleColor.Red, $"[fail] {failed.Message}");
                    break;
            }
        }

        while (audio.TryTakeNow(out var item))
        {
            idle = false;

            if (ReferenceEquals(item, SentinelMessage.PipelineEnd))
            {
                return;
            }

            if (ReferenceEquals(item, SentinelMessage.AudioResponseDone))
            {
                Write(ConsoleColor.DarkGreen, $"[tts ] response audio done ({spokenBytes / 2} samples)");
                spokenBytes = 0;
                // Drain the tail before re-arming the mic, or VAD triggers on our own playback.
                while (speakerBuffer.BufferedDuration > TimeSpan.Zero)
                {
                    Thread.Sleep(20);
                }

                shouldListen.Set();
                Write(ConsoleColor.DarkGray, "[mic ] listening again");
                continue;
            }

            var pcm = item switch
            {
                AudioOutput output => output.Audio,
                AudioChunk chunk => chunk.Data,
                _ => null,
            };

            if (pcm is null)
            {
                continue;
            }

            if (shouldListen.IsSet)
            {
                shouldListen.Reset();
                Write(ConsoleColor.DarkGray, "[mic ] muted while speaking");
            }

            speakerBuffer.AddSamples(pcm, 0, pcm.Length);
            spokenBytes += pcm.Length;
        }

        if (idle)
        {
            Thread.Sleep(20);
        }
    }
}

void Write(ConsoleColor color, string message)
{
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ResetColor();
}

// Parses `--key value` and `--flag` pairs. Unknown keys are kept so a typo is visible rather than silent.
Dictionary<string, string> ParseArguments(string[] argv)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = argv[i][2..];
        var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
        parsed[key] = hasValue ? argv[++i] : "true";
    }

    return parsed;
}

/// Reports peak Silero probability once per second so threshold tuning is not guesswork.
internal sealed class ProbeVadModel(IVadModel inner, ILogger logger, double threshold) : IVadModel
{
    private double _lastLog;
    private float _peak;
    private int _frames;
    private int _overThreshold;

    public float Predict(ReadOnlySpan<float> chunk, int sampleRate)
    {
        var probability = inner.Predict(chunk, sampleRate);

        _frames++;
        _peak = Math.Max(_peak, probability);
        if (probability >= threshold)
        {
            _overThreshold++;
        }

        var now = Clock.NowSeconds;
        if (now - _lastLog >= 1.0)
        {
            _lastLog = now;
            logger.LogInformation(
                "[silero] frames={Frames} peak={Peak:F3} over{Threshold:F2}={Over}",
                _frames, _peak, threshold, _overThreshold);
            _frames = 0;
            _peak = 0;
            _overThreshold = 0;
        }

        return probability;
    }

    public void ResetStates() => inner.ResetStates();

    public void Dispose() => inner.Dispose();
}
