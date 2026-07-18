using DeskPilot.Modules.AudioControl;

namespace DeskPilot.Infrastructure.WindowsAudio;

/// <summary>Encapsulates Windows Core Audio calls for testable infrastructure services.</summary>
public interface IWindowsCoreAudioClient
{
    IReadOnlyCollection<AudioOutputDevice> GetDevices();

    AudioOutputDevice? GetDefaultDevice(AudioDeviceRole role);

    AudioVolumeState GetVolumeState();

    void SetVolume(int percentage);

    void SetMute(bool muted);

    AudioDeviceSwitchResult SetDefaultDevice(AudioDeviceSwitchRequest request);
}
