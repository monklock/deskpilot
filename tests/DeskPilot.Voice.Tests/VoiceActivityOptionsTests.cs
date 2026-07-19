using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoiceActivityOptionsTests
{
    [Fact]
    public void Default_UsesApprovedContinuousEndpointingDurations()
    {
        var options = VoiceActivityOptions.Default;

        options.PreRollDuration.Should().Be(TimeSpan.FromMilliseconds(300));
        options.MinimumSpeechDuration.Should().Be(TimeSpan.FromMilliseconds(150));
        options.InitialSilenceTimeout.Should().Be(TimeSpan.FromMilliseconds(4_000));
        options.EndSilenceTimeout.Should().Be(TimeSpan.FromMilliseconds(1_200));
        options.MaximumCommandDuration.Should().Be(TimeSpan.FromMilliseconds(10_000));
        options.Sensitivity.Should().Be(0.80);
        typeof(VoiceActivityOptions).GetProperty("ContinuousDefault").Should().BeNull();
        typeof(VoiceActivityOptions).GetProperty("SilenceTimeout").Should().BeNull();
    }

    [Fact]
    public void WakeResult_RequiresAllAbsoluteSampleOffsets()
    {
        var result = new WakeWordDetectionResult("альфа", 0.93, 16_000, 24_000, 25_600);

        result.WakeStartSampleOffset.Should().Be(16_000);
        result.WakeEndSampleOffset.Should().Be(24_000);
        result.DetectionSampleOffset.Should().Be(25_600);
        typeof(WakeWordDetectionResult).GetConstructors()
            .Should().ContainSingle(constructor => constructor.GetParameters().Length == 5);
    }

    [Fact]
    public void VoiceActivityResult_RequiresDiagnosticsInPrimaryConstructor()
    {
        var diagnostics = VoiceActivityDiagnostics.Empty;
        var result = new VoiceActivityResult(false, TimeSpan.Zero, null, diagnostics);

        result.Diagnostics.Should().BeSameAs(diagnostics);
        typeof(VoiceActivityResult).GetConstructors()
            .Should().ContainSingle(constructor => constructor.GetParameters().Length == 4);
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
}
