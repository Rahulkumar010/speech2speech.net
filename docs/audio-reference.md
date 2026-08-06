# Audio Reference

## Audio contracts

| Boundary | Format |
| --- | --- |
| Demo microphone to VAD | 16 kHz, mono, signed 16-bit little-endian PCM, 512 samples per frame |
| VAD to STT | 16 kHz mono normalized `float[]` |
| Whisper feature input | 16 kHz audio padded/trimmed to 30 seconds |
| Kokoro model output | 24 kHz mono `float[]` |
| TTS pipeline output | Mono signed 16-bit little-endian PCM at `OutputSampleRate` |

`AudioConvert` performs PCM16/float conversion and linear sample-rate conversion. It does not perform channel mixing, codec decoding, or high-quality band-limited resampling.

## Voice activity detection

### `SileroVadOnnxModel`

[SileroVadOnnxModel.cs](../src/SpeechToSpeech.Vad/SileroVadOnnxModel.cs) wraps Silero VAD v5 through ONNX Runtime.

- Supports 8 kHz and 16 kHz sample rates.
- Maintains recurrent state and an audio context window between calls.
- Probes common input/output names to tolerate compatible export variants.
- Configures one intra-op and one inter-op inference thread.
- `ResetStates()` clears recurrent state between sessions/utterance contexts.
- `Dispose()` releases the inference session.

The `IVadModel` interface makes the state machine testable and permits alternate inference implementations.

### `VadIterator`

[VadIterator.cs](../src/SpeechToSpeech.Vad/VadIterator.cs) converts frame probabilities into utterances.

- Speech begins at `Threshold`.
- Once triggered, a 0.15 release margin prevents brief probability dips from ending speech.
- A prefix ring buffer retains pre-speech padding.
- Sustained silence finalizes and returns the accumulated audio.
- `Triggered`, active sample counts, and the current speech buffer expose state to `VadHandler`.

### `VadHandler`

[VadHandler.cs](../src/SpeechToSpeech.Vad/VadHandler.cs) adds pipeline behavior:

- reads per-session turn-detection overrides;
- honors the shared `shouldListen` gate;
- emits speech-started and speech-stopped events;
- emits progressive audio while speech is active;
- emits final audio after silence and minimum-speech checks;
- coordinates speculative continuation/reopen revisions;
- resets iterator and turn state on session end;
- disposes the VAD model during cleanup.

Relevant `VadOptions` defaults are 16 kHz, threshold `0.6`, 64 ms minimum silence, 384 ms minimum speech, and 30 ms speech padding. The sample deliberately changes minimum silence to 300 ms.

## Whisper speech-to-text

### Backing library: Whisper.net

Transcription runs whisper.cpp through [Whisper.net](https://github.com/sandrohanea/whisper.net). Feature extraction, tokenization and decoding all live in the native library, so there is no log-mel or GPT-2 byte-level decoding code in this repository.

`Whisper.net.Runtime` ships CPU binaries that require AVX/AVX2/FMA/F16C. On older hardware, swap the package for `Whisper.net.Runtime.NoAvx`.

### `GgmlModelResolver`

[GgmlModelResolver.cs](../src/SpeechToSpeech.Stt/Whisper/GgmlModelResolver.cs) turns the configured path into a ggml `.bin`: an existing file is used as-is, a directory is searched for `*.bin`, and otherwise the requested `GgmlType` is downloaded once to a `.partial` file and moved into place so an interrupted download cannot masquerade as valid weights.

### STT handlers

`BaseSttHandler` filters stale revisions, suppresses progressive work when a final segment for the same revision is already queued, and records completed final revisions.

`WhisperNetSttHandler` emits `PartialTranscription` for progressive audio and `Transcription` for final audio. One `WhisperProcessor` is reused for the lifetime of the pipeline, built `WithNoContext()` so an utterance is never prompted with the previous one, and with language detection unless `SttOptions.Language` pins a code. Multi-segment output for a long utterance is joined into a single transcript. The processor and factory are disposed on pipeline cleanup.

[TranscriptionNotifier.cs](../src/SpeechToSpeech.Stt/TranscriptionNotifier.cs) is the bridge to clients and the LLM:

- partial text becomes `PartialTranscriptionEvent` only;
- final text becomes `TranscriptionCompletedEvent`;
- a non-empty final transcript is added to chat and becomes `GenerateResponseRequest`;
- an empty final transcript does not call the LLM and re-enables listening.

## Kokoro text-to-speech

### Backing library: KokoroSharp

Phonemization, the token vocabulary, text normalization and voice loading come from [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp) rather than from hand-written equivalents in this repository.

There is **no external `espeak-ng` install to manage**. KokoroSharp phonemizes English natively through its misaki port, and ships eSpeak NG binaries for the other languages; the NuGet package copies them, together with all 164 voices, next to the built executable.

`KokoroLanguages` (still local) maps Whisper language codes to Kokoro language letters, default voices, and eSpeak voice names. Unsupported languages fall back to British English behavior.

### Voices

Voices are the `.npy` files KokoroSharp copies into `voices/` in the output directory, loaded through `KokoroVoiceManager`.

`TtsOptions.VoicesPath` is honoured only when it points at a **directory** in that same `.npy` layout. A packed `voices-v1.0.bin` is not a format KokoroSharp reads, so such a path is ignored and the bundled voices are used. The flag still parses, so existing command lines keep working.

### `KokoroOnnxModel`

[KokoroOnnxModel.cs](../src/SpeechToSpeech.Tts/Kokoro/KokoroOnnxModel.cs) converts text to 24 kHz mono float audio:

1. `Tokenizer.Tokenize` normalizes, phonemizes and encodes the text in one step.
2. Look up the voice's style tensor by name.
3. Run `KokoroModel.Infer` with token IDs, style, and speed.
4. Return the waveform.

Input longer than the model's 510-token context is split with `SegmentationSystem.SplitToSegments` and the segments are concatenated, rather than being silently truncated. The pipeline feeds sentence-sized text, so this is a guard rather than the normal path.

An empty phoneme sequence produces empty audio. `HasVoice` can validate a configured voice before synthesis.

The lower-level `KokoroModel` is used instead of the `KokoroTTS` facade because the facade owns job scheduling and audio playback, both of which this pipeline already provides and neither of which can be driven by its speculative-turn cancellation.

### `KokoroOnnxTtsHandler`

[KokoroOnnxTtsHandler.cs](../src/SpeechToSpeech.Tts/KokoroOnnxTtsHandler.cs) performs warm-up, language/voice switching, silence trimming, optional resampling, fixed-block chunking, and per-block cancellation checks. Session end restores the initial language and voice.

## Shared TTS behavior

[BaseTtsHandler.cs](../src/SpeechToSpeech.Tts/BaseTtsHandler.cs) handles both backends:

- Per-response voice overrides session voice.
- `EndOfResponse` becomes `SentinelMessage.AudioResponseDone`.
- Stale speculative revisions are dropped.
- Audio is zero-padded to fixed-size final blocks.
- Mid-utterance barge-in is checked between blocks.

For output consumers, `AudioResponseDone` is the authoritative response boundary; an empty or failed response may still produce that marker without audio chunks.

## Resource ownership

ONNX model wrappers implement `IDisposable`. Their owning handlers dispose them in `Cleanup`, which runs after the handler loop exits. Do not share one stateful Silero model instance across concurrent VAD handlers. Use one model/handler chain per isolated realtime pipeline.
