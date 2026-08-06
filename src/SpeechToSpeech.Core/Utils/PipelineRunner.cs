using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SpeechToSpeech.Core.Utils;

/// <summary>Runs each pipeline handler as a long-running task.</summary>
/// <remarks>
/// <see cref="TaskCreationOptions.LongRunning"/> gives every stage its own thread rather than a pool
/// thread. Stages whose work is CPU-bound synchronous inference (Silero, Whisper, Kokoro) occupy that
/// thread for the duration of a call, which would otherwise starve the thread pool.
/// </remarks>
public sealed class PipelineRunner(IReadOnlyList<IPipelineHandler> handlers, ILogger<PipelineRunner>? logger = null)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logger ?? NullLogger<PipelineRunner>.Instance;
    private readonly List<Task> _running = [];

    public void Start()
    {
        foreach (var handler in handlers)
        {
            _running.Add(Task.Factory.StartNew(
                handler.RunAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap());
        }
    }

    public Task WaitAsync() => Task.WhenAll(_running);

    public async Task StopAsync()
    {
        foreach (var handler in handlers)
        {
            handler.Stop();
        }

        for (var i = 0; i < _running.Count; i++)
        {
            var completed = await Task.WhenAny(_running[i], Task.Delay(StopTimeout)).ConfigureAwait(false);
            if (completed != _running[i])
            {
                _logger.LogWarning(
                    "Handler {Index} ({Name}) did not stop within timeout",
                    i,
                    handlers[i].GetType().Name);
                continue;
            }

            // Observed so a stage that threw is reported rather than swallowed.
            try
            {
                await _running[i].ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Handler {Name} faulted", handlers[i].GetType().Name);
            }
        }
    }
}
