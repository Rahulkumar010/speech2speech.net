using SpeechToSpeech.Pipeline;
using Xunit;

namespace SpeechToSpeech.Tests;

public class VoicePipelineConfigTests
{
    [Fact]
    public void DefaultsAreUsable()
    {
        var config = new VoicePipelineConfig();

        config.Validate();

        Assert.Equal(16000, config.Vad.SampleRate);
        Assert.Equal("b", config.Tts.ResolveLanguageCode(config.Stt.Language));
        Assert.Equal("bm_fable", config.Tts.ResolveVoice("b"));
        Assert.Equal(1200, config.Tts.ResolveBlockSize());
    }

    [Fact]
    public void SampleConfigParses()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SpeechToSpeech.Pipeline", "voice-pipeline.sample.json");

        var config = VoicePipelineConfig.Load(Path.GetFullPath(path));
        config.Validate();

        Assert.Equal("bm_fable", config.Tts.Voice);
        Assert.Equal(0.7, config.Llm.Temperature);
        var tool = Assert.Single(config.Llm.Tools).ToDefinition();
        Assert.Equal("get_weather", tool.Name);
        Assert.NotNull(tool.Parameters);
    }

    [Fact]
    public void InstructionsCombinePersonaAndBehaviour()
    {
        var llm = new LlmConfig { Persona = "You are Fable.", SystemInstructions = "Be brief." };

        Assert.Equal("You are Fable.\n\nBe brief.", llm.BuildInstructions());
    }

    [Fact]
    public void UserTemplateWrapsTranscript()
    {
        var llm = new LlmConfig { UserTemplate = "The user said: {transcript}" };

        Assert.Equal("The user said: hello", llm.ApplyUserTemplate("hello"));
    }

    [Fact]
    public void UserTemplateWithoutPlaceholderIsRejected()
    {
        var config = new VoicePipelineConfig { Llm = { UserTemplate = "no placeholder here" } };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void CaptureRateMustMatchVadRate()
    {
        var config = new VoicePipelineConfig { Audio = { InputSampleRate = 48000 } };

        Assert.Throws<InvalidOperationException>(config.Validate);
    }
}
