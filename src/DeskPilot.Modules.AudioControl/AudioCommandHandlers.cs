using System.Globalization;
using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions.Commands;
using static DeskPilot.Modules.AudioControl.AudioCommandHandlerSupport;

namespace DeskPilot.Modules.AudioControl;

public sealed class SetVolumeCommandHandler(IAudioVolumeService service) : ICommandHandler
{
    public CommandId CommandId => DeskPilot.Core.Commands.CommandId.From("audio.set-volume");

    public async Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetInt(request, "percentage", 0, 100, out var percentage)) return Rejected(request, "Percentage must be between 0 and 100.");
        return ToResult(request, await service.SetVolumeAsync(percentage, cancellationToken));
    }
}

public sealed class ChangeVolumeCommandHandler(IAudioVolumeService service) : ICommandHandler
{
    public CommandId CommandId => DeskPilot.Core.Commands.CommandId.From("audio.change-volume");

    public async Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetInt(request, "delta", -100, 100, out var delta)) return Rejected(request, "Delta must be between -100 and 100.");
        return ToResult(request, await service.ChangeVolumeAsync(delta, cancellationToken));
    }
}

public sealed class SetMuteCommandHandler(IAudioVolumeService service) : ICommandHandler
{
    public CommandId CommandId => DeskPilot.Core.Commands.CommandId.From("audio.set-mute");

    public async Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetBoolean(request, "muted", out var muted)) return Rejected(request, "Muted must be true or false.");
        return ToResult(request, await service.SetMuteAsync(muted, cancellationToken));
    }
}

public sealed class ToggleMuteCommandHandler(IAudioVolumeService service) : ICommandHandler
{
    public CommandId CommandId => DeskPilot.Core.Commands.CommandId.From("audio.toggle-mute");

    public async Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken) =>
        ToResult(request, await service.ToggleMuteAsync(cancellationToken));
}

public sealed class SetDefaultDeviceCommandHandler(IAudioOutputDeviceService service) : ICommandHandler
{
    public CommandId CommandId => DeskPilot.Core.Commands.CommandId.From("audio.set-default-device");

    public async Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetRequired(request, "endpointId", out var endpointId)) return Rejected(request, "Endpoint ID is required.");
        var result = await service.SetDefaultDeviceAsync(new AudioDeviceSwitchRequest(endpointId, AudioDeviceRole.Multimedia), cancellationToken);
        return result.Succeeded ? CommandExecutionResult.Succeeded(request.CommandId) : new CommandExecutionResult(request.CommandId, CommandExecutionStatus.Failed, result.Message);
    }
}

public sealed class SavePreferredDeviceCommandHandler(IAudioOutputDeviceService outputService, IAudioPreferredDeviceService preferences) : ICommandHandler
{
    public CommandId CommandId => DeskPilot.Core.Commands.CommandId.From("audio.save-preferred-device");

    public async Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetRequired(request, "endpointId", out var endpointId) || !TryGetSlot(request, out var slot)) return Rejected(request, "Endpoint ID and a valid slot are required.");
        var device = (await outputService.GetDevicesAsync(cancellationToken)).FirstOrDefault(candidate => string.Equals(candidate.EndpointId, endpointId, StringComparison.Ordinal));
        if (device is null) return Rejected(request, "The audio output device was not found.");
        await preferences.SaveAsync(slot, device, cancellationToken);
        return CommandExecutionResult.Succeeded(request.CommandId);
    }
}

file static class AudioCommandHandlerSupport
{
    public static bool TryGetInt(CommandRequest request, string key, int minimum, int maximum, out int value)
    {
        value = default;
        return request.Arguments is not null && request.Arguments.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
    }

    public static bool TryGetBoolean(CommandRequest request, string key, out bool value)
    {
        value = default;
        return request.Arguments is not null && request.Arguments.TryGetValue(key, out var text) && bool.TryParse(text, out value);
    }

    public static bool TryGetRequired(CommandRequest request, string key, out string value)
    {
        value = string.Empty;
        return request.Arguments is not null && request.Arguments.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) && (value = text.Trim()).Length > 0;
    }

    public static bool TryGetSlot(CommandRequest request, out AudioDeviceSlot slot)
    {
        slot = default;
        return request.Arguments is not null && request.Arguments.TryGetValue("slot", out var text) && Enum.TryParse(text, true, out slot) && Enum.IsDefined(slot);
    }

    public static CommandExecutionResult Rejected(CommandRequest request, string message) => new(request.CommandId, CommandExecutionStatus.Rejected, message);

    public static CommandExecutionResult ToResult(CommandRequest request, AudioOperationResult result) =>
        result.Succeeded ? CommandExecutionResult.Succeeded(request.CommandId) : new CommandExecutionResult(request.CommandId, CommandExecutionStatus.Failed, result.Message);
}
