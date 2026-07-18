using System.Runtime.CompilerServices;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Resolves explicit and default Windows capture endpoints without silent fallback.</summary>
public sealed class NAudioInputDeviceService(IWindowsCaptureEndpointSource source) : IAudioInputDeviceService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AudioInputDevice>> GetActiveAsync(CancellationToken cancellationToken) =>
        source.GetActiveAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<AudioInputResolution> ResolveAsync(
        string? explicitEndpointId,
        CancellationToken cancellationToken)
    {
        var devices = await source.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (explicitEndpointId is not null)
        {
            var selected = devices.FirstOrDefault(device =>
                string.Equals(device.EndpointId, explicitEndpointId, StringComparison.Ordinal));
            return selected is null
                ? new AudioInputResolution(
                    AudioInputResultCode.SelectedDeviceUnavailable,
                    null,
                    "Выбранный микрофон сейчас недоступен.")
                : new AudioInputResolution(AudioInputResultCode.Success, selected);
        }

        var defaultDevice = devices.FirstOrDefault(device => device.IsDefault);
        return defaultDevice is null
            ? new AudioInputResolution(
                AudioInputResultCode.NoDevice,
                null,
                "Активный микрофон Windows не найден.")
            : new AudioInputResolution(AudioInputResultCode.Success, defaultDevice);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var changes = source.WatchChangesAsync(watchCancellation.Token)
            .GetAsyncEnumerator(watchCancellation.Token);
        Task<bool>? pendingChange = null;
        try
        {
            pendingChange = changes.MoveNextAsync().AsTask();
            yield return await source.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            while (await pendingChange.ConfigureAwait(false))
            {
                pendingChange = changes.MoveNextAsync().AsTask();
                yield return await source.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            watchCancellation.Cancel();
            if (pendingChange is not null)
            {
                try
                {
                    await pendingChange.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (watchCancellation.IsCancellationRequested)
                {
                }
            }

            await changes.DisposeAsync().ConfigureAwait(false);
        }
    }
}
