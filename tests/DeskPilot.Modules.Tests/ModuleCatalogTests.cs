using DeskPilot.Modules.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeskPilot.Modules.Tests;

public sealed class ModuleCatalogTests
{
    [Fact]
    public async Task InitializeAsync_InitializesEachEnabledModuleOnce()
    {
        var module = new RecordingModule();
        var catalog = new ModuleCatalog([module]);

        await catalog.InitializeAsync(new TestModuleContext(), CancellationToken.None);

        module.InitializationCount.Should().Be(1);
    }

    private sealed class RecordingModule : IDeskPilotModule
    {
        public ModuleMetadata Metadata { get; } = new("test.module", "Test", new Version(1, 0, 0), "Test module", []);

        public int InitializationCount { get; private set; }

        public void RegisterServices(IServiceCollection services)
        {
        }

        public Task InitializeAsync(IModuleContext context, CancellationToken cancellationToken)
        {
            InitializationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestModuleContext : IModuleContext
    {
        public string ApplicationDataPath => "C:\\Temp";

        public TimeProvider TimeProvider => TimeProvider.System;
    }
}
