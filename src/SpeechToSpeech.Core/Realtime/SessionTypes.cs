using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SpeechToSpeech.Core.Realtime;

/// <summary>Content part of a conversation item.</summary>
public sealed class ContentPart
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("transcript")]
    public string? Transcript { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("audio")]
    public string? Audio { get; set; }

    public static ContentPart InputText(string text) => new() { Type = "input_text", Text = text };

    public static ContentPart OutputText(string text) => new() { Type = "output_text", Text = text };

    public ContentPart Clone() => new()
    {
        Type = Type,
        Text = Text,
        Transcript = Transcript,
        ImageUrl = ImageUrl,
        Audio = Audio,
    };
}

public enum ConversationItemType
{
    Message,
    FunctionCall,
    FunctionCallOutput,
}

public enum ConversationRole
{
    System,
    User,
    Assistant,
}

/// <summary>
/// One entry of the conversation; a single tagged record keeps the C# history buffer homogeneous while
/// <see cref="ItemType"/> and <see cref="Role"/> preserve the same distinctions.
/// </summary>
public sealed class ConversationItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public ConversationItemType ItemType { get; set; } = ConversationItemType.Message;

    [JsonPropertyName("role")]
    public ConversationRole? Role { get; set; }

    [JsonPropertyName("content")]
    public List<ContentPart> Content { get; set; } = [];

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("output")]
    public string? Output { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    public bool IsMessage(ConversationRole role) => ItemType == ConversationItemType.Message && Role == role;

    public string TextContent() => string.Concat(Content.Select(part => part.Text ?? part.Transcript ?? string.Empty));

    /// <summary>Deep copy, including <see cref="Content"/>, for building an independent history snapshot.</summary>
    public ConversationItem Clone() => new()
    {
        Id = Id,
        ItemType = ItemType,
        Role = Role,
        Content = [.. Content.Select(part => part.Clone())],
        Name = Name,
        CallId = CallId,
        Arguments = Arguments,
        Output = Output,
        Status = Status,
    };
}

/// <summary>A function call emitted by the model.</summary>
public sealed class FunctionToolCall
{
    [JsonPropertyName("type")]
    public string Type { get; } = "function_call";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public required string CallId { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

/// <summary>A function tool the session exposes to the model.</summary>
public sealed class FunctionToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; set; }
}

/// <summary>Server-side voice activity detection settings.</summary>
public sealed class TurnDetectionConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "server_vad";

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonPropertyName("prefix_padding_ms")]
    public int? PrefixPaddingMs { get; set; }

    [JsonPropertyName("silence_duration_ms")]
    public int? SilenceDurationMs { get; set; }

    [JsonPropertyName("create_response")]
    public bool? CreateResponse { get; set; }

    [JsonPropertyName("interrupt_response")]
    public bool? InterruptResponse { get; set; }

    public TurnDetectionConfig Clone() => new()
    {
        Type = Type,
        Threshold = Threshold,
        PrefixPaddingMs = PrefixPaddingMs,
        SilenceDurationMs = SilenceDurationMs,
        CreateResponse = CreateResponse,
        InterruptResponse = InterruptResponse,
    };
}

public sealed class AudioFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "audio/pcm";

    [JsonPropertyName("rate")]
    public int Rate { get; set; } = 24000;

    public AudioFormat Clone() => new() { Type = Type, Rate = Rate };
}

public sealed class AudioInputConfig
{
    [JsonPropertyName("format")]
    public AudioFormat? Format { get; set; }

    [JsonPropertyName("turn_detection")]
    public TurnDetectionConfig? TurnDetection { get; set; }

    [JsonPropertyName("transcription")]
    public JsonNode? Transcription { get; set; }

    public AudioInputConfig Clone() => new()
    {
        Format = Format?.Clone(),
        TurnDetection = TurnDetection?.Clone(),
        Transcription = Transcription?.DeepClone(),
    };
}

public sealed class AudioOutputConfig
{
    [JsonPropertyName("format")]
    public AudioFormat? Format { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    public AudioOutputConfig Clone() => new()
    {
        Format = Format?.Clone(),
        Voice = Voice,
        Speed = Speed,
    };
}

public sealed class AudioConfig
{
    [JsonPropertyName("input")]
    public AudioInputConfig? Input { get; set; }

    [JsonPropertyName("output")]
    public AudioOutputConfig? Output { get; set; }

    public AudioConfig Clone() => new()
    {
        Input = Input?.Clone(),
        Output = Output?.Clone(),
    };
}

/// <summary>Canonical session state, mirroring <c>RealtimeSessionCreateRequest</c>.</summary>
public sealed class SessionCreateRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "realtime";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; set; }

    [JsonPropertyName("audio")]
    public AudioConfig? Audio { get; set; }

    [JsonPropertyName("tools")]
    public List<FunctionToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Structural copy used by <c>RuntimeConfig</c> to apply updates copy-on-write, so a reader on
    /// the audio path never observes a half-applied <c>session.update</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Tools"/> entries are shared: tool definitions are treated as immutable once
    /// registered, and cloning the <see cref="JsonNode"/> schema of every tool on each update would
    /// cost more than it protects.
    /// </remarks>
    public SessionCreateRequest Clone() => new()
    {
        Type = Type,
        Model = Model,
        Instructions = Instructions,
        OutputModalities = OutputModalities is null ? null : [.. OutputModalities],
        Audio = Audio?.Clone(),
        Tools = Tools is null ? null : [.. Tools],
        ToolChoice = ToolChoice,
        Temperature = Temperature,
        MaxOutputTokens = MaxOutputTokens,
    };
}

/// <summary>Per-response overrides carried by <c>response.create</c>.</summary>
public sealed class ResponseCreateParams
{
    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; set; }

    [JsonPropertyName("conversation")]
    public string? Conversation { get; set; }

    [JsonPropertyName("input")]
    public List<ConversationItem>? Input { get; set; }

    [JsonPropertyName("tools")]
    public List<FunctionToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("audio")]
    public AudioConfig? Audio { get; set; }

    [JsonPropertyName("metadata")]
    public JsonNode? Metadata { get; set; }
}

public static class RealtimeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
