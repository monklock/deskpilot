using DeskPilot.Application.Voice;
using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;
using DeskPilot.Desktop.ViewModels;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeskPilot.Desktop.Tests;

public sealed class VoiceControlViewModelTests
{
    [Fact]
    public async Task RefreshMicrophonesAsync_KeepsDisconnectedSavedBluetoothMicrophoneSelected()
    {
        var fixture = VoiceViewModelFixture.Create(
            VoiceSettings.Default with
            {
                MicrophoneEndpointId = "bt-mic",
                MicrophoneFriendlyName = "Bluetooth microphone",
            },
            []);

        await fixture.ViewModel.RefreshMicrophonesAsync();

        fixture.ViewModel.SelectedMicrophone!.EndpointId.Should().Be("bt-mic");
        fixture.ViewModel.SelectedMicrophone.IsAvailable.Should().BeFalse();
        fixture.ViewModel.StatusMessage.Should().Contain("Bluetooth");
    }

    [Fact]
    public async Task ToggleVoiceCommand_EnablesPipelineAndPersistsSetting()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, [Microphone("usb-mic", "USB microphone")]);
        await fixture.ViewModel.InitializeAsync();

        await fixture.ViewModel.ToggleVoiceCommand.ExecuteAsync(null);

        await fixture.Controller.Received(1).EnableAsync(Arg.Any<CancellationToken>());
        await fixture.Settings.Received(1).SaveAsync(
            Arg.Is<VoiceSettings>(settings => settings.IsEnabled),
            Arg.Any<CancellationToken>());
        fixture.ViewModel.IsVoiceEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UseSelectedMicrophoneCommand_RestartsEnabledPipelineWithExactEndpoint()
    {
        var saved = VoiceSettings.Default with { IsEnabled = true };
        var microphone = Microphone("bt-mic", "Bluetooth microphone");
        var fixture = VoiceViewModelFixture.Create(saved, [microphone]);
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMicrophone = microphone;

        await fixture.ViewModel.UseSelectedMicrophoneCommand.ExecuteAsync(null);

        await fixture.Settings.Received().SaveAsync(
            Arg.Is<VoiceSettings>(settings =>
                settings.MicrophoneEndpointId == "bt-mic"
                && settings.MicrophoneFriendlyName == "Bluetooth microphone"),
            Arg.Any<CancellationToken>());
        await fixture.Controller.Received(1).RestartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveSensitivityCommand_PersistsIndependentVadSensitivity()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.Sensitivity = 0.89;

        await fixture.ViewModel.SaveSensitivityCommand.ExecuteAsync(null);

        await fixture.Settings.Received().SaveAsync(
            Arg.Is<VoiceSettings>(settings =>
                settings.VoiceActivitySensitivity == 0.89
                && settings.WakeConfidence == VoiceSettings.Default.WakeConfidence),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateChange_UpdatesRecognizedTextModelsAndSafeRecovery()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();
        var snapshot = new VoicePipelineSnapshot(
            VoiceAssistantState.Error,
            "bt-mic",
            "альфа",
            0.93,
            "сделай громче",
            0.91,
            "microphone-unavailable",
            "Подключите Bluetooth-микрофон.")
        {
            ActiveWakeModelVersion = "0.22",
            ActiveCommandModelVersion = "openai-base",
        };

        fixture.State.SnapshotChanged += Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

        fixture.ViewModel.CurrentState.Should().Be(VoiceAssistantState.Error);
        fixture.ViewModel.LastRecognizedText.Should().Be("сделай громче");
        fixture.ViewModel.ActiveWakeModelVersion.Should().Be("0.22");
        fixture.ViewModel.ActiveCommandModelVersion.Should().Be("openai-base");
        fixture.ViewModel.StatusMessage.Should().Be("Подключите Bluetooth-микрофон.");
    }

    [Fact]
    public async Task StateChange_ProjectsResolvedCommandAndSafeOutcome()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();
        var snapshot = VoicePipelineSnapshot.Disabled with
        {
            State = VoiceAssistantState.ExecutingCommand,
            LastRecognizedText = "сделай громче",
            LastResolvedCommandId = "audio.change-volume",
            LastIntentStatus = IntentResolutionStatus.Resolved,
            LastIntentConfidence = 1,
            LastExecutionStatus = CommandExecutionStatus.Succeeded,
            SafeMessage = "Команда выполнена.",
        };

        fixture.State.SnapshotChanged +=
            Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

        fixture.ViewModel.CurrentStateText.Should().Be("Выполняю команду");
        fixture.ViewModel.LastCommandOutcome.Should()
            .Be("audio.change-volume — выполнено");
        fixture.ViewModel.StatusMessage.Should().Be("Команда выполнена.");
    }

    private static AudioInputDevice Microphone(string id, string name) => new(id, name, false, true);

    private sealed class VoiceViewModelFixture
    {
        private VoiceViewModelFixture()
        {
        }

        public required VoiceControlViewModel ViewModel { get; init; }

        public required IVoiceSettingsRepository Settings { get; init; }

        public required IAudioInputDeviceService Devices { get; init; }

        public required IVoicePipelineController Controller { get; init; }

        public required IVoicePipelineStateSource State { get; init; }

        public static VoiceViewModelFixture Create(
            VoiceSettings settingsValue,
            IReadOnlyList<AudioInputDevice> microphones)
        {
            var settings = Substitute.For<IVoiceSettingsRepository>();
            var current = settingsValue;
            settings.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => current);
            settings.SaveAsync(Arg.Any<VoiceSettings>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    current = call.ArgAt<VoiceSettings>(0);
                    return Task.CompletedTask;
                });
            var devices = Substitute.For<IAudioInputDeviceService>();
            devices.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(microphones);
            var controller = Substitute.For<IVoicePipelineController>();
            var state = Substitute.For<IVoicePipelineStateSource>();
            state.Snapshot.Returns(VoicePipelineSnapshot.Disabled);
            var manager = Substitute.For<IVoiceModelManager>();
            manager.GetStateAsync(Arg.Any<CancellationToken>()).Returns(VoiceModelState.Empty);
            var models = new VoiceModelManagerViewModel(manager);
            var viewModel = new VoiceControlViewModel(settings, devices, controller, state, models);
            return new VoiceViewModelFixture
            {
                ViewModel = viewModel,
                Settings = settings,
                Devices = devices,
                Controller = controller,
                State = state,
            };
        }
    }
}
