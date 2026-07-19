using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions;
using DeskPilot.Modules.Abstractions.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Modules.AudioControl;

/// <summary>Registers the manual audio control command surface.</summary>
public sealed class AudioControlModule : IDeskPilotModule
{
    private static readonly IReadOnlyCollection<CommandId> Commands =
    [
        CommandId.From("audio.set-volume"),
        CommandId.From("audio.change-volume"),
        CommandId.From("audio.set-mute"),
        CommandId.From("audio.toggle-mute"),
        CommandId.From("audio.set-default-device"),
        CommandId.From("audio.save-preferred-device"),
        CommandId.From("audio.switch-preferred-device"),
        CommandId.From("audio.toggle-preferred-device"),
    ];

    /// <inheritdoc />
    public ModuleMetadata Metadata { get; } = new(
        "deskpilot.audio-control",
        "Audio Control",
        new Version(1, 0, 0),
        "Controls Windows output volume, mute state, and preferred audio endpoints.",
        Commands);

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICommandHandler, SetVolumeCommandHandler>();
        services.AddSingleton<ICommandHandler, ChangeVolumeCommandHandler>();
        services.AddSingleton<ICommandHandler, SetMuteCommandHandler>();
        services.AddSingleton<ICommandHandler, ToggleMuteCommandHandler>();
        services.AddSingleton<ICommandHandler, SetDefaultDeviceCommandHandler>();
        services.AddSingleton<ICommandHandler, SavePreferredDeviceCommandHandler>();
        services.AddSingleton<ICommandHandler, SwitchPreferredDeviceCommandHandler>();
        services.AddSingleton<ICommandHandler, TogglePreferredDeviceCommandHandler>();
        services.AddSingleton<ICommandDescriptionProvider, AudioCommandDescriptionProvider>();
    }

    /// <inheritdoc />
    public Task InitializeAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
