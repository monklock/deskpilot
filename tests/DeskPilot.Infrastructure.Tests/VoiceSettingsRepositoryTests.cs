using DeskPilot.Infrastructure.Preferences;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Infrastructure.Tests;

public sealed class VoiceSettingsRepositoryTests
{
    [Fact]
    public async Task GetAsync_EmptyDatabase_ReturnsApprovedDefaults()
    {
        await using var database = await VoiceModelManagementTests.TestDatabase.CreateAsync();
        var repository = new SqliteVoiceSettingsRepository(database.Factory);

        var settings = await repository.GetAsync(CancellationToken.None);

        settings.Should().Be(VoiceSettings.Default);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsBluetoothEndpointAndSensitivity()
    {
        await using var database = await VoiceModelManagementTests.TestDatabase.CreateAsync();
        var repository = new SqliteVoiceSettingsRepository(database.Factory);
        var expected = VoiceSettings.Default with
        {
            IsEnabled = true,
            MicrophoneEndpointId = "bluetooth-input-id",
            MicrophoneFriendlyName = "Bluetooth microphone",
            WakeConfidence = 0.87,
        };

        await repository.SaveAsync(expected, CancellationToken.None);

        (await repository.GetAsync(CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task SaveAsync_WakeConfidenceOutsideApprovedRange_IsRejected()
    {
        await using var database = await VoiceModelManagementTests.TestDatabase.CreateAsync();
        var repository = new SqliteVoiceSettingsRepository(database.Factory);

        var action = () => repository.SaveAsync(
            VoiceSettings.Default with { WakeConfidence = 0.91 },
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
