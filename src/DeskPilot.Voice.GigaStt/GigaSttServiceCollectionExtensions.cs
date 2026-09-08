using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Voice.GigaStt;

/// <summary>Registers the shared offline recognizer process.</summary>
public static class GigaSttServiceCollectionExtensions
{
    /// <summary>Registers the process owner used by both voice adapters.</summary>
    public static IServiceCollection AddDeskPilotGigaStt(this IServiceCollection services)
    {
        services.AddSingleton<GigaSttRuntime>();
        return services;
    }
}
