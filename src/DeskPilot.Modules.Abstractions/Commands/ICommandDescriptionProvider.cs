using DeskPilot.Core.Commands;

namespace DeskPilot.Modules.Abstractions.Commands;

/// <summary>Publishes finite command descriptions owned by one registered module.</summary>
public interface ICommandDescriptionProvider
{
    /// <summary>Gets the module-owned commands available to local intent resolvers.</summary>
    IReadOnlyCollection<AvailableCommand> GetCommands();
}
