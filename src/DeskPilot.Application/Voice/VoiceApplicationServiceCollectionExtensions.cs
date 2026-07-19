using DeskPilot.Application.Commands;
using DeskPilot.Application.Intents;
using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Application.Voice;

/// <summary>Registers the application-owned voice state machine.</summary>
public static class VoiceApplicationServiceCollectionExtensions
{
    /// <summary>Registers the coordinator, observable state, and model activation gate.</summary>
    public static IServiceCollection AddDeskPilotVoiceApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<VoicePipelineStateStore>();
        services.AddSingleton<IVoicePipelineStateSource>(provider =>
            provider.GetRequiredService<VoicePipelineStateStore>());
        services.AddSingleton<ICommandTextNormalizer, CommandTextNormalizer>();
        services.AddSingleton<RussianVolumeNumberParser>();
        services.AddSingleton<PhrasePatternMatcher>();
        services.AddSingleton<ExactPhraseIntentResolver>();
        services.AddSingleton(provider => new FuzzyPhraseIntentResolver(
            provider.GetRequiredService<ICommandTextNormalizer>(),
            provider.GetRequiredService<PhrasePatternMatcher>(),
            0.86,
            0.08));
        services.AddSingleton<IIntentResolver>(provider => new CompositeIntentResolver(
            provider.GetRequiredService<ExactPhraseIntentResolver>(),
            provider.GetRequiredService<FuzzyPhraseIntentResolver>()));
        services.AddSingleton<ICommandCatalog, CommandCatalog>();
        services.AddSingleton<IVoiceCommandExecutionService, VoiceCommandExecutionService>();
        services.AddSingleton<VoicePipelineCoordinator>();
        services.AddSingleton<IVoicePipelineController>(provider =>
            provider.GetRequiredService<VoicePipelineCoordinator>());
        services.AddSingleton<IVoiceModelActivationGate, VoiceModelActivationGate>();
        return services;
    }
}

/// <summary>Serializes model activation with the active voice pipeline.</summary>
public sealed class VoiceModelActivationGate(VoicePipelineCoordinator coordinator) : IVoiceModelActivationGate
{
    private readonly VoicePipelineCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <inheritdoc />
    public Task<IAsyncDisposable> EnterIdleAsync(CancellationToken cancellationToken) =>
        _coordinator.EnterIdleAsync(cancellationToken);
}
