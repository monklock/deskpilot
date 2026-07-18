namespace DeskPilot.Voice.Abstractions;

/// <summary>Recognizes speech from recorded command audio.</summary>
public interface ISpeechToTextProvider
{
    /// <summary>Gets the stable provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Recognizes speech from bounded normalized command audio.</summary>
    Task<SpeechRecognitionResult> RecognizeAsync(
        CapturedCommandAudio audio,
        SpeechRecognitionOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Waits until recorded speech ends.</summary>
public interface IVoiceActivityDetector
{
    /// <summary>Captures one bounded command after speech starts.</summary>
    Task<VoiceActivityResult> CaptureAsync(
        IAudioCaptureSession input,
        VoiceActivityOptions options,
        CancellationToken cancellationToken);
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
public sealed record SpeechRecognitionOptions(
    string Language = "ru",
    double MinimumConfidence = 0.70)
{
    /// <summary>Gets the approved Russian speech-recognition defaults.</summary>
    public static SpeechRecognitionOptions Default { get; } = new();
}

/// <summary>Contains recognized speech text and confidence.</summary>
public sealed record SpeechRecognitionResult(string Text, double Confidence, bool IsFinal);

/// <summary>Identifies a safe local speech-recognition failure.</summary>
public enum SpeechRecognitionFailureCode
{
    /// <summary>The selected model is missing, corrupt, or incompatible.</summary>
    ModelUnavailable,
    /// <summary>The captured command audio format is unsupported.</summary>
    UnsupportedFormat,
    /// <summary>The provider returned no final recognized text.</summary>
    NoText,
    /// <summary>The final recognition confidence is below the configured threshold.</summary>
    ConfidenceBelowThreshold,
    /// <summary>The local provider failed during recognition.</summary>
    ProviderFailure,
}

/// <summary>Represents a typed safe local speech-recognition failure.</summary>
public sealed class SpeechRecognitionException : Exception
{
    /// <summary>Creates a typed speech-recognition failure.</summary>
    public SpeechRecognitionException(
        SpeechRecognitionFailureCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the safe failure code.</summary>
    public SpeechRecognitionFailureCode Code { get; }
}

/// <summary>Identifies a short local voice-pipeline feedback signal.</summary>
public enum VoiceSignal
{
    /// <summary>The assistant is ready to capture a command.</summary>
    Ready,
    /// <summary>Local command recognition completed.</summary>
    Success,
    /// <summary>Local command recognition failed.</summary>
    Failure,
}

/// <summary>Plays short local feedback without user-supplied audio files.</summary>
public interface IVoiceSignalService
{
    /// <summary>Plays one fixed local feedback signal without blocking the caller.</summary>
    Task PlayAsync(VoiceSignal signal, CancellationToken cancellationToken);
}

/// <summary>Configures speech end detection.</summary>
public sealed record VoiceActivityOptions(
    TimeSpan MinimumSpeechDuration,
    TimeSpan SilenceTimeout,
    TimeSpan MaximumCommandDuration,
    double Sensitivity = 0.80)
{
    /// <summary>Gets the DeskPilot default voice activity configuration.</summary>
    public static VoiceActivityOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromSeconds(10),
        0.80);
}

/// <summary>Contains a voice activity detection result.</summary>
public sealed record VoiceActivityResult(
    bool SpeechDetected,
    TimeSpan Duration,
    CapturedCommandAudio? Audio);

/// <summary>Contains a wake phrase detection result.</summary>
public sealed record WakeWordDetectionResult(string Phrase, double Confidence);
