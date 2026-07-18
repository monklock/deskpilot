namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Defines trusted release endpoints and seed assets for voice model management.</summary>
public sealed record VoiceModelManagementOptions(
    Uri ManifestUri,
    Uri SignatureUri,
    string PublicKeyPem,
    string SeedDirectory,
    IReadOnlyCollection<Uri> AllowedOrigins)
{
    /// <summary>Validates that all required trust configuration is present.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ManifestUri);
        ArgumentNullException.ThrowIfNull(SignatureUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(PublicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(SeedDirectory);
        ArgumentNullException.ThrowIfNull(AllowedOrigins);
        if (AllowedOrigins.Count == 0)
        {
            throw new ArgumentException("At least one voice model origin is required.", nameof(AllowedOrigins));
        }
    }
}
