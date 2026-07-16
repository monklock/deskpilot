namespace DeskPilot.Infrastructure.Data;

/// <summary>Provides local runtime paths for DeskPilot.</summary>
public interface IAppDataPaths
{
    /// <summary>Gets the root directory under LocalApplicationData.</summary>
    string RootPath { get; }

    /// <summary>Gets the SQLite database file path.</summary>
    string DatabasePath { get; }
}

/// <summary>Builds DeskPilot runtime paths from the current user's local data directory.</summary>
public sealed class AppDataPaths : IAppDataPaths
{
    /// <summary>Creates paths from a LocalApplicationData directory.</summary>
    public AppDataPaths(string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);
        RootPath = Path.Combine(localApplicationDataPath, "DeskPilot");
        DatabasePath = Path.Combine(RootPath, "data", "deskpilot.db");
    }

    /// <inheritdoc />
    public string RootPath { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }
}
