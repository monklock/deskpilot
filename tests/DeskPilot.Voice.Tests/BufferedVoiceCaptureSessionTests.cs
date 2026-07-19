using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class BufferedVoiceCaptureSessionTests
{
    [Fact]
    public async Task SequentialCursors_ReadOneHardwarePumpAndPreserveHandoffFrames()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await raw.EmitAsync(TestPcm.Samples(0, 320));

        await using (var wake = session.OpenCursor(0))
        {
            var wakeFrame = await ReadFirstAsync(wake.ReadFramesAsync(CancellationToken.None));
            wakeFrame.StartSampleOffset.Should().Be(0);
            await raw.EmitAsync(TestPcm.Samples(320, 320));
            await WaitUntilAsync(() => session.LatestSampleOffset >= 640);
        }

        await using var command = session.OpenCursor(320);
        var commandFrame = await ReadFirstAsync(command.ReadFramesAsync(CancellationToken.None));

        raw.ReadEnumerationCount.Should().Be(1);
        commandFrame.StartSampleOffset.Should().Be(320);
        commandFrame.EndSampleOffset.Should().Be(640);
    }

    [Fact]
    public async Task LiveEdgeCursor_ReceivesEveryPulseWithoutLostWakeups()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var cursor = session.OpenCursor(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var frames = cursor.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        for (var index = 0; index < 20; index++)
        {
            var moveNext = frames.MoveNextAsync().AsTask();
            await raw.EmitAsync(TestPcm.Samples(index * 320, 320));

            (await moveNext).Should().BeTrue();
            frames.Current.StartSampleOffset.Should().Be(index * 320);
        }
    }

    [Fact]
    public async Task Cursor_WhenUnreadPositionIsOverwritten_ThrowsBufferOverrun()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var cursor = session.OpenCursor(0);

        await raw.EmitAsync(TestPcm.Samples(0, 32_001));
        await WaitUntilAsync(() => session.LatestSampleOffset == 32_001);

        var action = () => ReadFirstAsync(cursor.ReadFramesAsync(CancellationToken.None));

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.BufferOverrun);
    }

    [Fact]
    public async Task Cursor_SplitsRawFramesIntoBoundedSequentialChunks()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var cursor = session.OpenCursor(0);
        await raw.EmitAsync(TestPcm.Samples(0, 700));
        raw.Complete();

        var frames = await ReadAllAsync(cursor.ReadFramesAsync(CancellationToken.None));

        frames.Select(frame => frame.Pcm16.Length / sizeof(short)).Should().Equal(320, 320, 60);
        frames.Select(frame => frame.StartSampleOffset).Should().Equal(0, 320, 640);
        frames.Select(frame => frame.EndSampleOffset).Should().Equal(320, 640, 700);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task OpenCursor_WhenStartIsOutsideAvailableBounds_ThrowsBufferOverrun(long startOffset)
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);

        var action = () => session.OpenCursor(startOffset);

        action.Should().Throw<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.BufferOverrun);
    }

    [Fact]
    public async Task OpenCursor_AfterWrap_AcceptsEarliestAndRejectsOverwrittenStart()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await raw.EmitAsync(TestPcm.Samples(0, 32_001));
        await WaitUntilAsync(() => session.EarliestSampleOffset == 1);

        var overwritten = () => session.OpenCursor(0);

        overwritten.Should().Throw<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.BufferOverrun);
        await using var earliest = session.OpenCursor(1);
        earliest.StartSampleOffset.Should().Be(1);
    }

    [Fact]
    public async Task TerminalDisconnect_IsPublishedToEveryCursorAfterBufferedTail()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var first = session.OpenCursor(0);
        await using var second = session.OpenCursor(0);
        var failure = new AudioCaptureException(
            AudioInputResultCode.Disconnected,
            "Microphone disconnected.");

        await raw.EmitAsync(TestPcm.Samples(0, 320));
        raw.Fail(failure);

        await AssertTailThenFailureAsync(first, failure);
        await AssertTailThenFailureAsync(second, failure);
    }

    [Fact]
    public async Task RawNormalCompletion_EndsEveryCursorAfterBufferedTail()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var cursor = session.OpenCursor(0);
        await raw.EmitAsync(TestPcm.Samples(0, 320));
        raw.Complete();

        var frames = await ReadAllAsync(cursor.ReadFramesAsync(CancellationToken.None));

        frames.Should().ContainSingle();
        frames[0].StartSampleOffset.Should().Be(0);
        frames[0].EndSampleOffset.Should().Be(320);
    }

    [Fact]
    public async Task CursorCancellation_DoesNotDisposeSessionOrStopOtherCursors()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var cancelledCursor = session.OpenCursor(0);
        using var cancellation = new CancellationTokenSource();
        await using var cancelledFrames = cancelledCursor.ReadFramesAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var waitingRead = cancelledFrames.MoveNextAsync().AsTask();

        cancellation.Cancel();
        await ((Func<Task>)(async () => await waitingRead)).Should()
            .ThrowAsync<OperationCanceledException>();

        raw.DisposeCount.Should().Be(0);
        await using var nextCursor = session.OpenCursor(session.LatestSampleOffset);
        await raw.EmitAsync(TestPcm.Samples(0, 320));
        var nextFrame = await ReadFirstAsync(nextCursor.ReadFramesAsync(CancellationToken.None));
        nextFrame.EndSampleOffset.Should().Be(320);
    }

    [Fact]
    public async Task CursorDisposal_WhileWaiting_EndsOnlyThatCursor()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        var cursor = session.OpenCursor(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var frames = cursor.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var waitingRead = frames.MoveNextAsync().AsTask();

        await cursor.DisposeAsync();

        (await waitingRead).Should().BeFalse();
        raw.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhileCursorWaits_DisposesRawExactlyOnceAndEndsCursor()
    {
        var raw = TestCaptureSession.Create();
        var session = new BufferedVoiceCaptureSession(raw);
        await using var cursor = session.OpenCursor(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var frames = cursor.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var waitingRead = frames.MoveNextAsync().AsTask();

        await Task.WhenAll(
            session.DisposeAsync().AsTask(),
            session.DisposeAsync().AsTask());

        (await waitingRead).Should().BeFalse();
        raw.DisposeCount.Should().Be(1);
        raw.ReadEnumerationCount.Should().Be(1);
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task OpenCursor_AfterDispose_ThrowsObjectDisposedException()
    {
        var raw = TestCaptureSession.Create();
        var session = new BufferedVoiceCaptureSession(raw);
        await session.DisposeAsync();

        var action = () => session.OpenCursor(0);

        action.Should().Throw<ObjectDisposedException>();
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task Pump_ReframesEvenSourceChunksForAmbientEstimator()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);

        await raw.EmitAsync(TestPcm.Samples(0, 160));
        await WaitUntilAsync(() => session.LatestSampleOffset == 160);
        session.NoiseSnapshot.FrameCount.Should().Be(0);

        await raw.EmitAsync(TestPcm.Samples(160, 160));
        await WaitUntilAsync(() => session.NoiseSnapshot.FrameCount == 1);

        session.NoiseSnapshot.WindowDuration.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task Pump_WhenSourcePcmEndsWithIncompleteSample_PublishesUnsupportedFormat()
    {
        await using var raw = TestCaptureSession.Create();
        await using var session = new BufferedVoiceCaptureSession(raw);
        await using var cursor = session.OpenCursor(0);

        await raw.EmitAsync(new byte[3]);
        var action = () => ReadFirstAsync(cursor.ReadFramesAsync(CancellationToken.None));

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public async Task Factory_WhenSessionConstructionFails_DisposesRawCapture()
    {
        var raw = TestCaptureSession.Create(format: new AudioFormat(8_000, 1, 16, false));
        var captures = new TestCaptureSessionFactory(raw);
        var factory = new BufferedVoiceCaptureSessionFactory(captures);

        var action = () => factory.OpenAsync("microphone", CancellationToken.None);

        await action.Should().ThrowAsync<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
        raw.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Factory_WhenCancellationIsRequestedAfterRawOpen_DisposesRawCapture()
    {
        var raw = TestCaptureSession.Create();
        var captures = new TestCaptureSessionFactory(raw);
        var factory = new BufferedVoiceCaptureSessionFactory(captures);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => factory.OpenAsync("microphone", cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        raw.DisposeCount.Should().Be(1);
    }

    private static async Task AssertTailThenFailureAsync(
        IVoiceAudioCursor cursor,
        AudioCaptureException failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var frames = cursor.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        (await frames.MoveNextAsync()).Should().BeTrue();
        frames.Current.EndSampleOffset.Should().Be(320);
        var action = async () => await frames.MoveNextAsync();
        (await action.Should().ThrowAsync<AudioCaptureException>()).Which.Should().BeSameAs(failure);
    }

    private static async Task<SequencedAudioFrame> ReadFirstAsync(
        IAsyncEnumerable<SequencedAudioFrame> frames)
    {
        await foreach (var frame in frames)
        {
            return frame;
        }

        throw new EndOfStreamException("Expected a buffered audio frame.");
    }

    private static async Task<IReadOnlyList<SequencedAudioFrame>> ReadAllAsync(
        IAsyncEnumerable<SequencedAudioFrame> frames)
    {
        var result = new List<SequencedAudioFrame>();
        await foreach (var frame in frames)
        {
            result.Add(frame);
        }

        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static class TestPcm
    {
        internal static byte[] Samples(int startValue, int sampleCount)
        {
            var pcm16 = new byte[sampleCount * sizeof(short)];
            for (var index = 0; index < sampleCount; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm16.AsSpan(index * sizeof(short), sizeof(short)),
                    unchecked((short)(startValue + index)));
            }

            return pcm16;
        }
    }

    private sealed class TestCaptureSession : IAudioCaptureSession
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();
        private int _disposeCount;
        private int _readEnumerationCount;

        private TestCaptureSession(AudioFormat format)
        {
            Format = format;
        }

        public string EndpointId => "microphone";

        public AudioFormat Format { get; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int ReadEnumerationCount => Volatile.Read(ref _readEnumerationCount);

        internal static TestCaptureSession Create(AudioFormat? format = null) =>
            new(format ?? AudioFormat.Pcm16KhzMono);

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readEnumerationCount);
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        internal ValueTask EmitAsync(byte[] pcm16) => _frames.Writer.WriteAsync(
            new AudioFrame(
                pcm16,
                TimeSpan.FromSeconds(pcm16.Length / (16_000d * sizeof(short)))));

        internal void Complete() => _frames.Writer.TryComplete();

        internal void Fail(Exception exception) => _frames.Writer.TryComplete(exception);
    }

    private sealed class TestCaptureSessionFactory(IAudioCaptureSession source)
        : IAudioCaptureSessionFactory
    {
        public Task<IAudioCaptureSession> OpenAsync(
            string endpointId,
            CancellationToken cancellationToken) => Task.FromResult(source);
    }
}
