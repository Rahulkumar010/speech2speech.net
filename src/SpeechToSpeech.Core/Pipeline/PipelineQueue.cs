namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Blocking FIFO queue used between pipeline stages.
/// </summary>
/// <remarks>
/// The STT stage relies on direct buffer access to drop stale queued audio
/// (<see cref="RemoveWhere"/>, <see cref="Any"/>). <c>Channel&lt;T&gt;</c> cannot express those, so
/// the buffer is a list guarded by a monitor.
/// <para>
/// A queue may be given a <see cref="Capacity"/>. Producers are never blocked — the capture callback
/// runs on the audio device thread and blocking it would drop samples at the driver — so the oldest
/// item is evicted instead. Unbounded growth here is the failure mode where a stage that cannot keep
/// up (an overloaded LLM, a paused TTS) silently converts a latency problem into an out-of-memory
/// one, minutes later and far from the cause.
/// </para>
/// </remarks>
public sealed class PipelineQueue<T>
{
    /// <summary>Sentinel for <see cref="Capacity"/> meaning "no bound".</summary>
    public const int Unbounded = 0;

    private readonly object _gate = new();
    private readonly LinkedList<T> _items = [];
    private readonly Action<T>? _onDropped;

    /// <summary>Completed and replaced whenever an item arrives, to wake <see cref="TakeAsync"/>.</summary>
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates an unbounded queue.</summary>
    public PipelineQueue()
    {
    }

    /// <summary>Creates a queue that evicts its oldest item once <paramref name="capacity"/> is reached.</summary>
    /// <param name="capacity">Maximum queued items, or <see cref="Unbounded"/>.</param>
    /// <param name="onDropped">
    /// Invoked outside the queue lock for each evicted item, so a caller can log or count the loss.
    /// </param>
    public PipelineQueue(int capacity, Action<T>? onDropped = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
        _onDropped = onDropped;
    }

    /// <summary>Maximum queued items, or <see cref="Unbounded"/>.</summary>
    public int Capacity { get; }

    /// <summary>Total items evicted because the queue was full, for the life of the queue.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    private long _dropped;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public void Put(T item)
    {
        var dropped = default(T);
        var didDrop = false;
        TaskCompletionSource arrived;

        lock (_gate)
        {
            if (Capacity != Unbounded && _items.Count >= Capacity)
            {
                dropped = _items.First!.Value;
                _items.RemoveFirst();
                didDrop = true;
            }

            _items.AddLast(item);
            Monitor.PulseAll(_gate);
            arrived = _arrived;
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Outside the lock so a waiter's continuation cannot run while the queue is held.
        arrived.TrySetResult();

        if (didDrop)
        {
            Interlocked.Increment(ref _dropped);
            _onDropped?.Invoke(dropped!);
        }
    }

    /// <summary>
    /// Waits for an item without occupying a thread. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async ValueTask<T> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task arrived;
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    var item = _items.First!.Value;
                    _items.RemoveFirst();
                    return item;
                }

                // Captured under the lock so an item arriving right now cannot be missed.
                arrived = _arrived.Task;
            }

            await arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Blocks up to <paramref name="timeout"/> for an item. Returns false on timeout.</summary>
    public bool TryTake(TimeSpan timeout, out T item)
    {
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (_items.Count == 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    item = default!;
                    return false;
                }

                Monitor.Wait(_gate, (int)remaining);
            }

            item = _items.First!.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    public bool TryTakeNow(out T item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default!;
                return false;
            }

            item = _items.First!.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>Removes every queued item matching <paramref name="predicate"/>; returns how many.</summary>
    public int RemoveWhere(Func<T, bool> predicate)
    {
        lock (_gate)
        {
            var removed = 0;
            var node = _items.First;
            while (node is not null)
            {
                var next = node.Next;
                if (predicate(node.Value))
                {
                    _items.Remove(node);
                    removed++;
                }

                node = next;
            }

            if (removed > 0)
            {
                Monitor.PulseAll(_gate);
            }

            return removed;
        }
    }

    /// <summary>
    /// Atomically evaluates <paramref name="predicate"/> against the queued items. Used by the STT
    /// stage to detect a queued final segment for a revision before processing a progressive one.
    /// </summary>
    public bool Any(Func<T, bool> predicate)
    {
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (predicate(item))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the queue lock, so a caller can inspect and
    /// mutate the buffer as one atomic step.
    /// </summary>
    public TResult WithLock<TResult>(Func<PipelineQueue<T>, TResult> action)
    {
        lock (_gate)
        {
            return action(this);
        }
    }
}
