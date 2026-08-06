using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Llm.ChatClientBackend;

/// <summary>
/// Presents a session-declared tool to <c>Microsoft.Extensions.AI</c> as a declaration with no body.
/// </summary>
/// <remarks>
/// Tools here are declared by the realtime session and executed by the host, not by a local
/// delegate — the call has to leave the pipeline as a <c>ProviderEvent.ToolCall</c> so the host can
/// run it and feed the result back as a conversation item. That rules out
/// <c>AIFunctionFactory.Create</c> and <c>UseFunctionInvocation()</c>, which both assume the client
/// owns the implementation and can invoke it in-process.
/// </remarks>
internal sealed class DeclaredTool : AIFunctionDeclaration
{
    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private readonly JsonElement _schema;

    public DeclaredTool(FunctionToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Name = definition.Name;
        Description = definition.Description ?? string.Empty;
        _schema = ToSchema(definition.Parameters);
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema => _schema;

    private static JsonElement ToSchema(JsonNode? parameters)
    {
        if (parameters is null)
        {
            return EmptyObjectSchema;
        }

        // Deserialize rather than hand the node over: JsonElement must own its backing document, and
        // the session's node can be mutated by a later session.update.
        using var document = JsonDocument.Parse(parameters.ToJsonString());
        return document.RootElement.Clone();
    }
}
