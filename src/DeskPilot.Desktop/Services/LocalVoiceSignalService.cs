using System.Media;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Desktop.Services;

internal interface ISystemVoiceTonePlayer
{
    void Play(VoiceSignal signal);
}

internal sealed class SystemVoiceTonePlayer : ISystemVoiceTonePlayer
{
    public void Play(VoiceSignal signal) => Resolve(signal).Play();

    internal static SystemSound Resolve(VoiceSignal signal) => signal switch
    {
        VoiceSignal.Ready => SystemSounds.Asterisk,
        VoiceSignal.Success => SystemSounds.Exclamation,
        VoiceSignal.Failure => SystemSounds.Hand,
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
    };
}

/// <summary>Plays fixed Windows feedback tones outside the WPF dispatcher.</summary>
public sealed class LocalVoiceSignalService : IVoiceSignalService
{
    private readonly ISystemVoiceTonePlayer _player;

    /// <summary>Creates the production Windows system-tone service.</summary>
    public LocalVoiceSignalService()
        : this(new SystemVoiceTonePlayer())
    {
    }

    internal LocalVoiceSignalService(ISystemVoiceTonePlayer player) =>
        _player = player ?? throw new ArgumentNullException(nameof(player));

    /// <inheritdoc />
    public Task PlayAsync(VoiceSignal signal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => _player.Play(signal), cancellationToken);
    }
}
