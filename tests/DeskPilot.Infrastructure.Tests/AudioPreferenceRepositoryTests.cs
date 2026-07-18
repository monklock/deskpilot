using DeskPilot.Infrastructure.Preferences;
using DeskPilot.Infrastructure.Data;
using DeskPilot.Modules.AudioControl;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeskPilot.Infrastructure.Tests;

public sealed class AudioPreferenceRepositoryTests
{
    [Fact]
    public async Task SaveAsync_ReplacesThePreferenceForTheSameSlot()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"deskpilot-audio-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<DeskPilotDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            await using (var context = new DeskPilotDbContext(options))
            {
                await context.Database.MigrateAsync(CancellationToken.None);
            }

            var service = new SqliteAudioPreferredDeviceService(new TestDbContextFactory(options));
            await service.SaveAsync(AudioDeviceSlot.Speakers, new AudioOutputDevice("endpoint-a", "Old speakers", true, false), CancellationToken.None);
            await service.SaveAsync(AudioDeviceSlot.Speakers, new AudioOutputDevice("endpoint-b", "New speakers", true, false), CancellationToken.None);

            var preference = await service.GetAsync(AudioDeviceSlot.Speakers, CancellationToken.None);
            preference.Should().BeEquivalentTo(new AudioOutputDevice("endpoint-b", "New speakers", false, false));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<DeskPilotDbContext> options) : IDbContextFactory<DeskPilotDbContext>
    {
        public DeskPilotDbContext CreateDbContext() => new(options);

        public Task<DeskPilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
