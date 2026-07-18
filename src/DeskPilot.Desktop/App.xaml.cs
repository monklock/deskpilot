using System.Diagnostics;
using System.Windows;
using DeskPilot.Application.Commands;
using DeskPilot.Desktop.Services;
using DeskPilot.Desktop.ViewModels;
using DeskPilot.Infrastructure;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Modules.Abstractions;
using DeskPilot.Modules.AudioControl;
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
            await _host.Services.GetRequiredService<ModuleCatalog>().InitializeAsync(_host.Services.GetRequiredService<IModuleContext>(), CancellationToken.None);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            _host.Services.GetRequiredService<ITrayIconService>().Start();
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<ILogger<App>>().LogCritical(exception, "DeskPilot startup failed.");
            System.Windows.MessageBox.Show("DeskPilot could not start. See the local log for details.", "DeskPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.Services.GetRequiredService<ITrayIconService>().Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _host.StopAsync(timeout.Token);
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static IHost CreateHost()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddDebug())
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddDeskPilotInfrastructure();
                services.AddSingleton<IModuleContext, LocalModuleContext>();
                var moduleCatalog = new ModuleCatalog([new AudioControlModule()]);
                moduleCatalog.RegisterServices(services);
                services.AddSingleton(moduleCatalog);
                services.AddSingleton<ICommandDispatcher, CommandDispatcher>();
                services.AddSingleton<AudioControlViewModel>();
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
}
