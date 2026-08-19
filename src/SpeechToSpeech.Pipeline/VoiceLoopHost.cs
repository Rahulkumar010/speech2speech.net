using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Utils;
using System.Runtime.Versioning;

namespace SpeechToSpeech.Pipeline;

/// <summary>
/// Drives a <see cref="VoicePipeline"/> from the local microphone and speaker, with every device
/// setting taken from <see cref="AudioIoConfig"/>.
/// </summary>
/// <remarks>
/// Playback owns the listening gate: the microphone is muted while the assistant speaks and re-armed
/// only once the speaker buffer has actually drained, otherwise the VAD triggers on our own output.
/// </remarks>
// [SupportedOSPlatform("windows")]
public sealed class VoiceLoopHost : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly BufferedWaveProvider _speakerBuffer;
    private readonly WaveOutEvent _speaker;
    private readonly WaveInEvent _microphone;
    private readonly Thread _pump;
    private readonly ManualResetEventSlim _pumpStopped = new(false);

    private double _levelLoggedAt;

    private VoiceLoopHost(VoicePipeline pipeline, ILoggerFactory loggerFactory)
    {
        Pipeline = pipeline;
        _logger = loggerFactory.CreateLogger<VoiceLoopHost>();

        var audio = pipeline.Config.Audio;

        _speakerBuffer = new BufferedWaveProvider(new WaveFormat(pipeline.OutputSampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(audio.PlaybackBufferSeconds),
            DiscardOnBufferOverflow = true,
        };

        _speaker = new WaveOutEvent { DesiredLatency = audio.PlaybackLatencyMilliseconds };
        _speaker.Init(_speakerBuffer);

        _microphone = new WaveInEvent
        {
            DeviceNumber = audio.InputDeviceNumber,
            WaveFormat = new WaveFormat(audio.InputSampleRate, 16, 1),
            BufferMilliseconds = audio.CaptureBufferMilliseconds,
            NumberOfBuffers = audio.CaptureBufferCount,
        };

        _microphone.DataAvailable += OnDataAvailable;
        _microphone.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                _logger.LogError(e.Exception, "Microphone capture stopped with an error");
            }
        };

        _pump = new Thread(PumpOutputs) { Name = "VoiceLoopOutput", IsBackground = true };
    }

    public VoicePipeline Pipeline { get; }

    /// <summary>Raised on the pump thread for every transcript, assistant message, tool call and failure.</summary>
    public event Action<PipelineEvent>? EventReceived;

    /// <summary>Raised with the input level in dBFS when <see cref="AudioIoConfig.EnableLevelMeter"/> is set.</summary>
    public event Action<double>? InputLevel;

    public static async Task<VoiceLoopHost> CreateAsync(
        VoicePipelineConfig config,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var pipeline = await VoicePipeline.CreateAsync(config, factory, cancellationToken).ConfigureAwait(false);
        return new VoiceLoopHost(pipeline, factory);
    }

    /// <summary>Names the available capture devices, so a wrong <c>inputDeviceNumber</c> is visible.</summary>
    public static IReadOnlyList<string> CaptureDevices() =>
        [.. Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName)];

    /// <summary>
    /// Runs the loop until <paramref name="cancellationToken"/> fires, then lets the in-flight
    /// response finish speaking before shutting the pipeline down.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await Pipeline.WarmupAsync().ConfigureAwait(false);

        Pipeline.Start();
        _speaker.Play();
        _pump.Start();

        _microphone.StartRecording();
        _logger.LogInformation(
            "Listening on '{Device}'",
            WaveInEvent.GetCapabilities(_microphone.DeviceNumber).ProductName);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task ShutdownAsync()
    {
        _logger.LogInformation("Stopping capture; waiting for the in-flight response to finish");
        _microphone.StopRecording();

        var audio = Pipeline.Config.Audio;

        // Trailing silence lets the VAD observe the end-of-speech gap and release the last segment.
        for (var i = 0; i < audio.TrailingSilenceFrames; i++)
        {
            Pipeline.PushSilence();
            await Task.Delay(audio.CaptureBufferMilliseconds).ConfigureAwait(false);
        }

        await Pipeline.DrainAsync(TimeSpan.FromSeconds(audio.DrainTimeoutSeconds)).ConfigureAwait(false);
        await Pipeline.StopAsync().ConfigureAwait(false);

        _pumpStopped.Wait(TimeSpan.FromSeconds(15));
        _speaker.Stop();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (Pipeline.Config.Audio.EnableLevelMeter)
        {
            ReportLevel(e.Buffer.AsSpan(0, e.BytesRecorded));
        }

        Pipeline.PushAudio(e.Buffer.AsSpan(0, e.BytesRecorded));
    }

    private void ReportLevel(ReadOnlySpan<byte> pcm)
    {
        var now = Clock.NowSeconds;
        if (now - _levelLoggedAt < 0.25)
        {
            return;
        }

        _levelLoggedAt = now;
        var samples = AudioConvert.Int16BytesToFloat(pcm.ToArray());
        if (samples.Length == 0)
        {
            return;
        }

        var sum = 0f;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        var rms = MathF.Sqrt(sum / samples.Length);
        InputLevel?.Invoke(20 * Math.Log10(Math.Max(rms, 1e-6f)));
    }

    /// <summary>Moves synthesized audio to the speaker and surfaces text events, on one thread.</summary>
    private void PumpOutputs()
    {
        var spokenBytes = 0;

        while (true)
        {
            var idle = true;

            while (Pipeline.TextOutput.TryTakeNow(out var item))
            {
                idle = false;
                if (item is PipelineEvent pipelineEvent)
                {
                    EventReceived?.Invoke(pipelineEvent);
                }
            }

            while (Pipeline.AudioOutput.TryTakeNow(out var item))
            {
                idle = false;

                if (ReferenceEquals(item, SentinelMessage.PipelineEnd))
                {
                    _pumpStopped.Set();
                    return;
                }

                if (ReferenceEquals(item, SentinelMessage.AudioResponseDone))
                {
                    _logger.LogDebug("Response audio done ({Samples} samples)", spokenBytes / 2);
                    spokenBytes = 0;

                    // Drain the tail before re-arming the mic, or the VAD triggers on our own playback.
                    while (_speakerBuffer.BufferedDuration > TimeSpan.Zero)
                    {
                        Thread.Sleep(20);
                    }

                    Pipeline.ShouldListen.Set();
                    continue;
                }

                var pcm = item switch
                {
                    AudioOutput output => output.Audio,
                    AudioChunk chunk => chunk.Data,
                    _ => null,
                };

                if (pcm is null)
                {
                    continue;
                }

                if (Pipeline.ShouldListen.IsSet)
                {
                    Pipeline.ShouldListen.Reset();
                }

                _speakerBuffer.AddSamples(pcm, 0, pcm.Length);
                spokenBytes += pcm.Length;
            }

            if (idle)
            {
                Thread.Sleep(20);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _microphone.Dispose();
        _speaker.Dispose();
        await Pipeline.DisposeAsync().ConfigureAwait(false);
        _pumpStopped.Dispose();
    }
}
