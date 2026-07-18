using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Registers the Windows voice capture contour.</summary>
public static class AudioCaptureServiceCollectionExtensions
{
    /// <summary>Registers endpoint monitoring, explicit WASAPI capture, and normalized sessions.</summary>
    public static IServiceCollection AddDeskPilotVoiceAudioCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<NAudioWindowsCaptureEndpointSource>();
        services.AddSingleton<IWindowsCaptureEndpointSource>(provider =>
            provider.GetRequiredService<NAudioWindowsCaptureEndpointSource>());
        services.AddSingleton<IAudioInputDeviceService, NAudioInputDeviceService>();
        services.AddSingleton<IWindowsCaptureClientFactory, NAudioWindowsCaptureClientFactory>();
        services.AddSingleton<IAudioCaptureSessionFactory, NAudioCaptureFactory>();
        services.AddSingleton<IVoiceActivityDetector, EnergyVoiceActivityDetector>();
        return services;
    }
}
