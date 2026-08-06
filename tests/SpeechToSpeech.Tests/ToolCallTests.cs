using System.Diagnostics;
using System.Text.Json.Nodes;
using SpeechToSpeech.Llm.ToolCall;
using Xunit;

namespace SpeechToSpeech.Tests;

public class ToolCallTests
{
    private static readonly JsonNode? WeatherSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "city": { "type": "string", "description": "City name" },
            "unit": { "type": "string", "default": "c" }
          },
          "required": ["city"]
        }
        """);

    private static readonly FunctionTool WeatherTool = new("get_weather", "Look up weather", WeatherSchema);

    [Fact]
    public void ToolPromptIsEmptyWhenThereAreNoTools()
        => Assert.Equal("", ToolPrompt.BuildSystemPrompt([]));

    [Fact]
    public void ToolPromptDeclaresTheToolAsJsonInsideTheCodeDelimiters()
    {
        var prompt = ToolPrompt.BuildSystemPrompt([WeatherTool]);
        Assert.Contains(ToolPrompt.EnterCode, prompt, StringComparison.Ordinal);
        Assert.Contains("""{"name":"get_weather","description":"Look up weather""", prompt, StringComparison.Ordinal);
        Assert.Contains("City name", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TextOnlyToolPromptOmitsTheSpeakFirstRule()
        => Assert.DoesNotContain("speak first", ToolPrompt.BuildSystemPrompt([WeatherTool], textOnly: true), StringComparison.Ordinal);

    [Fact]
    public void ParsesNamedStringArguments()
    {
        var parsed = FunctionCallParser.ParseFunctionCall(
            """{"name": "get_weather", "arguments": {"city": "Paris", "unit": "f"}}""");
        var call = Assert.Single(parsed);
        Assert.Equal("get_weather", call.FunctionName);
        Assert.Equal(2, call.Parameters.Count);
        Assert.Equal("city", call.Parameters[0].Key);
        Assert.Equal("Paris", call.Parameters[0].Value?.GetValue<string>());
    }

    [Fact]
    public void ParsesArrayNumberBooleanAndNullValues()
    {
        var parsed = FunctionCallParser.ParseFunctionCall(
            """{"name": "search", "arguments": {"tags": ["x","y"], "limit": 3, "exact": true, "note": null}}""");
        var call = Assert.Single(parsed);
        Assert.Equal(4, call.Parameters.Count);
        Assert.Equal(3L, call.Parameters[1].Value?.GetValue<long>());
        Assert.True(call.Parameters[2].Value?.GetValue<bool>());
        Assert.Null(call.Parameters[3].Value);
    }

    /// <summary>The OpenAI wire format encodes arguments as a JSON string, and models reproduce it.</summary>
    [Fact]
    public void ParsesArgumentsSuppliedAsAnEncodedJsonString()
    {
        var parsed = FunctionCallParser.ParseFunctionCall(
            """{"name": "get_weather", "arguments": "{\"city\": \"Paris\"}"}""");
        Assert.Equal("Paris", Assert.Single(parsed).Parameters[0].Value?.GetValue<string>());
    }

    [Fact]
    public void ParsesTheNestedFunctionEnvelopeShape()
    {
        var parsed = FunctionCallParser.ParseFunctionCall(
            """{"type": "function", "function": {"name": "get_weather", "arguments": {"city": "Paris"}}}""");
        Assert.Equal("get_weather", Assert.Single(parsed).FunctionName);
    }

    [Fact]
    public void PatternFilterExcludesNonMatchingCalls()
        => Assert.Empty(FunctionCallParser.ParseFunctionCall(
            """{"name": "get_weather", "arguments": {"city": "Paris"}}""", ["translate"]));

    [Fact]
    public void ParseMultipleFunctionsSkipsMalformedEntries()
        => Assert.Single(FunctionCallParser.ParseMultipleFunctions(
            ["""{"name": "get_weather", "arguments": {"city": "Paris"}}""", "{ broken"]));

    [Fact]
    public void ExtractsCallsFromCodeBlocksAndStripsThemFromProse()
    {
        var (outside, calls) = FunctionCallParser.ExtractFunctionCallsFromText(
            """Let me check. <code>{"name": "get_weather", "arguments": {"city": "Paris"}}</code> Done.""",
            "<code>.*?</code>");
        Assert.Single(calls);
        Assert.DoesNotContain("get_weather", outside, StringComparison.Ordinal);
    }

    [Fact]
    public void ToRealtimeToolCallBuildsAValidCall()
    {
        var call = FunctionCallParser.ParseFunctionCall(
            """{"name": "get_weather", "arguments": {"city": "Paris", "unit": "f"}}""")[0]
            .ToRealtimeToolCall([WeatherTool]);
        Assert.Equal("get_weather", call.Name);
        Assert.StartsWith("call_", call.CallId, StringComparison.Ordinal);
        Assert.Contains("Paris", call.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFunctionIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            FunctionCallParser.ParseFunctionCall("""{"name": "unknown_fn", "arguments": {"a": "b"}}""")[0]
                .ToRealtimeToolCall([WeatherTool]));

    [Fact]
    public void MissingRequiredArgumentIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            FunctionCallParser.ParseFunctionCall("""{"name": "get_weather", "arguments": {"unit": "f"}}""")[0]
                .ToRealtimeToolCall([WeatherTool]));

    /// <summary>
    /// SEC-001 regression. The text is attacker-steerable through the model's own output, so
    /// locating the JSON values in it must stay a linear scan.
    /// </summary>
    [Fact]
    public void PathologicalCallTextParsesInBoundedTime()
    {
        var text = new string('{', 4000) + new string('a', 4000);
        var stopwatch = Stopwatch.StartNew();
        Assert.Empty(FunctionCallParser.ParseFunctionCall(text));
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>SEC-002 regression: a hostile block pattern must not hang the caller.</summary>
    [Fact]
    public void PathologicalBlockPatternIsBounded()
    {
        var stopwatch = Stopwatch.StartNew();
        var (outside, calls) = FunctionCallParser.ExtractFunctionCallsFromText(
            new string('a', 4000) + "!", "(a+)+$");
        Assert.Empty(calls);
        Assert.NotNull(outside);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }
}
