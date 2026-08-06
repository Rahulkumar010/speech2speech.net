using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Llm.ToolCall;

/// <summary>A parsed function call with its ordered parameters.</summary>
public sealed record ParsedFunctionCall(
    string FunctionName,
    IReadOnlyList<KeyValuePair<string, JsonNode?>> Parameters,
    string OriginalString)
{
    /// <summary>
    /// Converts to a realtime function tool call, validating against
    /// <paramref name="functionTools"/> when supplied.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The function is unknown, or a required parameter is missing.
    /// </exception>
    public FunctionToolCall ToRealtimeToolCall(
        IReadOnlyList<FunctionTool>? functionTools = null,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var arguments = new JsonObject();
        foreach (var (key, value) in Parameters)
        {
            arguments[key] = value?.DeepClone();
        }

        if (functionTools is not null)
        {
            var tool = functionTools.FirstOrDefault(candidate => candidate.Name == FunctionName)
                       ?? throw new InvalidOperationException(
                           $"Function '{FunctionName}' not found in available tools: "
                           + $"[{string.Join(", ", functionTools.Select(t => t.Name))}]");

            var schema = tool.Parameters as JsonObject;
            var properties = schema?["properties"] as JsonObject;
            var required = (schema?["required"] as JsonArray)?
                .Select(node => node?.GetValue<string>() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal) ?? [];

            var undeclared = arguments
                .Select(argument => argument.Key)
                .Where(key => properties?.ContainsKey(key) != true)
                .ToList();

            if (undeclared.Count > 0)
            {
                logger.LogWarning(
                    "Dropping undeclared parameters for '{Function}': {Parameters}",
                    FunctionName,
                    string.Join(", ", undeclared));

                foreach (var key in undeclared)
                {
                    arguments.Remove(key);
                }
            }

            var missing = required.Where(key => !arguments.ContainsKey(key)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Missing required parameters for '{FunctionName}': {string.Join(", ", missing)}");
            }
        }

        return new FunctionToolCall
        {
            Id = Ids.Generate("fc"),
            CallId = Ids.Generate("call"),
            Name = FunctionName,
            Arguments = arguments.ToJsonString(),
        };
    }
}

/// <summary>
/// Extracts function names and arguments from the JSON tool calls the model emits as text.
/// </summary>
public static class FunctionCallParser
{
    /// <summary>
    /// Parses <paramref name="functionString"/> and returns every function call found.
    /// </summary>
    /// <param name="patternsToMatch">
    /// When non-empty, only calls whose name contains at least one of these substrings are returned.
    /// </param>
    public static List<ParsedFunctionCall> ParseFunctionCall(
        string functionString,
        IReadOnlyList<string>? patternsToMatch = null)
    {
        functionString = functionString.Trim();
        if (functionString.Length == 0)
        {
            return [];
        }

        var results = new List<ParsedFunctionCall>();

        // The model's prose surrounds the JSON, so the values have to be located before they can be
        // parsed. SplitTopLevelJsonValues is a single linear scan over brace depth outside string
        // literals, which keeps adversarial output linear rather than backtracking.
        foreach (var candidate in SplitTopLevelJsonValues(functionString))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }

            IEnumerable<JsonNode?> elements = node is JsonArray array ? array : [node];
            foreach (var element in elements)
            {
                if (element is not JsonObject call || ToParsedCall(call, candidate) is not { } parsed)
                {
                    continue;
                }

                if (patternsToMatch is { Count: > 0 }
                    && patternsToMatch.All(pattern => !parsed.FunctionName.Contains(pattern, StringComparison.Ordinal)))
                {
                    continue;
                }

                results.Add(parsed);
            }
        }

        return results;
    }

    /// <summary>Parses several call strings, skipping any that fail.</summary>
    public static List<ParsedFunctionCall> ParseMultipleFunctions(IEnumerable<string> functionStrings)
    {
        var results = new List<ParsedFunctionCall>();
        foreach (var functionString in functionStrings)
        {
            try
            {
                results.AddRange(ParseFunctionCall(functionString));
            }
            catch (Exception)
            {
                // A single malformed call must not discard the others.
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts function calls from delimited code blocks inside <paramref name="text"/> and returns
    /// the remaining text with those blocks stripped.
    /// </summary>
    /// <param name="blockRegex">
    /// Regex matching the code-block delimiters and their content, e.g. <c>&lt;code&gt;.*?&lt;/code&gt;</c>.
    /// Only text inside matched blocks is scanned for calls.
    /// </param>
    public static (string OutsideText, List<ParsedFunctionCall> Calls) ExtractFunctionCallsFromText(
        string text,
        string blockRegex = ".*")
    {
        if (string.IsNullOrEmpty(blockRegex))
        {
            return (text, []);
        }

        string outside;
        string inside;
        try
        {
            // Bounded: the pattern comes from configuration but the input is model output, so a
            // pathological combination must degrade to "no tool calls", never to a stalled turn.
            var regex = new Regex(blockRegex, RegexOptions.Singleline, RegexBudget.MatchTimeout);
            var matches = regex.Matches(text);
            if (matches.Count == 0)
            {
                return (text, []);
            }

            outside = regex.Replace(text, string.Empty);
            inside = string.Join(" ", matches.Select(match => match.Value)).Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return (text, []);
        }
        catch (ArgumentException)
        {
            return (text, []);
        }

        if (inside.Length == 0)
        {
            return (outside, []);
        }

        try
        {
            return (outside, ParseFunctionCall(inside));
        }
        catch (FormatException)
        {
            return (outside, []);
        }
    }

    /// <summary>
    /// Reads the name and arguments out of one emitted call object, tolerating the key spellings
    /// models drift between. Returns <see langword="null"/> when it is not a call at all.
    /// </summary>
    private static ParsedFunctionCall? ToParsedCall(JsonObject call, string original)
    {
        // Some models wrap the call as {"type": "function", "function": {"name": ..., ...}}.
        if (call["function"] is JsonObject nested)
        {
            call = nested;
        }

        if ((call["name"] ?? call["tool_name"]) is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var name)
            || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var argumentsNode = call["arguments"] ?? call["parameters"] ?? call["args"];

        // The OpenAI wire format carries arguments as a JSON string rather than an object, and
        // models trained on it reproduce that shape here.
        if (argumentsNode is JsonValue stringValue && stringValue.TryGetValue<string>(out var encoded))
        {
            try
            {
                argumentsNode = JsonNode.Parse(encoded);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var parameters = argumentsNode is JsonObject arguments
            ? arguments.Select(argument => new KeyValuePair<string, JsonNode?>(argument.Key, argument.Value)).ToList()
            : [];

        return new ParsedFunctionCall(name, parameters, original);
    }

    /// <summary>
    /// Returns each top-level JSON object or array in <paramref name="source"/>, tracking bracket
    /// depth outside string literals so nested structures and quoted brackets are handled.
    /// </summary>
    private static List<string> SplitTopLevelJsonValues(string source)
    {
        var values = new List<string>();
        var start = -1;
        var depth = 0;
        var quoted = false;

        for (var i = 0; i < source.Length; i++)
        {
            var character = source[i];

            if (quoted)
            {
                if (character == '\\')
                {
                    i++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (character)
            {
                case '"' when depth > 0:
                    quoted = true;
                    break;

                case '{' or '[':
                    if (depth++ == 0)
                    {
                        start = i;
                    }

                    break;

                case '}' or ']':
                    if (depth == 0)
                    {
                        break;
                    }

                    if (--depth == 0)
                    {
                        values.Add(source[start..(i + 1)]);
                    }

                    break;
            }
        }

        return values;
    }
}
