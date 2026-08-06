using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace SpeechToSpeech.Stt.Whisper;

/// <summary>Locates the ggml weights Whisper.net needs, downloading them once if they are absent.</summary>
public static class GgmlModelResolver
{
    /// <summary>
    /// Resolves <paramref name="path"/> to a ggml <c>.bin</c> file.
    /// </summary>
    /// <remarks>
    /// The path may name the file directly or a directory to search and, failing that, download into.
    /// Accepting a directory is what keeps <c>--whisper models/whisper</c> — an ONNX export directory —
    /// working after the backend switch.
    /// </remarks>
    public static async Task<string> ResolveAsync(
        string path,
        GgmlType type = GgmlType.Base,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var fileName = $"ggml-{type.ToString().ToLowerInvariant()}.bin";
        string target;

        if (Directory.Exists(path))
        {
            var existing = Directory
                .EnumerateFiles(path, "*.bin", SearchOption.TopDirectoryOnly)
                .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (existing is not null)
            {
                return existing;
            }

            target = Path.Combine(path, fileName);
        }
        else
        {
            // An explicit .bin path that does not exist yet is a download target; anything else is
            // treated as a directory to create.
            target = Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase)
                ? path
                : Path.Combine(path, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }

        logger?.LogInformation("Downloading {Type} ggml model to {Path}", type, target);

        // Download beside the target and move on success, so an interrupted run cannot leave a
        // truncated file that later looks like a valid model.
        var partial = target + ".partial";
        try
        {
            await using (var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(type, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            await using (var destination = File.Create(partial))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        logger?.LogInformation("Downloaded {Type} ggml model", type);
        return target;
    }
}
