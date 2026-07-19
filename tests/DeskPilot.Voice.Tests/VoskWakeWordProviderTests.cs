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
        var frames = CreateContiguousFrames(startSampleOffset: 32_000, count: 60);
        var recognitions = Enumerable.Range(0, frames.Length - 1)
            .Select(_ => new VoskRecognition(string.Empty, 0, false))
            .Append(TimedRecognition("альфа", 0.94, 0.50, 1.10))
            .ToArray();
        var recognizer = new FakeVoskRecognizerClient(recognitions);
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(32_000, frames);

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
        recognizer.AcceptCount.Should().Be(60);
        recognizer.AcceptedPcmLengths.Should().OnlyContain(length => length == 640);
        frames.Should().OnlyContain(frame =>
            frame.Pcm16.Length == 640
            && frame.Duration == TimeSpan.FromMilliseconds(20)
            && frame.EndSampleOffset - frame.StartSampleOffset == 320);
        frames[^1].StartSampleOffset.Should().Be(50_880);
        frames[^1].EndSampleOffset.Should().Be(51_200);
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
            CreateFrame(32_000, 32_320));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Vosk returned invalid wake-word timing.*");
        audio.DisposeCount.Should().Be(0);
    }

    [Theory]
    [InlineData(32_640, 32_960)]
    [InlineData(32_160, 32_480)]
    [InlineData(31_680, 32_000)]
    [InlineData(32_320, 32_320)]
    public async Task WaitForDetectionAsync_Cursor_InvalidFrameSequenceRejectsBeforeRecognizer(
        long invalidStartSampleOffset,
        long invalidEndSampleOffset)
    {
        var recognizer = new FakeVoskRecognizerClient(
            new VoskRecognition(string.Empty, 0, false),
            TimedRecognition("альфа", 0.94, 0.01, 0.02));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            32_000,
            CreateFrame(32_000, 32_320),
            CreateFrame(invalidStartSampleOffset, invalidEndSampleOffset));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Vosk returned invalid wake-word timing.*");
        recognizer.AcceptCount.Should().Be(1);
        recognizer.DisposeCount.Should().Be(1);
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
            CreateFrame(32_000, 51_200));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Vosk returned invalid wake-word timing.*");
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_RoundsFractionalTimeToNearestWholeSample()
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
            CreateFrame(12_345, 12_665));

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
            CreateFrame(long.MaxValue - 100, long.MaxValue));

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
            CreateFrame(5_000, 5_320),
            CreateFrame(5_320, 5_640));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.92, 5_160, 5_480, 5_640));
        recognizer.AcceptCount.Should().Be(2);
        recognizer.AcceptedPcmLengths.Should().Equal(640, 640);
        recognizer.CompleteCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_TrimsConfiguredPhraseAndMatchesFinalCaseInsensitively()
    {
        var recognition = new VoskRecognition(
            "  АЛЬФА  ",
            0.94,
            true,
            [new VoskWordTiming("АЛЬФА", 0.94, TimeSpan.FromSeconds(0.005), TimeSpan.FromSeconds(0.010))]);
        var factory = new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient(recognition));
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            8_000,
            CreateFrame(8_000, 8_320));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("  альфа  ", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.94, 8_080, 8_160, 8_320));
        factory.GrammarJson.Should().Be("[\"альфа\"]");
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_RecognizerFailsDisposesRecognizerButNotCursor()
    {
        var recognizer = new FakeVoskRecognizerClient(new InvalidOperationException("native failure"));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            8_000,
            CreateFrame(8_000, 8_320));

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
    public async Task WaitForDetectionAsync_CursorEnumerationFailsDisposesRecognizerButNotCursor()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.Failing(
            startSampleOffset: 8_000,
            new AudioCaptureException(AudioInputResultCode.Disconnected, "capture failure"));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.Disconnected);
        recognizer.AcceptCount.Should().Be(0);
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
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
    public async Task WaitForDetectionAsync_Cursor_BelowThresholdContinuesUntilTimedDetection()
    {
        var recognizer = new FakeVoskRecognizerClient(
            TimedRecognition("альфа", 0.79, 0.005, 0.010),
            TimedRecognition("альфа", 0.91, 0.010, 0.020));
        var factory = new FakeVoskRecognizerClientFactory(recognizer);
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            0,
            CreateFrame(0, 320),
            CreateFrame(320, 640));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("  альфа  ", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.91, 160, 320, 640));
        factory.GrammarJson.Should().Be("[\"альфа\"]");
        recognizer.AcceptCount.Should().Be(2);
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_HighConfidenceAmbientTextIsIgnored()
    {
        var recognizer = new FakeVoskRecognizerClient(
            TimedRecognition("погода", 0.99, 0.005, 0.010),
            TimedRecognition("АЛЬФА", 0.85, 0.010, 0.020));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            0,
            CreateFrame(0, 320),
            CreateFrame(320, 640));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Confidence.Should().Be(0.85);
        recognizer.AcceptCount.Should().Be(2);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_PartialPhraseDoesNotActivate()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new VoskRecognition("альфа", 0.99, false),
            TimedRecognition("альфа", 0.88, 0.010, 0.020));
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            0,
            CreateFrame(0, 320),
            CreateFrame(320, 640));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Confidence.Should().Be(0.88);
        recognizer.AcceptCount.Should().Be(2);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_StreamEndFlushesTimedFinalRecognition()
    {
        var recognizer = new FakeVoskRecognizerClient(
            new VoskRecognition("альфа", 0, false))
        {
            Completion = TimedRecognition("альфа", 0.86, 0.005, 0.010),
        };
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(0, CreateFrame(0, 320));

        var result = await provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        result.Should().Be(new WakeWordDetectionResult("альфа", 0.86, 80, 160, 320));
        recognizer.CompleteCount.Should().Be(1);
        recognizer.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_StreamEndWithoutWakeDisposesRecognizer()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(0, CreateFrame(0, 320));

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
    public async Task WaitForDetectionAsync_Cursor_UnsupportedFormatRejectsBeforeRecognizer()
    {
        var factory = new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient());
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeVoiceAudioCursor.WithFormat(
            new AudioFormat(48_000, 2, 32, true));

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
    public async Task WaitForDetectionAsync_Cursor_InvalidThresholdRejectsBeforeRecognizer(
        double threshold)
    {
        var factory = new FakeVoskRecognizerClientFactory(new FakeVoskRecognizerClient());
        var provider = new VoskWakeWordProvider("wake-model", factory);
        await using var audio = FakeVoiceAudioCursor.WithFrames(0, CreateFrame(0, 320));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", threshold),
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForDetectionAsync_Cursor_MalformedPcmRejectsAndPreservesOwnership()
    {
        var recognizer = new FakeVoskRecognizerClient();
        var provider = new VoskWakeWordProvider(
            "wake-model",
            new FakeVoskRecognizerClientFactory(recognizer));
        await using var audio = FakeVoiceAudioCursor.WithFrames(
            0,
            new SequencedAudioFrame(
                new byte[] { 0 },
                TimeSpan.FromMilliseconds(20),
                0,
                1));

        var action = () => provider.WaitForDetectionAsync(
            audio,
            new WakeWordOptions("альфа", 0.80),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidDataException>();
        recognizer.DisposeCount.Should().Be(1);
        audio.DisposeCount.Should().Be(0);
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

    private static SequencedAudioFrame[] CreateContiguousFrames(
        long startSampleOffset,
        int count) =>
        Enumerable.Range(0, count)
            .Select(index => CreateFrame(
                checked(startSampleOffset + (index * 320L)),
                checked(startSampleOffset + ((index + 1) * 320L))))
            .ToArray();

    private static SequencedAudioFrame CreateFrame(
        long startSampleOffset,
        long endSampleOffset)
    {
        var sampleCount = checked((int)Math.Max(0, endSampleOffset - startSampleOffset));
        return new SequencedAudioFrame(
            new byte[checked(sampleCount * sizeof(short))],
            TimeSpan.FromSeconds(sampleCount / 16_000d),
            startSampleOffset,
            endSampleOffset);
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

        public List<int> AcceptedPcmLengths { get; } = [];

        public VoskRecognition Completion { get; init; } =
            new(string.Empty, 0, true);

        public VoskRecognition Accept(ReadOnlyMemory<byte> pcm16)
        {
            AcceptCount++;
            AcceptedPcmLengths.Add(pcm16.Length);
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

    private sealed class FakeVoiceAudioCursor : IVoiceAudioCursor
    {
        private readonly IReadOnlyList<SequencedAudioFrame> _frames;
        private readonly bool _waitForCancellation;
        private readonly Exception? _failure;

        private FakeVoiceAudioCursor(
            AudioFormat format,
            long startSampleOffset,
            IReadOnlyList<SequencedAudioFrame> frames,
            bool waitForCancellation,
            Exception? failure = null)
        {
            Format = format;
            StartSampleOffset = startSampleOffset;
            _frames = frames;
            _waitForCancellation = waitForCancellation;
            _failure = failure;
        }

        public AudioFormat Format { get; }

        public long StartSampleOffset { get; }

        public int DisposeCount { get; private set; }

        public static FakeVoiceAudioCursor WithFrames(
            long startSampleOffset,
            params SequencedAudioFrame[] frames) =>
            new(
                AudioFormat.Pcm16KhzMono,
                startSampleOffset,
                frames,
                waitForCancellation: false);

        public static FakeVoiceAudioCursor WithFormat(AudioFormat format) =>
            new(format, 0, [], waitForCancellation: false);

        public static FakeVoiceAudioCursor UntilCancelled(long startSampleOffset) =>
            new(
                AudioFormat.Pcm16KhzMono,
                startSampleOffset,
                [],
                waitForCancellation: true);

        public static FakeVoiceAudioCursor Failing(
            long startSampleOffset,
            Exception failure) =>
            new(
                AudioFormat.Pcm16KhzMono,
                startSampleOffset,
                [],
                waitForCancellation: false,
                failure);

        public async IAsyncEnumerable<SequencedAudioFrame> ReadFramesAsync(
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
