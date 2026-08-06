# LLM Reference

## Handler model

`SpeechToSpeech.Llm` depends only on Core and logging abstractions. It implements OpenAI-compatible Chat Completions, normalizes provider output, updates conversation history, emits text/tool events, and prepares text for TTS.

### `LanguageModelHandlerOptions`

| Option | Default | Meaning |
| --- | --- | --- |
| `ModelName` | `gpt-4o-mini` | Provider model identifier |
| `Stream` | `true` | Stream Server-Sent Events or parse one JSON response |
| `StreamBatchSentences` | `3` | Complete sentences grouped into each audio chunk |
| `StreamFirstBatchSentences` | `1` | Sentences in the first chunk of a response. Smaller than `StreamBatchSentences` because the first chunk sets time-to-first-audio and TTS synthesizes the whole batch before its first block |
| `EnableLanguagePrompt` | `false` | Append an `Always reply in {language}` rule to the system prompt, using the language STT detected |
| `CompactHistory` | `false` | Summarize old turns when the chat exceeds its soft limit |
| `RequestTimeout` | 20 seconds | Per-request timeout including stream reads |

`VoiceLoopDemo` overrides the batch size to `2`, enables the language prompt, and uses a 60-second timeout.

## Provider normalization

[BaseOpenAiCompatibleLanguageModel.cs](../src/SpeechToSpeech.Llm/BaseOpenAiCompatibleLanguageModel.cs) defines provider-independent events:

- `TextDelta`: incremental assistant text;
- `AssistantMessage`: complete assistant content for history;
- `ToolCall`: a completed function call;
- `Usage`: input/output token totals.

Concrete backends implement three operations:

1. Build a provider request payload from the active `Chat` and tool configuration.
2. Convert the provider response into normalized `ProviderEvent` values.
3. Supply a generation function used by optional history compaction.

The base class owns language prompting, text cleanup, sentence batching, cancellation, turn-revision checks, history write-back, image stripping, compaction triggering, and final response signaling.

## Chat client backend

[ChatClientLanguageModel.cs](../src/SpeechToSpeech.Llm/ChatClientLanguageModel.cs) talks to the OpenAI-compatible endpoint through `Microsoft.Extensions.AI`'s `IChatClient`, backed by the official OpenAI SDK. SSE parsing, tool-call delta accumulation and message serialization come from the library.

Tools are **declared, not invoked**. The realtime host executes them and feeds results back as conversation items, so `UseFunctionInvocation()` and `AIFunctionFactory.Create` are deliberately unused: each tool becomes an `AIFunctionDeclaration` with no body ([DeclaredTool.cs](../src/SpeechToSpeech.Llm/ChatClientBackend/DeclaredTool.cs)), and a returned `FunctionCallContent` is translated straight into a `ProviderEvent.ToolCall` that leaves the pipeline.

Two seams need provider-specific handling:

- Provider rejections arrive as `ClientResultException`, so `RejectionStatusCode` is overridden to read `Status`.
- `chat_template_kwargs.enable_thinking` has no property on the SDK's options type, so [ExtraBodyPolicy.cs](../src/SpeechToSpeech.Llm/ChatClientBackend/ExtraBodyPolicy.cs) merges it into the serialized request body in a per-call pipeline policy. `ExtraBodyPolicy.Build` decides what to merge: `reasoningEffort` takes precedence over `disableThinking`, and nothing is sent to the official OpenAI endpoint, which rejects unknown body keys.

The first streaming update is pulled eagerly inside `RequestEventsAsync` so a provider rejection surfaces where the prompted-tools retry can still rerun the turn, before anything has been spoken.

The backend returns no request when both instructions and conversation input are empty. That condition becomes a closed failed response rather than a hanging pipeline.

For audio responses, the base class removes unspeechable symbols and waits for sentence boundaries. For text-only responses, it preserves provider text, including Markdown and newlines.

## Warm-up and failures

`WarmupAsync()` sends a small request and retries six times with exponential delays. Exhausted warm-up retries are logged but do not prevent startup.

During generation:

- a timeout closes the response and emits a spoken "could you repeat that?" notice, not an error;
- shutdown cancellation closes the response silently;
- provider/parse failures are logged and attached to `EndOfResponse`;
- `EndOfResponse` is emitted even after failure so downstream listening/playback state can recover;
- stale cancellation generations or turn revisions stop output at event boundaries.

## Text and audio output routing

### `SentenceTokenizer`

[SentenceTokenizer.cs](../src/SpeechToSpeech.Llm/SentenceTokenizer.cs) recognizes Western and CJK terminators but conservatively creates a boundary only at end of input or when the punctuation is followed by whitespace. It avoids boundaries for common abbreviations, initials, decimal numbers, and mid-token dots. A trailing incomplete sentence is retained for later accumulation. Consequently, adjacent CJK sentences without spaces, such as `你好。再见。`, currently remain one segment.

### `LlmUtils`

[LlmUtils.cs](../src/SpeechToSpeech.Llm/LlmUtils.cs) maps Whisper language codes to prompt language names and removes symbols unsuitable for speech. Unicode letters and digits are retained; smart quotes are normalized. Cleanup applies to audio output only.

### `LmOutputProcessor`

[LmOutputProcessor.cs](../src/SpeechToSpeech.Llm/LmOutputProcessor.cs) consumes the normalized pipeline messages:

| Input | Audio-path output | Side-channel output |
| --- | --- | --- |
| `LlmResponseChunk` | `TtsInput` when audio is wanted and text is non-empty | `AssistantTextEvent` |
| `TokenUsage` | none | `TokenUsageEvent` |
| `EndOfResponse` | forwarded to TTS | `ResponseFailedEvent` when an error exists |

Turn revisions are checked through the reopen grace window before text or audio is released.

## Prompts

[ChannelPrompts.cs](../src/SpeechToSpeech.Llm/Prompts/ChannelPrompts.cs) builds separate system instructions for voice and text channels.

- Voice instructions favor concise, natural, non-Markdown speech and permit a short spoken lead-in before slow tools.
- Text instructions permit useful Markdown and avoid spoken filler or tool-call preambles.

Session instructions and the generated tool section are inserted between channel-specific lead and tail rules.

## History compaction

[CompactionPrompt.cs](../src/SpeechToSpeech.Llm/Prompts/CompactionPrompt.cs) builds a `Chat.CompactFn`. It renders old messages and tool interactions into a transcript, asks the model for a JSON object with `user_summary` and `assistant_summary`, and tolerates JSON wrapped in a Markdown fence.

Compaction runs through Core's single-flight background mechanism. Errors are logged and leave the original history intact.

## Tool calling

Two transports carry the same tools. There is no user-facing switch: the capability probe picks one, once, per endpoint and model.

| Transport | Behaviour |
| --- | --- |
| Native | Sends `tools`/`tool_choice` and reads `tool_calls` back. Chosen when the probe succeeds. |
| Prompted | Declares the tools as JSON in the system prompt and parses the JSON call object out of `<code>...</code>` in the assistant's own text. Chosen when the probe shows the server does not implement `tools`, and used for the turn when the probe is inconclusive. |

Prompted is kept because it is the only thing that works against a server without tool support, and it costs nothing when native is available — the probe never selects it there.

### Capability probe

The first turn that carries tools is preceded by a one-token request with `tool_choice: "none"` and a single no-op tool. A success means the server parsed the `tools` array; a 400, 404, 422, or 501 means it did not. Anything else (connection refused, 500, timeout) says nothing about tool support: that turn runs prompted, the verdict is not recorded, and the next tool-carrying turn probes again.

The verdict is cached by endpoint and model in `%LOCALAPPDATA%\SpeechToSpeech\tool-support.json`, so later runs against the same local server skip the probe entirely. An inconclusive result is never cached. Every file operation is best-effort: an unwritable or corrupt cache degrades to "probe again", never to a startup failure.

Because the probe settles the question before the payload is built, no user-visible turn is ever spent discovering the answer, and no request is ever retried for this reason.

Both transports deliver identical `ProviderEvent.ToolCall` events, so the output processor, history, and the rest of the pipeline are unaware of which one ran.

### Keeping call syntax out of the speaker

In prompted mode the call arrives inside the same token stream as the spoken reply, and a delimiter can straddle two deltas. `ToolBlockGate` buffers the stream, holds back any tail that is a prefix of `<code>`, and withholds everything between the tags. Without it the sentence tokenizer reads the braces, quotes, and punctuation of the JSON as sentence structure and hands fragments of it to TTS. An unterminated block at end of stream is dropped rather than spoken. The tags are also stripped from the assistant message written to history, so the model is not taught to repeat them.

### Definitions and prompt generation

- `FunctionTool` adapts a realtime `FunctionToolDefinition`.
- `FunctionTool.ToPromptJson` renders it as the same `{"name", "description", "parameters"}` object the native `tools` array carries, so the prompted path shows the model a shape it was trained on and no second schema renderer is needed.
- `ToolPrompt.BuildSystemPrompt` renders those declarations plus the `<code>...</code>` call instructions. The section is folded into the first system message, since local servers commonly collapse or ignore all but the first.

### Parsing text-encoded calls

[FunctionCallParser.cs](../src/SpeechToSpeech.Llm/ToolCall/FunctionCallParser.cs) extracts code blocks, parses the calls with `System.Text.Json`, validates function names and arguments against supplied definitions, and creates realtime call IDs.

Locating the JSON inside the model's prose is a single linear scan over bracket depth outside string literals; each candidate is then handed to `JsonNode.Parse`, and one that fails to parse is skipped rather than failing the turn. Three emitted shapes are accepted, because models drift between them: `{"name", "arguments"}`, the nested `{"type": "function", "function": {...}}` envelope, and `arguments` supplied as an encoded JSON string rather than an object. A top-level array is treated as a list of calls.

Validation rejects unknown functions, missing required parameters, and undeclared arguments when a schema is available. Generated function-call and call IDs use `fc_` and `call_` prefixes.
