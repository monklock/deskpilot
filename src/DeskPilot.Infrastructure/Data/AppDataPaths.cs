namespace DeskPilot.Infrastructure.Data;

/// <summary>Provides local runtime paths for DeskPilot.</summary>
public interface IAppDataPaths
{
    /// <summary>Gets the root directory under LocalApplicationData.</summary>
    string RootPath { get; }

    /// <summary>Gets the SQLite database file path.</summary>
    string DatabasePath { get; }

    /// <summary>Gets the writable root for immutable voice model versions.</summary>
    string ModelsRootPath { get; }

    /// <summary>Gets the temporary model download directory.</summary>
    string ModelDownloadsPath { get; }

    /// <summary>Gets the read-only voice model seed directory shipped with the application.</summary>
    string SeedModelsPath { get; }
}

/// <summary>Builds DeskPilot runtime paths from the current user's local data directory.</summary>
public sealed class AppDataPaths : IAppDataPaths
{
    /// <summary>Creates paths from a LocalApplicationData directory.</summary>
    public AppDataPaths(string localApplicationDataPath, string? applicationBasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);
        RootPath = Path.Combine(localApplicationDataPath, "DeskPilot");
        DatabasePath = Path.Combine(RootPath, "data", "deskpilot.db");
        ModelsRootPath = Path.Combine(RootPath, "models");
        ModelDownloadsPath = Path.Combine(RootPath, "tmp", "model-downloads");
        SeedModelsPath = Path.Combine(applicationBasePath ?? AppContext.BaseDirectory, "assets", "voice-models");
    }

    /// <inheritdoc />
    public string RootPath { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public string ModelsRootPath { get; }

    /// <inheritdoc />
    public string ModelDownloadsPath { get; }

    /// <inheritdoc />
    public string SeedModelsPath { get; }
}
