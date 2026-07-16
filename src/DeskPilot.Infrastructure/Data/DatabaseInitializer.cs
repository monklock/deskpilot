using Microsoft.EntityFrameworkCore;

namespace DeskPilot.Infrastructure.Data;

/// <summary>Initializes the local DeskPilot database.</summary>
public interface IDatabaseInitializer
{
    /// <summary>Applies pending local database migrations.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>Applies migrations with short-lived DbContext instances.</summary>
public sealed class DatabaseInitializer(IDbContextFactory<DeskPilotDbContext> contextFactory, IAppDataPaths paths) : IDatabaseInitializer
{
    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
