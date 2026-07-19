using System.Text;
using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions.Commands;

namespace DeskPilot.Application.Commands;

/// <summary>Exposes only validated command descriptions backed by registered handlers.</summary>
public interface ICommandCatalog
{
    /// <summary>Gets commands available to local intent resolvers.</summary>
    IReadOnlyCollection<AvailableCommand> Commands { get; }
}

/// <summary>Validates the static module command surface at composition time.</summary>
public sealed class CommandCatalog : ICommandCatalog
{
    private const string PlaceholderSentinel = "deskpilotpercentageplaceholder";

    /// <summary>Creates a validated command catalog.</summary>
    public CommandCatalog(
        IEnumerable<ICommandHandler> handlers,
        IEnumerable<ICommandDescriptionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(providers);

        var registered = handlers.Select(static handler => handler.CommandId).ToHashSet();
        var commands = providers.SelectMany(static provider => provider.GetCommands()).ToArray();
        foreach (var command in commands)
        {
            ValidateCommand(command, registered);
        }

        var duplicateId = commands
            .GroupBy(static command => command.CommandId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"Command '{duplicateId.Key}' has duplicate descriptions.");
        }

        var duplicatePattern = commands
            .SelectMany(static command => command.Phrases.Select(static phrase => NormalizePattern(phrase.Pattern)))
            .GroupBy(static pattern => pattern, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePattern is not null)
        {
            throw new InvalidOperationException(
                $"The duplicate command phrase '{duplicatePattern.Key}' is not allowed.");
        }

        Commands = commands;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AvailableCommand> Commands { get; }

    private static void ValidateCommand(
        AvailableCommand command,
        IReadOnlySet<CommandId> registered)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!registered.Contains(command.CommandId))
        {
            throw new InvalidOperationException(
                $"Command '{command.CommandId}' has no registered handler.");
        }

        if (string.IsNullOrWhiteSpace(command.DisplayName) || command.Phrases is null || command.Phrases.Count == 0)
        {
            throw new InvalidOperationException(
                $"Command '{command.CommandId}' has an invalid description.");
        }

        foreach (var phrase in command.Phrases)
        {
            ArgumentNullException.ThrowIfNull(phrase);
            _ = NormalizePattern(phrase.Pattern);
        }
    }

    private static string NormalizePattern(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var withoutApprovedPlaceholder = value.Replace(
            CommandPhrasePattern.PercentagePlaceholder,
            string.Empty,
            StringComparison.Ordinal);
        if (withoutApprovedPlaceholder.Contains('{') || withoutApprovedPlaceholder.Contains('}'))
        {
            throw new InvalidOperationException(
                $"Command phrase '{value}' contains an invalid placeholder.");
        }

        var placeholderCount = value.Split(
            CommandPhrasePattern.PercentagePlaceholder,
            StringSplitOptions.None).Length - 1;
        if (placeholderCount > 1)
        {
            throw new InvalidOperationException(
                $"Command phrase '{value}' contains an invalid placeholder.");
        }

        var source = value
            .Replace(CommandPhrasePattern.PercentagePlaceholder, PlaceholderSentinel, StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace('ё', 'е');
        var buffer = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            buffer.Append(char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ');
        }

        return string.Join(' ', buffer.ToString()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace(PlaceholderSentinel, CommandPhrasePattern.PercentagePlaceholder, StringComparison.Ordinal);
    }
}
