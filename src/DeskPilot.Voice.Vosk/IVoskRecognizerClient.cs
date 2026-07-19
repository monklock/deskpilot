namespace DeskPilot.Voice.Vosk;

/// <summary>Contains validated timing for one final Vosk word.</summary>
public sealed record VoskWordTiming(
    string Word,
    double Confidence,
    TimeSpan Start,
    TimeSpan End);

/// <summary>Contains one native Vosk recognition update.</summary>
public sealed record VoskRecognition(
    string Text,
    double Confidence,
    bool IsFinal,
    IReadOnlyList<VoskWordTiming> Words)
{
    /// <summary>Creates a recognition update without final word timing.</summary>
    public VoskRecognition(string text, double confidence, bool isFinal)
        : this(text, confidence, isFinal, [])
    {
    }
}

/// <summary>Feeds normalized PCM16 audio into one native Vosk recognizer.</summary>
public interface IVoskRecognizerClient : IDisposable
{
    /// <summary>Accepts one owned normalized audio frame.</summary>
    VoskRecognition Accept(ReadOnlyMemory<byte> pcm16);

    /// <summary>Flushes the final buffered recognition result after normal input completion.</summary>
    VoskRecognition Complete();
}

/// <summary>Creates one limited-grammar native recognizer per wake session.</summary>
public interface IVoskRecognizerClientFactory
{
    /// <summary>Creates a recognizer for an active model and exact JSON grammar.</summary>
    IVoskRecognizerClient Create(string modelPath, string grammarJson);
}
