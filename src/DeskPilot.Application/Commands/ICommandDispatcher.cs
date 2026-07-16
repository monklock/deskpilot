using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Commands;

/// <summary>Dispatches commands from any DeskPilot control surface.</summary>
public interface ICommandDispatcher
{
    /// <summary>Dispatches a request to its registered command handler.</summary>
    Task<CommandExecutionResult> DispatchAsync(CommandRequest request, CancellationToken cancellationToken);
}
