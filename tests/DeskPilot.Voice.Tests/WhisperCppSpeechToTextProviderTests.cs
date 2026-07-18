using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.WhisperCpp;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class WhisperCppSpeechToTextProviderTests
{
    [Fact]
    public async Task RecognizeAsync_ForcesRussianConvertsPcmAndConcatenatesSegments()
    {
        using var model = TemporaryModel.Create();
        var client = new FakeWhisperClient(
            new WhisperSegment(" включи ", 0.92),
            new WhisperSegment(" музыку ", 0.88));
        var factory = new FakeWhisperClientFactory(client);
        var provider = new WhisperCppSpeechToTextProvider(model.Path, factory);
        var audio = CommandAudio(short.MinValue, 0, short.MaxValue);

        var result = await provider.RecognizeAsync(
            audio,
            new SpeechRecognitionOptions("en", 0.70),
            CancellationToken.None);

        result.Should().Be(new SpeechRecognitionResult("включи музыку", 0.90, true));
        factory.ModelPath.Should().Be(model.Path);
        client.Language.Should().Be("ru");
        client.Samples.Should().Equal(-1f, 0f, short.MaxValue / 32_768f);
        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RecognizeAsync_ConfidenceBelowThreshold_ThrowsTypedFailure()
    {
        using var model = TemporaryModel.Create();
        var client = new FakeWhisperClient(new WhisperSegment("команда", 0.69));
        var provider = new WhisperCppSpeechToTextProvider(
            model.Path,
            new FakeWhisperClientFactory(client));

        var action = () => provider.RecognizeAsync(
            CommandAudio(1, 2),
            new SpeechRecognitionOptions("ru", 0.70),
            CancellationToken.None);

        await action.Should().ThrowAsync<SpeechRecognitionException>()
            .Where(exception =>
                exception.Code == SpeechRecognitionFailureCode.ConfidenceBelowThreshold);
        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RecognizeAsync_BlankFinalText_ThrowsTypedFailure()
    {
        using var model = TemporaryModel.Create();
        var client = new FakeWhisperClient(new WhisperSegment("   ", 0.95));
        var provider = new WhisperCppSpeechToTextProvider(
            model.Path,
            new FakeWhisperClientFactory(client));

        var action = () => provider.RecognizeAsync(
            CommandAudio(1, 2),
            SpeechRecognitionOptions.Default,
            CancellationToken.None);

        await action.Should().ThrowAsync<SpeechRecognitionException>()
            .Where(exception => exception.Code == SpeechRecognitionFailureCode.NoText);
        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RecognizeAsync_MissingModel_RejectsBeforeCreatingNativeClient()
    {
        var factory = new FakeWhisperClientFactory(new FakeWhisperClient());
        var provider = new WhisperCppSpeechToTextProvider(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin"),
            factory);

        var action = () => provider.RecognizeAsync(
            CommandAudio(1, 2),
            SpeechRecognitionOptions.Default,
            CancellationToken.None);

        await action.Should().ThrowAsync<SpeechRecognitionException>()
            .Where(exception => exception.Code == SpeechRecognitionFailureCode.ModelUnavailable);
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task RecognizeAsync_UnsupportedAudioFormat_RejectsBeforeCreatingNativeClient()
    {
        using var model = TemporaryModel.Create();
        var factory = new FakeWhisperClientFactory(new FakeWhisperClient());
        var provider = new WhisperCppSpeechToTextProvider(model.Path, factory);
        var audio = new CapturedCommandAudio(
            new byte[] { 0, 0, 0, 0 },
            new AudioFormat(48_000, 2, 16, false),
            TimeSpan.FromMilliseconds(1));

        var action = () => provider.RecognizeAsync(
            audio,
            SpeechRecognitionOptions.Default,
            CancellationToken.None);

        await action.Should().ThrowAsync<SpeechRecognitionException>()
            .Where(exception => exception.Code == SpeechRecognitionFailureCode.UnsupportedFormat);
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task RecognizeAsync_NativeFailure_WrapsTypedFailureAndDisposesClient()
    {
        using var model = TemporaryModel.Create();
        var client = new FakeWhisperClient(new InvalidOperationException("native failure"));
        var provider = new WhisperCppSpeechToTextProvider(
            model.Path,
            new FakeWhisperClientFactory(client));

        var action = () => provider.RecognizeAsync(
            CommandAudio(1, 2),
            SpeechRecognitionOptions.Default,
            CancellationToken.None);

        var assertion = await action.Should().ThrowAsync<SpeechRecognitionException>()
            .Where(exception => exception.Code == SpeechRecognitionFailureCode.ProviderFailure);
        assertion.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RecognizeAsync_Cancelled_PropagatesCancellationAndDisposesClient()
    {
        using var model = TemporaryModel.Create();
        var client = FakeWhisperClient.UntilCancelled();
        var factory = new FakeWhisperClientFactory(client);
        var provider = new WhisperCppSpeechToTextProvider(model.Path, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var recognition = provider.RecognizeAsync(
            CommandAudio(1, 2),
            SpeechRecognitionOptions.Default,
            cancellation.Token);
        await client.Processing.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await FluentActions.Awaiting(() => recognition)
            .Should().ThrowAsync<OperationCanceledException>();
        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void ProviderId_IsStableWhisperIdentifier()
    {
        var provider = new WhisperCppSpeechToTextProvider(
            "model.bin",
            new FakeWhisperClientFactory(new FakeWhisperClient()));

        provider.ProviderId.Should().Be("whisper.cpp");
    }

    private static CapturedCommandAudio CommandAudio(params short[] samples)
    {
        var pcm16 = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm16.AsSpan(index * sizeof(short), sizeof(short)),
                samples[index]);
        }

        return new CapturedCommandAudio(
            pcm16,
            AudioFormat.Pcm16KhzMono,
            TimeSpan.FromSeconds(samples.Length / 16_000d));
    }

    private sealed class FakeWhisperClientFactory(
        FakeWhisperClient client) : IWhisperClientFactory
    {
        public int CreateCount { get; private set; }

        public string? ModelPath { get; private set; }

        public IWhisperClient Create(string modelPath)
        {
            CreateCount++;
            ModelPath = modelPath;
            return client;
        }
    }

    private sealed class FakeWhisperClient : IWhisperClient
    {
        private readonly IReadOnlyList<WhisperSegment> _segments;
        private readonly Exception? _failure;
        private readonly bool _waitForCancellation;

        public FakeWhisperClient(params WhisperSegment[] segments) =>
            _segments = segments;

        public FakeWhisperClient(Exception failure)
        {
            _failure = failure;
            _segments = [];
        }

        private FakeWhisperClient(bool waitForCancellation)
        {
            _waitForCancellation = waitForCancellation;
            _segments = [];
        }

        public TaskCompletionSource Processing { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public float[] Samples { get; private set; } = [];

        public string? Language { get; private set; }

        public int DisposeCount { get; private set; }

        public static FakeWhisperClient UntilCancelled() => new(waitForCancellation: true);

        public async IAsyncEnumerable<WhisperSegment> ProcessAsync(
            float[] samples,
            string language,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Samples = samples;
            Language = language;
            Processing.TrySetResult();
            if (_failure is not null)
            {
                throw _failure;
            }

            if (_waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            foreach (var segment in _segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return segment;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryModel(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryModel Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"deskpilot-whisper-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, [0x67, 0x67, 0x6d, 0x6c]);
            return new TemporaryModel(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
