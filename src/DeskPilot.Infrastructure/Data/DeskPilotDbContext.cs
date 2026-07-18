using Microsoft.EntityFrameworkCore;

namespace DeskPilot.Infrastructure.Data;

/// <summary>Stores DeskPilot local settings.</summary>
public sealed class DeskPilotDbContext(DbContextOptions<DeskPilotDbContext> options) : DbContext(options)
{
    /// <summary>Gets application-wide settings.</summary>
    public DbSet<ApplicationSettingEntity> ApplicationSettings => Set<ApplicationSettingEntity>();

    /// <summary>Gets voice-specific settings.</summary>
    public DbSet<VoiceSettingEntity> VoiceSettings => Set<VoiceSettingEntity>();

    /// <summary>Gets persisted preferred audio output devices.</summary>
    public DbSet<AudioDevicePreferenceEntity> AudioDevicePreferences => Set<AudioDevicePreferenceEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationSettingEntity>(entity =>
        {
            entity.ToTable("ApplicationSettings");
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(128);
            entity.Property(setting => setting.Value).HasMaxLength(4096);
        });

        modelBuilder.Entity<VoiceSettingEntity>(entity =>
        {
            entity.ToTable("VoiceSettings");
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(128);
            entity.Property(setting => setting.Value).HasMaxLength(4096);
        });

        modelBuilder.Entity<AudioDevicePreferenceEntity>(entity =>
        {
            entity.ToTable("AudioDevicePreferences");
            entity.HasKey(preference => preference.Slot);
            entity.Property(preference => preference.Slot).HasMaxLength(32);
            entity.Property(preference => preference.EndpointId).HasMaxLength(1024);
            entity.Property(preference => preference.FriendlyName).HasMaxLength(512);
        });
    }
}

/// <summary>Represents one persisted application setting.</summary>
public sealed class ApplicationSettingEntity
{
    /// <summary>Gets or sets the unique setting key.</summary>
    public required string Key { get; set; }

    /// <summary>Gets or sets the serialized setting value.</summary>
    public required string Value { get; set; }
}

/// <summary>Represents one persisted voice setting.</summary>
public sealed class VoiceSettingEntity
{
    /// <summary>Gets or sets the unique setting key.</summary>
    public required string Key { get; set; }

    /// <summary>Gets or sets the serialized setting value.</summary>
    public required string Value { get; set; }
}

/// <summary>Represents one preferred audio output device.</summary>
public sealed class AudioDevicePreferenceEntity
{
    /// <summary>Gets or sets the logical device slot.</summary>
    public required string Slot { get; set; }

    /// <summary>Gets or sets the stable Windows endpoint identifier.</summary>
    public required string EndpointId { get; set; }

    /// <summary>Gets or sets the display name captured when saved.</summary>
    public required string FriendlyName { get; set; }
}
