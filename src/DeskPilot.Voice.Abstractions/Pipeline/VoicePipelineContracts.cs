namespace DeskPilot.Voice.Abstractions;

/// <summary>Contains persistent voice configuration.</summary>
public sealed record VoiceSettings(
    bool IsEnabled,
    string? MicrophoneEndpointId,
    string? MicrophoneFriendlyName,
    string WakePhrase,
    double WakeConfidence,
    TimeSpan Cooldown,
    string RecognitionLanguage)
{
    /// <summary>Gets the approved default voice configuration.</summary>
    public static VoiceSettings Default { get; } = new(
        false,
        null,
        null,
        "альфа",
        0.80,
        TimeSpan.FromSeconds(2),
        "ru");
}

/// <summary>Stores persistent voice configuration.</summary>
public interface IVoiceSettingsRepository
{
    /// <summary>Gets voice configuration.</summary>
    Task<VoiceSettings> GetAsync(CancellationToken cancellationToken);

    /// <summary>Saves voice configuration.</summary>
    Task SaveAsync(VoiceSettings settings, CancellationToken cancellationToken);
}
