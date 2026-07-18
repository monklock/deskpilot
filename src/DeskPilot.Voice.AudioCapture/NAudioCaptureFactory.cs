using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Opens an exact Windows capture endpoint without default-device fallback.</summary>
public sealed class NAudioCaptureFactory(
    IAudioInputDeviceService devices,
    IWindowsCaptureClientFactory clients) : IAudioCaptureSessionFactory
{
    /// <inheritdoc />
    public async Task<IAudioCaptureSession> OpenAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        var resolution = await devices.ResolveAsync(endpointId, cancellationToken).ConfigureAwait(false);
        if (resolution.Code != AudioInputResultCode.Success || resolution.Device is null)
        {
            throw new AudioCaptureException(
                resolution.Code,
                resolution.Message ?? "Выбранный микрофон недоступен.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var client = clients.Create(resolution.Device.EndpointId)
                ?? throw new InvalidOperationException("The native capture factory returned no client.");
            return new NAudioCaptureSession(client);
        }
        catch (AudioCaptureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.InitializationFailed,
                "Не удалось открыть выбранный микрофон.",
                exception);
        }
    }
}
