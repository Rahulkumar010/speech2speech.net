using System.Text;

namespace SpeechToSpeech.Llm.ToolCall;

/// <summary>
/// Separates a streamed token sequence into speakable text and completed tool-call block bodies.
/// </summary>
/// <remarks>
/// A delimiter can straddle two deltas, so the gate holds back any tail of the buffer that is a
/// prefix of the opening tag, and withholds everything after an opening tag until the closing tag
/// arrives. Without this the sentence tokenizer sees the '(', '.' and quotes inside a call
/// expression as sentence structure and hands fragments of it to TTS to be read aloud.
/// </remarks>
public sealed class ToolBlockGate
{
    private readonly string _enterTag;
    private readonly string _endTag;
    private readonly StringBuilder _buffer = new();
    private bool _inside;

    public ToolBlockGate(string enterTag, string endTag)
    {
        // Empty tags would make the scan loop below consume nothing and spin forever.
        ArgumentException.ThrowIfNullOrEmpty(enterTag);
        ArgumentException.ThrowIfNullOrEmpty(endTag);

        _enterTag = enterTag;
        _endTag = endTag;
    }

    /// <summary>Whether an opening tag has been seen whose closing tag has not arrived yet.</summary>
    public bool HasUnclosedBlock => _inside;

    /// <summary>
    /// Consumes one delta, appending the body of every block that completed to
    /// <paramref name="completedBlocks"/> and returning the text that is safe to speak.
    /// </summary>
    public string Feed(string delta, List<string> completedBlocks)
    {
        ArgumentNullException.ThrowIfNull(completedBlocks);

        _buffer.Append(delta);
        var speakable = new StringBuilder();

        while (true)
        {
            var text = _buffer.ToString();

            if (_inside)
            {
                var end = text.IndexOf(_endTag, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                completedBlocks.Add(text[..end]);
                Rebase(text, end + _endTag.Length);
                _inside = false;
                continue;
            }

            var start = text.IndexOf(_enterTag, StringComparison.Ordinal);
            if (start < 0)
            {
                var held = PartialTagLength(text, _enterTag);
                speakable.Append(text, 0, text.Length - held);
                Rebase(text, text.Length - held);
                break;
            }

            speakable.Append(text, 0, start);
            Rebase(text, start + _enterTag.Length);
            _inside = true;
        }

        return speakable.ToString();
    }

    /// <summary>
    /// Returns the withheld remainder at end of stream. An unclosed block is dropped rather than
    /// spoken, since its content is a half-written call expression.
    /// </summary>
    public string Flush()
    {
        var remainder = _inside ? string.Empty : _buffer.ToString();
        _buffer.Clear();
        _inside = false;
        return remainder;
    }

    private void Rebase(string text, int consumed) =>
        _buffer.Clear().Append(text, consumed, text.Length - consumed);

    /// <summary>Length of the longest suffix of <paramref name="text"/> that starts <paramref name="tag"/>.</summary>
    private static int PartialTagLength(string text, string tag)
    {
        for (var length = Math.Min(tag.Length - 1, text.Length); length > 0; length--)
        {
            if (string.CompareOrdinal(text, text.Length - length, tag, 0, length) == 0)
            {
                return length;
            }
        }

        return 0;
    }
}
