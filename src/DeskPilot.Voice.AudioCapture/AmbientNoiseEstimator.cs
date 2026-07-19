using System.Buffers.Binary;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Maintains a bounded lower-percentile estimate of ambient PCM16 noise.</summary>
internal sealed class AmbientNoiseEstimator
{
    private const int FrameBytes = (16_000 / 50) * sizeof(short);
    private readonly double[] _levels;
    private readonly object _sync = new();
    private int _count;
    private int _writeIndex;

    internal AmbientNoiseEstimator(int frameCapacity = 150)
    {
        if (frameCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCapacity));
        }

        _levels = new double[frameCapacity];
    }

    internal void Observe(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length != FrameBytes)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Ambient estimator requires one 20 ms mono 16 kHz PCM16 frame.");
        }

        lock (_sync)
        {
            _levels[_writeIndex] = PcmRms.Calculate(pcm16);
            _writeIndex = (_writeIndex + 1) % _levels.Length;
            _count = Math.Min(_count + 1, _levels.Length);
        }
    }

    internal AmbientNoiseSnapshot Snapshot
    {
        get
        {
            double[] values;
            int count;
            lock (_sync)
            {
                if (_count == 0)
                {
                    return AmbientNoiseSnapshot.Empty;
                }

                count = _count;
                values = _levels.AsSpan(0, count).ToArray();
            }

            Array.Sort(values);
            var index = (int)Math.Floor((values.Length - 1) * 0.20);
            return new AmbientNoiseSnapshot(
                values[index],
                TimeSpan.FromMilliseconds(count * 20),
                count);
        }
    }
}

/// <summary>Calculates a normalized RMS level from complete PCM16 samples.</summary>
internal static class PcmRms
{
    internal static double Calculate(ReadOnlySpan<byte> pcm16)
    {
        var sampleCount = pcm16.Length / sizeof(short);
        double sumOfSquares = 0;
        for (var offset = 0; offset < pcm16.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(
                pcm16.Slice(offset, sizeof(short))) / 32_768d;
            sumOfSquares += sample * sample;
        }

        return sampleCount == 0 ? 0 : Math.Sqrt(sumOfSquares / sampleCount);
    }
}
