using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Core.Conversation;

/// <summary>Helpers for validating and assembling the chat a response generates against.</summary>
public static class ChatFactory
{
    /// <summary>
    /// Validates a protocol conversation item and adds it to <paramref name="chat"/>.
    /// </summary>
    /// <remarks>
    /// Shared by the conversation handler (in-band item injection) and the language-model handlers
    /// (seeding an out-of-band response's throwaway chat from <c>response.input</c>).
    /// </remarks>
    /// <exception cref="ChatItemException">Validation failed or the item type is unsupported.</exception>
    public static void AddSupportedItem(Chat chat, ConversationItem item)
    {
        // call_id on function_call items must be client-supplied: it is referenced later by
        // function_call_output items, so one cannot silently be generated here.
        if (item.ItemType == ConversationItemType.FunctionCall
            && (item.CallId is null || !item.CallId.StartsWith("call_", StringComparison.Ordinal)))
        {
            throw new ChatItemException(
                "function_call item is missing a call_id. The call_id should start with 'call_'.");
        }

        if (item.ItemType == ConversationItemType.Message && item.Role is null)
        {
            throw new ChatItemException("message item is missing a role.");
        }

        chat.AddItem(item);
    }

    /// <summary>
    /// Builds the chat an out-of-band response generates against; the caller ensures the response is
    /// out-of-band.
    /// </summary>
    /// <remarks>
    /// Mirrors the OpenAI realtime semantics for <c>input</c>: null yields a read-only copy of the
    /// default conversation (read history, never commit back); an empty list yields a fresh empty
    /// chat (context cleared, only the system prompt added later by the handler); and a non-empty
    /// list yields a fresh chat seeded with those items.
    /// </remarks>
    /// <exception cref="ChatItemException">An input item failed validation.</exception>
    public static Chat BuildActiveChat(Chat originalChat, ResponseCreateParams? response)
    {
        if (response?.Input is null)
        {
            return originalChat.Copy();
        }

        var fresh = new Chat(originalChat.Size);
        foreach (var item in response.Input)
        {
            AddSupportedItem(fresh, item);
        }

        return fresh;
    }
}
