using System.Security.Cryptography;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Verifies exact catalog bytes with the release public key.</summary>
public sealed class VoiceModelManifestVerifier
{
    private readonly string _publicKeyPem;

    /// <summary>Creates a verifier from an ECDSA subject public key.</summary>
    public VoiceModelManifestVerifier(string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        _publicKeyPem = publicKeyPem;
    }

    /// <summary>Verifies an ECDSA P-256/SHA-256 signature.</summary>
    public bool Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature)
    {
        using var algorithm = ECDsa.Create();
        algorithm.ImportFromPem(_publicKeyPem);
        return algorithm.VerifyData(manifest, signature, HashAlgorithmName.SHA256);
    }
}
