using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Core.Conversation;

/// <summary>Factories for the conversation items exchanged with the language model.</summary>
public static class ChatMessages
{
    public static ConversationItem MakeUserMessage(string text) => new()
    {
        ItemType = ConversationItemType.Message,
        Role = ConversationRole.User,
        Content = [ContentPart.InputText(text)],
    };

    public static ConversationItem MakeAssistantMessage(string text) => new()
    {
        ItemType = ConversationItemType.Message,
        Role = ConversationRole.Assistant,
        Content = [ContentPart.OutputText(text)],
    };

    public static ConversationItem MakeSystemMessage(string text) => new()
    {
        ItemType = ConversationItemType.Message,
        Role = ConversationRole.System,
        Content = [ContentPart.InputText(text)],
    };
}
