using DeskPilot.Infrastructure.Data;
using DeskPilot.Infrastructure.Preferences;
using DeskPilot.Infrastructure.ModelManagement;
using DeskPilot.Infrastructure.WindowsAudio;
using DeskPilot.Modules.AudioControl;
using DeskPilot.Voice.Abstractions;
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
        services.AddSingleton<IVoiceSettingsRepository, SqliteVoiceSettingsRepository>();
        services.AddSingleton<IVoiceModelStore, VoiceModelStore>();
        services.AddSingleton<PolicyConfigAudioEndpointSwitcher>();
        services.AddSingleton<IWindowsCoreAudioClient, NAudioWindowsCoreAudioClient>();
        services.AddSingleton<WindowsCoreAudioService>();
        services.AddSingleton<IAudioVolumeService>(provider => provider.GetRequiredService<WindowsCoreAudioService>());
        services.AddSingleton<IAudioOutputDeviceService>(provider => provider.GetRequiredService<WindowsCoreAudioService>());
        services.AddSingleton<ISystemSoundSettingsLauncher, SystemSoundSettingsLauncher>();
        return services;
    }

    /// <summary>Registers the secure voice model graph after release trust options are supplied.</summary>
    public static IServiceCollection AddDeskPilotVoiceModelManagement(
        this IServiceCollection services,
        VoiceModelManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var allowedOrigins = options.AllowedOrigins.ToArray();
        var allowedHosts = allowedOrigins.Select(origin => origin.IdnHost).ToArray();

        services.AddSingleton(options);
        services.AddSingleton<IVoiceModelHttpClient>(_ => new SafeVoiceModelHttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                Credentials = null,
                PreAuthenticate = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            },
            allowedOrigins,
            TimeSpan.FromMinutes(5)));
        services.AddSingleton(_ => new VoiceModelManifestVerifier(options.PublicKeyPem));
        services.AddSingleton<IVoiceModelCatalogSource>(provider => new VoiceModelCatalogClient(
            provider.GetRequiredService<IVoiceModelHttpClient>(),
            provider.GetRequiredService<VoiceModelManifestVerifier>(),
            options.ManifestUri,
            options.SignatureUri,
            allowedHosts));
        services.AddSingleton<VoiceModelInstaller>(provider => new VoiceModelInstaller(
            provider.GetRequiredService<IVoiceModelHttpClient>(),
            provider.GetRequiredService<IAppDataPaths>(),
            provider.GetRequiredService<IVoiceModelStore>(),
            allowedHosts,
            provider.GetService<TimeProvider>()));
        services.AddSingleton<IVoiceModelPackageInstaller>(provider => provider.GetRequiredService<VoiceModelInstaller>());
        services.AddSingleton<SeedVoiceModelInitializer>(provider => new SeedVoiceModelInitializer(
            options.SeedDirectory,
            provider.GetRequiredService<VoiceModelInstaller>(),
            provider.GetRequiredService<IVoiceModelStore>(),
            provider.GetRequiredService<VoiceModelManifestVerifier>()));
        services.AddSingleton<ISeedVoiceModelSource>(provider => provider.GetRequiredService<SeedVoiceModelInitializer>());
        services.AddSingleton<IVoiceModelManager, VoiceModelManager>();
        return services;
    }
}
