using DeskPilot.Infrastructure.Data;
using DeskPilot.Voice.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Stores immutable model versions and atomically changes active versions.</summary>
public sealed class VoiceModelStore(IDbContextFactory<DeskPilotDbContext> contextFactory) : IVoiceModelStore
{
    /// <inheritdoc />
    public async Task RegisterAsync(InstalledVoiceModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var providerId = model.ProviderId.ToString();
        var existing = await context.InstalledVoiceModels
            .SingleOrDefaultAsync(
                item => item.ProviderId == providerId && item.Version == model.Version,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.ModelId, model.ModelId, StringComparison.Ordinal)
                || !string.Equals(existing.RelativePath, model.RelativePath, StringComparison.Ordinal)
                || !string.Equals(existing.Sha256, model.Sha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Source, model.Source.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The installed voice model version conflicts with immutable metadata.");
            }

            return;
        }

        if (model.IsActive)
        {
            var current = await context.InstalledVoiceModels
                .Where(item => item.ProviderId == providerId && item.IsActive)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in current)
            {
                item.IsActive = false;
                item.IsLastKnownGood = true;
            }
        }

        context.InstalledVoiceModels.Add(ToEntity(model));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledVoiceModel>> GetInstalledAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var providerId = provider.ToString();
        var entities = await context.InstalledVoiceModels
            .AsNoTracking()
            .Where(item => item.ProviderId == providerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities
            .OrderBy(item => item.InstalledAt)
            .Select(ToModel)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<InstalledVoiceModel?> GetActiveAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var providerId = provider.ToString();
        var entity = await context.InstalledVoiceModels
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProviderId == providerId && item.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    /// <inheritdoc />
    public async Task ActivateAsync(
        VoiceModelProvider provider,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var providerId = provider.ToString();
        var models = await context.InstalledVoiceModels
            .Where(item => item.ProviderId == providerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var requested = models.SingleOrDefault(item => item.Version == version)
            ?? throw new InvalidOperationException("The requested voice model version is not installed.");
        if (requested.IsActive)
        {
            return;
        }

        foreach (var model in models)
        {
            model.IsLastKnownGood = false;
            if (model.IsActive && model != requested)
            {
                model.IsActive = false;
                model.IsLastKnownGood = true;
            }
        }

        requested.IsActive = true;
        requested.IsLastKnownGood = false;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static InstalledVoiceModelEntity ToEntity(InstalledVoiceModel model) => new()
    {
        ProviderId = model.ProviderId.ToString(),
        Version = model.Version,
        ModelId = model.ModelId,
        RelativePath = model.RelativePath,
        Sha256 = model.Sha256,
        Source = model.Source.ToString(),
        IsActive = model.IsActive,
        IsLastKnownGood = model.IsLastKnownGood,
        InstalledAt = model.InstalledAt,
    };

    private static InstalledVoiceModel ToModel(InstalledVoiceModelEntity entity) => new(
        Enum.Parse<VoiceModelProvider>(entity.ProviderId),
        entity.ModelId,
        entity.Version,
        entity.RelativePath,
        entity.Sha256,
        Enum.Parse<VoiceModelSource>(entity.Source),
        entity.IsActive,
        entity.IsLastKnownGood,
        entity.InstalledAt);
}
