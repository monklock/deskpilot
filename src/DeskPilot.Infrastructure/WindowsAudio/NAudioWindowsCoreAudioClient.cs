using System.Runtime.InteropServices;
using DeskPilot.Modules.AudioControl;
using NAudio.CoreAudioApi;

namespace DeskPilot.Infrastructure.WindowsAudio;

/// <summary>Uses NAudio to access Windows render endpoints without leaking COM types.</summary>
public sealed class NAudioWindowsCoreAudioClient(PolicyConfigAudioEndpointSwitcher endpointSwitcher) : IWindowsCoreAudioClient
{
    /// <inheritdoc />
    public IReadOnlyCollection<AudioOutputDevice> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = new List<AudioOutputDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                devices.Add(new AudioOutputDevice(
                    endpoint.ID,
                    endpoint.FriendlyName,
                    true,
                    string.Equals(endpoint.ID, defaultEndpoint.ID, StringComparison.Ordinal)));
            }
        }

        return devices;
    }

    /// <inheritdoc />
    public AudioOutputDevice? GetDefaultDevice(AudioDeviceRole role)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, ToRole(role));
        return new AudioOutputDevice(endpoint.ID, endpoint.FriendlyName, true, true);
    }

    /// <inheritdoc />
    public AudioVolumeState GetVolumeState()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var percentage = (int)Math.Round(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100, MidpointRounding.AwayFromZero);
        return new AudioVolumeState(endpoint.ID, percentage, endpoint.AudioEndpointVolume.Mute);
    }

    /// <inheritdoc />
    public void SetVolume(int percentage)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percentage, 0, 100) / 100f;
    }

    /// <inheritdoc />
    public void SetMute(bool muted)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        endpoint.AudioEndpointVolume.Mute = muted;
    }

    /// <inheritdoc />
    public AudioDeviceSwitchResult SetDefaultDevice(AudioDeviceSwitchRequest request) => endpointSwitcher.SetDefaultDevice(request.EndpointId);

    private static Role ToRole(AudioDeviceRole role) => role == AudioDeviceRole.Multimedia ? Role.Multimedia : Role.Communications;
}
