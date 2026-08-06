using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Core;

/// <summary>Non-generic surface the thread manager needs.</summary>
public interface IPipelineHandler
{
    int? PipelineIndex { get; set; }

    /// <summary>Stage timeline recorder, when latency is being measured.</summary>
    TurnMetrics? Metrics { get; set; }

    Task RunAsync();

    void Stop();
}

/// <summary>
/// Base class for pipeline parts. Each part has an input and an output queue.
/// </summary>
/// <remarks>
/// To stop a handler properly, cancel its stop token and place <see cref="SentinelMessage.PipelineEnd"/>
/// in the input queue to avoid a queue deadlock. Items placed in the input queue are handed to
/// <see cref="Process"/>, and everything it yields is placed on the output queue.
/// <see cref="PipelineControlMessage.SessionEnd"/> is a soft control message that resets
/// per-session state without stopping the handler thread. On exit the handler runs
/// <see cref="Cleanup"/> and puts <see cref="SentinelMessage.PipelineEnd"/> on the output queue.
/// </remarks>
public abstract class BaseHandler<TIn, TOut> : IPipelineHandler
    where TIn : class
    where TOut : class
{
    private readonly CancellationTokenSource _stop;

    /// <summary>
    /// Duration of the most recent emitted output, in seconds.
    /// </summary>
    /// <remarks>
    /// Only the last value is ever read (<see cref="LastTime"/>), so this is a scalar. It used to be
    /// a <c>List&lt;double&gt;</c> appended to once per emitted chunk and never trimmed — roughly one
    /// entry per audio block, for the life of the process.
    /// </remarks>
    private double _lastTime;

    protected BaseHandler(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        ILogger? logger = null)
    {
        _stop = stopSource;
        QueueIn = queueIn;
        QueueOut = queueOut;
        Logger = logger ?? NullLogger.Instance;
    }

    protected PipelineQueue<IPipelineItem> QueueIn { get; }

    protected PipelineQueue<IPipelineItem> QueueOut { get; }

    protected ILogger Logger { get; }

    protected CancellationToken StopToken => _stop.Token;

    /// <summary>Cancellation scope shared with the realtime session, when the stage supports barge-in.</summary>
    public CancelScope? CancelScope { get; set; }

    public TurnMetrics? Metrics { get; set; }

    public int? PipelineIndex { get; set; }

    protected string Name => GetType().Name;

    protected double LastTime => _lastTime;

    protected virtual double MinTimeToDebugSeconds => 0.001;

    protected virtual LogLevel TimingLogLevel => LogLevel.Debug;

    /// <summary>Synchronous stage body. Override this for CPU-bound work such as ONNX inference.</summary>
    public virtual IEnumerable<TOut> Process(TIn input) =>
        throw new NotSupportedException($"{Name} overrides neither Process nor ProcessAsync.");

    /// <summary>Asynchronous stage body. Override this for I/O-bound work such as HTTP streaming.</summary>
    public virtual IAsyncEnumerable<TOut> ProcessAsync(TIn input, CancellationToken cancellationToken) =>
        Process(input).ToAsyncEnumerable();

    public void Stop() => _stop.Cancel();

    public async Task RunAsync()
    {
        PipelineLogContext.PipelineIndex = PipelineIndex;
        Logger.LogDebug("{Handler}: Handler started", Name);

        while (!StopToken.IsCancellationRequested)
        {
            IPipelineItem item;
            try
            {
                item = await QueueIn.TakeAsync(StopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (PipelineControlMessage.Is(item, ControlKind.SessionEnd))
            {
                Logger.LogDebug("{Handler}: session end received", Name);
                try
                {
                    OnSessionEnd();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "{Handler}: Error in OnSessionEnd()", Name);
                }

                QueueOut.Put(item);
                continue;
            }

            if (ReferenceEquals(item, SentinelMessage.PipelineEnd))
            {
                Logger.LogDebug("{Handler}: stopping", Name);
                break;
            }

            if (item is PipelineControlMessage control)
            {
                Logger.LogWarning("{Handler}: unexpected control message kind: {Kind}", Name, control.Kind);
                continue;
            }

            if (item is not TIn typed)
            {
                Logger.LogWarning("{Handler}: unexpected input type {Type}", Name, item.GetType().Name);
                continue;
            }

            if (!ShouldProcessInput(typed))
            {
                continue;
            }

            await ProcessOneAsync(typed).ConfigureAwait(false);
        }

        Cleanup();
        QueueOut.Put(SentinelMessage.PipelineEnd);
    }

    protected virtual bool ShouldProcessInput(TIn item)
    {
        if (CancelScope is null || item is not ICancellable { CancelGeneration: { } generation } || item is EndOfResponse)
        {
            return true;
        }

        if (!CancelScope.IsStale(generation))
        {
            return true;
        }

        Logger.LogDebug("{Handler}: dropping stale input for cancel generation {Generation}", Name, generation);
        return false;
    }

    protected virtual bool ShouldEmitOutput(TOut output) => true;

    protected virtual void BeforeEmitOutput(TOut output)
    {
    }

    /// <summary>
    /// Wraps raw audio in an <see cref="AudioOutput"/> tagged with the generation of the input that
    /// produced it, so the send loop can discard output from a cancelled response.
    /// </summary>
    protected virtual IPipelineItem OutputForQueue(TOut output, TIn sourceInput)
    {
        if (sourceInput is ICancellable { CancelGeneration: { } generation } && output is AudioChunk chunk)
        {
            return new AudioOutput { Audio = chunk.Data, CancelGeneration = generation };
        }

        return output as IPipelineItem ?? throw new InvalidOperationException(
            $"{Name}: output type {typeof(TOut).Name} cannot travel on a pipeline queue.");
    }

    protected virtual bool ShouldLogTiming(TOut output) => LastTime > MinTimeToDebugSeconds;

    protected virtual void Cleanup()
    {
    }

    protected virtual void OnSessionEnd()
    {
    }

    private async Task ProcessOneAsync(TIn typed)
    {
        var startTime = Clock.NowSeconds;
        try
        {
            await foreach (var output in ProcessAsync(typed, StopToken).ConfigureAwait(false))
            {
                startTime = Emit(output, typed, startTime);
            }
        }
        catch (OperationCanceledException) when (StopToken.IsCancellationRequested)
        {
            Logger.LogDebug("{Handler}: processing cancelled during shutdown", Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Handler}: Error in Process()", Name);
        }
    }

    /// <summary>Emits one output and returns the clock reading the next one should be timed from.</summary>
    private double Emit(TOut output, TIn typed, double startTime)
    {
        if (!ShouldEmitOutput(output))
        {
            return Clock.NowSeconds;
        }

        _lastTime = Clock.NowSeconds - startTime;
        if (ShouldLogTiming(output))
        {
            Logger.Log(TimingLogLevel, "{Handler}: {Elapsed:F3} s", Name, LastTime);
        }

        RecordMetrics(output);
        BeforeEmitOutput(output);
        QueueOut.Put(OutputForQueue(output, typed));
        return Clock.NowSeconds;
    }

    private void RecordMetrics(TOut output)
    {
        if (Metrics is not { } metrics || output is not PipelineMessage message)
        {
            return;
        }

        // Only some messages carry the moment speech stopped, and the earliest one to arrive wins.
        metrics.Anchor(message.TurnId, message switch
        {
            Transcription transcription => transcription.SpeechStoppedAtSeconds,
            GenerateResponseRequest request => request.SpeechStoppedAtSeconds,
            LlmResponseChunk chunk => chunk.SpeechStoppedAtSeconds,
            TtsInput input => input.SpeechStoppedAtSeconds,
            _ => null,
        });

        // VAD emits progressive segments and a final one under one tag; only the final one is on the
        // response path, and without the mode it would lose the first-wins race to a progressive one.
        var stage = message is VadAudio vadAudio
            ? $"{Name}/{message.Tag}:{vadAudio.Mode}"
            : $"{Name}/{message.Tag}";

        metrics.Mark(message.TurnId, stage);
    }
}
