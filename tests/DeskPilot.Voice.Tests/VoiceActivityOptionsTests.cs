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
    public void WakeResult_ContainsMonotonicAbsoluteSampleOffsets()
    {
        var result = new WakeWordDetectionResult("Р°Р»СЊС„Р°", 0.93, 16_000, 24_000, 25_600);

        result.WakeStartSampleOffset.Should().Be(16_000);
        result.WakeEndSampleOffset.Should().Be(24_000);
        result.DetectionSampleOffset.Should().Be(25_600);
    }
}
