using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class EnergyVoiceActivityDetectorTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(250, true)]
    [InlineData(300, true)]
    public async Task CaptureAsync_EnforcesMinimumSpeech(int speechMs, bool expected)
    {
        await using var audio = FakeCaptureSession.FromPcm(
            TestPcm.Concat(
                TestPcm.Silence(120),
                TestPcm.Speech(speechMs, amplitude: 0.25),
                TestPcm.Silence(900)));
        var detector = new EnergyVoiceActivityDetector();

        var result = await detector.CaptureAsync(
            audio,
            VoiceActivityOptions.Default,
            CancellationToken.None);

        result.SpeechDetected.Should().Be(expected);
        if (expected)
        {
            result.Audio.Should().NotBeNull();
            result.Audio!.Duration.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        }
        else
        {
            result.Audio.Should().BeNull();
        }

        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_DiscardsLeadingSilenceAndStopsAtTrailingSilence()
    {
        await using var audio = FakeCaptureSession.FromPcm(
            TestPcm.Concat(
                TestPcm.Silence(200),
                TestPcm.Speech(300, amplitude: 0.25),
                TestPcm.Silence(1_200),
                TestPcm.Speech(300, amplitude: 0.25)));
        var detector = new EnergyVoiceActivityDetector();

        var result = await detector.CaptureAsync(
            audio,
            VoiceActivityOptions.Default,
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(1_200));
        result.Audio.Should().NotBeNull();
        result.Audio!.Duration.Should().Be(TimeSpan.FromMilliseconds(1_200));
        result.Audio.Pcm16.Length.Should().Be(38_400);
        audio.ReadFrameCount.Should().Be(70);
    }

    [Theory]
    [InlineData(0.90, true)]
    [InlineData(0.65, false)]
    public async Task CaptureAsync_MapsSensitivityInverselyToEnergyThreshold(
        double sensitivity,
        bool expected)
    {
        await using var audio = FakeCaptureSession.FromPcm(
            TestPcm.Concat(
                TestPcm.Speech(300, amplitude: 0.03),
                TestPcm.Silence(900)));
        var detector = new EnergyVoiceActivityDetector();
        var options = VoiceActivityOptions.Default with { Sensitivity = sensitivity };

        var result = await detector.CaptureAsync(audio, options, CancellationToken.None);

        result.SpeechDetected.Should().Be(expected);
    }

    [Fact]
    public async Task CaptureAsync_ContinuousSpeech_HardStopsAtMaximumDuration()
    {
        await using var audio = FakeCaptureSession.FromPcm(TestPcm.Speech(12_000, 0.25));
        var detector = new EnergyVoiceActivityDetector();

        var result = await detector.CaptureAsync(
            audio,
            VoiceActivityOptions.Default,
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.FromSeconds(10));
        result.Audio.Should().NotBeNull();
        result.Audio!.Pcm16.Length.Should().Be(320_000);
    }

    [Fact]
    public async Task CaptureAsync_LeadingSilence_HardStopsAsNoSpeechTimeout()
    {
        await using var audio = FakeCaptureSession.FromPcm(TestPcm.Silence(12_000));
        var detector = new EnergyVoiceActivityDetector();

        var result = await detector.CaptureAsync(
            audio,
            VoiceActivityOptions.Default,
            CancellationToken.None);

        result.SpeechDetected.Should().BeFalse();
        result.Duration.Should().Be(TimeSpan.FromSeconds(10));
        result.Audio.Should().BeNull();
        audio.ReadFrameCount.Should().Be(500);
    }

    [Fact]
    public async Task CaptureAsync_ArbitraryCaptureBufferSizes_ReframesPcmIntoTwentyMilliseconds()
    {
        await using var audio = FakeCaptureSession.FromPcm(
            TestPcm.Concat(
                TestPcm.Speech(300, 0.25),
                TestPcm.Silence(900)),
            chunkSize: 137);
        var detector = new EnergyVoiceActivityDetector();

        var result = await detector.CaptureAsync(
            audio,
            VoiceActivityOptions.Default,
            CancellationToken.None);

        result.SpeechDetected.Should().BeTrue();
        result.Audio.Should().NotBeNull();
        result.Audio!.Duration.Should().Be(TimeSpan.FromMilliseconds(1_200));
    }

    [Fact]
    public async Task CaptureAsync_UnsupportedFormat_RejectsBeforeReadingAudio()
    {
        await using var audio = FakeCaptureSession.FromPcm(
            TestPcm.Silence(20),
            format: new AudioFormat(48_000, 2, 32, true));
        var detector = new EnergyVoiceActivityDetector();

        var action = () => detector.CaptureAsync(
            audio,
            VoiceActivityOptions.Default,
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        audio.ReadFrameCount.Should().Be(0);
    }

    private static class TestPcm
    {
        public static byte[] Silence(int durationMs) =>
            new byte[SamplesFor(durationMs) * sizeof(short)];

        public static byte[] Speech(int durationMs, double amplitude)
        {
            var sample = (short)Math.Round(short.MaxValue * amplitude);
            var pcm = new byte[SamplesFor(durationMs) * sizeof(short)];
            for (var offset = 0; offset < pcm.Length; offset += sizeof(short))
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset, sizeof(short)), sample);
            }

            return pcm;
        }

        public static byte[] Concat(params byte[][] segments) =>
            segments.SelectMany(segment => segment).ToArray();

        private static int SamplesFor(int durationMs) =>
            checked(16_000 * durationMs / 1_000);
    }

    private sealed class FakeCaptureSession(
        byte[] pcm16,
        int chunkSize,
        AudioFormat format) : IAudioCaptureSession
    {
        public string EndpointId => "vad-test-microphone";

        public AudioFormat Format { get; } = format;

        public int ReadFrameCount { get; private set; }

        public int DisposeCount { get; private set; }

        public static FakeCaptureSession FromPcm(
            byte[] pcm16,
            int chunkSize = 640,
            AudioFormat? format = null) =>
            new(pcm16, chunkSize, format ?? AudioFormat.Pcm16KhzMono);

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < pcm16.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(chunkSize, pcm16.Length - offset);
                var frame = new byte[length];
                Buffer.BlockCopy(pcm16, offset, frame, 0, length);
                ReadFrameCount++;
                await Task.Yield();
                yield return new AudioFrame(
                    frame,
                    TimeSpan.FromSeconds(length / 32_000d));
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
