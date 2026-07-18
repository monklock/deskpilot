using System.Buffers;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Loads and verifies the signed remote voice model catalog.</summary>
public sealed class VoiceModelCatalogClient : IVoiceModelCatalogSource
{
    private const int MaximumManifestBytes = 1_048_576;
    private const int MaximumSignatureBytes = 4_096;
    private readonly IVoiceModelHttpClient _httpClient;
    private readonly VoiceModelManifestVerifier _verifier;
    private readonly Uri _manifestUri;
    private readonly Uri _signatureUri;
    private readonly HashSet<string> _allowedHosts;

    /// <summary>Creates a signed catalog client.</summary>
    public VoiceModelCatalogClient(
        IVoiceModelHttpClient httpClient,
        VoiceModelManifestVerifier verifier,
        Uri manifestUri,
        Uri signatureUri,
        IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(signatureUri);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _httpClient = httpClient;
        _verifier = verifier;
        _manifestUri = manifestUri;
        _signatureUri = signatureUri;
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        ValidateUri(_manifestUri);
        ValidateUri(_signatureUri);
    }

    /// <summary>Gets the verified remote catalog.</summary>
    public async Task<IReadOnlyList<VoiceModelDescriptor>> GetAsync(CancellationToken cancellationToken)
    {
        var manifestBytes = await DownloadBoundedAsync(_manifestUri, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await DownloadBoundedAsync(_signatureUri, MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
        if (!_verifier.Verify(manifestBytes, signatureBytes))
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidSignature, "Подпись каталога моделей недействительна.");
        }

        var manifest = VoiceModelManifestSerializer.Deserialize(manifestBytes);
        if (manifest.SchemaVersion != 1)
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Версия каталога моделей не поддерживается.");
        }

        var models = new List<VoiceModelDescriptor>(manifest.Models.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in manifest.Models)
        {
            if (item.DownloadUri is null)
            {
                throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "В каталоге отсутствует адрес модели.");
            }

            ValidateUri(item.DownloadUri);
            ValidateItem(item);
            if (!identities.Add($"{item.Provider}:{item.Id}:{item.Version}"))
            {
                throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Каталог содержит повторяющуюся версию модели.");
            }

            models.Add(ToDescriptor(item, item.DownloadUri, isBuiltIn: false));
        }

        return models;
    }

    internal static VoiceModelDescriptor ToDescriptor(VoiceModelManifestItem item, Uri? downloadUri, bool isBuiltIn) => new(
        item.Id,
        item.Provider,
        item.DisplayName,
        item.Version,
        item.QualityTier,
        downloadUri,
        item.Sha256,
        item.DownloadBytes,
        item.InstalledBytes,
        item.EntryPoint,
        item.LicenseId,
        isBuiltIn,
        item.ArchiveFormat,
        item.MinimumProviderVersion,
        item.MaximumProviderVersion);

    internal static void ValidateItem(VoiceModelManifestItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.DisplayName)
            || string.IsNullOrWhiteSpace(item.Version)
            || string.IsNullOrWhiteSpace(item.QualityTier)
            || string.IsNullOrWhiteSpace(item.EntryPoint)
            || string.IsNullOrWhiteSpace(item.LicenseId)
            || !Version.TryParse(item.MinimumProviderVersion, out var minimumVersion)
            || !Version.TryParse(item.MaximumProviderVersion, out var maximumVersion)
            || minimumVersion > maximumVersion
            || item.Sha256.Length != 64
            || !item.Sha256.All(Uri.IsHexDigit)
            || item.DownloadBytes <= 0
            || item.InstalledBytes <= 0)
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Каталог содержит некорректную модель.");
        }
    }

    private async Task<byte[]> DownloadBoundedAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        return await _httpClient.GetAsync(
            uri,
            async (response, operationToken) =>
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
                {
                    throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Файл каталога превышает допустимый размер.");
                }

                await using var source = await response.Content.ReadAsStreamAsync(operationToken).ConfigureAwait(false);
                using var destination = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(16_384);
                try
                {
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), operationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }

                        if (destination.Length + count > maximumBytes)
                        {
                            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Файл каталога превышает допустимый размер.");
                        }

                        destination.Write(buffer, 0, count);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return destination.ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidateUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !_allowedHosts.Contains(uri.Host))
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Источник каталога моделей не разрешён.");
        }
    }
}

/// <summary>Represents a safe typed catalog failure.</summary>
public sealed class VoiceModelCatalogException : Exception
{
    /// <summary>Creates a catalog failure.</summary>
    public VoiceModelCatalogException(VoiceModelResultCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the typed failure code.</summary>
    public VoiceModelResultCode Code { get; }
}
