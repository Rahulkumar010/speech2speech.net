using System.Text.Json.Nodes;
using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Llm.ToolCall;

/// <summary>Prompt-facing view of a function tool definition.</summary>
public sealed class FunctionTool(string name, string? description, JsonNode? parameters)
{
    public string Name { get; } = name;

    public string? Description { get; } = description;

    public JsonNode? Parameters { get; } = parameters;

    public static FunctionTool FromDefinition(FunctionToolDefinition definition) =>
        new(definition.Name, definition.Description, definition.Parameters);

    /// <summary>
    /// Renders this tool for the system prompt as the same JSON object shape the native
    /// <c>tools</c> array uses, which instruction-tuned models are already trained on.
    /// </summary>
    public string ToPromptJson()
    {
        var declaration = new JsonObject { ["name"] = Name };

        if (!string.IsNullOrEmpty(Description))
        {
            declaration["description"] = Description;
        }

        declaration["parameters"] = Parameters?.DeepClone()
            ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

        return declaration.ToJsonString();
    }
}
