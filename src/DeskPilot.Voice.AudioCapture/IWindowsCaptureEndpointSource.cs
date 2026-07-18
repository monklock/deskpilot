using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Provides testable Windows capture endpoint snapshots and change signals.</summary>
public interface IWindowsCaptureEndpointSource
{
    /// <summary>Gets active capture endpoints and the current multimedia default marker.</summary>
    Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Watches coalesced Windows endpoint-change signals.</summary>
    IAsyncEnumerable<bool> WatchChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Contains copied native capture bytes.</summary>
public sealed class WindowsCaptureDataEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    /// <summary>Gets the callback buffer, which is valid only for the event duration.</summary>
    public byte[] Buffer { get; } = buffer;

    /// <summary>Gets the number of valid bytes.</summary>
    public int BytesRecorded { get; } = bytesRecorded;
}

/// <summary>Contains the native capture stop reason.</summary>
public sealed class WindowsCaptureStoppedEventArgs(Exception? exception) : EventArgs
{
    /// <summary>Gets the native failure, if present.</summary>
    public Exception? Exception { get; } = exception;
}

/// <summary>Abstracts one native Windows capture client.</summary>
public interface IWindowsCaptureClient : IDisposable
{
    /// <summary>Gets the exact endpoint ID.</summary>
    string EndpointId { get; }

    /// <summary>Gets the native sample format.</summary>
    AudioFormat NativeFormat { get; }

    /// <summary>Occurs when native capture data is available.</summary>
    event EventHandler<WindowsCaptureDataEventArgs>? DataAvailable;

    /// <summary>Occurs when native capture stops.</summary>
    event EventHandler<WindowsCaptureStoppedEventArgs>? RecordingStopped;

    /// <summary>Starts capture.</summary>
    void Start();

    /// <summary>Requests capture stop.</summary>
    void Stop();
}

/// <summary>Creates a native capture client for an exact endpoint ID.</summary>
public interface IWindowsCaptureClientFactory
{
    /// <summary>Creates the client without falling back to another endpoint.</summary>
    IWindowsCaptureClient Create(string endpointId);
}
