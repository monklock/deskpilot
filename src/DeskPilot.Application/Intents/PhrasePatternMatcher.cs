using System.Globalization;
using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Intents;

/// <summary>Contains one trusted request matched from a finite phrase pattern.</summary>
public sealed record PhraseMatch(
    CommandRequest Request,
    string ComparableInput,
    string ComparablePattern);

/// <summary>Matches literal phrases and the approved percentage placeholder.</summary>
public sealed class PhrasePatternMatcher
{
    private static readonly IReadOnlySet<string> PercentageWords =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "процент",
            "процента",
            "процентов",
        };

    private readonly ICommandTextNormalizer _normalizer;
    private readonly RussianVolumeNumberParser _numbers;

    /// <summary>Creates a deterministic phrase matcher.</summary>
    public PhrasePatternMatcher(
        ICommandTextNormalizer normalizer,
        RussianVolumeNumberParser numbers)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _numbers = numbers ?? throw new ArgumentNullException(nameof(numbers));
    }

    /// <summary>Matches one already-normalized input exactly.</summary>
    public PhraseMatch? MatchExact(
        string normalizedInput,
        AvailableCommand command,
        CommandPhrasePattern phrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedInput);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(phrase);

        var placeholder = CommandPhrasePattern.PercentagePlaceholder;
        var placeholderIndex = phrase.Pattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            var normalizedPattern = _normalizer.Normalize(phrase.Pattern);
            return string.Equals(normalizedInput, normalizedPattern, StringComparison.Ordinal)
                ? new PhraseMatch(
                    CreateRequest(command.CommandId, phrase.Arguments, null),
                    normalizedInput,
                    normalizedPattern)
                : null;
        }

        if (phrase.Pattern.IndexOf(
                placeholder,
                placeholderIndex + placeholder.Length,
                StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var prefix = NormalizeLiteral(phrase.Pattern[..placeholderIndex]);
        var suffix = NormalizeLiteral(phrase.Pattern[(placeholderIndex + placeholder.Length)..]);
        var inputTokens = CanonicalizePercentageWords(Split(normalizedInput));
        var prefixTokens = CanonicalizePercentageWords(Split(prefix));
        var suffixTokens = CanonicalizePercentageWords(Split(suffix));

        if (suffixTokens.Length == 0
            && inputTokens.Length > 0
            && string.Equals(inputTokens[^1], "процентов", StringComparison.Ordinal))
        {
            inputTokens = inputTokens[..^1];
        }

        if (inputTokens.Length <= prefixTokens.Length + suffixTokens.Length
            || !StartsWith(inputTokens, prefixTokens)
            || !EndsWith(inputTokens, suffixTokens))
        {
            return null;
        }

        var numericTokens = inputTokens[
            prefixTokens.Length..(inputTokens.Length - suffixTokens.Length)];
        if (!_numbers.TryParse(string.Join(' ', numericTokens), out var percentage))
        {
            return null;
        }

        var request = CreateRequest(command.CommandId, phrase.Arguments, percentage);
        var comparable = string.Join(' ', [.. prefixTokens, .. suffixTokens]);
        return new PhraseMatch(request, comparable, comparable);
    }

    /// <summary>Creates safe fuzzy candidates without guessing a numeric value.</summary>
    internal IReadOnlyCollection<PhraseMatch> CreateFuzzyCandidates(
        string normalizedInput,
        AvailableCommand command,
        CommandPhrasePattern phrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedInput);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(phrase);

        var placeholder = CommandPhrasePattern.PercentagePlaceholder;
        var placeholderIndex = phrase.Pattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            return
            [
                new PhraseMatch(
                    CreateRequest(command.CommandId, phrase.Arguments, null),
                    normalizedInput,
                    _normalizer.Normalize(phrase.Pattern)),
            ];
        }

        var prefix = NormalizeLiteral(phrase.Pattern[..placeholderIndex]);
        var suffix = NormalizeLiteral(phrase.Pattern[(placeholderIndex + placeholder.Length)..]);
        var inputTokens = CanonicalizePercentageWords(Split(normalizedInput));
        var patternTokens = CanonicalizePercentageWords([.. Split(prefix), .. Split(suffix)]);
        if (Split(suffix).Length == 0
            && inputTokens.Length > 0
            && string.Equals(inputTokens[^1], "процентов", StringComparison.Ordinal))
        {
            inputTokens = inputTokens[..^1];
        }

        var matches = new List<PhraseMatch>();
        for (var start = 0; start < inputTokens.Length; start++)
        {
            for (var length = 1; length <= 2 && start + length <= inputTokens.Length; length++)
            {
                var numericText = string.Join(' ', inputTokens[start..(start + length)]);
                if (!_numbers.TryParse(numericText, out var percentage))
                {
                    continue;
                }

                var remaining = inputTokens[..start]
                    .Concat(inputTokens[(start + length)..])
                    .ToArray();
                if (ContainsNumber(remaining))
                {
                    continue;
                }

                matches.Add(new PhraseMatch(
                    CreateRequest(command.CommandId, phrase.Arguments, percentage),
                    string.Join(' ', remaining),
                    string.Join(' ', patternTokens)));
            }
        }

        return matches;
    }

    private CommandRequest CreateRequest(
        CommandId commandId,
        IReadOnlyDictionary<string, string>? staticArguments,
        int? percentage)
    {
        var arguments = staticArguments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(staticArguments, StringComparer.Ordinal);
        if (percentage is not null
            && !arguments.TryAdd(
                "percentage",
                percentage.Value.ToString(CultureInfo.InvariantCulture)))
        {
            throw new InvalidOperationException(
                "A percentage phrase cannot override a static percentage argument.");
        }

        return new CommandRequest(commandId, arguments.Count == 0 ? null : arguments);
    }

    private string NormalizeLiteral(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : _normalizer.Normalize(value);

    private static string[] Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string[] CanonicalizePercentageWords(string[] tokens) =>
        tokens.Select(static token => PercentageWords.Contains(token) ? "процентов" : token).ToArray();

    private static bool StartsWith(string[] source, string[] prefix) =>
        prefix.Length == 0 || source.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool EndsWith(string[] source, string[] suffix) =>
        suffix.Length == 0
        || source.AsSpan(source.Length - suffix.Length, suffix.Length).SequenceEqual(suffix);

    private bool ContainsNumber(string[] tokens)
    {
        for (var start = 0; start < tokens.Length; start++)
        {
            for (var length = 1; length <= 2 && start + length <= tokens.Length; length++)
            {
                if (_numbers.TryParse(string.Join(' ', tokens[start..(start + length)]), out _))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
