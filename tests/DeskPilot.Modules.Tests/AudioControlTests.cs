using DeskPilot.Core.Commands;
using DeskPilot.Modules.AudioControl;
using DeskPilot.Modules.Abstractions.Commands;
using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeskPilot.Modules.Tests;

public sealed class AudioControlTests
{
    [Fact]
    public void AudioControlModule_RegistersEveryAdvertisedCommandHandler()
    {
        var module = new AudioControlModule();
        var services = new ServiceCollection();

        module.RegisterServices(services);

        var registeredHandlerTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(ICommandHandler))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        registeredHandlerTypes.Should().BeEquivalentTo(
        [
            typeof(SetVolumeCommandHandler),
            typeof(ChangeVolumeCommandHandler),
            typeof(SetMuteCommandHandler),
            typeof(ToggleMuteCommandHandler),
            typeof(SetDefaultDeviceCommandHandler),
            typeof(SavePreferredDeviceCommandHandler),
            typeof(SwitchPreferredDeviceCommandHandler),
            typeof(TogglePreferredDeviceCommandHandler),
        ]);
        module.Metadata.SupportedCommands.Select(static command => command.Value).Should().BeEquivalentTo(
        [
            "audio.set-volume",
            "audio.change-volume",
            "audio.set-mute",
            "audio.toggle-mute",
            "audio.set-default-device",
            "audio.save-preferred-device",
            "audio.switch-preferred-device",
            "audio.toggle-preferred-device",
        ]);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandDescriptionProvider)
            && descriptor.ImplementationType == typeof(AudioCommandDescriptionProvider));
    }

    [Fact]
    public void AudioCommandDescriptions_ContainEveryApprovedVoiceIntent()
    {
        var descriptions = new AudioCommandDescriptionProvider().GetCommands();

        descriptions.Select(static item => item.CommandId.Value).Should().BeEquivalentTo(
        [
            "audio.switch-preferred-device",
            "audio.toggle-preferred-device",
            "audio.change-volume",
            "audio.set-volume",
            "audio.set-mute",
            "audio.toggle-mute",
        ]);
        descriptions.SelectMany(static item => item.Phrases)
            .Should().Contain(phrase => phrase.Pattern == "сделай громче"
                && phrase.Arguments!["delta"] == "10")
            .And.Contain(phrase => phrase.Pattern == "сделай тише"
                && phrase.Arguments!["delta"] == "-10")
            .And.Contain(phrase => phrase.Pattern == "громкость {percentage}")
            .And.Contain(phrase => phrase.Pattern == "переключи на наушники"
                && phrase.Arguments!["slot"] == "Headphones")
            .And.Contain(phrase => phrase.Pattern == "переключи на колонки"
                && phrase.Arguments!["slot"] == "Speakers")
            .And.Contain(phrase => phrase.Pattern == "выключи звук"
                && phrase.Arguments!["muted"] == "true")
            .And.Contain(phrase => phrase.Pattern == "включи звук"
                && phrase.Arguments!["muted"] == "false");
    }

    [Fact]
    public async Task SetVolumeHandler_RejectsPercentageOutsideInclusiveRange()
    {
        var service = Substitute.For<IAudioVolumeService>();
        var handler = new SetVolumeCommandHandler(service);

        var result = await handler.HandleAsync(
            Request("audio.set-volume", ("percentage", "101")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        result.ErrorCode.Should().Be("audio-invalid-arguments");
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
        result.ErrorCode.Should().Be("no-endpoint");
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

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task SavePreferredDeviceHandler_RejectsNumericSlot(string slot)
    {
        var outputService = Substitute.For<IAudioOutputDeviceService>();
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        var handler = new SavePreferredDeviceCommandHandler(outputService, preferences);

        var result = await handler.HandleAsync(
            Request("audio.save-preferred-device", ("endpointId", "endpoint-a"), ("slot", slot)),
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

    [Fact]
    public async Task SwitchPreferredDeviceHandler_UsesSavedAvailableEndpoint()
    {
        var saved = new AudioOutputDevice("headset", "Headset", false, false);
        var active = saved with { IsAvailable = true };
        var output = Substitute.For<IAudioOutputDeviceService>();
        output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([active]);
        output.SetDefaultDeviceAsync(
                new AudioDeviceSwitchRequest("headset", AudioDeviceRole.Multimedia),
                Arg.Any<CancellationToken>())
            .Returns(new AudioDeviceSwitchResult(true));
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
            .Returns(saved);
        var handler = new SwitchPreferredDeviceCommandHandler(output, preferences);

        var result = await handler.HandleAsync(
            Request("audio.switch-preferred-device", ("slot", "Headphones")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await output.Received(1).SetDefaultDeviceAsync(
            new AudioDeviceSwitchRequest("headset", AudioDeviceRole.Multimedia),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchPreferredDeviceHandler_MissingPreferenceIsRejected()
    {
        var output = Substitute.For<IAudioOutputDeviceService>();
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        var handler = new SwitchPreferredDeviceCommandHandler(output, preferences);

        var result = await handler.HandleAsync(
            Request("audio.switch-preferred-device", ("slot", "Headphones")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        result.ErrorCode.Should().Be("audio-preference-missing");
        await output.DidNotReceive().SetDefaultDeviceAsync(
            Arg.Any<AudioDeviceSwitchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchPreferredDeviceHandler_DisconnectedEndpointFailsWithoutFallback()
    {
        var saved = new AudioOutputDevice("headset", "Headset", false, false);
        var unrelated = new AudioOutputDevice("speakers", "Speakers", true, true);
        var output = Substitute.For<IAudioOutputDeviceService>();
        output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([unrelated]);
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
            .Returns(saved);
        var handler = new SwitchPreferredDeviceCommandHandler(output, preferences);

        var result = await handler.HandleAsync(
            Request("audio.switch-preferred-device", ("slot", "Headphones")),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Failed);
        result.ErrorCode.Should().Be("audio-endpoint-unavailable");
        await output.DidNotReceive().SetDefaultDeviceAsync(
            Arg.Any<AudioDeviceSwitchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TogglePreferredDeviceHandler_SwitchesFromSpeakersToHeadphones()
    {
        var speakers = new AudioOutputDevice("speakers", "Speakers", true, true);
        var headphones = new AudioOutputDevice("headphones", "Headphones", true, false);
        var output = Substitute.For<IAudioOutputDeviceService>();
        output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([speakers, headphones]);
        output.GetDefaultDeviceAsync(AudioDeviceRole.Multimedia, Arg.Any<CancellationToken>())
            .Returns(speakers);
        output.SetDefaultDeviceAsync(
                new AudioDeviceSwitchRequest("headphones", AudioDeviceRole.Multimedia),
                Arg.Any<CancellationToken>())
            .Returns(new AudioDeviceSwitchResult(true));
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        preferences.GetAsync(AudioDeviceSlot.Speakers, Arg.Any<CancellationToken>())
            .Returns(speakers);
        preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
            .Returns(headphones);
        var handler = new TogglePreferredDeviceCommandHandler(output, preferences);

        var result = await handler.HandleAsync(
            Request("audio.toggle-preferred-device"),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        await output.Received(1).SetDefaultDeviceAsync(
            new AudioDeviceSwitchRequest("headphones", AudioDeviceRole.Multimedia),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TogglePreferredDeviceHandler_CurrentEndpointOutsideSavedPairIsRejected()
    {
        var speakers = new AudioOutputDevice("speakers", "Speakers", true, false);
        var headphones = new AudioOutputDevice("headphones", "Headphones", true, false);
        var current = new AudioOutputDevice("monitor", "Monitor", true, true);
        var output = Substitute.For<IAudioOutputDeviceService>();
        output.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns([speakers, headphones, current]);
        output.GetDefaultDeviceAsync(AudioDeviceRole.Multimedia, Arg.Any<CancellationToken>())
            .Returns(current);
        var preferences = Substitute.For<IAudioPreferredDeviceService>();
        preferences.GetAsync(AudioDeviceSlot.Speakers, Arg.Any<CancellationToken>())
            .Returns(speakers);
        preferences.GetAsync(AudioDeviceSlot.Headphones, Arg.Any<CancellationToken>())
            .Returns(headphones);
        var handler = new TogglePreferredDeviceCommandHandler(output, preferences);

        var result = await handler.HandleAsync(
            Request("audio.toggle-preferred-device"),
            CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Rejected);
        result.ErrorCode.Should().Be("audio-current-endpoint-not-preferred");
        await output.DidNotReceive().SetDefaultDeviceAsync(
            Arg.Any<AudioDeviceSwitchRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static CommandRequest Request(string commandId, params (string Key, string Value)[] arguments) =>
        new(CommandId.From(commandId), arguments.ToDictionary(argument => argument.Key, argument => argument.Value));
}
