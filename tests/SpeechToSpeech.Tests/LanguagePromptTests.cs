using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Llm;
using SpeechToSpeech.Llm.Prompts;
using Xunit;

namespace SpeechToSpeech.Tests;

public class LanguagePromptTests
{
    [Fact]
    public async Task TheLastMessageSentIsTheUserTranscript()
    {
        var chat = await RunTurnAsync(enableLanguagePrompt: true);

        // Regression: the reply-language instruction used to be appended as a trailing user message,
        // so small models answered it ("Understood, I will reply in English.") instead of the user.
        var last = Assert.IsType<ConversationItem>(chat.Buffer[^1]);
        Assert.Equal(ConversationRole.User, last.Role);
        Assert.Equal("could you describe more on quantum computing", last.Content[0].Text);
    }

    [Fact]
    public async Task TheReplyLanguageIsPinnedInTheSystemPrompt()
    {
        var chat = await RunTurnAsync(enableLanguagePrompt: true);

        Assert.Contains("Always reply in english", chat.InitChatMessage!.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLanguageIsPinnedWhenThePromptIsDisabled()
    {
        var chat = await RunTurnAsync(enableLanguagePrompt: false);

        Assert.DoesNotContain("Always reply in", chat.InitChatMessage!.Content[0].Text, StringComparison.Ordinal);
        Assert.Single(chat.Buffer);
    }

    private static async Task<Chat> RunTurnAsync(bool enableLanguagePrompt)
    {
        using var stop = new CancellationTokenSource();
        var queueIn = new PipelineQueue<IPipelineItem>();
        var queueOut = new PipelineQueue<IPipelineItem>();

        var chat = new Chat(size: 10);
        chat.AddItem(ChatMessages.MakeUserMessage("could you describe more on quantum computing"));

        var model = new CapturingLanguageModel(
            stop,
            queueIn,
            queueOut,
            new LanguageModelHandlerOptions { EnableLanguagePrompt = enableLanguagePrompt });

        var request = new GenerateResponseRequest
        {
            RuntimeConfig = new RuntimeConfig
            {
                Chat = chat,
                Session = new SessionCreateRequest
                {
                    Instructions = "You are a concise, friendly voice assistant.",
                    OutputModalities = ["audio"],
                },
            },
            LanguageCode = "en",
            TurnId = "t1",
            TurnRevision = 1,
        };

        await foreach (var _ in model.ProcessAsync(request, TestContext.Current.CancellationToken))
        {
            // Drained for effect; the assertions read the captured chat.
        }

        return model.Captured!;
    }

    /// <summary>Captures the chat the backend would serialise, then aborts before any network call.</summary>
    private sealed class CapturingLanguageModel(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        LanguageModelHandlerOptions options)
        : BaseOpenAiCompatibleLanguageModel(stopSource, queueIn, queueOut, options)
    {
        public Chat? Captured { get; private set; }

        public override Task WarmupAsync() => Task.CompletedTask;

        protected override CompactGenerateFn BuildCompactionGenerateFn() =>
            (_, _) => Task.FromResult(string.Empty);

        protected override object? BuildRequestPayload(Chat activeChat, ToolRequest toolRequest)
        {
            Captured = activeChat;

            // A null payload short-circuits generation, so the turn ends without a request.
            return null;
        }

        protected override Task<IAsyncEnumerable<ProviderEvent>> RequestEventsAsync(
            object payload,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
