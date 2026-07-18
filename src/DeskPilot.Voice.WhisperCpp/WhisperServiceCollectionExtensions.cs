using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Voice.WhisperCpp;

/// <summary>Creates a speech provider for the currently active Whisper model path.</summary>
public delegate ISpeechToTextProvider WhisperSpeechToTextProviderFactory(string modelPath);

/// <summary>Registers the offline Whisper.cpp speech-recognition contour.</summary>
public static class WhisperServiceCollectionExtensions
{
    /// <summary>Registers native client and model-path-aware provider factories.</summary>
    public static IServiceCollection AddDeskPilotWhisper(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IWhisperClientFactory, WhisperNetClientFactory>();
        services.AddSingleton<WhisperSpeechToTextProviderFactory>(provider =>
        {
            var clients = provider.GetRequiredService<IWhisperClientFactory>();
            return modelPath => new WhisperCppSpeechToTextProvider(modelPath, clients);
        });
        return services;
    }
}
