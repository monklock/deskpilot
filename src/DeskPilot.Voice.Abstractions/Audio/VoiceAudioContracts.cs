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

/// <summary>Owns a sequenced normalized PCM16 audio frame.</summary>
public sealed record SequencedAudioFrame
{
    /// <summary>Creates a sequenced normalized PCM16 audio frame.</summary>
    public SequencedAudioFrame(
        ReadOnlyMemory<byte> pcm16,
        TimeSpan duration,
        long startSampleOffset,
        long endSampleOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(endSampleOffset);
        if (startSampleOffset > endSampleOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endSampleOffset),
                "End sample offset must not precede the start sample offset.");
        }

        Pcm16 = pcm16;
        Duration = duration;
        StartSampleOffset = startSampleOffset;
        EndSampleOffset = endSampleOffset;
    }

    /// <summary>Gets the frame PCM16 data.</summary>
    public ReadOnlyMemory<byte> Pcm16 { get; }

    /// <summary>Gets the frame duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets the absolute sample offset at which this frame starts.</summary>
    public long StartSampleOffset { get; }

    /// <summary>Gets the absolute sample offset at which this frame ends.</summary>
    public long EndSampleOffset { get; }
}

/// <summary>Describes the ambient noise observed by a buffered capture session.</summary>
public sealed record AmbientNoiseSnapshot(
    double NoiseFloorRms,
    TimeSpan WindowDuration,
    int FrameCount)
{
    /// <summary>Gets an empty noise snapshot.</summary>
    public static AmbientNoiseSnapshot Empty { get; } = new(0.01, TimeSpan.Zero, 0);
}

/// <summary>Owns bounded normalized command audio captured entirely in memory.</summary>
public sealed record CapturedCommandAudio(
    ReadOnlyMemory<byte> Pcm16,
    AudioFormat Format,
    TimeSpan Duration)
{
    /// <summary>Opens an isolated read-only copy of the captured audio.</summary>
    public Stream OpenRead() => new MemoryStream(Pcm16.ToArray(), writable: false);
}

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
    /// <summary>Buffered audio was overwritten before a cursor could consume it.</summary>
    BufferOverrun,
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

/// <summary>Reads a sequenced view of a buffered voice capture session.</summary>
public interface IVoiceAudioCursor : IAsyncDisposable
{
    /// <summary>Gets the normalized output format.</summary>
    AudioFormat Format { get; }

    /// <summary>Gets the absolute sample offset at which this cursor starts.</summary>
    long StartSampleOffset { get; }

    /// <summary>Reads sequenced frames until stopped, disconnected, or overrun.</summary>
    /// <exception cref="AudioCaptureException">
    /// Thrown with <see cref="AudioCaptureException.Code"/> equal to
    /// <see cref="AudioInputResultCode.BufferOverrun"/> when the requested cursor position has been
    /// overwritten, including when the cursor was opened before the session's earliest sample offset.
    /// </exception>
    IAsyncEnumerable<SequencedAudioFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one endpoint capture session with buffered cursor access.</summary>
public interface IBufferedVoiceCaptureSession : IAsyncDisposable
{
    /// <summary>Gets the exact Windows endpoint ID.</summary>
    string EndpointId { get; }

    /// <summary>Gets the normalized output format.</summary>
    AudioFormat Format { get; }

    /// <summary>Gets the earliest sample offset currently available in the buffer.</summary>
    long EarliestSampleOffset { get; }

    /// <summary>Gets the latest sample offset currently available in the buffer.</summary>
    long LatestSampleOffset { get; }

    /// <summary>Gets the latest ambient noise snapshot.</summary>
    AmbientNoiseSnapshot NoiseSnapshot { get; }

    /// <summary>Opens a cursor at an absolute sample offset.</summary>
    IVoiceAudioCursor OpenCursor(long startSampleOffset);
}

/// <summary>Opens buffered continuous capture sessions for selected endpoints.</summary>
public interface IBufferedVoiceCaptureSessionFactory
{
    /// <summary>Opens the selected endpoint.</summary>
    Task<IBufferedVoiceCaptureSession> OpenAsync(string endpointId, CancellationToken cancellationToken);
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
