using System.Text.Json;
using Microsoft.Extensions.AI;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Llm.ChatClientBackend;

/// <summary>
/// Translates the pipeline's conversation history into <c>Microsoft.Extensions.AI</c> messages.
/// </summary>
internal static class ChatMessageMapper
{
    /// <summary>Maps a chat's provider-visible history to <see cref="ChatMessage"/> instances.</summary>
    public static List<ChatMessage> ToChatMessages(Chat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var messages = new List<ChatMessage>();

        foreach (var item in chat.ToProviderHistory())
        {
            switch (item.ItemType)
            {
                case ConversationItemType.FunctionCall:
                    messages.Add(new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent(item.CallId ?? string.Empty, item.Name ?? string.Empty, ParseArguments(item.Arguments))]));
                    break;

                case ConversationItemType.FunctionCallOutput:
                    messages.Add(new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(item.CallId ?? string.Empty, item.Output ?? string.Empty)]));
                    break;

                default:
                    messages.Add(new ChatMessage(RoleOf(item), ToContents(item)));
                    break;
            }
        }

        return messages;
    }

    private static ChatRole RoleOf(ConversationItem item) => item.Role switch
    {
        ConversationRole.System => ChatRole.System,
        ConversationRole.Assistant => ChatRole.Assistant,
        _ => ChatRole.User,
    };

    private static List<AIContent> ToContents(ConversationItem item)
    {
        var contents = new List<AIContent>();
        var text = item.TextContent();

        if (!string.IsNullOrEmpty(text))
        {
            contents.Add(new Microsoft.Extensions.AI.TextContent(text));
        }

        foreach (var part in item.Content)
        {
            if (part.ImageUrl is not { Length: > 0 } imageUrl)
            {
                continue;
            }

            contents.Add(imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? new DataContent(imageUrl)
                : new UriContent(imageUrl, "image/*"));
        }

        // A message with no parts at all is rejected by most providers.
        if (contents.Count == 0)
        {
            contents.Add(new Microsoft.Extensions.AI.TextContent(string.Empty));
        }

        return contents;
    }

    /// <summary>
    /// Turns the wire-format argument string back into the dictionary shape M.E.AI round-trips.
    /// </summary>
    private static Dictionary<string, object?> ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments) ?? [];
        }
        catch (JsonException)
        {
            // History replay must not fail a turn over a malformed argument blob the model produced.
            return [];
        }
    }
}
