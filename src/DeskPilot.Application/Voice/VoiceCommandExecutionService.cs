using DeskPilot.Application.Commands;
using DeskPilot.Application.Intents;
using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Voice;

/// <summary>Reports safe metadata immediately before one trusted request is dispatched.</summary>
public sealed record VoiceCommandExecutionProgress(
    CommandId CommandId,
    IntentResolutionStatus IntentStatus,
    double Confidence);

/// <summary>Contains local intent resolution and its optional command outcome.</summary>
public sealed record VoiceCommandExecutionResult(
    IntentResolutionResult Resolution,
    CommandExecutionResult? Execution);

/// <summary>Resolves recognized text and dispatches only a trusted resolved request.</summary>
public interface IVoiceCommandExecutionService
{
    /// <summary>Resolves and optionally dispatches one recognized phrase.</summary>
    Task<VoiceCommandExecutionResult> ExecuteAsync(
        string recognizedText,
        Action<VoiceCommandExecutionProgress> beforeDispatch,
        CancellationToken cancellationToken);
}

/// <summary>Owns the safe local resolve-to-dispatch boundary.</summary>
public sealed class VoiceCommandExecutionService : IVoiceCommandExecutionService
{
    private readonly IIntentResolver _resolver;
    private readonly ICommandCatalog _catalog;
    private readonly ICommandDispatcher _dispatcher;

    /// <summary>Creates the local command execution service.</summary>
    public VoiceCommandExecutionService(
        IIntentResolver resolver,
        ICommandCatalog catalog,
        ICommandDispatcher dispatcher)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public async Task<VoiceCommandExecutionResult> ExecuteAsync(
        string recognizedText,
        Action<VoiceCommandExecutionProgress> beforeDispatch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognizedText);
        ArgumentNullException.ThrowIfNull(beforeDispatch);
        var resolution = await _resolver.ResolveAsync(
            recognizedText,
            _catalog.Commands,
            cancellationToken).ConfigureAwait(false);
        if (resolution.Status != IntentResolutionStatus.Resolved
            || resolution.Request is null)
        {
            return new VoiceCommandExecutionResult(resolution, null);
        }

        beforeDispatch(new VoiceCommandExecutionProgress(
            resolution.Request.CommandId,
            resolution.Status,
            resolution.Confidence));
        var execution = await _dispatcher.DispatchAsync(
            resolution.Request,
            cancellationToken).ConfigureAwait(false);
        return new VoiceCommandExecutionResult(resolution, execution);
    }
}
