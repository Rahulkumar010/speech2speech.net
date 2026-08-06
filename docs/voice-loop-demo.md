# Voice Loop Demo

`VoiceLoopDemo` is the C# composition example. It captures a microphone, detects utterances, transcribes locally, calls an OpenAI-compatible LLM, synthesizes locally with Kokoro, and plays the response.

The implementation is in [Program.cs](../samples/VoiceLoopDemo/Program.cs).

## Platform and dependencies

The sample uses NAudio `WaveInEvent` and `WaveOutEvent`, so its interactive capture/playback path is Windows-oriented. The class libraries remain usable on other platforms where their managed and native dependencies are available.

Required runtime inputs:

- 16 kHz-capable capture device;
- playback device;
- Silero, Whisper, and Kokoro model files;
- Foundry Local's OpenAI-compatible endpoint with the test model loaded.

Kokoro voices and the eSpeak NG binaries ship with the KokoroSharp package and are copied next to the executable at build time, so neither has to be installed or downloaded.

The documented test configuration is:

```text
Foundry Local URL: http://127.0.0.1:39839/v1
Model ID:          qwen2.5-1.5b-instruct-openvino-npu:5
Execution target:  Intel NPU through OpenVINO
```

See [Getting started](getting-started.md) for installation, driver, model download, and service startup commands.

## Command-line options

The parser accepts case-insensitive `--name value` pairs and flag-only options. Unknown options are retained but ignored.

| Option | Default | Description |
| --- | --- | --- |
| `--vad` | `models/silero_vad.onnx` | Silero ONNX file |
| `--vad-threshold` | `0.6` | Speech probability threshold |
| `--whisper` | `models/whisper` | ggml weights: a `.bin` file, or a directory to search and download into |
| `--whisper-size` | `base` | ggml model to download when none is present: `Tiny`, `TinyEn`, `Base`, `BaseEn`, `Small`, `SmallEn`, `Medium`, `MediumEn`, `LargeV1`, `LargeV2`, `LargeV3`, `LargeV3Turbo` |
| `--llm-url` | `http://localhost:65466/v1` | OpenAI-compatible base URL |
| `--model` | `qwen2.5-1.5b-instruct-openvino-npu:5` | LLM model identifier |
| `--demo-tools` | absent | Declare two sample tools, so the capability probe and the tool-call path are exercised |
| `--kokoro` | `models/kokoro-v1.0.onnx` | Kokoro ONNX file |
| `--voices` | bundled | Honoured only for a directory of KokoroSharp `.npy` voices; a packed `.bin` is ignored in favour of the bundled voices |
| `--voice` | `bm_fable` | Initial Kokoro voice |
| `--device` | `0` | NAudio capture-device index |
| `--metrics` | absent | Log a per-turn stage-latency breakdown |
| `--verbose` | absent | Enable debug-level logging |

The source-code defaults remain `http://localhost:11434/v1` and `qwen2.5:3b` for compatibility with existing OpenAI-compatible services. The documented test command overrides both with Foundry Local and `qwen2.5-1.5b-instruct-openvino-npu:5`.

`OPENAI_API_KEY` is the only environment variable read by the sample. Leave it unset for Foundry Local. For a different authenticated endpoint, the value is sent as a Bearer token.

Tested Foundry Local command:

```powershell
dotnet run --project .\samples\VoiceLoopDemo\VoiceLoopDemo.csproj -- `
  --llm-url http://127.0.0.1:39839/v1 `
  --model qwen2.5-1.5b-instruct-openvino-npu:5 `
  --device 1 `
  --vad-threshold 0.7 `
  --verbose
```

## Runtime configuration

The sample configures:

- VAD at 16 kHz with 300 ms minimum silence and progressive transcription;
- Whisper automatic language detection with a 150 ms final revision settle window;
- streaming LLM output batched every two complete sentences;
- a language reply prompt and 60-second request timeout;
- Kokoro at 24 kHz with 1200-sample output blocks, or 50 ms per block;
- a ten-user-turn `Chat`;
- audio-only responses and an under-20-word assistant instruction.

These are sample-specific choices, not all library defaults.

## Startup sequence

1. Parse arguments and configure console logging.
2. Create shared cancellation, listening, speculative-turn, and session state.
3. Create one queue between every stage plus text and audio terminal queues.
4. Probe and print Silero ONNX input/output metadata.
5. Load Silero and Whisper; create the LLM `HttpClient`.
6. Construct VAD, STT, notifier, LLM, output processor, and Kokoro handlers.
7. Attach one `CancelScope` to VAD, LLM output processing, and TTS.
8. Warm the LLM.
9. Start six handler threads.
10. Start speaker playback, output printing, and microphone capture.

Kokoro warms itself during handler construction. Model loading and warm-up may make startup noticeably slower than later turns.

## Capture and playback

The microphone is opened as 16 kHz, mono, 16-bit PCM with nominal 32 ms buffers. Because WinMM callback sizes are not guaranteed, the sample carries partial data until exactly 512 samples are available, then enqueues one `AudioChunk`.

The output loop:

- prints events from the side-channel;
- mutes pipeline listening while synthesized audio is playing;
- writes PCM blocks to a 30-second NAudio buffer;
- waits for the playback buffer to drain after `AudioResponseDone`;
- re-enables listening afterward.

This mute/drain behavior prevents the demo from transcribing its own speaker output. It also means the sample does not demonstrate acoustic echo cancellation. Headphones are recommended when tuning or modifying the listening behavior.

## Console output

| Prefix | Meaning |
| --- | --- |
| `[mic ]` | Input RMS in dBFS, listening state, or mute state |
| `[silero]` | One-second VAD probability diagnostics |
| `[vad]` | Speech boundary |
| `[stt~]` | Partial transcript |
| `[stt ]` | Final transcript and language |
| `[llm ]` | Assistant text |
| `[tool]` | Completed tool call |
| `[cost]` | Input/output token usage |
| `[tts ]` | Synthesized response boundary and sample count |
| `[fail]` | Generation failure |

The sample logs every capture device at startup. Use its index with `--device`.

## Stage latency

`--metrics` logs one line per turn, measured from the moment the user stopped speaking:

```
turn 7f3a: 1.842 s from speech stop | VadHandler/vad_audio:Final +0.004 (@0.004) | WhisperNetSttHandler/transcription +0.412 (@0.416) | TranscriptionNotifier/generate_response +0.001 (@0.417) | ChatClientLanguageModel/request-sent +0.002 (@0.419) | ChatClientLanguageModel/first-text +0.394 (@0.813) | ChatClientLanguageModel/llm_response_chunk +0.286 (@1.099) | LmOutputProcessor/tts_input +0.002 (@1.101) | KokoroOnnxTtsHandler/audio +0.741 (@1.842)
```

Each entry is the first output a stage produced for that turn: `+` is the time since the previous
stage, `@` is the time since speech stopped. The stage with the largest `+` is the one to attack.
Reading the example above: Whisper cost 0.41 s, the model took 0.39 s to its first token and another
0.29 s to complete the first speakable sentence, and Kokoro cost 0.74 s to the first audio block.

Two entries are not stage outputs but internal split points, so provider time is not confused with
queue time:

| Mark | Meaning |
| --- | --- |
| `request-sent` | The HTTP request left the LLM handler. The gap before it is queue wait, the gap after it is the model. |
| `first-text` | The first token came back. The gap after it is sentence buffering before TTS can start. |

Turn totals end at the first audio block, not at playback, and marks recorded while the user was
still speaking are excluded. Turns abandoned by a barge-in are never reported.

## Shutdown

`Ctrl+C` is intercepted rather than terminating immediately:

1. Microphone capture stops.
2. Forty silent 512-sample frames are queued, giving VAD 1.28 seconds to close the last utterance.
3. The process waits ten seconds for in-flight response work.
4. `PipelineEnd` is put into the audio queue and cascades through all stages.
5. The output printer drains until the terminal sentinel.
6. Handler threads are stopped/joined.
7. Final conversation items are printed.

The fixed ten-second drain is demonstration logic, not a general completion guarantee. Production hosts should track response completion and cancellation explicitly.

## Troubleshooting

### Model file errors

Run from the repository root or pass absolute model paths. Verify Whisper has both encoder and decoder ONNX files plus `vocab.json`.

### Voice or phonemizer errors

The voices and eSpeak NG binaries are copied to the build output by the KokoroSharp package. If TTS reports a missing voice, rebuild rather than hunting for a download: `voices/` and `espeak/` should both be present next to `VoiceLoopDemo.exe`.

### LLM connection or warm-up failures

Confirm the base URL includes the API prefix expected by the service. The handler appends `chat/completions`. For the documented setup, run `foundry server status` and confirm port `39839`, then run `foundry model info qwen2.5-1.5b-instruct-openvino-npu:5`. Restart an inaccessible service with `foundry server restart --port 39839 --idle-timeout 0` and reload the model. An API key is not required for Foundry Local.

### OpenVINO NPU model is unavailable

The exact test model requires compatible Intel NPU hardware, current drivers, and a catalog entry available to the installed Foundry Local version. Inspect NPU candidates with:

```powershell
foundry model list --filter device=NPU
foundry model list --filter provider=OpenVINOExecutionProvider
```

If the exact model is absent, update Foundry Local with `winget upgrade --id Microsoft.FoundryLocal`, then check the catalog again. Do not silently replace the model ID when reproducing this documented test profile.

### No speech is detected

Watch `[mic ]` dBFS and `[silero]` peak values. Verify the capture-device index, microphone permissions, and 16 kHz support. Lower `--vad-threshold` only after confirming live audio levels.

### The assistant hears itself

Use headphones. If the issue follows code changes, verify `shouldListen` is reset before speaker buffering and set only after the playback buffer drains.

### Playback is distorted or too fast

The speaker is configured for 24 kHz mono PCM. Ensure the TTS handler's `OutputSampleRate` and the `WaveFormat` rate remain identical.
