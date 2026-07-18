using DeskPilot.Application.Commands;
using DeskPilot.Core.Commands;
using DeskPilot.Desktop.ViewModels;
using DeskPilot.Modules.AudioControl;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class AudioControlViewModelTests
{
    [Fact]
    public async Task RefreshAsync_MarksSavedBluetoothHeadphonesUnavailableWhileDisconnected()
    {
        var savedHeadphones = Device("bluetooth-headset", "Bluetooth Headset", isAvailable: false);
        var context = CreateContext([], savedHeadphones);

        await context.ViewModel.RefreshAsync();

        context.ViewModel.HeadphonesDevice.Should().BeEquivalentTo(savedHeadphones);
        context.ViewModel.CanUseHeadphones.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_RestoresSavedBluetoothHeadphonesWhenEndpointReconnects()
    {
        var savedHeadphones = Device("bluetooth-headset", "Bluetooth Headset", isAvailable: false);
        var connectedHeadphones = Device("bluetooth-headset", "Bluetooth Headset", isAvailable: true);
        var context = CreateContext([connectedHeadphones], savedHeadphones);

        await context.ViewModel.RefreshAsync();

        context.ViewModel.HeadphonesDevice.Should().BeEquivalentTo(connectedHeadphones);
        context.ViewModel.CanUseHeadphones.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAsync_ReflectsExternalDefaultVolumeAndMuteChanges()
    {
        var speakers = new AudioOutputDevice("speakers", "Desk Speakers", true, true);
        var context = CreateContext(
            [speakers],
            savedHeadphones: null,
            new AudioVolumeState("speakers", 72, true),
            speakers);

        await context.ViewModel.RefreshAsync();

        context.ViewModel.DefaultDeviceName.Should().Be("Desk Speakers");
        context.ViewModel.VolumePercentage.Should().Be(72);
        context.ViewModel.IsMuted.Should().BeTrue();
    }

    [Fact]
    public async Task UseHeadphonesCommand_DoesNotDispatchWhenBluetoothHeadphonesAreDisconnected()
    {
        var context = CreateContext([], Device("bluetooth-headset", "Bluetooth Headset", isAvailable: false));
        await context.ViewModel.RefreshAsync();

        await context.ViewModel.UseHeadphonesCommand.ExecuteAsync(null);

        await context.Dispatcher.DidNotReceive().DispatchAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>());
        context.ViewModel.StatusMessage.Should().Contain("Bluetooth");
    }

    [Fact]
    public async Task UseHeadphonesCommand_OpensWindowsSettingsWhenAutomaticSwitchFails()
    {
        var connectedHeadphones = Device("bluetooth-headset", "Bluetooth Headset", isAvailable: true);
        var context = CreateContext([connectedHeadphones], connectedHeadphones);
        context.Dispatcher.DispatchAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new CommandExecutionResult(call.Arg<CommandRequest>().CommandId, CommandExecutionStatus.Failed, "Switch failed."));
        context.SettingsLauncher.Open().Returns(new AudioOperationResult(true));
        await context.ViewModel.RefreshAsync();

        await context.ViewModel.UseHeadphonesCommand.ExecuteAsync(null);

        context.SettingsLauncher.Received(1).Open();
        context.ViewModel.StatusMessage.Should().Contain("параметры звука Windows");
    }

    [Fact]
    public async Task SaveHeadphonesCommand_DispatchesSelectedEndpointAndHeadphonesSlot()
    {
        var headphones = Device("bluetooth-headset", "Bluetooth Headset", isAvailable: true);
        var context = CreateContext([headphones], savedHeadphones: null);
        await context.ViewModel.RefreshAsync();

        await context.ViewModel.SaveHeadphonesCommand.ExecuteAsync(null);

        await context.Dispatcher.Received(1).DispatchAsync(
            Arg.Is<CommandRequest>(request =>
                request.CommandId == CommandId.From("audio.save-preferred-device")
                && request.Arguments!["endpointId"] == "bluetooth-headset"
                && request.Arguments["slot"] == "Headphones"),
            Arg.Any<CancellationToken>());
    }

    private static TestContext CreateContext(
        IReadOnlyCollection<AudioOutputDevice> devices,
        AudioOutputDevice? savedHeadphones,
        AudioVolumeState? volumeState = null,
        AudioOutputDevice? defaultDevice = null)
    {
        var output = Substitute.For<IAudioOutputDeviceService>();
        output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns(devices);
        output.GetDefaultDeviceAsync(AudioDeviceRole.Multimedia, Arg.Any<CancellationToken>()).Returns(defaultDevice ?? devices.FirstOrDefault());

        var volume = Substitute.For<IAudioVolumeService>();
        volume.GetStateAsync(Arg.Any<CancellationToken>()).Returns(volumeState ?? new AudioVolumeState("default", 35, false));

        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        preferences.GetAsync(AudioDeviceSlot.Speakers, Arg.Any<CancellationToken>()).Returns((AudioOutputDevice?)null);
        preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>()).Returns(savedHeadphones);

        var settingsLauncher = Substitute.For<ISystemSoundSettingsLauncher>();
        var dispatcher = Substitute.For<ICommandDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CommandExecutionResult.Succeeded(call.Arg<CommandRequest>().CommandId));

        return new TestContext(
            new AudioControlViewModel(
                output,
                volume,
                preferences,
                settingsLauncher,
                dispatcher,
                Substitute.For<ILogger<AudioControlViewModel>>()),
            dispatcher,
            settingsLauncher);
    }

    private static AudioOutputDevice Device(string endpointId, string name, bool isAvailable) =>
        new(endpointId, name, isAvailable, false);

    private sealed record TestContext(
        AudioControlViewModel ViewModel,
        ICommandDispatcher Dispatcher,
        ISystemSoundSettingsLauncher SettingsLauncher);
}
