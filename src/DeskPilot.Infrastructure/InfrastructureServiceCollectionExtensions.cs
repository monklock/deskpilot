using DeskPilot.Infrastructure.Data;
using DeskPilot.Infrastructure.Preferences;
using DeskPilot.Infrastructure.WindowsAudio;
using DeskPilot.Modules.AudioControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Infrastructure;

/// <summary>Registers DeskPilot infrastructure services.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers local persistence and runtime path services.</summary>
    public static IServiceCollection AddDeskPilotInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var paths = new AppDataPaths(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddDbContextFactory<DeskPilotDbContext>(options => options.UseSqlite($"Data Source={paths.DatabasePath}"));
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IAudioPreferredDeviceService, SqliteAudioPreferredDeviceService>();
        services.AddSingleton<PolicyConfigAudioEndpointSwitcher>();
        services.AddSingleton<IWindowsCoreAudioClient, NAudioWindowsCoreAudioClient>();
        services.AddSingleton<WindowsCoreAudioService>();
        services.AddSingleton<IAudioVolumeService>(provider => provider.GetRequiredService<WindowsCoreAudioService>());
        services.AddSingleton<IAudioOutputDeviceService>(provider => provider.GetRequiredService<WindowsCoreAudioService>());
        services.AddSingleton<ISystemSoundSettingsLauncher, SystemSoundSettingsLauncher>();
        return services;
    }
}
