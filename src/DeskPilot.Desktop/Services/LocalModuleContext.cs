using DeskPilot.Infrastructure.Data;
using DeskPilot.Modules.Abstractions;

namespace DeskPilot.Desktop.Services;

/// <summary>Provides typed local context to statically registered modules.</summary>
public sealed class LocalModuleContext(IAppDataPaths paths, TimeProvider timeProvider) : IModuleContext
{
    /// <inheritdoc />
    public string ApplicationDataPath => paths.RootPath;

    /// <inheritdoc />
    public TimeProvider TimeProvider { get; } = timeProvider;
}
