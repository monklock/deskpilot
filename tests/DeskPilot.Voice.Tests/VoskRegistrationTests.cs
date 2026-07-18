using DeskPilot.Voice.Vosk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class VoskRegistrationTests
{
    [Fact]
    public void AddDeskPilotVosk_RegistersNativeAndWakeProviderFactories()
    {
        var services = new ServiceCollection();

        services.AddDeskPilotVosk();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IVoskRecognizerClientFactory)
            && descriptor.ImplementationType == typeof(VoskRecognizerClientFactory)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(VoskWakeWordProviderFactory)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }
}
