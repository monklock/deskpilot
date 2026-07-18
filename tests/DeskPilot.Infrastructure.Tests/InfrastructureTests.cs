using DeskPilot.Infrastructure.Data;
using DeskPilot.Infrastructure.ModelManagement;
using DeskPilot.Infrastructure.Preferences;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Cryptography;
using Xunit;

namespace DeskPilot.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void DatabasePath_UsesConfiguredLocalApplicationDataDirectory()
    {
        var paths = new AppDataPaths("C:\\Users\\Test\\AppData\\Local");

        paths.DatabasePath.Should().Be("C:\\Users\\Test\\AppData\\Local\\DeskPilot\\data\\deskpilot.db");
        paths.ModelsRootPath.Should().Be("C:\\Users\\Test\\AppData\\Local\\DeskPilot\\models");
        paths.ModelDownloadsPath.Should().Be("C:\\Users\\Test\\AppData\\Local\\DeskPilot\\tmp\\model-downloads");
        paths.SeedModelsPath.Should().Be(Path.Combine(AppContext.BaseDirectory, "assets", "voice-models"));
    }

    [Fact]
    public async Task ApplyMigrationsAsync_CreatesExpectedLocalTables()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"deskpilot-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<DeskPilotDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            await using (var context = new DeskPilotDbContext(options))
            {
                await context.Database.MigrateAsync(CancellationToken.None);
                var tableNames = await context.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' ORDER BY name").ToListAsync(CancellationToken.None);

                tableNames.Should().Contain(["ApplicationSettings", "AudioDevicePreferences", "InstalledVoiceModels", "VoiceSettings"]);
                tableNames.Should().NotContain("Commands");
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void AddDeskPilotInfrastructure_RegistersVoiceSettingsAndModelStore()
    {
        var services = new ServiceCollection();

        services.AddDeskPilotInfrastructure();

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IVoiceSettingsRepository)
            && descriptor.ImplementationType == typeof(SqliteVoiceSettingsRepository));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IVoiceModelStore)
            && descriptor.ImplementationType == typeof(VoiceModelStore));
    }

    [Fact]
    public void AddDeskPilotVoiceModelManagement_RegistersResolvableProductionGraph()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var services = new ServiceCollection();
        var paths = new AppDataPaths(Path.GetTempPath());
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddSingleton(Substitute.For<IVoiceModelStore>());
        services.AddSingleton(Substitute.For<IVoiceModelActivationGate>());

        services.AddDeskPilotVoiceModelManagement(new VoiceModelManagementOptions(
            new Uri("https://downloads.example.test/models.manifest.json"),
            new Uri("https://downloads.example.test/models.manifest.sig"),
            signingKey.ExportSubjectPublicKeyInfoPem(),
            paths.SeedModelsPath,
            [new Uri("https://downloads.example.test")]));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        provider.GetRequiredService<IVoiceModelManager>().Should().BeOfType<VoiceModelManager>();
        provider.GetRequiredService<IVoiceModelHttpClient>().Should().BeOfType<SafeVoiceModelHttpClient>();
    }
}
