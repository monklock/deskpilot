using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Desktop.Services;

/// <summary>Reports missing release seed assets without accepting unverified model data.</summary>
internal sealed class UnavailableVoiceModelManager : IVoiceModelManager
{
    /// <inheritdoc />
    public Task<VoiceModelState> GetStateAsync(CancellationToken cancellationToken) =>
        Task.FromException<VoiceModelState>(Unavailable());

    /// <inheritdoc />
    public Task<VoiceModelState> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        Task.FromException<VoiceModelState>(Unavailable());

    /// <inheritdoc />
    public Task<VoiceModelOperationResult> InstallAsync(
        VoiceModelDescriptor model,
        IProgress<VoiceModelProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(new VoiceModelOperationResult(
            VoiceModelResultCode.InvalidModel,
            "Release-комплект встроенных моделей недоступен."));

    /// <inheritdoc />
    public Task<VoiceModelOperationResult> ActivateAsync(
        VoiceModelProvider provider,
        string version,
        CancellationToken cancellationToken) => UnavailableResult();

    /// <inheritdoc />
    public Task<VoiceModelOperationResult> RestoreBuiltInAsync(
        VoiceModelProvider provider,
        CancellationToken cancellationToken) => UnavailableResult();

    private static Task<VoiceModelOperationResult> UnavailableResult() =>
        Task.FromResult(new VoiceModelOperationResult(
            VoiceModelResultCode.InvalidModel,
            "Release-комплект встроенных моделей недоступен."));

    private static InvalidOperationException Unavailable() =>
        new("Release voice model assets are unavailable.");
}
