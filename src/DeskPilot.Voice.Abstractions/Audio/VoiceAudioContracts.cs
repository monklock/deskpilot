namespace DeskPilot.Voice.Abstractions;

/// <summary>Describes an interleaved native audio sample format.</summary>
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    /// <summary>Gets the normalized format consumed by local voice providers.</summary>
    public static AudioFormat Pcm16KhzMono { get; } = new(16_000, 1, 16, false);
}

/// <summary>Describes one Windows capture endpoint.</summary>
public sealed record AudioInputDevice(
    string EndpointId,
    string FriendlyName,
    bool IsDefault,
    bool IsAvailable);

/// <summary>Owns one normalized PCM16 audio buffer.</summary>
public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm16, TimeSpan Duration);

/// <summary>Identifies a microphone resolution or capture result.</summary>
public enum AudioInputResultCode
{
    /// <summary>The operation completed successfully.</summary>
    Success,
    /// <summary>No active capture device exists.</summary>
    NoDevice,
    /// <summary>The explicitly selected endpoint is unavailable.</summary>
    SelectedDeviceUnavailable,
    /// <summary>The native capture client could not be initialized.</summary>
    InitializationFailed,
    /// <summary>The active capture endpoint disconnected.</summary>
    Disconnected,
    /// <summary>The endpoint exposes an unsupported sample format.</summary>
    UnsupportedFormat,
}

/// <summary>Contains a safe microphone resolution result.</summary>
public sealed record AudioInputResolution(
    AudioInputResultCode Code,
    AudioInputDevice? Device,
    string? Message = null);

/// <summary>Enumerates and resolves Windows input devices.</summary>
public interface IAudioInputDeviceService
{
    /// <summary>Gets active Windows capture endpoints.</summary>
    Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Resolves an explicit endpoint or the current Windows default.</summary>
    Task<AudioInputResolution> ResolveAsync(
        string? explicitEndpointId,
        CancellationToken cancellationToken);

    /// <summary>Watches active endpoint snapshots.</summary>
    IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one active normalized capture session.</summary>
public interface IAudioCaptureSession : IAsyncDisposable
{
    /// <summary>Gets the exact Windows endpoint ID.</summary>
    string EndpointId { get; }

    /// <summary>Gets the normalized output format.</summary>
    AudioFormat Format { get; }

    /// <summary>Reads owned normalized frames until stopped or disconnected.</summary>
    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

/// <summary>Opens capture only for an explicit resolved endpoint.</summary>
public interface IAudioCaptureSessionFactory
{
    /// <summary>Opens the selected endpoint.</summary>
    Task<IAudioCaptureSession> OpenAsync(string endpointId, CancellationToken cancellationToken);
}

/// <summary>Represents a typed safe audio capture failure.</summary>
public sealed class AudioCaptureException : Exception
{
    /// <summary>Creates a capture failure.</summary>
    public AudioCaptureException(
        AudioInputResultCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the safe result code.</summary>
    public AudioInputResultCode Code { get; }
}
