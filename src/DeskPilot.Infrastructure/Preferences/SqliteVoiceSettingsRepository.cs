using System.Globalization;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DeskPilot.Infrastructure.Preferences;

/// <summary>Stores voice configuration as explicit SQLite settings.</summary>
public sealed class SqliteVoiceSettingsRepository(IDbContextFactory<DeskPilotDbContext> contextFactory) : IVoiceSettingsRepository
{
    private const string EnabledKey = "voice.enabled";
    private const string MicrophoneEndpointKey = "voice.microphone.endpoint-id";
    private const string MicrophoneNameKey = "voice.microphone.friendly-name";
    private const string WakePhraseKey = "voice.wake.phrase";
    private const string WakeConfidenceKey = "voice.wake.confidence";
    private const string CooldownMillisecondsKey = "voice.cooldown-milliseconds";
    private const string RecognitionLanguageKey = "voice.recognition.language";

    /// <inheritdoc />
    public async Task<VoiceSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var values = await context.VoiceSettings
            .AsNoTracking()
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken)
            .ConfigureAwait(false);
        var defaults = VoiceSettings.Default;

        return new VoiceSettings(
            ReadBool(values, EnabledKey, defaults.IsEnabled),
            ReadNullable(values, MicrophoneEndpointKey),
            ReadNullable(values, MicrophoneNameKey),
            ReadString(values, WakePhraseKey, defaults.WakePhrase),
            ReadDouble(values, WakeConfidenceKey, defaults.WakeConfidence),
            TimeSpan.FromMilliseconds(ReadDouble(values, CooldownMillisecondsKey, defaults.Cooldown.TotalMilliseconds)),
            ReadString(values, RecognitionLanguageKey, defaults.RecognitionLanguage));
    }

    /// <inheritdoc />
    public async Task SaveAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.WakePhrase))
        {
            throw new ArgumentException("Wake phrase is required.", nameof(settings));
        }

        if (settings.WakeConfidence is < 0.65 or > 0.90)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Wake confidence must be in the inclusive 0.65..0.90 range.");
        }

        if (settings.Cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Cooldown must be positive.");
        }

        if (string.IsNullOrWhiteSpace(settings.RecognitionLanguage))
        {
            throw new ArgumentException("Recognition language is required.", nameof(settings));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnabledKey] = settings.IsEnabled.ToString(CultureInfo.InvariantCulture),
            [MicrophoneEndpointKey] = settings.MicrophoneEndpointId ?? string.Empty,
            [MicrophoneNameKey] = settings.MicrophoneFriendlyName ?? string.Empty,
            [WakePhraseKey] = settings.WakePhrase,
            [WakeConfidenceKey] = settings.WakeConfidence.ToString("R", CultureInfo.InvariantCulture),
            [CooldownMillisecondsKey] = settings.Cooldown.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture),
            [RecognitionLanguageKey] = settings.RecognitionLanguage,
        };

        var existing = await context.VoiceSettings
            .Where(setting => values.Keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, cancellationToken)
            .ConfigureAwait(false);

        foreach (var value in values)
        {
            if (existing.TryGetValue(value.Key, out var entity))
            {
                entity.Value = value.Value;
            }
            else
            {
                context.VoiceSettings.Add(new VoiceSettingEntity { Key = value.Key, Value = value.Value });
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var value)
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string? ReadNullable(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
