using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeskPilot.Voice.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Enumerates Windows Core Audio capture endpoints and coalesces device notifications.</summary>
public sealed class NAudioWindowsCaptureEndpointSource :
    IWindowsCaptureEndpointSource,
    IMMNotificationClient,
    IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _enumeratorLock = new();
    private readonly CaptureEndpointChangeBroadcaster _changes = new();
    private int _disposed;

    /// <summary>Creates the endpoint source and subscribes to Windows notifications.</summary>
    public NAudioWindowsCaptureEndpointSource() =>
        _enumerator.RegisterEndpointNotificationCallback(this);

    /// <inheritdoc />
    public Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_enumeratorLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? defaultEndpointId = null;
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
            {
                using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                defaultEndpointId = defaultDevice.ID;
            }

            var devices = new List<AudioInputDevice>();
            foreach (var endpoint in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (endpoint)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    devices.Add(new AudioInputDevice(
                        endpoint.ID,
                        endpoint.FriendlyName,
                        string.Equals(endpoint.ID, defaultEndpointId, StringComparison.Ordinal),
                        true));
                }
            }

            return Task.FromResult<IReadOnlyList<AudioInputDevice>>(devices
                .OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<bool> WatchChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in _changes.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    /// <inheritdoc />
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => SignalChanged();

    /// <inheritdoc />
    public void OnDeviceAdded(string pwstrDeviceId) => SignalChanged();

    /// <inheritdoc />
    public void OnDeviceRemoved(string deviceId) => SignalChanged();

    /// <inheritdoc />
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow is DataFlow.Capture or DataFlow.All)
        {
            SignalChanged();
        }
    }

    /// <inheritdoc />
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => SignalChanged();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _changes.Complete();
        lock (_enumeratorLock)
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }
    }

    private void SignalChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _changes.Publish();
        }
    }
}

internal sealed class CaptureEndpointChangeBroadcaster
{
    private readonly object _sync = new();
    private readonly HashSet<Channel<bool>> _subscribers = [];
    private bool _completed;

    public async IAsyncEnumerable<bool> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var subscriber = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _subscribers.Add(subscriber);
        }

        try
        {
            await foreach (var signal in subscriber.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return signal;
            }
        }
        finally
        {
            lock (_sync)
            {
                _subscribers.Remove(subscriber);
            }
        }
    }

    public void Publish()
    {
        Channel<bool>[] subscribers;
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(true);
        }
    }

    public void Complete()
    {
        Channel<bool>[] subscribers;
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            subscribers = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryComplete();
        }
    }
}
