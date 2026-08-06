# Speech2Speech.Net

## Documentation map

| Guide | Contents |
| --- | --- |
| [Getting started](docs/getting-started.md) | Prerequisites, model layout, restore, build, and first validation |
| [Architecture](docs/architecture.md) | Project boundaries, pipeline flow, messages, events, cancellation, and lifecycle |
| [Core reference](docs/core-reference.md) | Queues, handlers, conversation state, realtime contracts, and configuration |
| [LLM reference](docs/llm-reference.md) | Chat Completions, streaming, prompts, output processing, compaction, and tools |
| [Audio reference](docs/audio-reference.md) | Silero VAD, Whisper STT, Kokoro TTS, remote TTS, formats, and native dependencies |
| [Voice loop sample](docs/voice-loop-demo.md) | End-to-end microphone sample, options, output, shutdown, and troubleshooting |
| [Development and testing](docs/development.md) | Solution layout, smoke harness, extension points, conventions, and diagnostics |

## Scope and status

The solution targets .NET 9 and contains five libraries, one interactive sample, and one executable smoke-test harness.

| Project | Role |
| --- | --- |
| `SpeechToSpeech.Core` | Pipeline contracts, queues, conversation state, realtime session models, cancellation, and utilities |
| `SpeechToSpeech.Vad` | Streaming Silero voice activity detection |
| `SpeechToSpeech.Stt` | Local Whisper ONNX transcription |
| `SpeechToSpeech.Llm` | OpenAI-compatible Chat Completions and LLM output routing |
| `SpeechToSpeech.Tts` | Local Kokoro and OpenAI-compatible speech synthesis |
| `SpeechToSpeech.Pipeline` | Config-driven assembly of the full loop, plus an optional microphone/speaker host |
| `VoiceLoopDemo` | NAudio speech-to-speech demonstration, uses core libraries |
| `VoicePipelineDemo` | The same loop driven entirely from `voice-pipeline.json` |
| `SpeechToSpeech.Tests` | xUnit unit and regression tests |

The C# solution is a library and local sample implementation. Types for realtime sessions and transports exist in Core so a host can be built around the pipeline.

## Quick commands

From the repository root:

```powershell
dotnet restore .\SpeechToSpeech.sln
dotnet build .\SpeechToSpeech.sln --configuration Release
dotnet test .\SpeechToSpeech.sln --configuration Release
```

Run the interactive sample from the repository root so its default `models/...` paths resolve correctly:

```powershell
dotnet run --project .\samples\VoiceLoopDemo\VoiceLoopDemo.csproj -- `
	--llm-url http://127.0.0.1:39839/v1 `
	--model qwen2.5-1.5b-instruct-openvino-npu:5 `
	--verbose
```

The config-driven sample reads `samples/VoicePipelineDemo/voice-pipeline.json`, where models, voice, language, persona, system instructions and every tuning parameter live. Command-line flags override only the settings worth changing between two runs; `--help` lists them.

```powershell
dotnet run --project .\samples\VoicePipelineDemo\VoicePipelineDemo.csproj -- `
	--config .\samples\VoicePipelineDemo\voice-pipeline.json `
	--llm-url http://127.0.0.1:39839/v1
```

The documented test profile uses Foundry Local CLI with `qwen2.5-1.5b-instruct-openvino-npu:5` on an Intel NPU. See [Getting started](docs/getting-started.md) before running the sample because it also requires .NET 9, model files, Foundry Local, compatible Intel NPU drivers, and Windows audio input/output.
