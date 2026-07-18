using DeskPilot.Infrastructure.Data;
using DeskPilot.Modules.AudioControl;
using Microsoft.EntityFrameworkCore;

namespace DeskPilot.Infrastructure.Preferences;

/// <summary>Stores preferred audio output devices in the local SQLite database.</summary>
public sealed class SqliteAudioPreferredDeviceService(IDbContextFactory<DeskPilotDbContext> contextFactory) : IAudioPreferredDeviceService
{
    /// <inheritdoc />
    public async Task<AudioOutputDevice?> GetAsync(AudioDeviceSlot slot, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var preference = await context.AudioDevicePreferences.FindAsync([slot.ToString()], cancellationToken).ConfigureAwait(false);
        return preference is null ? null : new AudioOutputDevice(preference.EndpointId, preference.FriendlyName, false, false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(AudioDeviceSlot slot, AudioOutputDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = slot.ToString();
        var preference = await context.AudioDevicePreferences.FindAsync([key], cancellationToken).ConfigureAwait(false);

        if (preference is null)
        {
            context.AudioDevicePreferences.Add(new AudioDevicePreferenceEntity { Slot = key, EndpointId = device.EndpointId, FriendlyName = device.FriendlyName });
        }
        else
        {
            preference.EndpointId = device.EndpointId;
            preference.FriendlyName = device.FriendlyName;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
