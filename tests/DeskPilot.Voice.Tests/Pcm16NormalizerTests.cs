using System.Buffers.Binary;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class Pcm16NormalizerTests
{
    [Fact]
    public void Normalize_StereoFloat48k_ReturnsMonoPcm16At16k()
    {
        var input = CreateStereoFloat(sampleRate: 48_000, durationMilliseconds: 60);

        var result = Pcm16Normalizer.Normalize(input, new AudioFormat(48_000, 2, 32, true));

        result.Format.Should().Be(AudioFormat.Pcm16KhzMono);
        result.Pcm16.Length.Should().Be(1_920);
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public void Normalize_Pcm16Mono16k_ReturnsOwnedCopy()
    {
        var input = new byte[] { 1, 2, 3, 4 };

        var result = Pcm16Normalizer.Normalize(input, AudioFormat.Pcm16KhzMono);
        input.AsSpan().Fill(0);

        result.Pcm16.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Normalize_FloatWithInvalidBitDepth_ThrowsTypedUnsupportedFormat()
    {
        var action = () => Pcm16Normalizer.Normalize(
            new byte[16],
            new AudioFormat(48_000, 2, 64, true));

        action.Should().Throw<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public void StreamingNormalizer_SplitInput_MatchesSingleContinuousInput()
    {
        var format = new AudioFormat(48_000, 2, 32, true);
        var input = CreateStereoFloat(sampleRate: format.SampleRate, durationMilliseconds: 300);
        var singleNormalizer = new Pcm16StreamingNormalizer(format);
        var splitNormalizer = new Pcm16StreamingNormalizer(format);

        var single = singleNormalizer.Normalize(input).Pcm16.ToArray();
        var split = input
            .Chunk(format.SampleRate / 100 * format.Channels * sizeof(float))
            .SelectMany(chunk => splitNormalizer.Normalize(chunk).Pcm16.ToArray())
            .ToArray();

        split.Should().Equal(single);
    }

    private static byte[] CreateStereoFloat(int sampleRate, int durationMilliseconds)
    {
        var frameCount = sampleRate * durationMilliseconds / 1_000;
        var bytes = new byte[frameCount * 2 * sizeof(float)];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sample = 0.4f * MathF.Sin(2 * MathF.PI * 440 * frame / sampleRate);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((frame * 2) * sizeof(float), sizeof(float)), sample);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(((frame * 2) + 1) * sizeof(float), sizeof(float)), sample / 2);
        }

        return bytes;
    }
}
