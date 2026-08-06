# Getting Started

## Supported test profile

The commands in this guide use this Windows test profile:

| Component | Tested value |
| --- | --- |
| .NET | .NET 9 SDK |
| LLM runtime | Foundry Local CLI |
| LLM model ID | `qwen2.5-1.5b-instruct-openvino-npu:5` |
| LLM provider | OpenVINO on an Intel NPU |
| OpenAI-compatible URL | `http://127.0.0.1:39839/v1` |

The exact model ID selects the OpenVINO NPU variant instead of allowing Foundry Local to choose a hardware variant from an alias. It therefore requires compatible Intel NPU hardware and drivers. Use `foundry model info qwen2.5-1.5b-instruct-openvino-npu:5` to inspect its current requirements and license before downloading it.

## Install .NET 9

Install the SDK on Windows:

```powershell
winget install --id Microsoft.DotNet.SDK.9 --exact --accept-package-agreements --accept-source-agreements
```

Open a new terminal after installation, then verify:

```powershell
dotnet --version
dotnet --list-sdks
```

The active SDK must support the `net9.0` target declared in [Directory.Build.props](../Directory.Build.props). If multiple SDKs are installed, any .NET 9 SDK listed by `dotnet --list-sdks` can build this solution.

## Phonemizer

Nothing to install. KokoroSharp phonemizes English natively and ships eSpeak NG binaries for the other languages; both they and all Kokoro voices are copied into the build output automatically.

## Install Foundry Local CLI

Foundry Local provides an on-device OpenAI-compatible service and requires no Azure subscription for this workflow.

```powershell
winget install --id Microsoft.FoundryLocal --exact --accept-package-agreements --accept-source-agreements
```

Open a new terminal, then verify the CLI and daemon:

```powershell
foundry --version
foundry --help
foundry server status
```

If the status command reports a service connection error, restart it:

```powershell
foundry server restart
```

For Intel NPU acceleration, install a driver supported by the Foundry Local OpenVINO execution provider. The model-specific details are shown by `foundry model info`.

## Download and load the test LLM

Confirm that the requested model ID is present in the current catalog:

```powershell
foundry model list --filter alias=qwen*
foundry model info qwen2.5-1.5b-instruct-openvino-npu:5
foundry model info qwen2.5-1.5b-instruct-openvino-npu:5 --license
```

Start the local service on a stable port, keep it running across idle periods, then download and load the exact model:

```powershell
foundry server restart --port 39839 --idle-timeout 0
foundry model download qwen2.5-1.5b-instruct-openvino-npu:5
foundry model load qwen2.5-1.5b-instruct-openvino-npu:5
foundry server status
```

`foundry server status` must report a local service URL on port `39839`. The C# sample uses its OpenAI-compatible `/v1` endpoint and does not require an API key for this local service.

You can test inference independently before starting the voice pipeline:

```powershell
foundry complete qwen2.5-1.5b-instruct-openvino-npu:5 "Reply with exactly: model ready"
```

Foundry Local is currently a preview product. CLI commands and catalog model IDs can change; use `foundry --help`, `foundry model --help`, and `foundry server --help` when upgrading.

## Remaining prerequisites

Required for restore, build, and the smoke harness:

- .NET 9 SDK
- A platform supported by .NET 9 and ONNX Runtime

Additional requirements for `VoiceLoopDemo`:

- Windows audio input and output supported by NAudio's WinMM APIs
- A microphone and speaker or headphones
- Silero VAD, Whisper, and Kokoro model files
- Foundry Local with `qwen2.5-1.5b-instruct-openvino-npu:5` loaded

## Expected model layout

The sample's defaults are resolved relative to the process working directory. Run it from the repository root with this layout:

```text
models/
  silero_vad.onnx
  kokoro-v1.0.onnx
  whisper/
    ggml-base.bin
```

The STT backend is whisper.cpp via Whisper.net, and `ggml-base.bin` is downloaded into `models/whisper/` on first run if it is absent. Pick a different size with `--whisper-size`, or point `--whisper` straight at an existing `.bin`.

The reusable option defaults match the sample's: `TtsOptions.ModelPath` defaults to `models/kokoro-v1.0.onnx`. Voices are not a model asset — KokoroSharp bundles them.

## Restore and build

From the repository root:

```powershell
dotnet restore .\SpeechToSpeech.sln
dotnet build .\SpeechToSpeech.sln --configuration Release --no-restore
```

All projects inherit these settings:

- `TargetFramework`: `net9.0`
- nullable reference types enabled
- implicit global usings enabled
- latest available C# language version
- invariant globalization enabled
- warnings treated as errors, with the .NET analyzers and `.editorconfig` style rules enforced at build time
- XML documentation file generation enabled

Build output is written beneath each project's `bin/<Configuration>/net9.0/` directory. These generated outputs should not be committed.

## Run the tests

```powershell
dotnet test .\SpeechToSpeech.sln --configuration Release
```

The tests do not require model files, audio hardware, or a network service.

## Run the voice loop

From the repository root:

```powershell
dotnet run --project .\samples\VoiceLoopDemo\VoiceLoopDemo.csproj -- `
  --llm-url http://127.0.0.1:39839/v1 `
  --model qwen2.5-1.5b-instruct-openvino-npu:5 `
  --verbose
```

The sample enumerates capture devices, opens device `0` by default, listens at 16 kHz mono PCM, and plays 24 kHz mono PCM. It performs an LLM warm-up request during startup. Press `Ctrl+C` to drain and stop.

Use explicit paths when running from another working directory:

```powershell
dotnet run --project .\samples\VoiceLoopDemo\VoiceLoopDemo.csproj -- `
  --vad C:\models\silero_vad.onnx `
  --whisper C:\models\whisper `
  --kokoro C:\models\kokoro-v1.0.onnx `
  --llm-url http://127.0.0.1:39839/v1 `
  --model qwen2.5-1.5b-instruct-openvino-npu:5
```

See [Voice loop sample](voice-loop-demo.md) for every option and runtime phase.

## Stop or unload Foundry Local

After testing, release NPU and memory resources:

```powershell
foundry model unload qwen2.5-1.5b-instruct-openvino-npu:5
foundry server stop
```

The downloaded model remains in the local cache. Inspect it with `foundry cache list` or locate the cache with `foundry cache location`.
