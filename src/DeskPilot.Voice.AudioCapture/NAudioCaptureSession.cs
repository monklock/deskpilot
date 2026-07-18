using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Owns one native NAudio capture client.</summary>
public sealed class NAudioCaptureSession : IAudioCaptureSession
{
    private readonly IWindowsCaptureClient _client;
    private readonly IAudioBufferNormalizer _normalizer;
    private readonly Channel<byte[]> _nativeBuffers = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Task _processingTask;
    private int _nativeCleaned;
    private int _disposed;

    /// <summary>Creates and starts a capture session.</summary>
    public NAudioCaptureSession(IWindowsCaptureClient client)
        : this(client, normalizer: null)
    {
    }

    internal NAudioCaptureSession(
        IWindowsCaptureClient client,
        IAudioBufferNormalizer? normalizer)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        EndpointId = client.EndpointId;
        try
        {
            _normalizer = normalizer ?? new Pcm16StreamingNormalizer(client.NativeFormat);
            _client.DataAvailable += OnDataAvailable;
            _client.RecordingStopped += OnRecordingStopped;
            _processingTask = Task.Run(ProcessBuffersAsync);
            _client.Start();
        }
        catch (AudioCaptureException)
        {
            _nativeBuffers.Writer.TryComplete();
            _frames.Writer.TryComplete();
            CleanupNative(stop: false);
            throw;
        }
        catch (Exception exception)
        {
            _nativeBuffers.Writer.TryComplete();
            _frames.Writer.TryComplete();
            CleanupNative(stop: false);
            throw new AudioCaptureException(
                AudioInputResultCode.InitializationFailed,
                "Не удалось запустить выбранный микрофон.",
                exception);
        }
    }

    /// <inheritdoc />
    public string EndpointId { get; }

    /// <inheritdoc />
    public AudioFormat Format => AudioFormat.Pcm16KhzMono;

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _nativeBuffers.Writer.TryComplete();
            CleanupNative(stop: true);
            await _processingTask.ConfigureAwait(false);
            _frames.Writer.TryComplete();
        }
    }

    private void OnDataAvailable(object? sender, WindowsCaptureDataEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (eventArgs.BytesRecorded < 0 || eventArgs.BytesRecorded > eventArgs.Buffer.Length)
        {
            CompleteWithFailure(new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Микрофон вернул некорректный размер аудиобуфера."));
            return;
        }

        var callbackCopy = eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded).ToArray();
        _nativeBuffers.Writer.TryWrite(callbackCopy);
    }

    private void OnRecordingStopped(object? sender, WindowsCaptureStoppedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CompleteWithFailure(new AudioCaptureException(
            AudioInputResultCode.Disconnected,
            "Выбранный микрофон отключён.",
            eventArgs.Exception));
    }

    private async Task ProcessBuffersAsync()
    {
        try
        {
            await foreach (var buffer in _nativeBuffers.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var normalized = _normalizer.Normalize(buffer);
                if (!normalized.Pcm16.IsEmpty)
                {
                    _frames.Writer.TryWrite(new AudioFrame(normalized.Pcm16, normalized.Duration));
                }
            }

            _frames.Writer.TryComplete();
        }
        catch (AudioCaptureException exception)
        {
            CompleteWithFailure(exception);
        }
        catch (Exception exception)
        {
            CompleteWithFailure(new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Не удалось преобразовать формат микрофона.",
                exception));
        }
    }

    private void CompleteWithFailure(AudioCaptureException exception)
    {
        _frames.Writer.TryComplete(exception);
        _nativeBuffers.Writer.TryComplete();
        CleanupNative(stop: true);
    }

    private void CleanupNative(bool stop)
    {
        if (Interlocked.Exchange(ref _nativeCleaned, 1) != 0)
        {
            return;
        }

        _client.DataAvailable -= OnDataAvailable;
        _client.RecordingStopped -= OnRecordingStopped;
        try
        {
            if (stop)
            {
                _client.Stop();
            }
        }
        finally
        {
            _client.Dispose();
        }
    }
}
