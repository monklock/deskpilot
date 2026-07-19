using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Owns one continuous capture pump and bounded sequenced PCM16 history.</summary>
public sealed class BufferedVoiceCaptureSession : IBufferedVoiceCaptureSession
{
    private const int RingCapacityBytes = 64_000;
    private const int CursorFrameSamples = 320;
    private const int AmbientFrameBytes = CursorFrameSamples * sizeof(short);
    private readonly object _gate = new();
    private readonly IAudioCaptureSession _source;
    private readonly VoiceAudioRingBuffer _ring;
    private readonly AmbientNoiseEstimator _noise = new();
    private readonly byte[] _ambientFrame = new byte[AmbientFrameBytes];
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _pumpTask;
    private readonly TaskCompletionSource _disposeCompletion = NewPulse();
    private TaskCompletionSource _pulse = NewPulse();
    private AudioCaptureException? _terminalFailure;
    private int _ambientFrameBytes;
    private bool _pumpCompleted;
    private int _disposeStarted;

    /// <summary>Creates a buffered owner over one normalized raw capture session.</summary>
    public BufferedVoiceCaptureSession(IAudioCaptureSession source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format != AudioFormat.Pcm16KhzMono)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Buffered voice capture requires mono 16 kHz PCM16 audio.");
        }

        _source = source;
        _ring = new VoiceAudioRingBuffer(RingCapacityBytes);
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <inheritdoc />
    public string EndpointId => _source.EndpointId;

    /// <inheritdoc />
    public AudioFormat Format => _source.Format;

    /// <inheritdoc />
    public long EarliestSampleOffset
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _ring.EarliestSampleOffset;
            }
        }
    }

    /// <inheritdoc />
    public long LatestSampleOffset
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _ring.LatestSampleOffset;
            }
        }
    }

    /// <inheritdoc />
    public AmbientNoiseSnapshot NoiseSnapshot
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _noise.Snapshot;
            }
        }
    }

    /// <inheritdoc />
    public IVoiceAudioCursor OpenCursor(long startSampleOffset)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (startSampleOffset < _ring.EarliestSampleOffset
                || startSampleOffset > _ring.LatestSampleOffset)
            {
                throw BufferPositionFailure();
            }

            return new Cursor(this, startSampleOffset);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _ = CompleteDisposalAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task PumpAsync()
    {
        AudioCaptureException? failure = null;
        try
        {
            await foreach (var frame in _source.ReadFramesAsync(_pumpCancellation.Token)
                               .ConfigureAwait(false))
            {
                TaskCompletionSource pulse;
                lock (_gate)
                {
                    if (Volatile.Read(ref _disposeStarted) != 0)
                    {
                        return;
                    }

                    _ring.Append(frame.Pcm16.Span);
                    ObserveAmbientFrames(frame.Pcm16.Span);
                    pulse = RotatePulse();
                }

                pulse.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (_pumpCancellation.IsCancellationRequested)
        {
        }
        catch (AudioCaptureException exception)
        {
            failure = exception;
        }
        catch (Exception exception)
        {
            failure = new AudioCaptureException(
                AudioInputResultCode.InitializationFailed,
                "The normalized microphone stream failed.",
                exception);
        }
        finally
        {
            TaskCompletionSource pulse;
            lock (_gate)
            {
                _terminalFailure = failure;
                _pumpCompleted = true;
                pulse = RotatePulse();
            }

            pulse.TrySetResult();
        }
    }

    private void ObserveAmbientFrames(ReadOnlySpan<byte> pcm16)
    {
        var sourceOffset = 0;
        while (sourceOffset < pcm16.Length)
        {
            var bytesToCopy = Math.Min(
                AmbientFrameBytes - _ambientFrameBytes,
                pcm16.Length - sourceOffset);
            pcm16.Slice(sourceOffset, bytesToCopy)
                .CopyTo(_ambientFrame.AsSpan(_ambientFrameBytes));
            sourceOffset += bytesToCopy;
            _ambientFrameBytes += bytesToCopy;
            if (_ambientFrameBytes == AmbientFrameBytes)
            {
                _noise.Observe(_ambientFrame);
                Array.Clear(_ambientFrame);
                _ambientFrameBytes = 0;
            }
        }
    }

    private CursorReadResult Read(long position)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return CursorReadResult.CompletedResult;
            }

            if (position < _ring.EarliestSampleOffset)
            {
                throw BufferPositionFailure();
            }

            if (position < _ring.LatestSampleOffset)
            {
                var pcm16 = _ring.Read(position, CursorFrameSamples);
                var sampleCount = pcm16.Length / sizeof(short);
                return CursorReadResult.FrameResult(new SequencedAudioFrame(
                    pcm16,
                    TimeSpan.FromSeconds(sampleCount / 16_000d),
                    position,
                    position + sampleCount));
            }

            if (_terminalFailure is not null)
            {
                return CursorReadResult.FailureResult(_terminalFailure);
            }

            return _pumpCompleted
                ? CursorReadResult.CompletedResult
                : CursorReadResult.WaitResult(_pulse.Task);
        }
    }

    private async Task CompleteDisposalAsync()
    {
        Exception? failure = null;
        try
        {
            _pumpCancellation.Cancel();
            try
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            TaskCompletionSource pulse;
            lock (_gate)
            {
                _pumpCompleted = true;
                pulse = RotatePulse();
                Array.Clear(_ambientFrame);
                _ambientFrameBytes = 0;
                _ring.Dispose();
            }

            pulse.TrySetResult();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            try
            {
                _pumpCancellation.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(failure);
            }
        }
    }

    private TaskCompletionSource RotatePulse()
    {
        var current = _pulse;
        _pulse = NewPulse();
        return current;
    }

    private static TaskCompletionSource NewPulse() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AudioCaptureException BufferPositionFailure() =>
        new(
            AudioInputResultCode.BufferOverrun,
            "Requested voice audio is outside the available buffer range.");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

    private sealed class Cursor(BufferedVoiceCaptureSession owner, long startSampleOffset)
        : IVoiceAudioCursor
    {
        private readonly TaskCompletionSource _disposeSignal = NewPulse();
        private int _disposed;

        public AudioFormat Format => owner.Format;

        public long StartSampleOffset { get; } = startSampleOffset;

        public async IAsyncEnumerable<SequencedAudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var position = StartSampleOffset;
            while (Volatile.Read(ref _disposed) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = owner.Read(position);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    yield break;
                }

                if (result.Frame is not null)
                {
                    position = result.Frame.EndSampleOffset;
                    yield return result.Frame;
                    continue;
                }

                if (result.Failure is not null)
                {
                    throw result.Failure;
                }

                if (result.Completed)
                {
                    yield break;
                }

                await Task.WhenAny(result.Pulse!, _disposeSignal.Task)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _disposeSignal.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record CursorReadResult(
        SequencedAudioFrame? Frame,
        AudioCaptureException? Failure,
        Task? Pulse,
        bool Completed)
    {
        internal static CursorReadResult CompletedResult { get; } = new(null, null, null, true);

        internal static CursorReadResult FrameResult(SequencedAudioFrame frame) =>
            new(frame, null, null, false);

        internal static CursorReadResult FailureResult(AudioCaptureException failure) =>
            new(null, failure, null, false);

        internal static CursorReadResult WaitResult(Task pulse) =>
            new(null, null, pulse, false);
    }
}
