# Core Reference

## Pipeline infrastructure

### `IPipelineHandler`

The non-generic lifecycle interface used by `PipelineRunner`:

- `Run()` executes the stage loop.
- `Stop()` cancels the handler.
- `PipelineIndex` adds a pool index to logs.
- `Metrics` attaches a `TurnMetrics` recorder; `null` disables instrumentation.

### `BaseHandler<TIn,TOut>`

[BaseHandler.cs](../src/SpeechToSpeech.Core/BaseHandler.cs) provides the common stage loop.

Subclass extension points:

| Member | Responsibility |
| --- | --- |
| `Process(TIn)` | Transform one typed input into zero or more outputs |
| `ShouldProcessInput(TIn)` | Reject an input before processing |
| `ShouldEmitOutput(TOut)` | Reject an output before enqueueing |
| `BeforeEmitOutput(TOut)` | Record state immediately before enqueueing |
| `OnSessionEnd()` | Reset state without unloading the stage |
| `Cleanup()` | Release resources when the run loop exits |

The base loop recognizes `PipelineControlMessage.SessionEnd` and `SentinelMessage.PipelineEnd` before type dispatch. It awaits the input queue rather than polling it, so an idle stage costs nothing and a queued item is picked up immediately instead of on the next tick. `AudioChunk` output is tagged with the active cancellation generation.

A stage supplies its body as either `Process` (synchronous `IEnumerable`, for CPU-bound work such as ONNX inference) or `ProcessAsync` (`IAsyncEnumerable`, for I/O-bound work such as HTTP streaming). The default `ProcessAsync` adapts `Process`, so only the language-model stage overrides it.

### `PipelineQueue<T>`

[PipelineQueue.cs](../src/SpeechToSpeech.Core/Pipeline/PipelineQueue.cs) is a monitor-based FIFO built on `LinkedList<T>`.

| API | Behavior |
| --- | --- |
| `Put` | Appends and wakes waiters |
| `TakeAsync(cancellationToken)` | Awaits the first item without occupying a thread |
| `TryTake(timeout)` | Waits up to the supplied timeout for the first item |
| `TryTakeNow` | Non-blocking take |
| `RemoveWhere` | Atomically removes matching entries |
| `Any` | Inspects under the queue lock |
| `WithLock` | Runs custom logic while holding the internal lock |
| `Clear` | Removes all entries |
| `Count`, `IsEmpty` | Lock-protected snapshots |

The mutation APIs support STT's requirement to drop stale progressive audio already queued behind a final segment. This queue has no asynchronous API and should not be awaited.

### Control and sentinels

- `PipelineControlMessage.SessionEnd` resets reusable stage state.
- `SentinelMessage.PipelineEnd` terminates and cascades through the chain.
- `SentinelMessage.AudioResponseDone` marks the end of one synthesized response.

### `TurnMetrics`

[TurnMetrics.cs](../src/SpeechToSpeech.Core/Pipeline/TurnMetrics.cs) attributes response latency to a
stage. Assign one shared instance to every handler's `Metrics` property and each stage records the
moment it first produced output for a turn; the turn is rendered as a single log line when the TTS
stage sees its `EndOfResponse`.

| API | Behavior |
| --- | --- |
| `Anchor(turnId, atSeconds)` | Sets the speech-stop reading all deltas are measured from; first call wins |
| `Mark(turnId, stage)` | Records a stage's first output; later calls for the same stage are dropped |
| `Complete(turnId)` | Logs and forgets the timeline |

Most marks are taken automatically in `BaseHandler`, keyed on the emitted message's `Tag`, with the
anchor read from whichever message first carries `SpeechStoppedAtSeconds`. Stages whose output leaves
the typed-message world take their own mark: TTS emits bare `AudioChunk`, and the language model adds
`request-sent` and `first-text` so provider time is separable from queue time.

A turn with no anchor, or whose marks all predate it, is not reported: it belongs to the window where
the user was still speaking. Timelines are capped at eight turns, because a turn dropped by a barge-in
never reaches `Complete`.

## Conversation model

### `Chat`

[Chat.cs](../src/SpeechToSpeech.Core/Conversation/Chat.cs) stores a bounded, validated conversation.

Important operations:

| API | Use |
| --- | --- |
| `InitChat` | Sets the system item, which is stored separately and never evicted |
| `AddItem` | Validates and appends messages, function calls, or function outputs |
| `AppendToolOutput` | Adds a tool result and restores its call if that call was evicted |
| `ReplaceUserMessageText` | Revises a speculative user transcript |
| `RemoveUserMessage` | Removes a rejected speculative user message |
| `ToProviderHistory` | Converts items to OpenAI-compatible message objects |
| `TrimIfNeeded` | Evicts old turns or starts background compaction |
| `StripImages` | Removes image parts after provider consumption |
| `Copy` | Creates an independent conversation snapshot |
| `Reset` | Clears session state and invalidates compaction work |
| `Close` | Permanently cancels compaction and closes the instance |

Validation rules include:

- Message items require a role.
- Function calls require a `call_`-prefixed `CallId`.
- Empty text/image parts are filtered.
- The soft history limit is `Size` user turns.
- A hard safety limit rejects growth beyond twice `Size`.
- Function-call/output pairing is preserved across eviction.

When a `CompactFn` is supplied, compaction is single-flight on a background task. It excludes the latest user turn, replaces old history with user/assistant summaries, and ignores stale results after reset.

### Conversation contracts

[SessionTypes.cs](../src/SpeechToSpeech.Core/Realtime/SessionTypes.cs) defines:

- `ContentPart`: text, transcript, image, or audio content.
- `ConversationItem`: message, function call, or function-call output.
- `FunctionToolCall`: completed call emitted by a model.
- `FunctionToolDefinition`: name, description, and JSON Schema parameters.
- `SessionCreateRequest`: persistent session configuration.
- `ResponseCreateParams`: one-response overrides.

`ChatMessages` provides factories for system, user, and assistant text messages. `ChatFactory` validates supported client items and builds normal or out-of-band active chats.

## Realtime configuration

### `RuntimeConfig`

[RuntimeConfig.cs](../src/SpeechToSpeech.Core/Realtime/RuntimeConfig.cs) is mutable shared session state. It guarantees non-null audio input/output structures and merges partial updates field by field.

`InterruptResponseEnabled` reads `session.audio.input.turn_detection.interrupt_response`. Handlers should read the current session at processing time rather than caching mutable nested objects.

### Session defaults

The realtime contracts default to PCM audio and a 24 kHz output format. `TurnDetectionConfig` exposes threshold, prefix padding, silence duration, automatic response creation, and interruption behavior. A hosting layer is responsible for validating formats against the configured pipeline.

## Configuration option classes

[StageOptions.cs](../src/SpeechToSpeech.Core/Configuration/StageOptions.cs) provides the DTOs each pipeline stage is constructed from.

- `VadOptions`: model path, threshold, sample rate, minimum silence/speech, padding, realtime intervals, and reopen windows.
- `SttOptions`: Whisper weights path, language, and final revision settle time.
- `TtsOptions`: Kokoro model path, optional voices directory, voice, speed, input sample rate, and delivered sample rate.

The LLM stage is configured separately through `LanguageModelHandlerOptions`, documented in [LLM reference](llm-reference.md).

## Cancellation and speculative turns

### `CancelScope`

[CancelScope.cs](../src/SpeechToSpeech.Core/Pipeline/CancelScope.cs) exposes `Generation`, `IsStale`, `Cancel`, `NewResponse`, `ResponseDone`, and `Reset`. Capture the generation when creating a response and propagate it on every cancellable message.

### `SpeculativeTurnTracker`

[SpeculativeTurnTracker.cs](../src/SpeechToSpeech.Core/Pipeline/SpeculativeTurnTracker.cs) supports:

- observing and committing monotonically increasing revisions;
- opening, confirming, or cancelling a reopen candidate;
- waiting for a pending reopen or grace period;
- stability-window checks;
- non-blocking `Try...` forms for routing code;
- bounded pruning and full reset.

Use one tracker per isolated pipeline/session, not a global tracker shared across unrelated conversations.

## Utilities

| Type | Purpose |
| --- | --- |
| `PipelineRunner` | Starts, awaits, and stops handler tasks |
| `PipelineLogContext` | Adds an optional pipeline-pool index through `AsyncLocal` |
| `Ids` | Generates prefixed GUID-based IDs |
| `ResponseSemantics` | Determines audio output and out-of-band responses |
| `AudioConvert` | PCM16/float conversion and linear resampling |
| `Clock` | Monotonic seconds for cross-stage timestamps |
