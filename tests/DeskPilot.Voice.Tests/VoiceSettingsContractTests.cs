using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoiceSettingsContractTests
{
    [Fact]
    public void DefaultValues_MatchApprovedVoiceConfiguration()
    {
        var settings = VoiceSettings.Default;

        settings.IsEnabled.Should().BeFalse();
        settings.MicrophoneEndpointId.Should().BeNull();
        settings.WakePhrase.Should().Be("альфа");
        settings.WakeConfidence.Should().Be(0.80);
        settings.Cooldown.Should().Be(TimeSpan.FromSeconds(2));
        settings.RecognitionLanguage.Should().Be("ru");
    }
}
