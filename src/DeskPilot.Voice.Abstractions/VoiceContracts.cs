namespace DeskPilot.Voice.Abstractions;

/// <summary>Provides an audio stream captured from an input device.</summary>
public interface IAudioInputStream : IAsyncDisposable
{
    /// <summary>Gets the captured audio content.</summary>
    Stream Content { get; }
}

/// <summary>Recognizes speech from recorded command audio.</summary>
public interface ISpeechToTextProvider
{
    /// <summary>Gets the stable provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Recognizes speech from an audio stream.</summary>
    Task<SpeechRecognitionResult> RecognizeAsync(Stream audio, SpeechRecognitionOptions options, CancellationToken cancellationToken);
}

/// <summary>Waits until recorded speech ends.</summary>
public interface IVoiceActivityDetector
{
    /// <summary>Waits for a speech end event.</summary>
    Task<VoiceActivityResult> WaitForSpeechEndAsync(IAudioInputStream input, VoiceActivityOptions options, CancellationToken cancellationToken);
}

/// <summary>Detects a configured wake phrase.</summary>
public interface IWakeWordProvider
{
    /// <summary>Gets the stable provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Waits for a wake phrase detection event.</summary>
    Task<WakeWordDetectionResult> WaitForDetectionAsync(
        IAudioCaptureSession audio,
        WakeWordOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Configures limited-grammar wake phrase detection.</summary>
public sealed record WakeWordOptions(string Phrase, double MinimumConfidence);

/// <summary>Configures speech-to-text recognition.</summary>
public sealed record SpeechRecognitionOptions(string Language = "ru", double MinimumConfidence = 0.70);

/// <summary>Contains recognized speech text and confidence.</summary>
public sealed record SpeechRecognitionResult(string Text, double Confidence, bool IsFinal);

/// <summary>Configures speech end detection.</summary>
public sealed record VoiceActivityOptions(TimeSpan MinimumSpeechDuration, TimeSpan SilenceTimeout, TimeSpan MaximumCommandDuration)
{
    /// <summary>Gets the DeskPilot default voice activity configuration.</summary>
    public static VoiceActivityOptions Default { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(10));
}

/// <summary>Contains a voice activity detection result.</summary>
public sealed record VoiceActivityResult(bool SpeechDetected, TimeSpan Duration);

/// <summary>Contains a wake phrase detection result.</summary>
public sealed record WakeWordDetectionResult(string Phrase, double Confidence);
