using DeskPilot.Application.Voice;
using DeskPilot.Desktop.Services;
using DeskPilot.Desktop.ViewModels;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class DesktopHostRegistrationTests
{
    [Fact]
    public void CreateHost_RegistersCompleteVoiceGraphWithoutStartingNativeProviders()
    {
        using var host = App.CreateHost();

        host.Services.GetRequiredService<IVoicePipelineController>().Should().NotBeNull();
        host.Services.GetRequiredService<IVoicePipelineStateSource>().Should().NotBeNull();
        host.Services.GetRequiredService<IVoiceModelActivationGate>().Should().NotBeNull();
        host.Services.GetRequiredService<VoiceControlViewModel>().Should().NotBeNull();
        host.Services.GetRequiredService<VoiceModelManagerViewModel>().Should().NotBeNull();
    }

    [Fact]
    public async Task ShutdownHostAsync_DisableFailure_StillStopsAndDisposesHostResources()
    {
        var controller = Substitute.For<IVoicePipelineController>();
        controller.DisableAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("C:\\Users\\Person\\microphone"));
        var tray = Substitute.For<ITrayIconService>();
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IVoicePipelineController)).Returns(controller);
        services.GetService(typeof(ITrayIconService)).Returns(tray);
        var host = Substitute.For<IHost>();
        host.Services.Returns(services);

        await App.ShutdownHostAsync(host, TimeSpan.FromSeconds(1));

        tray.Received(1).Dispose();
        await host.Received(1).StopAsync(Arg.Any<CancellationToken>());
        host.Received(1).Dispose();
    }
}
