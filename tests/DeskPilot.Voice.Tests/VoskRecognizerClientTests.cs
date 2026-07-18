using DeskPilot.Voice.Vosk;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoskRecognizerClientTests
{
    [Fact]
    public void Accept_FinalNativeResult_ParsesResultAndCopiesOnlyCurrentFrame()
    {
        var native = new FakeNativeRecognizer(
            acceptResult: true,
            resultJson: "{\"text\":\"альфа\",\"result\":[{\"conf\":0.87,\"word\":\"альфа\"}]}");
        using var client = new VoskRecognizerClient(native, new TrackingDisposable());
        var buffer = new byte[] { 99, 1, 2, 98 };

        var result = client.Accept(new ReadOnlyMemory<byte>(buffer, 1, 2));

        result.Should().Be(new VoskRecognition("альфа", 0.87, true));
        native.AcceptedBytes.Should().Equal(1, 2);
        native.ResultCallCount.Should().Be(1);
        native.PartialResultCallCount.Should().Be(0);
    }

    [Fact]
    public void Accept_PartialNativeResult_NeverReturnsActivationConfidence()
    {
        var native = new FakeNativeRecognizer(
            acceptResult: false,
            partialJson: "{\"partial\":\"альфа\"}");
        using var client = new VoskRecognizerClient(native, new TrackingDisposable());

        var result = client.Accept(new byte[] { 0, 0 });

        result.Should().Be(new VoskRecognition("альфа", 0, false));
        native.ResultCallCount.Should().Be(0);
        native.PartialResultCallCount.Should().Be(1);
    }

    [Fact]
    public void Complete_ReturnsParsedNativeFinalResult()
    {
        var native = new FakeNativeRecognizer(
            acceptResult: false,
            finalJson: "{\"text\":\"альфа\",\"result\":[{\"conf\":0.89,\"word\":\"альфа\"}]}");
        using var client = new VoskRecognizerClient(native, new TrackingDisposable());

        var result = client.Complete();

        result.Should().Be(new VoskRecognition("альфа", 0.89, true));
        native.FinalResultCallCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_CalledTwice_DisposesNativeRecognizerAndModelOnce()
    {
        var native = new FakeNativeRecognizer(false);
        var model = new TrackingDisposable();
        var client = new VoskRecognizerClient(native, model);

        client.Dispose();
        client.Dispose();

        native.DisposeCount.Should().Be(1);
        model.DisposeCount.Should().Be(1);
    }

    private sealed class FakeNativeRecognizer(
        bool acceptResult,
        string resultJson = "{}",
        string partialJson = "{}",
        string finalJson = "{}") : IVoskNativeRecognizer
    {
        public byte[] AcceptedBytes { get; private set; } = [];

        public int DisposeCount { get; private set; }

        public int ResultCallCount { get; private set; }

        public int PartialResultCallCount { get; private set; }

        public int FinalResultCallCount { get; private set; }

        public bool AcceptWaveform(byte[] buffer, int length)
        {
            AcceptedBytes = buffer[..length];
            return acceptResult;
        }

        public string Result()
        {
            ResultCallCount++;
            return resultJson;
        }

        public string PartialResult()
        {
            PartialResultCallCount++;
            return partialJson;
        }

        public string FinalResult()
        {
            FinalResultCallCount++;
            return finalJson;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
