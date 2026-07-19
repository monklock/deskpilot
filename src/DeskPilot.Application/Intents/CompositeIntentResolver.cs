using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Intents;

/// <summary>Runs exact resolution before bounded fuzzy fallback.</summary>
public sealed class CompositeIntentResolver(
    IIntentResolver exact,
    IIntentResolver fuzzy) : IIntentResolver
{
    private readonly IIntentResolver _exact = exact ?? throw new ArgumentNullException(nameof(exact));
    private readonly IIntentResolver _fuzzy = fuzzy ?? throw new ArgumentNullException(nameof(fuzzy));

    /// <inheritdoc />
    public async Task<IntentResolutionResult> ResolveAsync(
        string input,
        IReadOnlyCollection<AvailableCommand> commands,
        CancellationToken cancellationToken)
    {
        var exactResult = await _exact.ResolveAsync(input, commands, cancellationToken)
            .ConfigureAwait(false);
        return exactResult.Status == IntentResolutionStatus.NotFound
            ? await _fuzzy.ResolveAsync(input, commands, cancellationToken).ConfigureAwait(false)
            : exactResult;
    }
}
