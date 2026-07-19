using DeskPilot.Application.Voice;
using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;
using DeskPilot.Desktop.ViewModels;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using NSubstitute;
using System.Globalization;
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

    [Fact]
    public async Task StateChange_LowConfidence_ShowsRecognizedTextAndConfidence()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();
        var snapshot = VoicePipelineSnapshot.Disabled with
        {
            State = VoiceAssistantState.Error,
            LastRecognizedText = "сделай тише",
            LastRecognitionConfidence = 0.42,
            ErrorCode = "speech-confidence-low",
            SafeMessage = "Команда распознана неуверенно. Повторите её.",
        };

        fixture.State.SnapshotChanged +=
            Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

        fixture.ViewModel.LastRecognizedText.Should().Be("сделай тише");
        fixture.ViewModel.LastCommandOutcome.Should().Be("Распознано, confidence: 0.42");
    }

    [Theory]
    [InlineData("speech-not-detected")]
    [InlineData("speech-not-recognized")]
    public async Task StateChange_NoRecognizedText_ShowsStableDiagnosticOutcome(string errorCode)
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();
        var snapshot = VoicePipelineSnapshot.Disabled with
        {
            State = VoiceAssistantState.Error,
            ErrorCode = errorCode,
            SafeMessage = "Команда не распознана",
        };

        fixture.State.SnapshotChanged +=
            Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, snapshot);

        fixture.ViewModel.LastRecognizedText.Should().Be("—");
        fixture.ViewModel.LastCommandOutcome.Should().Be("Команда не распознана");
    }

    [Fact]
    public async Task InitializeAsync_ProjectsAlreadyActiveCurrentCaptureDiagnosticsUsingRussianCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var fixture = VoiceViewModelFixture.Create(
                VoiceSettings.Default,
                [],
                ActiveCaptureSnapshot());

            await fixture.ViewModel.InitializeAsync();

            fixture.ViewModel.CaptureStatus.Should().Be("Микрофон активен");
            fixture.ViewModel.CapturedDuration.Should().Be("1,84 с");
            fixture.ViewModel.AudioLevelDiagnostics.Should().Be("Шум: 0,0123; пик: 0,3487");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task StateChange_ProjectsCurrentCaptureDiagnosticsAndRaisesPropertyNotifications()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();
        var changed = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        fixture.State.SnapshotChanged +=
            Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, ActiveCaptureSnapshot());

        fixture.ViewModel.CaptureStatus.Should().Be("Микрофон активен");
        fixture.ViewModel.CapturedDuration.Should().Be("1,84 с");
        fixture.ViewModel.AudioLevelDiagnostics.Should().Be("Шум: 0,0123; пик: 0,3487");
        changed.Should().Contain(nameof(VoiceControlViewModel.CaptureStatus));
        changed.Should().Contain(nameof(VoiceControlViewModel.CapturedDuration));
        changed.Should().Contain(nameof(VoiceControlViewModel.AudioLevelDiagnostics));
    }

    [Fact]
    public async Task StateChange_NewCycleAndDisabled_ClearCaptureDiagnostics()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, [], ActiveCaptureSnapshot());
        await fixture.ViewModel.InitializeAsync();

        fixture.State.SnapshotChanged += Raise.Event<EventHandler<VoicePipelineSnapshot>>(
            fixture.State,
            VoicePipelineSnapshot.Disabled with
            {
                State = VoiceAssistantState.ListeningForCommand,
                IsCaptureActive = true,
            });

        fixture.ViewModel.CaptureStatus.Should().Be("Микрофон активен");
        fixture.ViewModel.CapturedDuration.Should().Be("—");
        fixture.ViewModel.AudioLevelDiagnostics.Should().Be("—");

        fixture.State.SnapshotChanged += Raise.Event<EventHandler<VoicePipelineSnapshot>>(
            fixture.State,
            ActiveCaptureSnapshot() with
            {
                State = VoiceAssistantState.Disabled,
                IsCaptureActive = false,
            });

        fixture.ViewModel.CaptureStatus.Should().Be("Микрофон не активен");
        fixture.ViewModel.CapturedDuration.Should().Be("—");
        fixture.ViewModel.AudioLevelDiagnostics.Should().Be("—");
    }

    [Theory]
    [InlineData(double.NaN, 0.3487)]
    [InlineData(0.0123, double.PositiveInfinity)]
    [InlineData(-0.0123, 0.3487)]
    public async Task StateChange_PartialOrInvalidLevels_HidesAllLevelDiagnostics(double noise, double peak)
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();

        fixture.State.SnapshotChanged += Raise.Event<EventHandler<VoicePipelineSnapshot>>(
            fixture.State,
            ActiveCaptureSnapshot() with
            {
                LastNoiseFloorRms = noise,
                LastPeakRms = peak,
            });

        fixture.ViewModel.AudioLevelDiagnostics.Should().Be("—");
    }

    [Fact]
    public async Task StateChange_MissingLevel_HidesAllLevelDiagnostics()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        await fixture.ViewModel.InitializeAsync();

        fixture.State.SnapshotChanged += Raise.Event<EventHandler<VoicePipelineSnapshot>>(
            fixture.State,
            ActiveCaptureSnapshot() with
            {
                LastNoiseFloorRms = null,
            });

        fixture.ViewModel.AudioLevelDiagnostics.Should().Be("—");
    }

    [Fact]
    public async Task StateChange_FromBackgroundThread_UsesCapturedUiSynchronizationContext()
    {
        var previousContext = SynchronizationContext.Current;
        var context = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
            await fixture.ViewModel.InitializeAsync();

            SynchronizationContext.SetSynchronizationContext(null);
            await Task.Run(() => fixture.State.SnapshotChanged +=
                Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, ActiveCaptureSnapshot()));

            fixture.ViewModel.CaptureStatus.Should().Be("Микрофон не активен");
            context.Drain();
            fixture.ViewModel.CaptureStatus.Should().Be("Микрофон активен");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task StateChange_QueuedOlderSnapshotAfterSynchronousNewerSnapshot_DoesNotOverwriteNewerState()
    {
        var previousContext = SynchronizationContext.Current;
        var context = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
            await fixture.ViewModel.InitializeAsync();
            var older = ActiveCaptureSnapshot() with { State = VoiceAssistantState.DetectingSpeechEnd };
            var newer = ActiveCaptureSnapshot() with { State = VoiceAssistantState.RecognizingCommand };

            SynchronizationContext.SetSynchronizationContext(null);
            await Task.Run(() => fixture.State.SnapshotChanged +=
                Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, older));

            context.PendingCount.Should().Be(1);
            SynchronizationContext.SetSynchronizationContext(context);
            fixture.State.SnapshotChanged +=
                Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, newer);
            fixture.ViewModel.CurrentState.Should().Be(VoiceAssistantState.RecognizingCommand);

            context.Drain();
            fixture.ViewModel.CurrentState.Should().Be(VoiceAssistantState.RecognizingCommand);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task Dispose_PendingSnapshotDoesNotApplyOrRaisePropertyChanged()
    {
        var previousContext = SynchronizationContext.Current;
        var context = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
            await fixture.ViewModel.InitializeAsync();
            var changes = new List<string?>();
            fixture.ViewModel.PropertyChanged += (_, eventArgs) => changes.Add(eventArgs.PropertyName);

            SynchronizationContext.SetSynchronizationContext(null);
            await Task.Run(() => fixture.State.SnapshotChanged +=
                Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, ActiveCaptureSnapshot()));

            context.PendingCount.Should().Be(1);
            fixture.ViewModel.Dispose();
            fixture.ViewModel.Dispose();
            context.Drain();

            fixture.ViewModel.CaptureStatus.Should().Be("Микрофон не активен");
            changes.Should().BeEmpty();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void Dispose_UnsubscribesFromSnapshotChanges()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);

        fixture.ViewModel.Dispose();
        fixture.State.SnapshotChanged +=
            Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, ActiveCaptureSnapshot());

        fixture.ViewModel.CaptureStatus.Should().Be("Микрофон не активен");
    }

    [Fact]
    public void StateChange_DisposeFromPropertyChanged_DoesNotRaiseNotificationsAfterDisposal()
    {
        var fixture = VoiceViewModelFixture.Create(VoiceSettings.Default, []);
        var disposed = false;
        var notificationsAfterDisposal = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (!disposed)
            {
                fixture.ViewModel.Dispose();
                disposed = true;
                return;
            }

            notificationsAfterDisposal.Add(eventArgs.PropertyName);
        };

        fixture.State.SnapshotChanged +=
            Raise.Event<EventHandler<VoicePipelineSnapshot>>(fixture.State, ActiveCaptureSnapshot());

        disposed.Should().BeTrue();
        notificationsAfterDisposal.Should().BeEmpty();
    }

    private static VoicePipelineSnapshot ActiveCaptureSnapshot() => VoicePipelineSnapshot.Disabled with
    {
        State = VoiceAssistantState.DetectingSpeechEnd,
        IsCaptureActive = true,
        LastCapturedCommandDuration = TimeSpan.FromMilliseconds(1_840),
        LastNoiseFloorRms = 0.0123,
        LastPeakRms = 0.3487,
    };

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
            IReadOnlyList<AudioInputDevice> microphones,
            VoicePipelineSnapshot? snapshot = null)
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
            state.Snapshot.Returns(snapshot ?? VoicePipelineSnapshot.Disabled);
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

    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly object _sync = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _work = [];

        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _work.Count;
                }
            }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_sync)
            {
                _work.Enqueue((callback, state));
            }
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (_sync)
                {
                    if (!_work.TryDequeue(out work))
                    {
                        return;
                    }
                }

                work.Callback(work.State);
            }
        }
    }
}
