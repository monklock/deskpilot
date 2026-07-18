using DeskPilot.Core.Commands;
using DeskPilot.Modules.AudioControl;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeskPilot.Modules.Tests;

public sealed class AudioControlTests
{
    [Fact]
    public async Task SetVolumeHandler_RejectsPercentageOutsideInclusiveRange()
    {
        var service = Substitute.For<IAudioVolumeService>();
        var handler = new SetVolumeCommandHandler(service);

        var result = await handler.HandleAsync(
            Request("audio.set-volume", ("percentage", "101")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        await service.DidNotReceive().SetVolumeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetVolumeHandler_RoutesValidPercentageToVolumeService()
    {
        var service = Substitute.For<IAudioVolumeService>();
        service.SetVolumeAsync(45, Arg.Any<CancellationToken>()).Returns(new AudioOperationResult(true));
        var handler = new SetVolumeCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.set-volume", ("percentage", "45")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await service.Received(1).SetVolumeAsync(45, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeVolumeHandler_RejectsDeltaOutsideInclusiveRange()
    {
        var service = Substitute.For<IAudioVolumeService>();
        var handler = new ChangeVolumeCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.change-volume", ("delta", "-101")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        await service.DidNotReceive().ChangeVolumeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeVolumeHandler_MapsUnsuccessfulOperationToFailed()
    {
        var service = Substitute.For<IAudioVolumeService>();
        service.ChangeVolumeAsync(10, Arg.Any<CancellationToken>()).Returns(new AudioOperationResult(false, "no-endpoint", "No endpoint."));
        var handler = new ChangeVolumeCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.change-volume", ("delta", "10")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Failed);
        result.Message.Should().Be("No endpoint.");
    }

    [Fact]
    public async Task SetMuteHandler_RejectsNonBooleanMutedValue()
    {
        var service = Substitute.For<IAudioVolumeService>();
        var handler = new SetMuteCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.set-mute", ("muted", "yes")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        await service.DidNotReceive().SetMuteAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMuteHandler_RoutesBooleanValueToVolumeService()
    {
        var service = Substitute.For<IAudioVolumeService>();
        service.SetMuteAsync(true, Arg.Any<CancellationToken>()).Returns(new AudioOperationResult(true));
        var handler = new SetMuteCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.set-mute", ("muted", "true")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await service.Received(1).SetMuteAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleMuteHandler_RoutesToVolumeService()
    {
        var service = Substitute.For<IAudioVolumeService>();
        service.ToggleMuteAsync(Arg.Any<CancellationToken>()).Returns(new AudioOperationResult(true));
        var handler = new ToggleMuteCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.toggle-mute"), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await service.Received(1).ToggleMuteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefaultDeviceHandler_RejectsBlankEndpointId()
    {
        var service = Substitute.For<IAudioOutputDeviceService>();
        var handler = new SetDefaultDeviceCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.set-default-device", ("endpointId", "  ")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        await service.DidNotReceive().SetDefaultDeviceAsync(Arg.Any<AudioDeviceSwitchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefaultDeviceHandler_RoutesEndpointToMultimediaRole()
    {
        var service = Substitute.For<IAudioOutputDeviceService>();
        service.SetDefaultDeviceAsync(new AudioDeviceSwitchRequest("endpoint-a", AudioDeviceRole.Multimedia), Arg.Any<CancellationToken>())
            .Returns(new AudioDeviceSwitchResult(true));
        var handler = new SetDefaultDeviceCommandHandler(service);

        var result = await handler.HandleAsync(Request("audio.set-default-device", ("endpointId", "endpoint-a")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await service.Received(1).SetDefaultDeviceAsync(
            new AudioDeviceSwitchRequest("endpoint-a", AudioDeviceRole.Multimedia),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SavePreferredDeviceHandler_RejectsUnknownSlot()
    {
        var outputService = Substitute.For<IAudioOutputDeviceService>();
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        var handler = new SavePreferredDeviceCommandHandler(outputService, preferences);

        var result = await handler.HandleAsync(
            Request("audio.save-preferred-device", ("endpointId", "endpoint-a"), ("slot", "Monitor")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        await preferences.DidNotReceive().SaveAsync(Arg.Any<AudioDeviceSlot>(), Arg.Any<AudioOutputDevice>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SavePreferredDeviceHandler_SavesMatchingOutputDeviceInRequestedSlot()
    {
        var device = new AudioOutputDevice("endpoint-a", "Headphones", true, false);
        var outputService = Substitute.For<IAudioOutputDeviceService>();
        outputService.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([device]);
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        var handler = new SavePreferredDeviceCommandHandler(outputService, preferences);

        var result = await handler.HandleAsync(
            Request("audio.save-preferred-device", ("endpointId", "endpoint-a"), ("slot", "Headphones")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await preferences.Received(1).SaveAsync(AudioDeviceSlot.Headphones, device, Arg.Any<CancellationToken>());
    }

    private static CommandRequest Request(string commandId, params (string Key, string Value)[] arguments) =>
        new(CommandId.From(commandId), arguments.ToDictionary(argument => argument.Key, argument => argument.Value));
}
