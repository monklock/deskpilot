using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoiceActivityOptionsTests
{
    [Fact]
    public void DefaultValues_MatchVoicePipelineRequirements()
    {
        var options = VoiceActivityOptions.Default;

        options.MinimumSpeechDuration.Should().Be(TimeSpan.FromMilliseconds(250));
        options.SilenceTimeout.Should().Be(TimeSpan.FromMilliseconds(900));
        options.MaximumCommandDuration.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ContinuousDefault_UsesApprovedContinuousEndpointingDurations()
    {
        var options = VoiceActivityOptions.ContinuousDefault;

        options.PreRollDuration.Should().Be(TimeSpan.FromMilliseconds(300));
        options.MinimumSpeechDuration.Should().Be(TimeSpan.FromMilliseconds(150));
        options.InitialSilenceTimeout.Should().Be(TimeSpan.FromSeconds(4));
        options.EndSilenceTimeout.Should().Be(TimeSpan.FromMilliseconds(1_200));
        options.MaximumCommandDuration.Should().Be(TimeSpan.FromSeconds(10));
        options.Sensitivity.Should().Be(0.80);
    }

    [Fact]
    public void WakeResult_WithOrderedAbsoluteSampleOffsets_PreservesOffsets()
    {
        var result = new WakeWordDetectionResult("Р°Р»СЊС„Р°", 0.93, 16_000, 24_000, 25_600);

        result.WakeStartSampleOffset.Should().Be(16_000);
        result.WakeEndSampleOffset.Should().Be(24_000);
        result.DetectionSampleOffset.Should().Be(25_600);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    public void SequencedAudioFrame_RejectsNegativeOrDescendingSampleOffsets(
        long startSampleOffset,
        long endSampleOffset)
    {
        var action = () => new SequencedAudioFrame(
            ReadOnlyMemory<byte>.Empty,
            TimeSpan.Zero,
            startSampleOffset,
            endSampleOffset);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(10, 5, 10)]
    [InlineData(5, 10, 9)]
    public void WakeResult_WithTimedOffsets_RejectsNegativeOrNonMonotonicSampleOffsets(
        long wakeStartSampleOffset,
        long wakeEndSampleOffset,
        long detectionSampleOffset)
    {
        var action = () => new WakeWordDetectionResult(
            "Р°Р»СЊС„Р°",
            0.93,
            wakeStartSampleOffset,
            wakeEndSampleOffset,
            detectionSampleOffset);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void VoiceActivityDiagnosticsEmpty_UsesZeroAndNullValues()
    {
        var diagnostics = VoiceActivityDiagnostics.Empty;

        diagnostics.ObservedDuration.Should().Be(TimeSpan.Zero);
        diagnostics.CapturedDuration.Should().Be(TimeSpan.Zero);
        diagnostics.SpeechStartSampleOffset.Should().BeNull();
        diagnostics.SpeechEndSampleOffset.Should().BeNull();
        diagnostics.NoiseFloorRms.Should().Be(0);
        diagnostics.PeakRms.Should().Be(0);
    }

    [Fact]
    public void VoiceActivityResult_LegacyConstructor_UsesEmptyDiagnostics()
    {
        var result = new VoiceActivityResult(false, TimeSpan.Zero, null);

        result.Diagnostics.Should().BeSameAs(VoiceActivityDiagnostics.Empty);
    }

    [Fact]
    public void WakeResult_LegacyConstructor_UsesNullSampleOffsets()
    {
        var result = new WakeWordDetectionResult("Р°Р»СЊС„Р°", 0.93);

        result.WakeStartSampleOffset.Should().BeNull();
        result.WakeEndSampleOffset.Should().BeNull();
        result.DetectionSampleOffset.Should().BeNull();
    }
}
