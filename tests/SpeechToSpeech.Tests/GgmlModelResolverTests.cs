using SpeechToSpeech.Stt.Whisper;
using Xunit;

namespace SpeechToSpeech.Tests;

public class GgmlModelResolverTests
{
    [Fact]
    public async Task ExistingFilePathIsReturnedUnchanged()
    {
        using var temp = new TempDirectory();
        var model = Path.Combine(temp.Path, "ggml-tiny.bin");
        await File.WriteAllTextAsync(model, "weights", TestContext.Current.CancellationToken);

        var resolved = await GgmlModelResolver.ResolveAsync(
            model,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(model, resolved);
    }

    [Fact]
    public async Task ExistingBinaryInADirectoryIsFound()
    {
        using var temp = new TempDirectory();
        var model = Path.Combine(temp.Path, "ggml-base.bin");
        await File.WriteAllTextAsync(model, "weights", TestContext.Current.CancellationToken);

        // An ONNX export directory that also holds ggml weights must not trigger a download.
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "config.json"),
            "{}",
            TestContext.Current.CancellationToken);

        var resolved = await GgmlModelResolver.ResolveAsync(
            temp.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(model, resolved);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
