namespace DeskPilot.Modules.AudioControl;

/// <summary>Provides platform-specific volume and mute operations.</summary>
public interface IAudioVolumeService
{
    Task<AudioVolumeState> GetStateAsync(CancellationToken cancellationToken);

    Task<AudioOperationResult> SetVolumeAsync(int percentage, CancellationToken cancellationToken);

    Task<AudioOperationResult> ChangeVolumeAsync(int delta, CancellationToken cancellationToken);

    Task<AudioOperationResult> SetMuteAsync(bool muted, CancellationToken cancellationToken);

    Task<AudioOperationResult> ToggleMuteAsync(CancellationToken cancellationToken);
}

/// <summary>Provides platform-specific audio output device operations.</summary>
public interface IAudioOutputDeviceService
{
    Task<IReadOnlyCollection<AudioOutputDevice>> GetDevicesAsync(CancellationToken cancellationToken);

    Task<AudioOutputDevice?> GetDefaultDeviceAsync(AudioDeviceRole role, CancellationToken cancellationToken);

    Task<AudioDeviceSwitchResult> SetDefaultDeviceAsync(AudioDeviceSwitchRequest request, CancellationToken cancellationToken);
}

/// <summary>Persists a preferred output device for a logical slot.</summary>
public interface IAudioPreferredDeviceService
{
    Task<AudioOutputDevice?> GetAsync(AudioDeviceSlot slot, CancellationToken cancellationToken);

    Task SaveAsync(AudioDeviceSlot slot, AudioOutputDevice device, CancellationToken cancellationToken);
}

/// <summary>Opens the system sound settings when automatic endpoint switching is unavailable.</summary>
public interface ISystemSoundSettingsLauncher
{
    AudioOperationResult Open();
}

public sealed record AudioOperationResult(bool IsSuccess, string? ErrorCode = null, string? Message = null);

public sealed record AudioVolumeState(string EndpointId, int Percentage, bool IsMuted);

public enum AudioDeviceRole
{
    Multimedia,
}

public sealed record AudioDeviceSwitchRequest(string EndpointId, AudioDeviceRole Role);

public sealed record AudioDeviceSwitchResult(bool IsSuccess, string? ErrorCode = null, string? Message = null);

public sealed record AudioOutputDevice(string EndpointId, string FriendlyName, bool IsAvailable, bool IsDefault);

public enum AudioDeviceSlot
{
    Speakers,
    Headphones,
}
