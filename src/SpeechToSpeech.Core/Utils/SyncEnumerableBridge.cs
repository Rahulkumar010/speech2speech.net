namespace SpeechToSpeech.Core.Utils;

/// <summary>
/// Presents a synchronous sequence as an <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
/// <remarks>
/// Written by hand rather than as an <c>async IAsyncEnumerable</c> iterator because such an iterator
/// contains no await and would trip CS1998, which this repository treats as an error. Handlers whose
/// work is CPU-bound (ONNX inference) stay synchronous and run on a dedicated thread; this only
/// adapts their shape so one pipeline loop can drive both kinds.
/// </remarks>
public static class SyncEnumerableBridge
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source) => new Adapter<T>(source);

    private sealed class Adapter<T>(IEnumerable<T> source) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(source.GetEnumerator(), cancellationToken);

        private sealed class Enumerator(IEnumerator<T> inner, CancellationToken cancellationToken) : IAsyncEnumerator<T>
        {
            public T Current => inner.Current;

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(inner.MoveNext());
            }

            public ValueTask DisposeAsync()
            {
                inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
