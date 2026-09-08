namespace DeskPilot.Voice.Abstractions;

/// <summary>Identifies a supported voice model provider.</summary>
public enum VoiceModelProvider
{
    /// <summary>Vosk wake-word recognition.</summary>
    WakeVosk,
    /// <summary>Whisper command recognition.</summary>
    CommandWhisper,
    /// <summary>Shared GigaSTT wake-word and command recognition bundle.</summary>
    GigaStt,
}

/// <summary>Identifies how a voice model was installed.</summary>
public enum VoiceModelSource
{
    /// <summary>The model was included with the application release.</summary>
    Seed,
    /// <summary>The model was downloaded by explicit user action.</summary>
    Download,
}

/// <summary>Identifies how a downloaded model payload is packaged.</summary>
public enum VoiceModelArchiveFormat
{
    /// <summary>The payload is the model entry point.</summary>
    None,
    /// <summary>The payload is a ZIP archive.</summary>
    Zip,
}

/// <summary>Identifies a model-management operation result.</summary>
public enum VoiceModelResultCode
{
    /// <summary>The operation completed successfully.</summary>
    Success,
    /// <summary>The operation was cancelled.</summary>
    Cancelled,
    /// <summary>The catalog manifest is invalid.</summary>
    InvalidManifest,
    /// <summary>The catalog signature is invalid.</summary>
    InvalidSignature,
    /// <summary>The downloaded content hash is invalid.</summary>
    HashMismatch,
    /// <summary>The model is incompatible with the provider.</summary>
    Incompatible,
    /// <summary>There is insufficient local disk space.</summary>
    InsufficientSpace,
    /// <summary>The archive contains unsafe content.</summary>
    UnsafeArchive,
    /// <summary>The model content is invalid.</summary>
    InvalidModel,
    /// <summary>A local input/output operation failed.</summary>
    IoFailure,
}

/// <summary>Describes one available voice model.</summary>
public sealed record VoiceModelDescriptor(
    string Id,
    VoiceModelProvider ProviderId,
    string DisplayName,
    string Version,
    string QualityTier,
    Uri? DownloadUri,
    string Sha256,
    long DownloadBytes,
    long InstalledBytes,
    string EntryPoint,
    string LicenseId,
    bool IsBuiltIn,
    VoiceModelArchiveFormat ArchiveFormat,
    string MinimumProviderVersion,
    string MaximumProviderVersion);

/// <summary>Reports model installation progress.</summary>
public sealed record VoiceModelProgress(string ModelId, long BytesReceived, long TotalBytes, string Stage);

/// <summary>Describes one locally installed immutable model version.</summary>
public sealed record InstalledVoiceModel(
    VoiceModelProvider ProviderId,
    string ModelId,
    string Version,
    string RelativePath,
    string Sha256,
    VoiceModelSource Source,
    bool IsActive,
    bool IsLastKnownGood,
    DateTimeOffset InstalledAt);

/// <summary>Contains the result of a model-management operation.</summary>
public sealed record VoiceModelOperationResult(VoiceModelResultCode Code, string? Message = null);

/// <summary>Contains the available catalog and active model versions.</summary>
public sealed record VoiceModelState(
    IReadOnlyList<VoiceModelDescriptor> Catalog,
    IReadOnlyDictionary<VoiceModelProvider, string> ActiveVersions)
{
    /// <summary>Gets every locally installed version, including the rollback candidate.</summary>
    public IReadOnlyList<InstalledVoiceModel> InstalledModels { get; init; } = [];

    /// <summary>Gets an empty model state.</summary>
    public static VoiceModelState Empty { get; } = new([], new Dictionary<VoiceModelProvider, string>());
}

/// <summary>Manages local voice models.</summary>
public interface IVoiceModelManager
{
    /// <summary>Gets installed and available model state.</summary>
    Task<VoiceModelState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Checks the signed remote catalog without downloading a model.</summary>
    Task<VoiceModelState> CheckForUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>Downloads and installs a model after explicit user action.</summary>
    Task<VoiceModelOperationResult> InstallAsync(
        VoiceModelDescriptor model,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Activates an installed model version.</summary>
    Task<VoiceModelOperationResult> ActivateAsync(
        VoiceModelProvider provider,
        string version,
        CancellationToken cancellationToken);

    /// <summary>Restores and activates the release seed model.</summary>
    Task<VoiceModelOperationResult> RestoreBuiltInAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken);
}

/// <summary>Serializes model activation with the active voice pipeline.</summary>
public interface IVoiceModelActivationGate
{
    /// <summary>Stops active provider sessions and holds the pipeline idle.</summary>
    Task<IAsyncDisposable> EnterIdleAsync(CancellationToken cancellationToken);
}

/// <summary>Stores installed voice model versions and active selections.</summary>
public interface IVoiceModelStore
{
    /// <summary>Registers an installed immutable model version.</summary>
    Task RegisterAsync(InstalledVoiceModel model, CancellationToken cancellationToken);

    /// <summary>Gets installed versions for a provider.</summary>
    Task<IReadOnlyList<InstalledVoiceModel>> GetInstalledAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken);

    /// <summary>Gets the active version for a provider.</summary>
    Task<InstalledVoiceModel?> GetActiveAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken);

    /// <summary>Atomically activates an installed version.</summary>
    Task ActivateAsync(
        VoiceModelProvider provider,
        string version,
        CancellationToken cancellationToken);
}
