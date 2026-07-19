using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Intents;

/// <summary>Resolves bounded spelling variation after exact matching has failed.</summary>
public sealed class FuzzyPhraseIntentResolver : IIntentResolver
{
    private readonly ICommandTextNormalizer _normalizer;
    private readonly PhrasePatternMatcher _matcher;
    private readonly double _threshold;
    private readonly double _ambiguityMargin;

    /// <summary>Creates a bounded fuzzy resolver.</summary>
    public FuzzyPhraseIntentResolver(
        ICommandTextNormalizer normalizer,
        PhrasePatternMatcher matcher,
        double threshold = 0.86,
        double ambiguityMargin = 0.08)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        if (threshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        if (ambiguityMargin is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ambiguityMargin));
        }

        _threshold = threshold;
        _ambiguityMargin = ambiguityMargin;
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
            .SelectMany(command => command.Phrases.SelectMany(
                phrase => _matcher.CreateFuzzyCandidates(normalizedInput, command, phrase)))
            .Select(static match => new ScoredCandidate(
                match.Request,
                Similarity(match.ComparableInput, match.ComparablePattern)))
            .GroupBy(static candidate => CommandRequestIdentity.Create(candidate.Request), StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static candidate => candidate.Score).First())
            .OrderByDescending(static candidate => candidate.Score)
            .ToArray();

        if (candidates.Length == 0 || candidates[0].Score < _threshold)
        {
            return Task.FromResult(IntentResolutionResult.NotFound);
        }

        if (candidates.Length > 1
            && candidates[0].Score - candidates[1].Score < _ambiguityMargin)
        {
            return Task.FromResult(new IntentResolutionResult(
                IntentResolutionStatus.Ambiguous,
                null,
                candidates[0].Score));
        }

        return Task.FromResult(new IntentResolutionResult(
            IntentResolutionStatus.Resolved,
            candidates[0].Request,
            candidates[0].Score));
    }

    internal static double Similarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var matrix = new int[left.Length + 1, right.Length + 1];
        for (var row = 0; row <= left.Length; row++)
        {
            matrix[row, 0] = row;
        }

        for (var column = 0; column <= right.Length; column++)
        {
            matrix[0, column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                matrix[row, column] = Math.Min(
                    Math.Min(matrix[row - 1, column] + 1, matrix[row, column - 1] + 1),
                    matrix[row - 1, column - 1] + cost);
                if (row > 1
                    && column > 1
                    && left[row - 1] == right[column - 2]
                    && left[row - 2] == right[column - 1])
                {
                    matrix[row, column] = Math.Min(
                        matrix[row, column],
                        matrix[row - 2, column - 2] + cost);
                }
            }
        }

        return 1d - (double)matrix[left.Length, right.Length]
            / Math.Max(left.Length, right.Length);
    }

    private sealed record ScoredCandidate(CommandRequest Request, double Score);
}
