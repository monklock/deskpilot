using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.Vosk;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoskWakeWordProviderTests
{
    [Fact]
    public async Task WaitForDetectionAsync_Cursor_MapsTimedWakeWordToAbsoluteOffsets()
    {
        var recognizer = new FakeVoskRecognizerClient(
            TimedRecognition("альфа", 0.94, 0.50, 1.10));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            32_000,
            new SequencedAudioFrame(
                new byte[] { 0, 0 },
                TimeSpan.FromMilliseconds(20),
                32_000,
                51_200));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult(
            "альфа",
            0.94,
            40_000,
            49_600,
            51_200));
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_WakeEndBeyondDetection_ThrowsInvalidDataException()
    {
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(
                new FakeVoskRecognizerClient(TimedRecognition("альфа", 0.94, 0.50, 1.10))));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            32_000,
            new SequencedAudioFrame(
                new byte[] { 0, 0 },
                TimeSpan.FromMilliseconds(20),
                32_000,
                48_000));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Vosk returned invalid wake-word timing.*");
        audio.DisposeCount.Should().Be(0);
    }

    public static TheoryData<VoskRecognition> InvalidExactWakeResults => new()
    {
        {
            new VoskRecognition(
                "альфа",
                0.94,
                true,
                [new VoskWordTiming("бета", 0.94, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.1))])
        },
        {
            new VoskRecognition(
                "альфа",
                0.94,
                true,
                [
                    new VoskWordTiming("аль", 0.94, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.8)),
                    new VoskWordTiming("фа", 0.93, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.1)),
                ])
        },
        { new VoskRecognition("альфа", 0.94, true) },
    };

    [Theory]
    [MemberData(nameof(InvalidExactWakeResults))]
    public async Task WaitForDetectionAsync_Cursor_FinalExactTextWithoutOneExactTimedToken_Throws(
        VoskRecognition recognition)
    {
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient(recognition)));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            32_000,
            new SequencedAudioFrame(
                new byte[] { 0, 0 },
                TimeSpan.FromMilliseconds(20),
                32_000,
                51_200));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Vosk returned invalid wake-word timing.*");
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_RoundsFractionalSamplesAwayFromZero()
    {
        var recognition = new VoskRecognition(
            "альфа",
            0.94,
            true,
            [new VoskWordTiming("альфа", 0.94, TimeSpan.FromTicks(313), TimeSpan.FromTicks(938))]);
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient(recognition)));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            12_345,
            new SequencedAudioFrame(
                new byte[] { 0, 0 },
                TimeSpan.FromMilliseconds(20),
                12_345,
                12_665));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.WakeStartSampleOffset.Should().Be(12_346);
        result.WakeEndSampleOffset.Should().Be(12_347);
        result.DetectionSampleOffset.Should().Be(12_665);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_AbsoluteOffsetOverflow_ThrowsInvalidDataException()
    {
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(
                new FakeVoskRecognizerClient(TimedRecognition("альфа", 0.94, 0.01, 0.02))));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            long.MaxValue - 100,
            new SequencedAudioFrame(
                new byte[] { 0, 0 },
                TimeSpan.FromMilliseconds(20),
                long.MaxValue - 100,
                long.MaxValue));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Vosk returned invalid wake-word timing.*");
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_CompletedFinalResultUsesLastSequencedFrameEnd()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new VoskRecognition(string.Empty, 0, false),
            new VoskRecognition(string.Empty, 0, false))
        {
            Completion = TimedRecognition("альфа", 0.92, 0.01, 0.03),
        };
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            5_000,
            new SequencedAudioFrame(new byte[] { 0, 0 }, TimeSpan.FromMilliseconds(20), 5_000, 5_320),
            new SequencedAudioFrame(new byte[] { 0, 0 }, TimeSpan.FromMilliseconds(20), 5_320, 5_640));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.92, 5_160, 5_480, 5_640));
        recognizer.AcceptCount.Should().Be(2);
        recognizer.CompleteCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_CancelledDisposesRecognizerButNotCursor()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var factory = new FakeVoskRecognizerClientFactory(recognizer);
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeVoiceAudioCursor.UntilCancelled(startSampleOffset: 7_000);
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

    private static VoskRecognition TimedRecognition(
        string word,
        double confidence,
        double startSeconds,
        double endSeconds) =>
        new(
            word,
            confidence,
            true,
            [
                new VoskWordTiming(
                    word,
                    confidence,
                    TimeSpan.FromSeconds(startSeconds),
                    TimeSpan.FromSeconds(endSeconds)),
            ]);

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

    private sealed class FakeVoiceAudioCursor : IVoiceAudioCursor
    {
        private readonly IReadOnlyList<SequencedAudioFrame> _frames;
        private readonly bool _waitForCancellation;

        private FakeVoiceAudioCursor(
            long startSampleOffset,
            IReadOnlyList<SequencedAudioFrame> frames,
            bool waitForCancellation)
        {
            StartSampleOffset = startSampleOffset;
            _frames = frames;
            _waitForCancellation = waitForCancellation;
        }

        public AudioFormat Format => AudioFormat.Pcm16KhzMono;

        public long StartSampleOffset { get; }

        public int DisposeCount { get; private set; }

        public static FakeVoiceAudioCursor WithFrames(
            long startSampleOffset,
            params SequencedAudioFrame[] frames) =>
            new(startSampleOffset, frames, waitForCancellation: false);

        public static FakeVoiceAudioCursor UntilCancelled(long startSampleOffset) =>
            new(startSampleOffset, [], waitForCancellation: true);

        public async IAsyncEnumerable<SequencedAudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var frame in _frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return frame;
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
