using System.Text;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.WhisperCpp;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class WhisperSmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SpeechProvider_WithConfiguredLocalModelAndWave_ReturnsRussianText()
    {
        var modelPath = Environment.GetEnvironmentVariable("DESKPILOT_WHISPER_SMOKE_MODEL");
        var audioPath = Environment.GetEnvironmentVariable("DESKPILOT_WHISPER_SMOKE_AUDIO");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(audioPath))
        {
            return;
        }

        var audio = LoadWave(audioPath);
        var provider = new WhisperCppSpeechToTextProvider(
            modelPath,
            new WhisperNetClientFactory());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var result = await provider.RecognizeAsync(
            audio,
            new SpeechRecognitionOptions("ru", 0),
            timeout.Token);

        result.IsFinal.Should().BeTrue();
        result.Text.Should().NotBeNullOrWhiteSpace();
    }

    private static CapturedCommandAudio LoadWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        if (ReadFourCc(reader) != "RIFF"
            || reader.ReadUInt32() < 4
            || ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("The Whisper smoke audio must be a RIFF WAVE file.");
        }

        WaveFormat? format = null;
        byte[]? audio = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();
            if (chunkSize > int.MaxValue || stream.Position + chunkSize > stream.Length)
            {
                throw new InvalidDataException(
                    "The Whisper smoke WAVE file contains an invalid chunk.");
            }

            var chunkStart = stream.Position;
            if (chunkId == "fmt " && chunkSize >= 16)
            {
                format = new WaveFormat(
                    reader.ReadUInt16(),
                    reader.ReadUInt16(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt16(),
                    reader.ReadUInt16());
            }
            else if (chunkId == "data")
            {
                audio = reader.ReadBytes((int)chunkSize);
            }

            stream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (format != new WaveFormat(1, 1, 16_000, 32_000, 2, 16)
            || audio is null
            || audio.Length == 0
            || audio.Length % sizeof(short) != 0)
        {
            throw new InvalidDataException(
                "The Whisper smoke audio must contain mono 16 kHz PCM16 data.");
        }

        return new CapturedCommandAudio(
            audio,
            AudioFormat.Pcm16KhzMono,
            TimeSpan.FromSeconds(audio.Length / 32_000d));
    }

    private static string ReadFourCc(BinaryReader reader) =>
        new(reader.ReadChars(4));

    private sealed record WaveFormat(
        ushort Encoding,
        ushort Channels,
        uint SampleRate,
        uint BytesPerSecond,
        ushort BlockAlign,
        ushort BitsPerSample);
}
