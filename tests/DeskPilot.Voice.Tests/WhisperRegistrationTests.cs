using DeskPilot.Voice.WhisperCpp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class WhisperRegistrationTests
{
    [Fact]
    public void AddDeskPilotWhisper_RegistersNativeAndProviderFactories()
    {
        var services = new ServiceCollection();

        services.AddDeskPilotWhisper();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWhisperClientFactory)
            && descriptor.ImplementationType == typeof(WhisperNetClientFactory)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(WhisperSpeechToTextProviderFactory)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }
}
