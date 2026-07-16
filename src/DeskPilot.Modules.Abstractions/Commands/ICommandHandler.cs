using DeskPilot.Core.Commands;

namespace DeskPilot.Modules.Abstractions.Commands;

/// <summary>Executes one registered DeskPilot command.</summary>
public interface ICommandHandler
{
    /// <summary>Gets the command handled by this instance.</summary>
    CommandId CommandId { get; }

    /// <summary>Executes a command request.</summary>
    Task<CommandExecutionResult> HandleAsync(CommandRequest request, CancellationToken cancellationToken);
}
