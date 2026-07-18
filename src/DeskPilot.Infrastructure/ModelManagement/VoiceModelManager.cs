using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Coordinates model catalog, installation, activation, and seed restoration.</summary>
public sealed class VoiceModelManager(
    IVoiceModelCatalogSource catalog,
    IVoiceModelPackageInstaller installer,
    ISeedVoiceModelSource seeds,
    IVoiceModelStore store,
    IVoiceModelActivationGate activationGate) : IVoiceModelManager
{
    private IReadOnlyList<VoiceModelDescriptor> _remoteCatalog = [];

    /// <inheritdoc />
    public async Task<VoiceModelState> GetStateAsync(CancellationToken cancellationToken) =>
        await BuildStateAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<VoiceModelState> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        _remoteCatalog = await catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        return await BuildStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<VoiceModelOperationResult> InstallAsync(
        VoiceModelDescriptor model,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken) =>
        installer.InstallAsync(model, progress, cancellationToken);

    /// <inheritdoc />
    public async Task<VoiceModelOperationResult> ActivateAsync(
        VoiceModelProvider provider,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await activationGate.EnterIdleAsync(cancellationToken).ConfigureAwait(false);
            await store.ActivateAsync(provider, version, cancellationToken).ConfigureAwait(false);
            return new VoiceModelOperationResult(VoiceModelResultCode.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.Cancelled, "Активация модели отменена.");
        }
        catch (InvalidOperationException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.InvalidModel, "Выбранная версия модели не установлена.");
        }
        catch (IOException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Не удалось активировать модель.");
        }
    }

    /// <inheritdoc />
    public async Task<VoiceModelOperationResult> RestoreBuiltInAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await activationGate.EnterIdleAsync(cancellationToken).ConfigureAwait(false);
            var restored = await seeds.RestoreAsync(provider, cancellationToken).ConfigureAwait(false);
            if (restored.Result.Code != VoiceModelResultCode.Success || restored.Version is null)
            {
                return restored.Result;
            }

            await store.ActivateAsync(provider, restored.Version, cancellationToken).ConfigureAwait(false);
            return restored.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.Cancelled, "Восстановление модели отменено.");
        }
        catch (IOException)
        {
            return new VoiceModelOperationResult(VoiceModelResultCode.IoFailure, "Не удалось восстановить встроенную модель.");
        }
    }

    private async Task<VoiceModelState> BuildStateAsync(CancellationToken cancellationToken)
    {
        var seedCatalog = await seeds.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var catalogByIdentity = seedCatalog
            .Concat(_remoteCatalog)
            .GroupBy(model => $"{model.ProviderId}:{model.Id}:{model.Version}", StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var activeVersions = new Dictionary<VoiceModelProvider, string>();
        var installedModels = new List<InstalledVoiceModel>();
        foreach (var provider in Enum.GetValues<VoiceModelProvider>())
        {
            var installed = await store.GetInstalledAsync(provider, cancellationToken).ConfigureAwait(false);
            installedModels.AddRange(installed);
            var active = installed.SingleOrDefault(model => model.IsActive);
            if (active is not null)
            {
                activeVersions[provider] = active.Version;
            }
        }

        return new VoiceModelState(catalogByIdentity, activeVersions)
        {
            InstalledModels = installedModels,
        };
    }
}
