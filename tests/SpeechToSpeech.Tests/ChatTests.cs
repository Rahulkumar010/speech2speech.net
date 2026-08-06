using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Realtime;
using Xunit;

namespace SpeechToSpeech.Tests;

public class ChatTests
{
    private static Chat MakeChat()
    {
        var chat = new Chat(size: 5);
        ChatFactory.AddSupportedItem(chat, ChatMessages.MakeUserMessage("hello"));
        ChatFactory.AddSupportedItem(chat, ChatMessages.MakeAssistantMessage("hi"));
        return chat;
    }

    [Fact]
    public void UserAndAssistantItemsLandInTheBuffer()
        => Assert.Equal(2, MakeChat().Buffer.Count);

    [Fact]
    public void UserTurnIsCounted()
        => Assert.Equal(1, MakeChat().UserTurnCount);

    [Fact]
    public void MakeUserMessageUsesInputText()
        => Assert.Equal("input_text", MakeChat().Buffer[0].Content[0].Type);

    [Fact]
    public void MakeAssistantMessageUsesOutputText()
        => Assert.Equal("output_text", MakeChat().Buffer[1].Content[0].Type);

    [Fact]
    public void TextContentConcatenatesParts()
        => Assert.Equal("hello", MakeChat().Buffer[0].TextContent());

    [Fact]
    public void MessageWithoutARoleIsRejected()
        => Assert.Throws<ChatItemException>(() =>
            ChatFactory.AddSupportedItem(MakeChat(), new ConversationItem { ItemType = ConversationItemType.Message }));

    [Fact]
    public void FunctionCallWithoutACallIdIsRejected()
        => Assert.Throws<ChatItemException>(() => ChatFactory.AddSupportedItem(MakeChat(), new ConversationItem
        {
            ItemType = ConversationItemType.FunctionCall,
            Name = "get_weather",
        }));

    [Fact]
    public void FunctionCallWithABadCallIdPrefixIsRejected()
        => Assert.Throws<ChatItemException>(() => ChatFactory.AddSupportedItem(MakeChat(), new ConversationItem
        {
            ItemType = ConversationItemType.FunctionCall,
            Name = "get_weather",
            CallId = "xyz_123",
        }));

    [Fact]
    public void BuildActiveChatCopiesTheHistory()
    {
        var chat = MakeChat();
        Assert.Equal(chat.Buffer.Count, ChatFactory.BuildActiveChat(chat, null).Buffer.Count);
    }

    [Fact]
    public void AppendingToTheCopyDoesNotMutateTheOriginal()
    {
        var chat = MakeChat();
        var copy = ChatFactory.BuildActiveChat(chat, null);
        ChatFactory.AddSupportedItem(copy, ChatMessages.MakeUserMessage("only in copy"));
        Assert.Equal(2, chat.Buffer.Count);
        Assert.Equal(3, copy.Buffer.Count);
    }

    /// <summary>
    /// CON-002 regression. Copy() used to share the <see cref="ConversationItem"/> instances, so
    /// editing an item in the snapshot silently edited the live conversation.
    /// </summary>
    [Fact]
    public void CopyDeepClonesItemsAndContent()
    {
        var chat = MakeChat();
        var copy = chat.Copy();

        Assert.NotSame(chat.Buffer[0], copy.Buffer[0]);
        Assert.NotSame(chat.Buffer[0].Content[0], copy.Buffer[0].Content[0]);

        copy.Buffer[0].Content[0].Text = "tampered";
        copy.Buffer[1].Content.Clear();

        Assert.Equal("hello", chat.Buffer[0].TextContent());
        Assert.NotEmpty(chat.Buffer[1].Content);
    }

    /// <summary>CON-002 regression: the provider projection is a snapshot, not a live view.</summary>
    [Fact]
    public void ProviderHistoryDoesNotAliasTheBuffer()
    {
        var chat = MakeChat();
        var history = chat.ToProviderHistory();
        var assistant = history.Single(i => i.Role == ConversationRole.Assistant);

        Assert.NotSame(chat.Buffer[1], assistant);
        assistant.Content[0].Text = "tampered";
        Assert.Equal("hi", chat.Buffer[1].TextContent());
    }
}
