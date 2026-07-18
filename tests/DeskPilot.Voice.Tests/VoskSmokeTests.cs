using System.Runtime.CompilerServices;
using System.Text;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.Vosk;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoskSmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WakeProvider_WithConfiguredLocalModelAndWave_DetectsAlpha()
    {
        var modelPath = Environment.GetEnvironmentVariable("DESKPILOT_VOSK_SMOKE_MODEL");
        var audioPath = Environment.GetEnvironmentVariable("DESKPILOT_VOSK_SMOKE_AUDIO");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(audioPath))
        {
            return;
        }

        await using var audio = WaveCaptureSession.Load(audioPath);
        var provider = new VoskWakeWordProvider(modelPath, new VoskRecognizerClientFactory());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.65),
            timeout.Token);

        result.Phrase.Should().Be("альфа");
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.65);
    }

    private sealed class WaveCaptureSession(byte[] pcm16) : IAudioCaptureSession
    {
        private const int FrameSize = 3_200;

        public string EndpointId => "vosk-smoke-wave";

        public AudioFormat Format => AudioFormat.Pcm16KhzMono;

        public static WaveCaptureSession Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
            if (ReadFourCc(reader) != "RIFF"
                || reader.ReadUInt32() < 4
                || ReadFourCc(reader) != "WAVE")
            {
                throw new InvalidDataException("The Vosk smoke audio must be a RIFF WAVE file.");
            }

            WaveFormat? format = null;
            byte[]? audio = null;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = ReadFourCc(reader);
                var chunkSize = reader.ReadUInt32();
                if (chunkSize > int.MaxValue || stream.Position + chunkSize > stream.Length)
                {
                    throw new InvalidDataException("The Vosk smoke WAVE file contains an invalid chunk.");
                }

                var chunkStart = stream.Position;
                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    var encoding = reader.ReadUInt16();
                    var channels = reader.ReadUInt16();
                    var sampleRate = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    format = new WaveFormat(
                        encoding,
                        channels,
                        sampleRate,
                        reader.ReadUInt16());
                }
                else if (chunkId == "data")
                {
                    audio = reader.ReadBytes((int)chunkSize);
                }

                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
            }

            if (format != new WaveFormat(1, 1, 16_000, 16)
                || audio is null
                || audio.Length % 2 != 0)
            {
                throw new InvalidDataException(
                    "The Vosk smoke audio must contain mono 16 kHz PCM16 data.");
            }

            return new WaveCaptureSession(audio);
        }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < pcm16.Length; offset += FrameSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(FrameSize, pcm16.Length - offset);
                var frame = new byte[length];
                Buffer.BlockCopy(pcm16, offset, frame, 0, length);
                await Task.Yield();
                yield return new AudioFrame(
                    frame,
                    TimeSpan.FromSeconds(length / 32_000d));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string ReadFourCc(BinaryReader reader) =>
            new(reader.ReadChars(4));

        private sealed record WaveFormat(
            ushort Encoding,
            ushort Channels,
            uint SampleRate,
            ushort BitsPerSample);
    }
}
