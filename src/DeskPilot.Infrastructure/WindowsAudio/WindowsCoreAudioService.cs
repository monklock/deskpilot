using System.Runtime.InteropServices;
using DeskPilot.Modules.AudioControl;
using Microsoft.Extensions.Logging;

namespace DeskPilot.Infrastructure.WindowsAudio;

/// <summary>Provides safe asynchronous access to Windows Core Audio operations.</summary>
public sealed class WindowsCoreAudioService(IWindowsCoreAudioClient client, ILogger<WindowsCoreAudioService> logger) : IAudioVolumeService, IAudioOutputDeviceService
{
    /// <inheritdoc />
    public Task<AudioVolumeState> GetStateAsync(CancellationToken cancellationToken) => ExecuteAsync(client.GetVolumeState, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyCollection<AudioOutputDevice>> GetDevicesAsync(CancellationToken cancellationToken) => ExecuteAsync(client.GetDevices, cancellationToken);

    /// <inheritdoc />
    public Task<AudioOutputDevice?> GetDefaultDeviceAsync(AudioDeviceRole role, CancellationToken cancellationToken) => ExecuteAsync(() => client.GetDefaultDevice(role), cancellationToken);

    /// <inheritdoc />
    public Task<AudioOperationResult> SetVolumeAsync(int percentage, CancellationToken cancellationToken) => ExecuteOperationAsync(() => client.SetVolume(Math.Clamp(percentage, 0, 100)), cancellationToken);

    /// <inheritdoc />
    public async Task<AudioOperationResult> ChangeVolumeAsync(int delta, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        return await SetVolumeAsync(Math.Clamp(state.Percentage + delta, 0, 100), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AudioOperationResult> SetMuteAsync(bool muted, CancellationToken cancellationToken) => ExecuteOperationAsync(() => client.SetMute(muted), cancellationToken);

    /// <inheritdoc />
    public async Task<AudioOperationResult> ToggleMuteAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        return await SetMuteAsync(!state.IsMuted, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AudioDeviceSwitchResult> SetDefaultDeviceAsync(AudioDeviceSwitchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(client.SetDefaultDevice(request));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogWarning(exception, "Unable to select Windows audio endpoint {EndpointId}.", request.EndpointId);
            return Task.FromResult(new AudioDeviceSwitchResult(false, "audio-operation-failed", "The audio device could not be selected."));
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogWarning(exception, "Windows Core Audio operation failed.");
            throw new InvalidOperationException("Windows audio is unavailable.", exception);
        }
    }

    private Task<AudioOperationResult> ExecuteOperationAsync(Action operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            operation();
            return Task.FromResult(new AudioOperationResult(true));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogWarning(exception, "Windows Core Audio operation failed.");
            return Task.FromResult(new AudioOperationResult(false, "audio-operation-failed", "The audio operation could not be completed."));
        }
    }
}
