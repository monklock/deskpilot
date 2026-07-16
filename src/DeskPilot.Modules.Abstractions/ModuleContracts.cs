using DeskPilot.Core.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace DeskPilot.Modules.Abstractions;

/// <summary>Describes a statically registered DeskPilot module.</summary>
public sealed record ModuleMetadata(
    string ModuleId,
    string Name,
    Version Version,
    string Description,
    IReadOnlyCollection<CommandId> SupportedCommands,
    bool IsEnabled = true);

/// <summary>Provides typed context for module initialization.</summary>
public interface IModuleContext
{
    /// <summary>Gets the local application data root.</summary>
    string ApplicationDataPath { get; }

    /// <summary>Gets the testable time source.</summary>
    TimeProvider TimeProvider { get; }
}

/// <summary>Defines a DeskPilot module.</summary>
public interface IDeskPilotModule
{
    /// <summary>Gets immutable module metadata.</summary>
    ModuleMetadata Metadata { get; }

    /// <summary>Registers module services in the composition root.</summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>Initializes the module after the host starts.</summary>
    Task InitializeAsync(IModuleContext context, CancellationToken cancellationToken);
}

/// <summary>Initializes statically registered modules.</summary>
public sealed class ModuleCatalog(IEnumerable<IDeskPilotModule> modules)
{
    private readonly IReadOnlyList<IDeskPilotModule> _modules = modules.ToArray();

    /// <summary>Registers all enabled module services.</summary>
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var module in _modules.Where(static module => module.Metadata.IsEnabled))
        {
            module.RegisterServices(services);
        }
    }

    /// <summary>Initializes all enabled modules in static registration order.</summary>
    public async Task InitializeAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var module in _modules.Where(static module => module.Metadata.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await module.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }
}
