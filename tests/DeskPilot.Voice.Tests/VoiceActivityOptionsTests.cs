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
}
