using System.Buffers.Binary;
using System.Collections.Concurrent;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class AmbientNoiseEstimatorTests
{
    [Fact]
    public void Snapshot_UsesLowerPercentileSoSpeechSpikeDoesNotRaiseNoiseFloor()
    {
        var estimator = new AmbientNoiseEstimator(frameCapacity: 10);
        for (var index = 0; index < 8; index++)
        {
            estimator.Observe(ConstantFrame(0.02));
        }

        estimator.Observe(ConstantFrame(0.80));
        estimator.Observe(ConstantFrame(0.90));

        estimator.Snapshot.NoiseFloorRms.Should().BeApproximately(0.02, 0.001);
        estimator.Snapshot.FrameCount.Should().Be(10);
        estimator.Snapshot.WindowDuration.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Snapshot_WhenHistoryExceedsDefaultCapacity_RetainsOnlyLatest150Frames()
    {
        var estimator = new AmbientNoiseEstimator();

        estimator.Observe(ConstantFrame(0.01));
        for (var index = 0; index < 150; index++)
        {
            estimator.Observe(ConstantFrame(0.04));
        }

        estimator.Snapshot.FrameCount.Should().Be(150);
        estimator.Snapshot.WindowDuration.Should().Be(TimeSpan.FromSeconds(3));
        estimator.Snapshot.NoiseFloorRms.Should().BeApproximately(0.04, 0.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(320)]
    [InlineData(641)]
    [InlineData(1280)]
    public void Observe_WhenPcmIsNotOne20MillisecondFrame_ThrowsUnsupportedFormat(int pcm16Length)
    {
        var estimator = new AmbientNoiseEstimator();

        var action = () => estimator.Observe(new byte[pcm16Length]);

        action.Should().Throw<AudioCaptureException>()
            .Where(exception => exception.Code == AudioInputResultCode.UnsupportedFormat);
    }

    [Fact]
    public void Constructor_WhenFrameCapacityIsNotPositive_ThrowsForFrameCapacity()
    {
        var action = () => new AmbientNoiseEstimator(frameCapacity: 0);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("frameCapacity");
    }

    [Fact]
    public void PcmRms_Calculate_NormalizesLittleEndianPcm16Samples()
    {
        PcmRms.Calculate(ConstantFrame(0.25)).Should().BeApproximately(0.25, 0.001);
    }

    [Fact]
    public async Task ConcurrentObserveAndSnapshot_PublishesBoundedFiniteSnapshots()
    {
        var estimator = new AmbientNoiseEstimator();
        using var start = new ManualResetEventSlim();
        var snapshots = new ConcurrentBag<AmbientNoiseSnapshot>();

        var observers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            start.Wait();
            for (var frame = 0; frame < 1_000; frame++)
            {
                estimator.Observe(ConstantFrame((index + frame) % 2 == 0 ? 0.02 : 0.80));
            }
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var read = 0; read < 1_000; read++)
            {
                snapshots.Add(estimator.Snapshot);
            }
        }));

        start.Set();
        await Task.WhenAll(observers.Concat(readers));

        snapshots.Should().NotBeEmpty();
        snapshots.Should().OnlyContain(snapshot =>
            snapshot.FrameCount >= 0
            && snapshot.FrameCount <= 150
            && snapshot.WindowDuration == TimeSpan.FromMilliseconds(snapshot.FrameCount * 20)
            && double.IsFinite(snapshot.NoiseFloorRms)
            && snapshot.NoiseFloorRms >= 0);
    }

    private static byte[] ConstantFrame(double amplitude)
    {
        var sample = (short)Math.Round(short.MaxValue * amplitude);
        var pcm16 = new byte[320 * sizeof(short)];
        for (var offset = 0; offset < pcm16.Length; offset += sizeof(short))
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(offset, sizeof(short)), sample);
        }

        return pcm16;
    }
}
