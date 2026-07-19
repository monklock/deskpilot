using DeskPilot.Application.Commands;
using DeskPilot.Application.Intents;
using DeskPilot.Application.Voice;
using DeskPilot.Core.Commands;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class VoiceCommandExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ResolvedCommand_ReportsProgressThenDispatchesExactRequest()
    {
        var request = new CommandRequest(
            CommandId.From("audio.change-volume"),
            new Dictionary<string, string> { ["delta"] = "10" });
        var resolver = new StubResolver(
            new IntentResolutionResult(IntentResolutionStatus.Resolved, request, 0.91));
        var dispatcher = new RecordingDispatcher();
        var progress = new List<VoiceCommandExecutionProgress>();
        var service = new VoiceCommandExecutionService(
            resolver,
            new StubCatalog(),
            dispatcher);

        var result = await service.ExecuteAsync(
            "сделай громче",
            progress.Add,
            CancellationToken.None);

        progress.Should().ContainSingle().Which.Should().Be(
            new VoiceCommandExecutionProgress(
                request.CommandId,
                IntentResolutionStatus.Resolved,
                0.91));
        dispatcher.Requests.Should().ContainSingle().Which.Should().BeSameAs(request);
        result.Execution!.Status.Should().Be(CommandExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData(IntentResolutionStatus.NotFound)]
    [InlineData(IntentResolutionStatus.Ambiguous)]
    public async Task ExecuteAsync_UnresolvedText_NeverDispatches(IntentResolutionStatus status)
    {
        var dispatcher = new RecordingDispatcher();
        var service = new VoiceCommandExecutionService(
            new StubResolver(new IntentResolutionResult(status, null, 0)),
            new StubCatalog(),
            dispatcher);

        var result = await service.ExecuteAsync(
            "неизвестная команда",
            _ => throw new InvalidOperationException("Progress must not run."),
            CancellationToken.None);

        result.Execution.Should().BeNull();
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationWithoutDispatch()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var dispatcher = new RecordingDispatcher();
        var service = new VoiceCommandExecutionService(
            new StubResolver(IntentResolutionResult.NotFound),
            new StubCatalog(),
            dispatcher);

        var action = () => service.ExecuteAsync(
            "выключи звук",
            _ => { },
            source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        dispatcher.Requests.Should().BeEmpty();
    }

    private sealed class StubCatalog : ICommandCatalog
    {
        public IReadOnlyCollection<AvailableCommand> Commands { get; } =
        [
            new AvailableCommand(
                CommandId.From("audio.change-volume"),
                "Изменить громкость",
                [new CommandPhrasePattern("сделай громче")]),
        ];
    }

    private sealed class StubResolver(IntentResolutionResult result) : IIntentResolver
    {
        public Task<IntentResolutionResult> ResolveAsync(
            string input,
            IReadOnlyCollection<AvailableCommand> commands,
            CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<IntentResolutionResult>(cancellationToken)
                : Task.FromResult(result);
    }

    private sealed class RecordingDispatcher : ICommandDispatcher
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandExecutionResult> DispatchAsync(
            CommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(CommandExecutionResult.Succeeded(request.CommandId));
        }
    }
}
