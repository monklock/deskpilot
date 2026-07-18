using System.Runtime.CompilerServices;
using DeskPilot.Voice.WhisperCpp;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class WhisperNetClientTests
{
    [Fact]
    public async Task ProcessAsync_CreatesLanguageProcessorMapsSegmentsAndDisposesProcessor()
    {
        var processor = new FakeNativeProcessor(
            new WhisperSegment("команда", 0.91));
        var factory = new FakeNativeFactory(processor);
        await using var client = new WhisperNetClient(factory);
        var samples = new[] { -1f, 0f, 0.5f };

        var segments = await CollectAsync(
            client.ProcessAsync(samples, "ru", CancellationToken.None));

        segments.Should().Equal(new WhisperSegment("команда", 0.91));
        factory.Language.Should().Be("ru");
        processor.Samples.Should().BeSameAs(samples);
        processor.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_Cancelled_DisposesProcessor()
    {
        var processor = FakeNativeProcessor.UntilCancelled();
        var factory = new FakeNativeFactory(processor);
        await using var client = new WhisperNetClient(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var processing = CollectAsync(client.ProcessAsync([], "ru", cancellation.Token));
        await processor.Processing.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await FluentActions.Awaiting(() => processing)
            .Should().ThrowAsync<OperationCanceledException>();
        processor.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DisposesNativeFactoryOnce()
    {
        var factory = new FakeNativeFactory(new FakeNativeProcessor());
        var client = new WhisperNetClient(factory);

        await client.DisposeAsync();
        await client.DisposeAsync();

        factory.DisposeCount.Should().Be(1);
    }

    private static async Task<IReadOnlyList<WhisperSegment>> CollectAsync(
        IAsyncEnumerable<WhisperSegment> source)
    {
        var segments = new List<WhisperSegment>();
        await foreach (var segment in source)
        {
            segments.Add(segment);
        }

        return segments;
    }

    private sealed class FakeNativeFactory(
        FakeNativeProcessor processor) : IWhisperNativeFactory
    {
        public string? Language { get; private set; }

        public int DisposeCount { get; private set; }

        public IWhisperNativeProcessor CreateProcessor(string language)
        {
            Language = language;
            return processor;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeNativeProcessor : IWhisperNativeProcessor
    {
        private readonly IReadOnlyList<WhisperSegment> _segments;
        private readonly bool _waitForCancellation;

        public FakeNativeProcessor(params WhisperSegment[] segments) =>
            _segments = segments;

        private FakeNativeProcessor(bool waitForCancellation)
        {
            _waitForCancellation = waitForCancellation;
            _segments = [];
        }

        public TaskCompletionSource Processing { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public float[]? Samples { get; private set; }

        public int DisposeCount { get; private set; }

        public static FakeNativeProcessor UntilCancelled() => new(waitForCancellation: true);

        public async IAsyncEnumerable<WhisperSegment> ProcessAsync(
            float[] samples,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Samples = samples;
            Processing.TrySetResult();
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
}
