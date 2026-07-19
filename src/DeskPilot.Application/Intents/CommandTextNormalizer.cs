using System.Text;

namespace DeskPilot.Application.Intents;

/// <summary>Normalizes recognized Russian command text deterministically.</summary>
public interface ICommandTextNormalizer
{
    /// <summary>Returns one lower-case, punctuation-free, single-spaced string.</summary>
    string Normalize(string input);
}

/// <summary>Provides culture-independent normalization for local command matching.</summary>
public sealed class CommandTextNormalizer : ICommandTextNormalizer
{
    /// <inheritdoc />
    public string Normalize(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var source = input
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace('ё', 'е');
        var buffer = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            buffer.Append(char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)
                ? character
                : ' ');
        }

        return string.Join(
            ' ',
            buffer.ToString().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
    }
}
