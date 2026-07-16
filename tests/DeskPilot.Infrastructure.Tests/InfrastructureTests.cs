using DeskPilot.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeskPilot.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void DatabasePath_UsesConfiguredLocalApplicationDataDirectory()
    {
        var paths = new AppDataPaths("C:\\Users\\Test\\AppData\\Local");

        paths.DatabasePath.Should().Be("C:\\Users\\Test\\AppData\\Local\\DeskPilot\\data\\deskpilot.db");
    }

    [Fact]
    public async Task ApplyMigrationsAsync_CreatesOnlySettingsTables()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"deskpilot-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<DeskPilotDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            await using (var context = new DeskPilotDbContext(options))
            {
                await context.Database.MigrateAsync(CancellationToken.None);
                var tableNames = await context.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' ORDER BY name").ToListAsync(CancellationToken.None);

                tableNames.Should().Contain(["ApplicationSettings", "VoiceSettings"]);
                tableNames.Should().NotContain("Commands");
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}
