using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Intents;

/// <summary>Resolves only complete normalized phrase-pattern matches.</summary>
public sealed class ExactPhraseIntentResolver : IIntentResolver
{
    private readonly ICommandTextNormalizer _normalizer;
    private readonly PhrasePatternMatcher _matcher;

    /// <summary>Creates an exact local intent resolver.</summary>
    public ExactPhraseIntentResolver(
        ICommandTextNormalizer normalizer,
        PhrasePatternMatcher matcher)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
    }

    /// <inheritdoc />
    public Task<IntentResolutionResult> ResolveAsync(
        string input,
        IReadOnlyCollection<AvailableCommand> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IntentResolutionResult>(cancellationToken);
        }

        var normalizedInput = _normalizer.Normalize(input);
        var candidates = commands
            .SelectMany(command => command.Phrases
                .Select(phrase => _matcher.MatchExact(normalizedInput, command, phrase)))
            .Where(static match => match is not null)
            .Select(static match => match!)
            .GroupBy(static match => CreateIdentity(match.Request), StringComparer.Ordinal)
            .Select(static group => group.First().Request)
            .ToArray();

        var result = candidates.Length switch
        {
            0 => IntentResolutionResult.NotFound,
            1 => new IntentResolutionResult(IntentResolutionStatus.Resolved, candidates[0], 1),
            _ => new IntentResolutionResult(IntentResolutionStatus.Ambiguous, null, 1),
        };
        return Task.FromResult(result);
    }

    private static string CreateIdentity(CommandRequest request)
    {
        var arguments = request.Arguments is null
            ? string.Empty
            : string.Join(
                '\u001f',
                request.Arguments
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => $"{item.Key}\u001e{item.Value}"));
        return $"{request.CommandId.Value}\u001d{arguments}";
    }
}
