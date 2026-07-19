using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoiceAudioRingBufferTests
{
    [Fact]
    public void Append_WhenCapacityWraps_PreservesNewestSamplesWithAbsoluteOffsets()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 8);

        buffer.Append(new byte[] { 1, 0, 2, 0, 3, 0 });
        buffer.Append(new byte[] { 4, 0, 5, 0 });

        buffer.EarliestSampleOffset.Should().Be(1);
        buffer.LatestSampleOffset.Should().Be(5);
        buffer.Read(1, 4).Should().Equal(2, 0, 3, 0, 4, 0, 5, 0);
    }

    [Fact]
    public void Append_WhenCapacityIsFilledExactly_PreservesAllSamples()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 8);

        buffer.Append(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 });

        buffer.EarliestSampleOffset.Should().Be(0);
        buffer.LatestSampleOffset.Should().Be(4);
        buffer.Read(0, 4).Should().Equal(1, 0, 2, 0, 3, 0, 4, 0);
    }

    [Fact]
    public void Append_WhenInputExceedsCapacity_PreservesOnlyNewestCompleteSamples()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);

        buffer.Append(new byte[] { 1, 0, 2, 0, 3, 0 });

        buffer.EarliestSampleOffset.Should().Be(1);
        buffer.LatestSampleOffset.Should().Be(3);
        buffer.Read(1, 2).Should().Equal(2, 0, 3, 0);
    }

    [Fact]
    public void Append_WhenPoolRentsLargerArray_DoesNotExpandLogicalCapacity()
    {
        var pool = new RecordingArrayPool(rentedLength: 16, fillValue: 0x7F);
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4, pool);

        buffer.Append(new byte[] { 1, 0, 2, 0, 3, 0 });

        buffer.EarliestSampleOffset.Should().Be(1);
        buffer.LatestSampleOffset.Should().Be(3);
        buffer.Read(1, 2).Should().Equal(2, 0, 3, 0);
    }

    [Fact]
    public void Append_WhenPcmIsEmpty_DoesNotAdvanceOffsets()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);

        buffer.Append(ReadOnlySpan<byte>.Empty);

        buffer.EarliestSampleOffset.Should().Be(0);
        buffer.LatestSampleOffset.Should().Be(0);
        buffer.Read(0, 1).Should().BeEmpty();
    }

    [Fact]
    public void Append_WhenPcmEndsWithIncompleteSample_ThrowsUnsupportedFormat()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);

        var action = () => buffer.Append(new byte[] { 1 });

        action.Should().Throw<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public void Read_OverwrittenOffset_ThrowsTypedBufferOverrun()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);
        buffer.Append(new byte[] { 1, 0, 2, 0, 3, 0 });

        var action = () => buffer.Read(0, 1);

        action.Should().Throw<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.BufferOverrun);
    }

    [Fact]
    public void Read_WhenMaximumSamplesIsNotPositive_ThrowsForMaximumSamples()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);

        var action = () => buffer.Read(0, 0);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("maximumSamples");
    }

    [Fact]
    public void Dispose_ClearsEntireRentedArrayAndReturnsItOnlyOnce()
    {
        var pool = new RecordingArrayPool(rentedLength: 16, fillValue: 0x7F);
        var buffer = new VoiceAudioRingBuffer(capacityBytes: 4, pool);
        buffer.Append(new byte[] { 1, 0, 2, 0 });

        buffer.Dispose();
        buffer.Dispose();

        pool.ReturnCount.Should().Be(1);
        pool.ReturnedArray.Should().NotBeNull();
        pool.ReturnedArray!.Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposedException()
    {
        var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);
        buffer.Dispose();

        var action = () => buffer.Read(0, 1);

        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Append_WhenLatestOffsetWouldOverflow_ThrowsWithoutChangingBufferState()
    {
        using var buffer = new VoiceAudioRingBuffer(capacityBytes: 4);
        SetLatestSampleOffset(buffer, long.MaxValue);

        var action = () => buffer.Append(new byte[] { 1, 0 });

        action.Should().Throw<OverflowException>();
        buffer.LatestSampleOffset.Should().Be(long.MaxValue);
        buffer.EarliestSampleOffset.Should().Be(long.MaxValue);
        buffer.Read(long.MaxValue, 1).Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentAppendReadAndDispose_CompletesWithoutDataRaces()
    {
        var pool = new RecordingArrayPool(rentedLength: 128, fillValue: 0x7F);
        var buffer = new VoiceAudioRingBuffer(capacityBytes: 64, pool);
        using var start = new ManualResetEventSlim();
        using var writerMadeProgress = new ManualResetEventSlim();
        var failures = new ConcurrentQueue<Exception>();
        var writerStarted = 0;

        var writer = Task.Run(() =>
        {
            start.Wait();
            Volatile.Write(ref writerStarted, 1);
            for (var index = 0; index < 2_000; index++)
            {
                try
                {
                    buffer.Append(PatternFrame(index));
                    if (index == 100)
                    {
                        writerMadeProgress.Set();
                    }

                    if (index % 10 == 0)
                    {
                        Thread.Yield();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                    return;
                }
            }
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            start.Wait();
            while (!writer.IsCompleted)
            {
                try
                {
                    var earliest = buffer.EarliestSampleOffset;
                    var latest = buffer.LatestSampleOffset;
                    if (earliest < latest)
                    {
                        var sample = buffer.Read(latest - 1, 1);
                        sample.Length.Should().Be(sizeof(short));
                        sample[1].Should().Be((byte)~sample[0]);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (AudioCaptureException exception)
                    when (exception.Code == AudioInputResultCode.BufferOverrun)
                {
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                    return;
                }
            }
        })).ToArray();

        var disposer = Task.Run(() =>
        {
            start.Wait();
            SpinWait.SpinUntil(() => Volatile.Read(ref writerStarted) == 1);
            writerMadeProgress.Wait();
            buffer.Dispose();
        });

        start.Set();
        await Task.WhenAll(readers.Append(writer).Append(disposer));

        failures.Should().BeEmpty();
        pool.ReturnCount.Should().Be(1);
        ((Action)(() => buffer.Read(0, 1))).Should().Throw<ObjectDisposedException>();
    }

    private static byte[] PatternFrame(int value)
    {
        var lowByte = (byte)value;
        var highByte = (byte)~lowByte;
        return new byte[] { lowByte, highByte, lowByte, highByte };
    }

    private static void SetLatestSampleOffset(VoiceAudioRingBuffer buffer, long value)
    {
        var field = typeof(VoiceAudioRingBuffer).GetField(
            "_latestSampleOffset",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(buffer, value);
    }

    private sealed class RecordingArrayPool(int rentedLength, byte fillValue) : ArrayPool<byte>
    {
        private readonly byte[] _rented = Enumerable.Repeat(fillValue, rentedLength).ToArray();

        public int ReturnCount { get; private set; }

        public byte[]? ReturnedArray { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            minimumLength.Should().BeLessThanOrEqualTo(_rented.Length);
            return _rented;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            array.Should().BeSameAs(_rented);
            clearArray.Should().BeFalse();
            ReturnCount++;
            ReturnedArray = array;
        }
    }
}
