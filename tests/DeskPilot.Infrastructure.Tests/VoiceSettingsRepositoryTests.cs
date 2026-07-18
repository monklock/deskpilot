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
            WakeConfidence = 0.82,
            VoiceActivitySensitivity = 0.87,
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

    [Fact]
    public async Task SaveAsync_VoiceActivitySensitivityOutsideApprovedRange_IsRejected()
    {
        await using var database = await VoiceModelManagementTests.TestDatabase.CreateAsync();
        var repository = new SqliteVoiceSettingsRepository(database.Factory);

        var action = () => repository.SaveAsync(
            VoiceSettings.Default with { VoiceActivitySensitivity = 0.64 },
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsync_NonFiniteSensitivity_IsRejected(bool wakeSensitivity)
    {
        await using var database = await VoiceModelManagementTests.TestDatabase.CreateAsync();
        var repository = new SqliteVoiceSettingsRepository(database.Factory);
        var settings = wakeSensitivity
            ? VoiceSettings.Default with { WakeConfidence = double.NaN }
            : VoiceSettings.Default with { VoiceActivitySensitivity = double.NaN };

        var action = () => repository.SaveAsync(settings, CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
