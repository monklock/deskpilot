using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Intents;

/// <summary>Resolves recognized text to exactly one registered command.</summary>
public interface IIntentResolver
{
    /// <summary>Resolves input against commands exposed by registered modules.</summary>
    Task<IntentResolutionResult> ResolveAsync(string input, IReadOnlyCollection<AvailableCommand> commands, CancellationToken cancellationToken);
}
