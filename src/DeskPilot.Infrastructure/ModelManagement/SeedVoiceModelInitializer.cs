using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Verifies and installs read-only release seed models.</summary>
public sealed class SeedVoiceModelInitializer(
    string seedDirectory,
    VoiceModelInstaller installer,
    IVoiceModelStore store,
    VoiceModelManifestVerifier verifier) : ISeedVoiceModelSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<VoiceModelDescriptor>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        return manifest.Models
            .Select(item =>
            {
                VoiceModelCatalogClient.ValidateItem(item);
                return VoiceModelCatalogClient.ToDescriptor(
                    item,
                    downloadUri: null,
                    isBuiltIn: true);
            })
            .ToArray();
    }

    /// <summary>Installs missing seed versions and activates them only when no active version exists.</summary>
    public async Task<IReadOnlyList<VoiceModelOperationResult>> InitializeAsync(CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<VoiceModelOperationResult>(manifest.Models.Count);

        foreach (var item in manifest.Models)
        {
            VoiceModelCatalogClient.ValidateItem(item);
            var assetPath = ResolveAssetPath(item.Asset);
            var descriptor = VoiceModelCatalogClient.ToDescriptor(
                item,
                downloadUri: null,
                isBuiltIn: true);
            var result = await installer
                .InstallLocalAsync(descriptor, assetPath, cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);
            if (result.Code == VoiceModelResultCode.Success
                && await store.GetActiveAsync(item.Provider, cancellationToken).ConfigureAwait(false) is null)
            {
                await store.ActivateAsync(item.Provider, item.Version, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>Reinstalls one release seed model and returns its version.</summary>
    public async Task<(VoiceModelOperationResult Result, string? Version)> RestoreAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var item = manifest.Models.SingleOrDefault(model => model.Provider == provider);
        if (item is null)
        {
            return (new VoiceModelOperationResult(VoiceModelResultCode.InvalidManifest, "Встроенная модель провайдера не найдена."), null);
        }

        VoiceModelCatalogClient.ValidateItem(item);
        var descriptor = VoiceModelCatalogClient.ToDescriptor(
            item,
            downloadUri: null,
            isBuiltIn: true);
        var result = await installer
            .InstallLocalAsync(descriptor, ResolveAssetPath(item.Asset), cancellationToken, replaceExisting: true)
            .ConfigureAwait(false);
        return (result, result.Code == VoiceModelResultCode.Success ? item.Version : null);
    }

    private async Task<VoiceModelManifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        byte[] signature;
        try
        {
            var seedRoot = Path.GetFullPath(seedDirectory);
            bytes = await File
                .ReadAllBytesAsync(Path.Combine(seedRoot, "seed-manifest.json"), cancellationToken)
                .ConfigureAwait(false);
            signature = await File
                .ReadAllBytesAsync(Path.Combine(seedRoot, "seed-manifest.sig"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new VoiceModelCatalogException(
                VoiceModelResultCode.InvalidSignature,
                "Подпись встроенного каталога моделей недоступна.",
                exception);
        }

        if (!verifier.Verify(bytes, signature))
        {
            throw new VoiceModelCatalogException(
                VoiceModelResultCode.InvalidSignature,
                "Подпись встроенного каталога моделей недействительна.");
        }

        var manifest = VoiceModelManifestSerializer.Deserialize(bytes);
        if (manifest.SchemaVersion != 1 || manifest.Models.Count == 0)
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Встроенный каталог моделей некорректен.");
        }

        return manifest;
    }

    private string ResolveAssetPath(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset) || Path.IsPathRooted(asset))
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Имя встроенного файла модели некорректно.");
        }

        var root = Path.GetFullPath(seedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, asset));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Встроенный файл модели отсутствует.");
        }

        return path;
    }
}
