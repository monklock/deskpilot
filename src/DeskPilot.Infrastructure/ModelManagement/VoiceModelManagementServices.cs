using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Provides a verified remote voice model catalog.</summary>
public interface IVoiceModelCatalogSource
{
    /// <summary>Gets the verified catalog.</summary>
    Task<IReadOnlyList<VoiceModelDescriptor>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Installs a downloaded voice model package.</summary>
public interface IVoiceModelPackageInstaller
{
    /// <summary>Downloads and installs one model.</summary>
    Task<VoiceModelOperationResult> InstallAsync(
        VoiceModelDescriptor model,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Provides and restores release seed models.</summary>
public interface ISeedVoiceModelSource
{
    /// <summary>Gets release seed catalog items.</summary>
    Task<IReadOnlyList<VoiceModelDescriptor>> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Installs missing release seed models.</summary>
    Task<IReadOnlyList<VoiceModelOperationResult>> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Restores one release seed model.</summary>
    Task<(VoiceModelOperationResult Result, string? Version)> RestoreAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken);
}
