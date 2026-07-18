using System.Buffers.Binary;
using DeskPilot.Voice.Abstractions;
using NAudio.Dsp;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Contains normalized owned PCM16 audio.</summary>
public sealed record NormalizedAudioBuffer(
    ReadOnlyMemory<byte> Pcm16,
    AudioFormat Format,
    TimeSpan Duration);

/// <summary>Normalizes native capture bytes for local voice providers.</summary>
public static class Pcm16Normalizer
{
    /// <summary>Converts one native buffer into mono 16 kHz signed PCM16.</summary>
    public static NormalizedAudioBuffer Normalize(ReadOnlySpan<byte> input, AudioFormat format) =>
        new Pcm16StreamingNormalizer(format).Normalize(input);

    internal static float[] DecodeMono(ReadOnlySpan<byte> input, AudioFormat format)
    {
        ValidateFormat(format);
        var bytesPerSample = format.BitsPerSample / 8;
        var blockAlign = checked(bytesPerSample * format.Channels);
        if (input.Length % blockAlign != 0)
        {
            throw UnsupportedFormat("Буфер микрофона не соответствует заявленному формату.");
        }

        var frameCount = input.Length / blockAlign;
        var monoSamples = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = (frame * blockAlign) + (channel * bytesPerSample);
                sum += ReadSample(input.Slice(sampleOffset, bytesPerSample), format);
            }

            monoSamples[frame] = (float)(sum / format.Channels);
        }

        return monoSamples;
    }

    internal static NormalizedAudioBuffer Encode(float[] normalizedSamples)
    {
        var pcm16 = new byte[normalizedSamples.Length * sizeof(short)];
        for (var index = 0; index < normalizedSamples.Length; index++)
        {
            var sample = float.IsFinite(normalizedSamples[index])
                ? Math.Clamp(normalizedSamples[index], -1f, 1f)
                : 0f;
            var scaled = sample < 0
                ? (int)MathF.Round(sample * 32_768f)
                : (int)MathF.Round(sample * 32_767f);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm16.AsSpan(index * sizeof(short), sizeof(short)),
                (short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
        }

        return new NormalizedAudioBuffer(
            pcm16,
            AudioFormat.Pcm16KhzMono,
            TimeSpan.FromSeconds(normalizedSamples.Length / 16_000d));
    }

    private static float ReadSample(ReadOnlySpan<byte> bytes, AudioFormat format)
    {
        if (format.IsFloat)
        {
            return BinaryPrimitives.ReadSingleLittleEndian(bytes);
        }

        return format.BitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32_768f,
            24 => ReadPcm24(bytes) / 8_388_608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648f,
            _ => throw UnsupportedFormat("Разрядность PCM микрофона не поддерживается."),
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    internal static void ValidateFormat(AudioFormat format)
    {
        if (format.SampleRate <= 0
            || format.Channels <= 0
            || format.Channels > 32
            || (format.IsFloat && format.BitsPerSample != 32)
            || (!format.IsFloat && format.BitsPerSample is not (16 or 24 or 32)))
        {
            throw UnsupportedFormat("Формат микрофона не поддерживается.");
        }
    }

    private static AudioCaptureException UnsupportedFormat(string message) =>
        new(AudioInputResultCode.UnsupportedFormat, message);
}

internal interface IAudioBufferNormalizer
{
    NormalizedAudioBuffer Normalize(ReadOnlySpan<byte> input);
}

/// <summary>Preserves WDL resampling state across native capture buffers.</summary>
internal sealed class Pcm16StreamingNormalizer : IAudioBufferNormalizer
{
    private const int TargetSampleRate = 16_000;
    private readonly AudioFormat _format;
    private readonly WdlResampler? _resampler;

    public Pcm16StreamingNormalizer(AudioFormat format)
    {
        Pcm16Normalizer.ValidateFormat(format);
        _format = format;
        if (format.SampleRate != TargetSampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(wantInputDriven: true);
            _resampler.SetRates(format.SampleRate, TargetSampleRate);
        }
    }

    public NormalizedAudioBuffer Normalize(ReadOnlySpan<byte> input)
    {
        var monoSamples = Pcm16Normalizer.DecodeMono(input, _format);
        if (_resampler is null || monoSamples.Length == 0)
        {
            return Pcm16Normalizer.Encode(monoSamples);
        }

        var expectedOutput = checked((int)Math.Ceiling(
            monoSamples.Length * TargetSampleRate / (double)_format.SampleRate));
        var output = new List<float>(expectedOutput + 64);
        var consumed = 0;
        while (consumed < monoSamples.Length)
        {
            var available = monoSamples.Length - consumed;
            var requested = _resampler.ResamplePrepare(
                available,
                1,
                out var inputBuffer,
                out var inputOffset);
            if (requested <= 0)
            {
                throw new AudioCaptureException(
                    AudioInputResultCode.UnsupportedFormat,
                    "Не удалось подготовить потоковый ресемплер микрофона.");
            }

            var supplied = Math.Min(requested, available);
            monoSamples.AsSpan(consumed, supplied).CopyTo(inputBuffer.AsSpan(inputOffset, supplied));
            var outputCapacity = checked((int)Math.Ceiling(
                (supplied + 2) * TargetSampleRate / (double)_format.SampleRate) + 64);
            var outputBuffer = new float[Math.Max(outputCapacity, 1)];
            var written = _resampler.ResampleOut(
                outputBuffer,
                0,
                supplied,
                outputBuffer.Length,
                1);
            if (written > 0)
            {
                output.AddRange(outputBuffer.AsSpan(0, written).ToArray());
            }

            consumed += supplied;
        }

        return Pcm16Normalizer.Encode(output.ToArray());
    }
}
