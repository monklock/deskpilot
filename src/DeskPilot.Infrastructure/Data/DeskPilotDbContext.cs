using Microsoft.EntityFrameworkCore;

namespace DeskPilot.Infrastructure.Data;

/// <summary>Stores DeskPilot local settings.</summary>
public sealed class DeskPilotDbContext(DbContextOptions<DeskPilotDbContext> options) : DbContext(options)
{
    /// <summary>Gets application-wide settings.</summary>
    public DbSet<ApplicationSettingEntity> ApplicationSettings => Set<ApplicationSettingEntity>();

    /// <summary>Gets voice-specific settings.</summary>
    public DbSet<VoiceSettingEntity> VoiceSettings => Set<VoiceSettingEntity>();

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
