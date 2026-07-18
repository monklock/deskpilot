namespace DeskPilot.Voice.WhisperCpp;

/// <summary>Contains one final local Whisper segment.</summary>
public sealed record WhisperSegment(string Text, double Confidence);

/// <summary>Processes normalized in-memory samples through one local Whisper model.</summary>
public interface IWhisperClient : IAsyncDisposable
{
    /// <summary>Recognizes final segments using the requested language.</summary>
    IAsyncEnumerable<WhisperSegment> ProcessAsync(
        float[] samples,
        string language,
        CancellationToken cancellationToken);
}

/// <summary>Creates one local Whisper client for the active model path.</summary>
public interface IWhisperClientFactory
{
    /// <summary>Loads a local client from the active model path.</summary>
    IWhisperClient Create(string modelPath);
}
