using System.Buffers;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Stores a bounded, sequence-indexed PCM16 audio history.</summary>
internal sealed class VoiceAudioRingBuffer : IDisposable
{
    private const int BytesPerSample = sizeof(short);
    private readonly ArrayPool<byte> _pool;
    private readonly byte[] _buffer;
    private readonly int _capacityBytes;
    private readonly object _sync = new();
    private long _latestSampleOffset;
    private int _storedBytes;
    private int _writeIndex;
    private int _disposed;

    internal VoiceAudioRingBuffer(int capacityBytes, ArrayPool<byte>? pool = null)
    {
        if (capacityBytes <= 0 || capacityBytes % BytesPerSample != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        }

        _pool = pool ?? ArrayPool<byte>.Shared;
        _capacityBytes = capacityBytes;
        _buffer = _pool.Rent(capacityBytes);
        Array.Clear(_buffer);
    }

    internal long LatestSampleOffset
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _latestSampleOffset;
            }
        }
    }

    internal long EarliestSampleOffset
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return EarliestSampleOffsetUnsafe();
            }
        }
    }

    internal void Append(ReadOnlySpan<byte> pcm16)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (pcm16.Length % BytesPerSample != 0)
            {
                throw new AudioCaptureException(
                    AudioInputResultCode.UnsupportedFormat,
                    "PCM16 frame ended with an incomplete sample.");
            }

            var nextLatestSampleOffset = checked(
                _latestSampleOffset + (pcm16.Length / BytesPerSample));
            foreach (var value in pcm16)
            {
                if (_storedBytes == _capacityBytes)
                {
                    _buffer[_writeIndex] = 0;
                }

                _buffer[_writeIndex] = value;
                _writeIndex = (_writeIndex + 1) % _capacityBytes;
                _storedBytes = Math.Min(_storedBytes + 1, _capacityBytes);
            }

            _latestSampleOffset = nextLatestSampleOffset;
        }
    }

    internal byte[] Read(long startSampleOffset, int maximumSamples)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var earliestSampleOffset = EarliestSampleOffsetUnsafe();
            var latestSampleOffset = _latestSampleOffset;
            if (startSampleOffset < earliestSampleOffset)
            {
                throw new AudioCaptureException(
                    AudioInputResultCode.BufferOverrun,
                    "Requested voice audio has already been overwritten.");
            }

            if (maximumSamples <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSamples));
            }

            if (startSampleOffset > latestSampleOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(startSampleOffset));
            }

            var samples = (int)Math.Min(maximumSamples, latestSampleOffset - startSampleOffset);
            var result = new byte[samples * BytesPerSample];
            var oldestIndex = (_writeIndex - _storedBytes + _capacityBytes) % _capacityBytes;
            var relativeBytes = checked((int)((startSampleOffset - earliestSampleOffset) * BytesPerSample));
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = _buffer[(oldestIndex + relativeBytes + index) % _capacityBytes];
            }

            return result;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            Array.Clear(_buffer);
            _pool.Return(_buffer, clearArray: false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private long EarliestSampleOffsetUnsafe() =>
        _latestSampleOffset - (_storedBytes / BytesPerSample);
}
