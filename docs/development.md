# Development and Testing

## Solution layout

```text
/
  Directory.Build.props
  SpeechToSpeech.sln
  src/
    SpeechToSpeech.Core/
    SpeechToSpeech.Llm/
    SpeechToSpeech.Stt/
    SpeechToSpeech.Tts/
    SpeechToSpeech.Vad/
  samples/
    VoiceLoopDemo/
  tests/
    SpeechToSpeech.Tests/
  docs/
```

Package versions are pinned centrally in `Directory.Packages.props`; individual project files reference packages without a version.

| Package | Projects | Version |
| --- | --- | --- |
| `Microsoft.Extensions.Logging.Abstractions` | Core and backend libraries as needed | 9.0.0 |
| `Microsoft.ML.OnnxRuntime` | VAD, STT, TTS | 1.20.1 |
| `NAudio` | VoiceLoopDemo | 2.3.0 |
| `Microsoft.Extensions.Logging.Console` | VoiceLoopDemo | 9.0.0 |
| `xunit.v3` | SpeechToSpeech.Tests | 3.2.2 |

## Build checks

Run the full solution check from the repository root:

```powershell
dotnet restore .\SpeechToSpeech.sln
dotnet build .\SpeechToSpeech.sln --configuration Release --no-restore
dotnet test .\SpeechToSpeech.sln --configuration Release --no-build
```

`SpeechToSpeech.sln` contains the five library projects, the `VoiceLoopDemo` sample, and the test project. The build treats warnings as errors and runs the .NET analyzers plus the `.editorconfig` style rules, so a style violation fails the build rather than the review.

For a narrow project build:

```powershell
dotnet build .\src\SpeechToSpeech.Llm\SpeechToSpeech.Llm.csproj
```

Do not commit generated `bin/`, `obj/`, package, or publish output.

## Test coverage

The tests in [tests/SpeechToSpeech.Tests](../tests/SpeechToSpeech.Tests) run on xUnit v3.

Current areas cover:

- language-code resolution and speechable-text filtering;
- conservative sentence tokenization;
- JSON Schema signature rendering and text tool-call parsing;
- conversation validation and independent chat copies;
- Whisper feature shape, dynamic range, padding, and a generated test tone;
- LLM output routing to text and TTS queues;
- partial/final transcription notification behavior;
- stale STT input/output filtering across turn revisions.

It does not run ONNX inference, call a network endpoint, phonemize text, or exercise audio hardware. Model integration and the complete threaded pipeline are covered manually by `VoiceLoopDemo`.

At the documented snapshot, the harness executes but reports one known failure: `CJK terminator splits`. `SentenceTokenizer` requires whitespace after punctuation, so `你好。再见。` remains one segment. This is an implementation/test mismatch outside the documentation changes; all other harness checks pass.

When adding a deterministic behavior to Core, LLM, or Whisper preprocessing, add a harness check in the matching section. Keep checks independent of local model files and services unless a separate integration harness is introduced.

## Adding a pipeline stage

1. Define input/output contracts in Core when they cross project boundaries.
2. Derive from `BaseHandler<TIn,TOut>` in the owning backend project.
3. Implement synchronous `Process` iteration.
4. Add `OnSessionEnd` for reusable per-session state.
5. Add `Cleanup` for owned disposable resources.
6. Propagate `TurnId`, `TurnRevision`, and `CancelGeneration` where applicable.
7. Insert a dedicated `PipelineQueue<IPipelineItem>` at the composition root.
8. Add the handler to `PipelineRunner` in pipeline order.
9. Add deterministic smoke checks and an end-to-end sample check.

Do not block indefinitely inside `Process`. Long network operations must honor `StopToken` and a bounded request timeout.

## Adding an STT backend

Derive from `BaseSttHandler` to reuse progressive/final and stale-revision filtering. Consume `VadAudio` and emit:

- `PartialTranscription` for progressive segments;
- `Transcription` for final segments, including language and `SpeechStoppedAtSeconds`.

Document required sample rate and perform conversion before model inference if it differs from the VAD output contract.

## Adding an LLM backend

Prefer deriving from `BaseOpenAiCompatibleLanguageModel` when the backend can be mapped to normalized provider events. Implement payload construction, provider event iteration, warm-up, and compaction generation.

The backend must always allow the base layer to emit `EndOfResponse`, including on parse/provider failure. Check cancellation during stream reads and between provider events. Keep text-only output verbatim and apply speech cleanup only when audio is requested.

## Adding a TTS backend

Derive from `BaseTtsHandler` and implement `Synthesize(TtsInput)`. Yield `AudioChunk` values containing signed 16-bit little-endian mono PCM at the host's configured output rate.

Use `ToChunks` when starting from float audio because it applies fixed block sizing and generation cancellation. A streaming backend should perform equivalent stale-generation checks between reads. Let the base class convert `EndOfResponse` to `AudioResponseDone`.

## State and ownership rules

- One handler instance is consumed by one dedicated thread.
- One stateful ONNX wrapper belongs to its handler unless explicitly designed for concurrency.
- One `RuntimeConfig`, `CancelScope`, and `SpeculativeTurnTracker` should belong to one isolated conversation pipeline.
- Session-end resets state but retains expensive model sessions.
- Pipeline-end releases handler-owned models.
- `Chat.Close` should be called by a long-lived host when the conversation is permanently discarded.

## Logging and diagnostics

Handlers accept `ILogger` and include their handler name in timing/error messages. Work that exceeds the base timing threshold is logged at debug level. Pipeline pools can assign `PipelineIndex` to all handlers so `PipelineLogContext.Prefix` distinguishes concurrent chains.

For audio/model diagnosis:

- inspect ONNX input/output metadata before assuming an export is compatible;
- log sample rate, channel count, and block size at host boundaries;
- use monotonic `Clock.NowSeconds` for cross-stage latency;
- distinguish empty model output from pipeline completion;
- include turn ID, revision, and cancellation generation in race-condition logs.

## Known boundaries

- The C# solution does not currently provide a socket/WebSocket/realtime server host.
- Only Chat Completions is implemented despite the `ResponsesApi` configuration enum.
- Only local Whisper STT is implemented despite the remote STT configuration enum/options.
- The demo is Windows-oriented because of its NAudio capture/playback APIs.
- The smoke harness is deterministic but is not a comprehensive model/network integration suite.
- Linear resampling is suitable for pipeline adaptation but is not a production-grade sample-rate converter.
- XML API documentation generation is disabled; this Markdown set is the maintained C# developer reference.
