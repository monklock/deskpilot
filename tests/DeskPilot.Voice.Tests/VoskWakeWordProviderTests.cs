using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.Vosk;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoskWakeWordProviderTests
{
    [Fact]
    public async Task WaitForDetectionAsync_PhraseBelowThreshold_ContinuesUntilAcceptedDetection()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new("альфа", 0.79, true),
            new("альфа", 0.91, true));
        var factory = new FakeVoskRecognizerClientFactory(recognizer);
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeCaptureSession.WithFrames(2);

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("  альфа  ", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.91));
        factory.ModelPath.Should().Be("wake-model");
        factory.GrammarJson.Should().Be("[\"альфа\"]");
        recognizer.AcceptCount.Should().Be(2);
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_HighConfidenceAmbientText_IgnoresIt()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new("погода", 0.99, true),
            new("АЛЬФА", 0.85, true));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeCaptureSession.WithFrames(2);

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Confidence.Should().Be(0.85);
        recognizer.AcceptCount.Should().Be(2);
    }

    [Fact]
    public async Task WaitForDetectionAsync_PartialPhraseAboveThreshold_DoesNotActivate()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new("альфа", 0.99, false),
            new("альфа", 0.88, true));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeCaptureSession.WithFrames(2);

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Confidence.Should().Be(0.88);
        recognizer.AcceptCount.Should().Be(2);
    }

    [Fact]
    public async Task WaitForDetectionAsync_StreamEnds_FlushesFinalRecognition()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new VoskRecognition("альфа", 0, false))
        {
            Completion = new VoskRecognition("альфа", 0.86, true),
        };
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeCaptureSession.WithFrames(1);

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.86));
        recognizer.CompleteCount.Should().Be(1);
        recognizer.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cancelled_DisposesRecognizer()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var factory = new FakeVoskRecognizerClientFactory(recognizer);
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeCaptureSession.UntilCancelled();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var detection = provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            cancellation.Token);
        await factory.Created.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await FluentActions.Awaiting(() => detection).Should().ThrowAsync<OperationCanceledException>();
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_RecognizerFails_DisposesRecognizer()
    {
        var recognizer = new FakeVoskRecognizerClient(new InvalidOperationException("native failure"));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeCaptureSession.WithFrames(1);

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("native failure");
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_CaptureFails_DisposesRecognizer()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeCaptureSession.Failing(
            new AudioCaptureException(
                AudioInputResultCode.Disconnected,
                "capture failure"));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.Disconnected);
        recognizer.CompleteCount.Should().Be(0);
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_CaptureEnds_DisposesRecognizer()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeCaptureSession.WithFrames(1);

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<EndOfStreamException>();
        recognizer.CompleteCount.Should().Be(1);
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_UnsupportedAudioFormat_RejectsBeforeCreatingRecognizer()
    {
        var factory = new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient());
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeCaptureSession.WithFormat(new AudioFormat(48_000, 2, 32, true));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        factory.CreateCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0.64)]
    [InlineData(0.91)]
    [InlineData(double.NaN)]
    public async Task WaitForDetectionAsync_ThresholdOutsideSupportedRange_RejectsOptions(
        double threshold)
    {
        var factory = new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient());
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeCaptureSession.WithFrames(1);

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", threshold),
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public void ProviderId_IsStableVoskIdentifier()
    {
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient()));

        provider.ProviderId.Should().Be("vosk");
    }

    private sealed class FakeVoskRecognizerClientFactory(
        FakeVoskRecognizerClient recognizer) : IVoskRecognizerClientFactory
    {
        public TaskCompletionSource Created { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount { get; private set; }

        public string? ModelPath { get; private set; }

        public string? GrammarJson { get; private set; }

        public IVoskRecognizerClient Create(string modelPath, string grammarJson)
        {
            CreateCount++;
            ModelPath = modelPath;
            GrammarJson = grammarJson;
            Created.TrySetResult();
            return recognizer;
        }
    }

    private sealed class FakeVoskRecognizerClient : IVoskRecognizerClient
    {
        private readonly Queue<VoskRecognition> _results;
        private readonly Exception? _failure;

        public FakeVoskRecognizerClient(params VoskRecognition[] results) =>
            _results = new Queue<VoskRecognition>(results);

        public FakeVoskRecognizerClient(Exception failure)
        {
            _failure = failure;
            _results = new Queue<VoskRecognition>();
        }

        public int AcceptCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int CompleteCount { get; private set; }

        public VoskRecognition Completion { get; init; } =
            new(string.Empty, 0, true);

        public VoskRecognition Accept(ReadOnlyMemory<byte> pcm16)
        {
            AcceptCount++;
            if (_failure is not null)
            {
                throw _failure;
            }

            return _results.Count > 0
                ? _results.Dequeue()
                : new VoskRecognition(string.Empty, 0, false);
        }

        public VoskRecognition Complete()
        {
            CompleteCount++;
            return Completion;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeCaptureSession : IAudioCaptureSession
    {
        private readonly IReadOnlyList<AudioFrame> _frames;
        private readonly bool _waitForCancellation;
        private readonly Exception? _failure;

        private FakeCaptureSession(
            AudioFormat format,
            IReadOnlyList<AudioFrame> frames,
            bool waitForCancellation,
            Exception? failure = null)
        {
            Format = format;
            _frames = frames;
            _waitForCancellation = waitForCancellation;
            _failure = failure;
        }

        public string EndpointId => "test-microphone";

        public AudioFormat Format { get; }

        public int DisposeCount { get; private set; }

        public static FakeCaptureSession WithFrames(int count) => new(
            AudioFormat.Pcm16KhzMono,
            Enumerable.Range(0, count)
                .Select(_ => new AudioFrame(new byte[] { 0, 0 }, TimeSpan.FromTicks(625)))
                .ToArray(),
            waitForCancellation: false);

        public static FakeCaptureSession WithFormat(AudioFormat format) =>
            new(format, [], waitForCancellation: false);

        public static FakeCaptureSession UntilCancelled() =>
            new(AudioFormat.Pcm16KhzMono, [], waitForCancellation: true);

        public static FakeCaptureSession Failing(Exception failure) =>
            new(
                AudioFormat.Pcm16KhzMono,
                [],
                waitForCancellation: false,
                failure);

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var frame in _frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return frame;
            }

            if (_failure is not null)
            {
                throw _failure;
            }

            if (_waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
