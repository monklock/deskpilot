using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Voice.Vosk;

/// <summary>Creates a wake provider for the currently active Vosk model path.</summary>
public delegate IWakeWordProvider VoskWakeWordProviderFactory(string modelPath);

/// <summary>Registers the offline Vosk wake-word contour.</summary>
public static class VoskServiceCollectionExtensions
{
    /// <summary>Registers native recognizer and model-path-aware wake provider factories.</summary>
    public static IServiceCollection AddDeskPilotVosk(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IVoskRecognizerClientFactory, VoskRecognizerClientFactory>();
        services.AddSingleton<VoskWakeWordProviderFactory>(provider =>
        {
            var recognizers = provider.GetRequiredService<IVoskRecognizerClientFactory>();
            return modelPath => new VoskWakeWordProvider(modelPath, recognizers);
        });
        return services;
    }
}
