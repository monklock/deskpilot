using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeskPilot.Voice.Tests;

public sealed class AudioCaptureRegistrationTests
{
    [Fact]
    public void AddDeskPilotVoiceAudioCapture_RegistersWindowsCaptureGraph()
    {
        var services = new ServiceCollection();

        services.AddDeskPilotVoiceAudioCapture();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IWindowsCaptureEndpointSource)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAudioInputDeviceService)
            && descriptor.ImplementationType == typeof(NAudioInputDeviceService));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IWindowsCaptureClientFactory)
            && descriptor.ImplementationType == typeof(NAudioWindowsCaptureClientFactory));
        services.Count(descriptor =>
            descriptor.ServiceType == typeof(IWindowsCaptureClientFactory)).Should().Be(1);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAudioCaptureSessionFactory)
            && descriptor.ImplementationType == typeof(NAudioCaptureFactory)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IBufferedVoiceCaptureSessionFactory)
            && descriptor.ImplementationType == typeof(BufferedVoiceCaptureSessionFactory)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAudioCaptureSessionFactory));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IVoiceActivityDetector)
            && descriptor.ImplementationType == typeof(EnergyVoiceActivityDetector)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }
}
