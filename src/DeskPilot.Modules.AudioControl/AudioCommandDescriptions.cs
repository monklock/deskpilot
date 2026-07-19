using System.Collections.ObjectModel;
using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions.Commands;

namespace DeskPilot.Modules.AudioControl;

/// <summary>Publishes the finite Russian voice surface owned by AudioControl.</summary>
public sealed class AudioCommandDescriptionProvider : ICommandDescriptionProvider
{
    private static readonly IReadOnlyCollection<AvailableCommand> Commands =
    [
        new AvailableCommand(
            CommandId.From("audio.switch-preferred-device"),
            "Переключить устройство вывода",
            [
                Phrase("переключи на наушники", ("slot", "Headphones")),
                Phrase("включи наушники", ("slot", "Headphones")),
                Phrase("звук на наушники", ("slot", "Headphones")),
                Phrase("переключи на колонки", ("slot", "Speakers")),
                Phrase("включи колонки", ("slot", "Speakers")),
                Phrase("звук на колонки", ("slot", "Speakers")),
            ]),
        new AvailableCommand(
            CommandId.From("audio.toggle-preferred-device"),
            "Переключить сохранённое устройство",
            [
                Phrase("переключи устройство"),
                Phrase("переключи звук между устройствами"),
            ]),
        new AvailableCommand(
            CommandId.From("audio.change-volume"),
            "Изменить громкость",
            [
                Phrase("сделай громче", ("delta", "10")),
                Phrase("громче", ("delta", "10")),
                Phrase("увеличь громкость", ("delta", "10")),
                Phrase("сделай тише", ("delta", "-10")),
                Phrase("тише", ("delta", "-10")),
                Phrase("уменьши громкость", ("delta", "-10")),
            ]),
        new AvailableCommand(
            CommandId.From("audio.set-volume"),
            "Установить громкость",
            [
                Phrase("громкость {percentage}"),
                Phrase("установи громкость {percentage}"),
                Phrase("сделай громкость {percentage} процентов"),
            ]),
        new AvailableCommand(
            CommandId.From("audio.set-mute"),
            "Установить режим звука",
            [
                Phrase("выключи звук", ("muted", "true")),
                Phrase("убери звук", ("muted", "true")),
                Phrase("без звука", ("muted", "true")),
                Phrase("включи звук", ("muted", "false")),
                Phrase("верни звук", ("muted", "false")),
            ]),
        new AvailableCommand(
            CommandId.From("audio.toggle-mute"),
            "Переключить режим звука",
            [
                Phrase("переключи мьют"),
                Phrase("переключи беззвучный режим"),
            ]),
    ];

    /// <inheritdoc />
    public IReadOnlyCollection<AvailableCommand> GetCommands() => Commands;

    private static CommandPhrasePattern Phrase(
        string pattern,
        params (string Key, string Value)[] arguments)
    {
        IReadOnlyDictionary<string, string>? trustedArguments = arguments.Length == 0
            ? null
            : new ReadOnlyDictionary<string, string>(arguments.ToDictionary(
                static argument => argument.Key,
                static argument => argument.Value,
                StringComparer.Ordinal));
        return new CommandPhrasePattern(pattern, trustedArguments);
    }
}
