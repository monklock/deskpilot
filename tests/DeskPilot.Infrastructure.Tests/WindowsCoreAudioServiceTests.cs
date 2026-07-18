using DeskPilot.Infrastructure.WindowsAudio;
using DeskPilot.Modules.AudioControl;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskPilot.Infrastructure.Tests;

public sealed class WindowsCoreAudioServiceTests
{
    [Fact]
    public async Task ChangeVolumeAsync_ClampsTheTargetToOneHundred()
    {
        var client = new RecordingCoreAudioClient(new AudioVolumeState("default", 95, false));
        var service = new WindowsCoreAudioService(client, NullLogger<WindowsCoreAudioService>.Instance);

        var result = await service.ChangeVolumeAsync(20, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        client.LastVolume.Should().Be(100);
    }

    [Fact]
    public async Task SetDefaultDeviceAsync_MapsUnavailableEndpointToFailedResult()
    {
        var client = new RecordingCoreAudioClient(new AudioVolumeState("default", 50, false))
        {
            SwitchResult = new AudioDeviceSwitchResult(false, "device-unavailable", "The device is unavailable."),
        };
        var service = new WindowsCoreAudioService(client, NullLogger<WindowsCoreAudioService>.Instance);

        var result = await service.SetDefaultDeviceAsync(new AudioDeviceSwitchRequest("headphones", AudioDeviceRole.Multimedia), CancellationToken.None);

        result.Should().Be(new AudioDeviceSwitchResult(false, "device-unavailable", "The device is unavailable."));
    }

    private sealed class RecordingCoreAudioClient(AudioVolumeState state) : IWindowsCoreAudioClient
    {
        public int? LastVolume { get; private set; }

        public AudioDeviceSwitchResult SwitchResult { get; set; } = new(true);

        public IReadOnlyCollection<AudioOutputDevice> GetDevices() => [];

        public AudioOutputDevice? GetDefaultDevice(AudioDeviceRole role) => new(state.EndpointId, "Default", true, true);

        public AudioVolumeState GetVolumeState() => state;

        public void SetMute(bool muted)
        {
        }

        public void SetVolume(int percentage) => LastVolume = percentage;

        public AudioDeviceSwitchResult SetDefaultDevice(AudioDeviceSwitchRequest request) => SwitchResult;
    }
}
