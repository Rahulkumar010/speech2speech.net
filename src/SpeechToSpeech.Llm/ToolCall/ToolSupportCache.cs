using System.Text.Json;

namespace SpeechToSpeech.Llm.ToolCall;

/// <summary>What a probe established about a provider's native tool-calling support.</summary>
public enum ToolSupport
{
    /// <summary>Not established yet, or the probe was inconclusive (server down, network error).</summary>
    Unknown,

    /// <summary>The provider accepted a request carrying a <c>tools</c> array.</summary>
    Native,

    /// <summary>The provider rejected <c>tools</c>; calls must be prompted in the system message.</summary>
    Prompted,
}

/// <summary>
/// Remembers, per endpoint and model, whether native tool calling works.
/// </summary>
/// <remarks>
/// The probe costs one round trip. Without a cache that round trip is paid on every process start,
/// which is squarely in the path of the first spoken turn. Results are written to a small JSON file
/// so a restart against the same local server reuses the verdict.
/// <para>
/// Every file operation is best-effort. A cache that throws — unwritable directory, corrupt file,
/// a second process holding the file — must degrade to "probe again", never to a failed startup.
/// </para>
/// </remarks>
public sealed class ToolSupportCache
{
    private readonly object _gate = new();
    private readonly string? _path;
    private Dictionary<string, string>? _entries;

    public ToolSupportCache(string? path) => _path = path;

    /// <summary>Process-wide cache backed by the user's local application data folder.</summary>
    public static ToolSupportCache Shared { get; } = new(DefaultPath());

    /// <summary>Cache key for a provider endpoint and model. Case-insensitive on the URL.</summary>
    public static string KeyFor(string? baseUrl, string? model) =>
        $"{baseUrl?.TrimEnd('/').ToLowerInvariant() ?? string.Empty}|{model ?? string.Empty}";

    public ToolSupport Get(string key)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _entries!.TryGetValue(key, out var value) && Enum.TryParse<ToolSupport>(value, out var parsed)
                ? parsed
                : ToolSupport.Unknown;
        }
    }

    /// <summary>Records a verdict. <see cref="ToolSupport.Unknown"/> is ignored, so an inconclusive
    /// probe does not pin the wrong answer until the file is deleted.</summary>
    public void Set(string key, ToolSupport support)
    {
        if (support == ToolSupport.Unknown)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoadedLocked();
            _entries![key] = support.ToString();
            SaveLocked();
        }
    }

    private static string? DefaultPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return root.Length == 0 ? null : Path.Combine(root, "SpeechToSpeech", "tool-support.json");
        }
        catch (Exception exception) when (exception is ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_entries is not null)
        {
            return;
        }

        _entries = [];
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (parsed is not null)
            {
                _entries = parsed;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable or corrupt: start empty and let the next Set overwrite it.
        }
    }

    private void SaveLocked()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // In-memory result still stands for this process.
        }
    }
}
