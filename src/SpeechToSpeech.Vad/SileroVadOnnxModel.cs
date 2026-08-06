using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpeechToSpeech.Vad;

/// <summary>Speech probability estimator driving <see cref="VadIterator"/>.</summary>
public interface IVadModel : IDisposable
{
    /// <summary>Returns the speech probability of one audio window.</summary>
    float Predict(ReadOnlySpan<float> chunk, int sampleRate);

    void ResetStates();
}

/// <summary>
/// Silero VAD v5 running on ONNX Runtime. The recurrent state is carried between calls, so audio
/// must be fed as a continuous stream and <see cref="ResetStates"/> called at a segment boundary.
/// </summary>
public sealed class SileroVadOnnxModel : IVadModel
{
    private const int StateSize = 128;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _stateName;
    private readonly string _sampleRateName;
    private float[] _state = new float[2 * 1 * StateSize];
    private float[] _context = [];

    public SileroVadOnnxModel(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Silero VAD ONNX model not found at '{modelPath}'. Download silero_vad.onnx from "
                + "https://github.com/snakers4/silero-vad and point VadOptions.ModelPath at it.",
                modelPath);
        }

        var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        _session = new InferenceSession(modelPath, options);

        var inputs = _session.InputMetadata.Keys.ToList();
        _inputName = inputs.FirstOrDefault(n => n is "input") ?? inputs[0];
        _stateName = inputs.FirstOrDefault(n => n is "state" or "h") ?? "state";
        _sampleRateName = inputs.FirstOrDefault(n => n is "sr") ?? "sr";
    }

    public float Predict(ReadOnlySpan<float> chunk, int sampleRate)
    {
        // v5 expects the previous window's tail prepended; the ONNX graph carries no context itself.
        var contextSize = sampleRate == 16000 ? 64 : 32;
        if (_context.Length != contextSize)
        {
            _context = new float[contextSize];
        }

        var input = new float[contextSize + chunk.Length];
        _context.CopyTo(input, 0);
        chunk.CopyTo(input.AsSpan(contextSize));

        var audio = new DenseTensor<float>(input, [1, input.Length]);
        var state = new DenseTensor<float>(_state, [2, 1, StateSize]);
        var rate = new DenseTensor<long>(new[] { (long)sampleRate }, [1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, audio),
            NamedOnnxValue.CreateFromTensor(_stateName, state),
            NamedOnnxValue.CreateFromTensor(_sampleRateName, rate),
        };

        using var results = _session.Run(inputs);
        var outputs = results.ToList();

        var probability = outputs[0].AsEnumerable<float>().First();
        if (outputs.Count > 1)
        {
            _state = outputs[1].AsEnumerable<float>().ToArray();
        }

        Array.Copy(input, input.Length - contextSize, _context, 0, contextSize);
        return probability;
    }

    public void ResetStates()
    {
        _state = new float[2 * 1 * StateSize];
        _context = [];
    }

    public void Dispose() => _session.Dispose();
}
