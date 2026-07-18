using System.Runtime.InteropServices;
using DeskPilot.Voice.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Creates WASAPI capture clients for exact Windows endpoint IDs.</summary>
public sealed class NAudioWindowsCaptureClientFactory : IWindowsCaptureClientFactory
{
    /// <inheritdoc />
    public IWindowsCaptureClient Create(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            device = GetExactDevice(enumerator, endpointId);
            return CreateClient(ref device);
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static MMDevice GetExactDevice(MMDeviceEnumerator enumerator, string endpointId)
    {
        MMDevice? device = null;
        try
        {
            device = enumerator.GetDevice(endpointId);
            if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
            {
                throw new AudioCaptureException(
                    AudioInputResultCode.SelectedDeviceUnavailable,
                    "Выбранный микрофон сейчас недоступен.");
            }

            var resolved = device;
            device = null;
            return resolved;
        }
        catch (COMException exception)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.SelectedDeviceUnavailable,
                "Выбранный микрофон отключён.",
                exception);
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static IWindowsCaptureClient CreateClient(ref MMDevice? device)
    {
        var ownedDevice = device!;
        device = null;
        return new NAudioWindowsCaptureClient(ownedDevice);
    }
}

internal sealed class NAudioWindowsCaptureClient : IWindowsCaptureClient
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private int _started;
    private int _disposed;

    public NAudioWindowsCaptureClient(MMDevice device)
    {
        _device = device;
        EndpointId = device.ID;
        try
        {
            _capture = new WasapiCapture(device);
            NativeFormat = ToAudioFormat(_capture.WaveFormat);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }
        catch
        {
            try
            {
                _capture?.Dispose();
            }
            finally
            {
                _device.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc />
    public string EndpointId { get; }

    /// <inheritdoc />
    public AudioFormat NativeFormat { get; }

    /// <inheritdoc />
    public event EventHandler<WindowsCaptureDataEventArgs>? DataAvailable;

    /// <inheritdoc />
    public event EventHandler<WindowsCaptureStoppedEventArgs>? RecordingStopped;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _capture.StartRecording();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _started, 0) != 0)
        {
            _capture.StopRecording();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            _capture.Dispose();
        }
        finally
        {
            _device.Dispose();
        }
    }

    private static AudioFormat ToAudioFormat(WaveFormat waveFormat)
    {
        var standardFormat = waveFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : waveFormat;
        var isFloat = standardFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        if (!isFloat && standardFormat.Encoding != WaveFormatEncoding.Pcm)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Формат выбранного микрофона не поддерживается.");
        }

        return new AudioFormat(
            standardFormat.SampleRate,
            standardFormat.Channels,
            standardFormat.BitsPerSample,
            isFloat);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs) =>
        DataAvailable?.Invoke(
            this,
            new WindowsCaptureDataEventArgs(eventArgs.Buffer, eventArgs.BytesRecorded));

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _started, 0);
        RecordingStopped?.Invoke(this, new WindowsCaptureStoppedEventArgs(eventArgs.Exception));
    }
}
