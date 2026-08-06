using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Pipeline;

// A microphone-to-speaker voice loop where every model, voice, language and prompt comes from a
// JSON file. Compare with VoiceLoopDemo, which wires the same stages by hand: here the sample only
// loads config, prints events and waits for Ctrl+C.

var arguments = ParseArguments(args);

if (arguments.ContainsKey("help"))
{
    PrintUsage();
    return 0;
}

string configPath;
VoicePipelineConfig config;
try
{
    configPath = ResolveConfigPath(arguments.GetValueOrDefault("config"));
    config = VoicePipelineConfig.Load(configPath);
    ApplyOverrides(config, arguments);
    config.Validate();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

var verbose = arguments.ContainsKey("verbose");

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

var log = loggerFactory.CreateLogger("VoicePipelineDemo");
log.LogInformation("Loaded configuration from {Path}", configPath);

foreach (var (name, index) in VoiceLoopHost.CaptureDevices().Select((name, index) => (name, index)))
{
    log.LogInformation("  capture device {Index}: {Name}", index, name);
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                       // let the pipeline drain instead of killing the process
    stopping.Cancel();
};

await using var host = await VoiceLoopHost.CreateAsync(config, loggerFactory, stopping.Token)
    .ConfigureAwait(false);

host.EventReceived += Print;
host.InputLevel += level =>
{
    var bars = Math.Clamp((int)((level + 60) / 3), 0, 20);
    Write(ConsoleColor.DarkYellow, $"[mic ] {level,6:F1} dBFS {new string('█', bars)}");
};

Write(ConsoleColor.Green,
    $"Speaking to {config.Llm.Model} as '{config.Tts.ResolveVoice(config.Tts.ResolveLanguageCode(config.Stt.Language))}'. Press Ctrl+C to stop.");

await host.RunAsync(stopping.Token).ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine("── Final conversation history ──");
foreach (var item in host.Pipeline.Chat.Buffer)
{
    Console.WriteLine($"  [{item.Role}] {item.TextContent()}");
}

return 0;

// ── Helpers ───────────────────────────────────────────────────────────

void Print(PipelineEvent pipelineEvent)
{
    switch (pipelineEvent)
    {
        case SpeechStartedEvent:
            Write(ConsoleColor.DarkGray, "[vad ] speech started");
            break;
        case SpeechStoppedEvent:
            Write(ConsoleColor.DarkGray, "[vad ] speech stopped");
            break;
        case PartialTranscriptionEvent partial:
            Write(ConsoleColor.DarkCyan, $"[stt~] {partial.Delta}");
            break;
        case TranscriptionCompletedEvent completed:
            Write(ConsoleColor.Cyan, $"[stt ] ({completed.LanguageCode ?? "?"}) {completed.Transcript}");
            break;
        case AssistantTextEvent assistant when assistant.Tools.Count > 0:
            Write(ConsoleColor.Magenta,
                $"[tool] {string.Join(", ", assistant.Tools.Select(tool => $"{tool.Name}{tool.Arguments}"))}");
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

void Write(ConsoleColor color, string message)
{
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ResetColor();
}

// Looks beside the working directory first so `dotnet run` from the repository root picks up an
// edited config without a rebuild, then falls back to the copy deployed next to the assembly.
string ResolveConfigPath(string? requested)
{
    if (!string.IsNullOrWhiteSpace(requested))
    {
        return File.Exists(requested)
            ? Path.GetFullPath(requested)
            : throw new FileNotFoundException($"Configuration file not found: {requested}");
    }

    string[] candidates =
    [
        Path.Combine(Environment.CurrentDirectory, "voice-pipeline.json"),
        Path.Combine(AppContext.BaseDirectory, "voice-pipeline.json"),
    ];

    return candidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException(
            "No voice-pipeline.json found. Pass --config <path> or run from a directory that contains one.");
}

// Overrides exist for the settings worth changing between two consecutive runs. Everything else is
// deliberately config-only, so the file stays the single description of a deployment.
void ApplyOverrides(VoicePipelineConfig target, Dictionary<string, string> overrides)
{
    if (overrides.GetValueOrDefault("llm-url") is { Length: > 0 } url)
    {
        target.Llm.BaseUrl = url;
    }

    if (overrides.GetValueOrDefault("model") is { Length: > 0 } model)
    {
        target.Llm.Model = model;
    }

    if (overrides.GetValueOrDefault("persona") is { Length: > 0 } persona)
    {
        target.Llm.Persona = persona;
    }

    if (overrides.GetValueOrDefault("voice") is { Length: > 0 } voice)
    {
        target.Tts.Voice = voice;
        target.Tts.LanguageCode = null;     // let the voice's language be re-derived
    }

    if (overrides.GetValueOrDefault("language") is { Length: > 0 } language)
    {
        target.Stt.Language = language;
    }

    if (double.TryParse(overrides.GetValueOrDefault("speed"), out var speed))
    {
        target.Tts.Speed = speed;
    }

    if (double.TryParse(overrides.GetValueOrDefault("vad-threshold"), out var threshold))
    {
        target.Vad.Threshold = threshold;
    }

    if (int.TryParse(overrides.GetValueOrDefault("device"), out var device))
    {
        target.Audio.InputDeviceNumber = device;
    }

    if (overrides.ContainsKey("metrics"))
    {
        target.EnableMetrics = true;
    }
}

void PrintUsage() => Console.WriteLine(
    """
    Config-driven speech-to-speech loop.

      --config <path>        Configuration file (default: ./voice-pipeline.json)
      --llm-url <url>        Override llm.baseUrl
      --model <name>         Override llm.model
      --persona <text>       Override llm.persona
      --voice <id>           Override tts.voice
      --language <code>      Override stt.language
      --speed <factor>       Override tts.speed
      --vad-threshold <n>    Override vad.threshold
      --device <index>       Override audio.inputDeviceNumber
      --metrics              Enable per-turn latency metrics
      --verbose              Debug-level logging
    """);

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
