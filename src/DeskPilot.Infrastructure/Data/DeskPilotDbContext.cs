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

    /// <summary>Gets installed voice model versions.</summary>
    public DbSet<InstalledVoiceModelEntity> InstalledVoiceModels => Set<InstalledVoiceModelEntity>();

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

        modelBuilder.Entity<InstalledVoiceModelEntity>(entity =>
        {
            entity.ToTable("InstalledVoiceModels");
            entity.HasKey(model => new { model.ProviderId, model.Version });
            entity.Property(model => model.ProviderId).HasMaxLength(32);
            entity.Property(model => model.Version).HasMaxLength(128);
            entity.Property(model => model.ModelId).HasMaxLength(128);
            entity.Property(model => model.RelativePath).HasMaxLength(1024);
            entity.Property(model => model.Sha256).HasMaxLength(64);
            entity.Property(model => model.Source).HasMaxLength(32);
            entity.HasIndex(model => model.ProviderId)
                .IsUnique()
                .HasFilter("\"IsActive\" = 1");
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

/// <summary>Represents one immutable installed voice model version.</summary>
public sealed class InstalledVoiceModelEntity
{
    /// <summary>Gets or sets the stable provider identifier.</summary>
    public required string ProviderId { get; set; }

    /// <summary>Gets or sets the model version.</summary>
    public required string Version { get; set; }

    /// <summary>Gets or sets the stable model identifier.</summary>
    public required string ModelId { get; set; }

    /// <summary>Gets or sets the path relative to the models root.</summary>
    public required string RelativePath { get; set; }

    /// <summary>Gets or sets the verified model hash.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Gets or sets the installation source.</summary>
    public required string Source { get; set; }

    /// <summary>Gets or sets whether this version is active.</summary>
    public bool IsActive { get; set; }

    /// <summary>Gets or sets whether this version is the rollback target.</summary>
    public bool IsLastKnownGood { get; set; }

    /// <summary>Gets or sets the installation timestamp.</summary>
    public DateTimeOffset InstalledAt { get; set; }
}
