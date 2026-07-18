using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Represents a signed remote catalog or a release seed manifest.</summary>
public sealed class VoiceModelManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the catalog models.</summary>
    public required IReadOnlyList<VoiceModelManifestItem> Models { get; init; }
}

/// <summary>Represents one serialized model catalog item.</summary>
public sealed class VoiceModelManifestItem
{
    /// <summary>Gets the stable model identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the provider identifier.</summary>
    public VoiceModelProvider Provider { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the immutable model version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the quality tier.</summary>
    public required string QualityTier { get; init; }

    /// <summary>Gets the remote download URI.</summary>
    public Uri? DownloadUri { get; init; }

    /// <summary>Gets the release seed asset filename.</summary>
    public string? Asset { get; init; }

    /// <summary>Gets the payload SHA-256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the maximum download size.</summary>
    public long DownloadBytes { get; init; }

    /// <summary>Gets the maximum installed size.</summary>
    public long InstalledBytes { get; init; }

    /// <summary>Gets the expected installed entry point.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>Gets the SPDX license identifier.</summary>
    public required string LicenseId { get; init; }

    /// <summary>Gets the payload archive format.</summary>
    public VoiceModelArchiveFormat ArchiveFormat { get; init; }

    /// <summary>Gets the minimum compatible provider version.</summary>
    public required string MinimumProviderVersion { get; init; }

    /// <summary>Gets the maximum compatible provider version.</summary>
    public required string MaximumProviderVersion { get; init; }
}

internal static class VoiceModelManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static VoiceModelManifest Deserialize(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }

            return JsonSerializer.Deserialize<VoiceModelManifest>(bytes, Options)
                ?? throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Каталог моделей пуст.");
        }
        catch (JsonException exception)
        {
            throw new VoiceModelCatalogException(VoiceModelResultCode.InvalidManifest, "Каталог моделей имеет некорректный формат.", exception);
        }
    }
}
