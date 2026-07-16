using DeskPilot.Core.Commands;
using DeskPilot.Modules.Abstractions.Commands;

namespace DeskPilot.Application.Commands;

/// <summary>Dispatches a request only to a statically registered command handler.</summary>
public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IReadOnlyDictionary<CommandId, ICommandHandler> _handlers;

    /// <summary>Creates a dispatcher from registered handlers.</summary>
    public CommandDispatcher(IEnumerable<ICommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToDictionary(static handler => handler.CommandId);
    }

    /// <inheritdoc />
    public Task<CommandExecutionResult> DispatchAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return _handlers.TryGetValue(request.CommandId, out var handler)
            ? handler.HandleAsync(request, cancellationToken)
            : Task.FromResult(CommandExecutionResult.NotFound(request.CommandId));
    }
}
