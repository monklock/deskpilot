using System.Buffers.Binary;
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
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { })]
    public void Observe_WhenPcmIsEmptyOrEndsWithIncompleteSample_ThrowsUnsupportedFormat(byte[] pcm16)
    {
        var estimator = new AmbientNoiseEstimator();

        var action = () => estimator.Observe(pcm16);

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
