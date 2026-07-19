using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Voice.AudioCapture;

/// <summary>Wraps exact-device raw capture sessions with one continuous buffered owner.</summary>
public sealed class BufferedVoiceCaptureSessionFactory(
    IAudioCaptureSessionFactory captures) : IBufferedVoiceCaptureSessionFactory
{
    /// <inheritdoc />
    public async Task<IBufferedVoiceCaptureSession> OpenAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        var source = await captures.OpenAsync(endpointId, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new BufferedVoiceCaptureSession(source);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
