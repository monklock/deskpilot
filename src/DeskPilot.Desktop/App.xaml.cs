using System.Diagnostics;
using System.IO;
using System.Windows;
using DeskPilot.Application.Commands;
using DeskPilot.Application.Voice;
using DeskPilot.Desktop.Services;
using DeskPilot.Desktop.ViewModels;
using DeskPilot.Infrastructure;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Infrastructure.ModelManagement;
using DeskPilot.Modules.Abstractions;
using DeskPilot.Modules.AudioControl;
using DeskPilot.Voice.Abstractions;
using DeskPilot.Voice.AudioCapture;
using DeskPilot.Voice.GigaStt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeskPilot.Desktop;

/// <summary>Owns the WPF and Generic Host lifecycles.</summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = CreateHost();

        try
        {
            await _host.StartAsync();
            await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
            var seedInitializer = _host.Services.GetService<SeedVoiceModelInitializer>();
            bool? seedInitializationSucceeded = null;
            if (seedInitializer is not null)
            {
                var results = await seedInitializer.InitializeAsync(CancellationToken.None);
                seedInitializationSucceeded = results.All(result => result.Code == VoiceModelResultCode.Success);
            }

            await _host.Services.GetRequiredService<ModuleCatalog>().InitializeAsync(_host.Services.GetRequiredService<IModuleContext>(), CancellationToken.None);
            var voiceSettings = await _host.Services.GetRequiredService<IVoiceSettingsRepository>().GetAsync(CancellationToken.None);
            if (ShouldEnableVoicePipeline(voiceSettings.IsEnabled, seedInitializationSucceeded))
            {
                await _host.Services.GetRequiredService<IVoicePipelineController>().EnableAsync(CancellationToken.None);
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            _host.Services.GetRequiredService<ITrayIconService>().Start();
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogCritical("DeskPilot startup failed with {ExceptionType}.", exception.GetType().Name);
            System.Windows.MessageBox.Show("DeskPilot could not start. See the local log for details.", "DeskPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await ShutdownHostAsync(_host, TimeSpan.FromSeconds(5));
        }

        base.OnExit(e);
    }

    internal static IHost CreateHost()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddDebug())
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddDeskPilotInfrastructure();
                services.AddDeskPilotVoiceAudioCapture();
                services.AddSingleton<GigaSttRuntime>();
                services.AddDeskPilotVoiceApplication();
                services.AddSingleton<IVoiceRuntimeProviderFactory, LocalVoiceRuntimeProviderFactory>();
                services.AddSingleton<IVoiceSignalService, LocalVoiceSignalService>();
                AddVoiceModelManagement(services);
                services.AddSingleton<IModuleContext, LocalModuleContext>();
                var moduleCatalog = new ModuleCatalog([new AudioControlModule()]);
                moduleCatalog.RegisterServices(services);
                services.AddSingleton(moduleCatalog);
                services.AddSingleton<ICommandDispatcher, CommandDispatcher>();
                services.AddSingleton<AudioControlViewModel>();
                services.AddSingleton<VoiceModelManagerViewModel>();
                services.AddSingleton<VoiceControlViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<ITrayIconService, TrayIconService>();
            });

        if (Debugger.IsAttached)
        {
            builder.UseDefaultServiceProvider((_, options) =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
        }

        return builder.Build();
    }

    internal static bool ShouldEnableVoicePipeline(
        bool voiceEnabled,
        bool? seedInitializationSucceeded) =>
        voiceEnabled && seedInitializationSucceeded is not false;

    internal static async Task ShutdownHostAsync(IHost host, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var cancellation = new CancellationTokenSource(timeout);
        var logger = host.Services.GetService<ILogger<App>>();
        try
        {
            await host.Services
                .GetRequiredService<IVoicePipelineController>()
                .DisableAsync(cancellation.Token);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                "Voice pipeline shutdown failed with {ExceptionType}.",
                exception.GetType().Name);
        }

        try
        {
            host.Services.GetService<ITrayIconService>()?.Dispose();
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Tray shutdown failed with {ExceptionType}.", exception.GetType().Name);
        }

        try
        {
            if (!cancellation.IsCancellationRequested)
            {
                await host.StopAsync(cancellation.Token);
            }
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Host shutdown failed with {ExceptionType}.", exception.GetType().Name);
        }
        finally
        {
            host.Dispose();
        }
    }

    private static void AddVoiceModelManagement(IServiceCollection services)
    {
        var seedDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "voice-models");
        var seedManifestPath = Path.Combine(seedDirectory, "seed-manifest.json");
        var publicKeyPath = Path.Combine(seedDirectory, "catalog-public-key.pem");
        if (!File.Exists(seedManifestPath))
        {
            services.AddSingleton<IVoiceModelManager, UnavailableVoiceModelManager>();
            return;
        }

        if (!File.Exists(publicKeyPath))
        {
            throw new InvalidOperationException("The release voice model public key is missing.");
        }

        var publicKey = File.ReadAllText(publicKeyPath);

        var options = new VoiceModelManagementOptions(
            new Uri("https://github.com/monklock/deskpilot/releases/download/voice-models-gigastt-v1/models.manifest.json"),
            new Uri("https://github.com/monklock/deskpilot/releases/download/voice-models-gigastt-v1/models.manifest.sig"),
            publicKey,
            seedDirectory,
            [
                new Uri("https://raw.githubusercontent.com"),
                new Uri("https://github.com"),
                new Uri("https://objects.githubusercontent.com"),
                new Uri("https://release-assets.githubusercontent.com"),
            ]);
        services.AddDeskPilotVoiceModelManagement(options);
    }
}
