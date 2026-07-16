using DeskPilot.Application.Commands;
using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions.Commands;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class CommandDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_InvokesHandlerForRegisteredCommand()
    {
        var commandId = CommandId.From("audio.set-volume");
        var handler = new RecordingCommandHandler(commandId);
        var dispatcher = new CommandDispatcher([handler]);

        var result = await dispatcher.DispatchAsync(new CommandRequest(commandId), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Succeeded);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_ReturnsNotFoundForUnknownCommand()
    {
        var dispatcher = new CommandDispatcher([]);

        var result = await dispatcher.DispatchAsync(new CommandRequest(CommandId.From("unknown")), CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.NotFound);
    }

    [Fact]
    public async Task DispatchAsync_PropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var dispatcher = new CommandDispatcher([]);

        var action = () => dispatcher.DispatchAsync(new CommandRequest(CommandId.From("unknown")), source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class RecordingCommandHandler(CommandId commandId) : ICommandHandler
    {
        public int CallCount { get; private set; }

        public CommandId CommandId { get; } = commandId;

        public Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(CommandExecutionResult.Succeeded(request.CommandId));
        }
    }
}
