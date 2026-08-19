using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Pipeline;

// The package ships code, not weights: point this at your own downloaded model files.
var models = Path.GetFullPath(args.FirstOrDefault() ?? "models");
Console.WriteLine($"Using models from: {models}");

var config = new VoicePipelineConfig();
config.Vad.ModelPath = Path.Combine(models, "silero_vad.onnx");
config.Stt.ModelPath = Path.Combine(models, "whisper");
config.Tts.ModelPath = Path.Combine(models, "kokoro-v1.0.onnx");
config.Tts.Voice = "bm_fable";
config.Llm.BaseUrl = "http://localhost:58466/v1";
config.Llm.Model = "qwen2.5-1.5b-instruct-openvino-npu:5";
config.Llm.Persona = "You are Amy, a calm voice assistant with a dry sense of humour.";
config.Llm.SystemInstructions = "Answer in under 20 words. Speak plainly: no lists, no markdown, no emoji.";
config.Validate();

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                       // let the in-flight reply finish speaking
    stopping.Cancel();
};

await using var host = await VoiceLoopHost.CreateAsync(config, loggerFactory, stopping.Token);

host.EventReceived += pipelineEvent =>
{
    switch (pipelineEvent)
    {
        case TranscriptionCompletedEvent transcript:
            Console.WriteLine($"you : {transcript.Transcript}");
            break;
        case AssistantTextEvent reply:
            Console.WriteLine($"amy : {reply.Text}");
            break;
        case ResponseFailedEvent failed:
            Console.Error.WriteLine($"fail: {failed.Message}");
            break;
    }
};

Console.WriteLine($"Models: {models}");
Console.WriteLine("Speak into the microphone. Ctrl+C to stop.");

await host.RunAsync(stopping.Token);
return 0;