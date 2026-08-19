# UserDemo Sample — Quick Start

The `UserDemo` sample demonstrates how to use the **Speech2Speech** NuGet package as an external consumer — no repository source code required, only the published package.

## What it does

Runs an interactive voice-to-voice loop:
1. Listens to your microphone
2. Transcribes speech to text (Whisper STT)
3. Sends the transcript to an LLM (OpenAI-compatible API)
4. Synthesizes the response back to audio (Kokoro TTS)
5. Plays it through your speaker

All in real time, with live transcription and level metering.

## Prerequisites

### 1. Windows with audio devices
The sample uses NAudio, which is Windows-only. Requires:
- A microphone and speaker connected and enabled as default devices
- Windows audio service running: `Get-Service Audiosrv | Select-Object Status`

### 2. Model files
The sample does not ship weights. Download or symlink three model files:

```
models/
  silero_vad.onnx          (VAD model, ~40 MB)
  kokoro-v1.0.onnx         (TTS model, ~500 MB)
  whisper/                 (STT weights, auto-download or pre-download)
    config.json
    encoder_model.onnx
    decoder_model.onnx
    ...
```

### 3. Whisper.net.Runtime companion package

**Critical:** The Speech2Speech package depends on `Whisper.net`, but not on the native runtime binaries. You must explicitly install one of:

- `Whisper.net.Runtime` — CPU-based (slowest, always works)
- `Whisper.net.Runtime.Cuda` — NVIDIA GPU (requires CUDA Toolkit)
- `Whisper.net.Runtime.CoreML` — Apple Neural Engine (macOS only)

For this sample:
```bash
dotnet add package Whisper.net.Runtime --version 1.9.1
```

**Why is this required?**  
The Speech2Speech NuGet package includes the managed `Whisper.net` library, but the native binaries (ONNX Runtime, ggml, etc.) are delivered via `Whisper.net.Runtime` build targets. Without explicitly referencing the runtime package, the build cannot access these natives and fails at runtime with:
```
FileNotFoundException: Native Library not found in default paths.
```

See [Whisper.net documentation](https://github.com/sandrohanea/whisper.net?tab=readme-ov-file#runtime-nuget-packages) for runtime-specific options (GPU, Apple Silicon, etc.).

### 4. An OpenAI-compatible LLM endpoint

The default configuration expects a local API at `http://localhost:53488/v1` with a model named `qwen2.5-1.5b-instruct-openvino-npu:5`.

**Options:**
- **Foundry Local** (documented setup) — run your own LLM locally via Foundry
- **OpenAI API** — set `Llm.BaseUrl = "https://api.openai.com/v1"` and `Llm.ApiKeyEnvironmentVariable = "OPENAI_API_KEY"`
- **Any OpenAI-compatible server** — LM Studio, Ollama, Replicate, etc.

## Installation

```bash
cd samples/UserDemo

# Install the NuGet packages
dotnet add package Speech2Speech --version 0.0.1-alpha.0.1
dotnet add package Whisper.net.Runtime --version 1.9.1

# Restore
dotnet restore
```

Or edit `UserDemo.csproj` directly:
```xml
<ItemGroup>
  <PackageReference Include="Speech2Speech" Version="0.0.1-alpha.0.1" />
  <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
</ItemGroup>
```

## Running

```bash
# From the repo root, pass the path to your downloaded models
dotnet run --project samples/UserDemo/UserDemo.csproj -- path/to/models

# Or from samples/UserDemo/ directly
dotnet run -- ../../models
```

## What you'll see

```
Models: C:\Users\you\speech2speech\models
Listening on 'Microphone (Realtek High Definition Audio)'.
Speak into the microphone. Ctrl+C to stop.
you : what time is it
amy : It's approximately 2:45 PM.
you : can you tell me a joke
amy : Why don't scientists trust atoms? Because they make up everything!
```

Press `Ctrl+C` to stop gracefully — the in-flight response will finish speaking before exit.

## Troubleshooting

### `FileNotFoundException: Native Library not found`
✗ Missing `Whisper.net.Runtime`.  
✓ Run: `dotnet add package Whisper.net.Runtime --version 1.9.1`  
✓ If using GPU, use `Whisper.net.Runtime.Cuda` instead.

### `MmException: UnspecifiedError calling waveOutOpen`
✗ No default audio output device.  
✓ Check Windows Settings → Sound → Output, and enable a device as Default.  
✓ If on RDP, enable audio redirection in the Remote Desktop client.

### `FileNotFoundException: Configuration file not found` or models not found
✗ Model path is wrong or relative to the wrong directory.  
✓ Use an absolute path: `dotnet run -- C:\full\path\to\models`

### `HttpRequestException: No connection could be made... port 53488`
✗ The LLM endpoint is not running.  
✓ Start Foundry Local, or change `Llm.BaseUrl` in [Program.cs](../samples/UserDemo/Program.cs#L15) to point at your endpoint.

### Slow transcription or garbled audio
✗ Likely CPU bottleneck; Whisper base model on CPU is ~8 seconds per 30-second clip.  
✓ Switch to `Whisper.net.Runtime.Cuda` if you have an NVIDIA GPU.  
✓ Reduce `Stt.ModelSize` to `"tiny"` or `"small"` for faster results.

## Customization

Edit the configuration in [Program.cs](../samples/UserDemo/Program.cs):

```csharp
config.Tts.Voice = "bm_fable";              // Change to "am_michael", "bf_emma", etc.
config.Llm.Persona = "You are ...";         // Change the assistant's personality
config.Llm.SystemInstructions = "Answer in under 20 words.";  // Tune response length
config.Audio.EnableLevelMeter = false;      // Disable live level meter
```

See [VoicePipelineConfig](../src/SpeechToSpeech.Pipeline/VoicePipelineConfig.cs) for the full configuration surface.

## Next steps

- **Production deployment**: Deploy the package via your artifact feed or nuget.org, and consume it in your own app.
- **Different runtime**: Try `Whisper.net.Runtime.Cuda` for GPU acceleration, or `CoreML` on macOS.
- **Custom LLM**: Point at a different endpoint or use OpenAI's API.
- **Advanced configuration**: Load settings from a JSON file using `VoicePipelineConfig.Load("config.json")`.
