using DeskPilot.Application.Commands;
using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions.Commands;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class CommandCatalogTests
{
    [Fact]
    public void Constructor_RejectsDescriptionWithoutRegisteredHandler()
    {
        var description = Description("audio.set-volume", "громкость {percentage}");

        var action = () => new CommandCatalog([], [Provider(description)]);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*audio.set-volume*registered handler*");
    }

    [Fact]
    public void Constructor_RejectsDuplicateNormalizedPattern()
    {
        var first = Description("audio.first", "Сделай громче");
        var second = Description("audio.second", "  сделай   громче  ");

        var action = () => new CommandCatalog(
            [Handler("audio.first"), Handler("audio.second")],
            [Provider(first, second)]);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate command phrase*");
    }

    [Fact]
    public void Constructor_RejectsUnknownPlaceholder()
    {
        var description = Description("audio.set-volume", "громкость {value}");

        var action = () => new CommandCatalog(
            [Handler("audio.set-volume")],
            [Provider(description)]);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid placeholder*");
    }

    [Fact]
    public void Constructor_ExposesOnlyValidatedRegisteredDescriptions()
    {
        var description = Description("audio.toggle-mute", "переключи мьют");

        var catalog = new CommandCatalog(
            [Handler("audio.toggle-mute")],
            [Provider(description)]);

        catalog.Commands.Should().ContainSingle().Which.Should().Be(description);
    }

    private static AvailableCommand Description(string commandId, string phrase) => new(
        CommandId.From(commandId),
        commandId,
        [new CommandPhrasePattern(phrase)]);

    private static ICommandHandler Handler(string commandId) =>
        new RecordingCommandHandler(CommandId.From(commandId));

    private static ICommandDescriptionProvider Provider(params AvailableCommand[] commands) =>
        new StaticDescriptionProvider(commands);

    private sealed class StaticDescriptionProvider(
        IReadOnlyCollection<AvailableCommand> commands) : ICommandDescriptionProvider
    {
        public IReadOnlyCollection<AvailableCommand> GetCommands() => commands;
    }

    private sealed class RecordingCommandHandler(CommandId commandId) : ICommandHandler
    {
        public CommandId CommandId { get; } = commandId;

        public Task<CommandExecutionResult> HandleAsync(
            CommandRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(CommandExecutionResult.Succeeded(request.CommandId));
    }
}
