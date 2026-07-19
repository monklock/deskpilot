using DeskPilot.Application.Voice;
using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class VoicePipelineCoordinatorTests
{
    [Fact]
    public async Task RunSingleCycleAsync_ResolvesDispatchesAndSignalsSuccess()
    {
        var fixture = PipelineFixture.Create();
        var history = new List<VoiceAssistantState>();
        fixture.State.SnapshotChanged += (_, snapshot) => history.Add(snapshot.State);

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай громче");
        fixture.State.Snapshot.LastRecognitionConfidence.Should().Be(0.91);
        history.Should().ContainInOrder(
            VoiceAssistantState.WaitingForWakeWord,
            VoiceAssistantState.WakeWordDetected,
            VoiceAssistantState.ListeningForCommand,
            VoiceAssistantState.DetectingSpeechEnd,
            VoiceAssistantState.RecognizingCommand,
            VoiceAssistantState.ResolvingCommand,
            VoiceAssistantState.ExecutingCommand,
            VoiceAssistantState.Cooldown,
            VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.LastResolvedCommandId.Should().Be("audio.change-volume");
        fixture.State.Snapshot.LastIntentStatus.Should().Be(IntentResolutionStatus.Resolved);
        fixture.State.Snapshot.LastIntentConfidence.Should().Be(1);
        fixture.State.Snapshot.LastExecutionStatus.Should().Be(CommandExecutionStatus.Succeeded);
        await fixture.Signals.Received().PlayAsync(VoiceSignal.Success, Arg.Any<CancellationToken>());
        fixture.WakeCapture.DisposeCount.Should().Be(1);
        fixture.CommandCapture.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData(IntentResolutionStatus.NotFound, "voice-command-not-found")]
    [InlineData(IntentResolutionStatus.Ambiguous, "voice-command-ambiguous")]
    public async Task RunSingleCycleAsync_UnresolvedCommandSignalsFailure(
        IntentResolutionStatus status,
        string expectedCode)
    {
        var fixture = PipelineFixture.Create();
        var history = new List<VoiceAssistantState>();
        fixture.State.SnapshotChanged += (_, snapshot) => history.Add(snapshot.State);
        fixture.Commands.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<Action<VoiceCommandExecutionProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(new VoiceCommandExecutionResult(
                new IntentResolutionResult(status, null, 0),
                null));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.LastIntentStatus.Should().Be(status);
        fixture.State.Snapshot.LastExecutionStatus.Should().BeNull();
        fixture.State.Snapshot.ErrorCode.Should().Be(expectedCode);
        history.Should().Contain(VoiceAssistantState.ResolvingCommand);
        history.Should().NotContain(VoiceAssistantState.ExecutingCommand);
        await fixture.Signals.Received().PlayAsync(VoiceSignal.Failure, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleCycleAsync_FailedHandlerPublishesSafeCodeAndRecovers()
    {
        var fixture = PipelineFixture.Create();
        var request = new CommandRequest(CommandId.From("audio.switch-preferred-device"));
        fixture.Commands.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<Action<VoiceCommandExecutionProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Action<VoiceCommandExecutionProgress>>()(
                    new VoiceCommandExecutionProgress(
                        request.CommandId,
                        IntentResolutionStatus.Resolved,
                        0.93));
                return new VoiceCommandExecutionResult(
                    new IntentResolutionResult(
                        IntentResolutionStatus.Resolved,
                        request,
                        0.93),
                    new CommandExecutionResult(
                        request.CommandId,
                        CommandExecutionStatus.Failed,
                        "Private endpoint bt-personal failed.")
                    {
                        ErrorCode = "audio-endpoint-unavailable",
                    });
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.LastExecutionStatus.Should().Be(CommandExecutionStatus.Failed);
        fixture.State.Snapshot.ErrorCode.Should().Be("audio-endpoint-unavailable");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("bt-personal");
        await fixture.Signals.Received().PlayAsync(VoiceSignal.Failure, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleCycleAsync_DisposesCommandCaptureBeforeRecognition()
    {
        var fixture = PipelineFixture.Create();
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                fixture.CommandCapture.DisposeCount.Should().Be(1);
                return new SpeechRecognitionResult("команда", 0.90, true);
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.LastRecognizedText.Should().Be("команда");
    }

    [Fact]
    public async Task RunSingleCycleAsync_SelectedMicrophoneUnavailable_PublishesSafeError()
    {
        var fixture = PipelineFixture.Create();
        fixture.Devices.ResolveAsync("bt-mic", Arg.Any<CancellationToken>()).Returns(
            new AudioInputResolution(AudioInputResultCode.SelectedDeviceUnavailable, null, "private device detail"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-unavailable");
        fixture.State.Snapshot.SafeMessage.Should().Contain("Bluetooth");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("private device detail");
        await fixture.Captures.DidNotReceiveWithAnyArgs().OpenAsync(default!, default);
    }

    [Fact]
    public async Task RunSingleCycleAsync_ActiveModelMissing_PublishesSafeError()
    {
        var fixture = PipelineFixture.Create();
        fixture.Models.GetActiveAsync(VoiceModelProvider.CommandWhisper, Arg.Any<CancellationToken>())
            .Returns((InstalledVoiceModel?)null);

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("model-unavailable");
        fixture.State.Snapshot.SafeMessage.Should().Contain("модель");
    }

    [Fact]
    public async Task RunSingleCycleAsync_ActiveModelFilesMissing_PublishesModelUnavailable()
    {
        var fixture = PipelineFixture.Create();
        fixture.RuntimeProviders.Create(Arg.Any<InstalledVoiceModel>(), Arg.Any<InstalledVoiceModel>())
            .Returns<VoiceRuntimeProviders>(_ => throw new InvalidOperationException("C:\\Users\\Person\\missing-model"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("model-unavailable");
        fixture.State.Snapshot.SafeMessage.Should().Contain("модель");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("Person");
    }

    [Fact]
    public async Task DisableAsync_CancelsOwnedRunAndDisposesCapture()
    {
        var fixture = PipelineFixture.Create(blockWakeUntilCancellation: true);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == VoiceAssistantState.WaitingForWakeWord)
            {
                waiting.TrySetResult();
            }
        };

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
        fixture.WakeCapture.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_ReturnsBeforeSynchronouslyBlockingProviderLoop()
    {
        var fixture = PipelineFixture.Create();
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<WakeWordDetectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseProvider = new ManualResetEventSlim(false);
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                providerEntered.TrySetResult();
                releaseProvider.Wait(TimeSpan.FromSeconds(5));
                return neverCompletes.Task.WaitAsync(call.ArgAt<CancellationToken>(2));
            });

        var enableTask = Task.Run(() => fixture.Coordinator.EnableAsync(CancellationToken.None));
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var enableReturnedBeforeRelease = true;
        try
        {
            await enableTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            enableReturnedBeforeRelease = false;
        }

        releaseProvider.Set();
        await enableTask.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        enableReturnedBeforeRelease.Should().BeTrue();
    }

    [Fact]
    public async Task EnableAsync_WhileDisableIsStopping_WaitsForPreviousRunOwnership()
    {
        var fixture = PipelineFixture.Create();
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRunCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.ArgAt<CancellationToken>(2);
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstRunStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        firstRunCancellationObserved.TrySetResult();
                        await releaseFirstRun.Task;
                        throw;
                    }
                }

                secondRunStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new WakeWordDetectionResult("альфа", 0.93);
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await firstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disableTask = fixture.Coordinator.DisableAsync(CancellationToken.None);
        await firstRunCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var enableTask = fixture.Coordinator.EnableAsync(CancellationToken.None);
        await Task.Yield();

        var enableCompletedBeforeRelease = enableTask.IsCompleted;
        releaseFirstRun.TrySetResult();
        await disableTask.WaitAsync(TimeSpan.FromSeconds(2));
        await enableTask.WaitAsync(TimeSpan.FromSeconds(2));
        await secondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stateBeforeCleanup = fixture.State.Snapshot.State;
        await fixture.Coordinator.DisableAsync(CancellationToken.None);
        stateBeforeCleanup.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        enableCompletedBeforeRelease.Should().BeFalse();
    }

    [Fact]
    public async Task RunSingleCycleAsync_ProviderThrows_PublishesSafeGenericError()
    {
        var fixture = PipelineFixture.Create();
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<WakeWordDetectionResult>>(_ => throw new InvalidOperationException("C:\\Users\\Person\\private-model"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("voice-pipeline-failed");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("Person");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("private-model");
    }

    [Fact]
    public async Task RunSingleCycleAsync_LowConfidence_PublishesTextWithoutExecutingCommand()
    {
        var fixture = PipelineFixture.Create();
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<SpeechRecognitionResult>(_ => throw new SpeechRecognitionException(
                SpeechRecognitionFailureCode.ConfidenceBelowThreshold,
                "safe failure")
            {
                RecognizedText = "сделай тише",
                RecognitionConfidence = 0.42,
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("speech-confidence-low");
        fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай тише");
        fixture.State.Snapshot.LastRecognitionConfidence.Should().Be(0.42);
        await fixture.Commands.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<Action<VoiceCommandExecutionProgress>>(),
            Arg.Any<CancellationToken>());
        await fixture.Signals.Received().PlayAsync(
            VoiceSignal.Failure,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleCycleAsync_NoSpeech_PublishesNotRecognizedWithoutCallingWhisper()
    {
        var fixture = PipelineFixture.Create();
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new VoiceActivityResult(false, TimeSpan.Zero, null));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.ErrorCode.Should().Be("speech-not-detected");
        fixture.State.Snapshot.SafeMessage.Should().Be("Команда не распознана");
        fixture.State.Snapshot.LastRecognizedText.Should().BeNull();
        fixture.State.Snapshot.LastRecognitionConfidence.Should().BeNull();
        await fixture.Speech.DidNotReceive().RecognizeAsync(
            Arg.Any<CapturedCommandAudio>(),
            Arg.Any<SpeechRecognitionOptions>(),
            Arg.Any<CancellationToken>());
        await fixture.Commands.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<Action<VoiceCommandExecutionProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplicitBluetoothMicrophoneReconnect_ResumesSameEndpoint()
    {
        var fixture = PipelineFixture.Create(blockWakeUntilCancellation: true);
        fixture.Devices.ResolveAsync("bt-mic", Arg.Any<CancellationToken>()).Returns(
            new AudioInputResolution(AudioInputResultCode.SelectedDeviceUnavailable, null),
            new AudioInputResolution(
                AudioInputResultCode.Success,
                new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)));
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(
            DeviceSnapshots(
                [],
                [new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)]));
        var waiting = WaitForStateAsync(fixture.State, VoiceAssistantState.WaitingForWakeWord);

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        await fixture.Captures.Received().OpenAsync("bt-mic", Arg.Any<CancellationToken>());
        await fixture.Captures.DidNotReceive().OpenAsync(
            Arg.Is<string>(id => !string.Equals(id, "bt-mic", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveBluetoothCaptureDisconnect_WaitsForSameEndpointBeforeRetry()
    {
        var fixture = PipelineFixture.Create();
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new AudioCaptureException(AudioInputResultCode.Disconnected, "Microphone disconnected."),
                async call =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                    return new WakeWordDetectionResult("альфа", 0.93);
                });
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(
            DeviceSnapshots(
                watchStarted,
                [new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)]));

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await watchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.Devices.Received(1).WatchAsync(Arg.Any<CancellationToken>());
        await fixture.Captures.DidNotReceive().OpenAsync(
            Arg.Is<string>(id => !string.Equals(id, "bt-mic", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommandBluetoothCaptureDisconnect_WaitsForSameEndpointBeforeRetry()
    {
        var fixture = PipelineFixture.Create();
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<VoiceActivityResult>(_ => throw new AudioCaptureException(
                AudioInputResultCode.Disconnected,
                "Microphone disconnected."));
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(
            DeviceSnapshots(
                watchStarted,
                [new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)]));

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await watchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.Devices.Received(1).WatchAsync(Arg.Any<CancellationToken>());
        await fixture.Captures.DidNotReceive().OpenAsync(
            Arg.Is<string>(id => !string.Equals(id, "bt-mic", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivationGate_StopsPipelineWhileLeaseIsHeldAndResumesAfterRelease()
    {
        var fixture = PipelineFixture.Create(blockWakeUntilCancellation: true);
        var firstWaiting = WaitForStateAsync(fixture.State, VoiceAssistantState.WaitingForWakeWord);
        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await firstWaiting.WaitAsync(TimeSpan.FromSeconds(2));
        var activationGate = new VoiceModelActivationGate(fixture.Coordinator);

        var secondWaiting = WaitForStateAsync(fixture.State, VoiceAssistantState.WaitingForWakeWord);
        await using (await activationGate.EnterIdleAsync(CancellationToken.None))
        {
            fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
            fixture.WakeCapture.DisposeCount.Should().Be(1);
        }

        await secondWaiting.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.CommandCapture.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task SuccessSignalThrows_DoesNotDiscardRecognizedTextOrFailCycle()
    {
        var fixture = PipelineFixture.Create();
        fixture.Signals.PlayAsync(VoiceSignal.Success, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("tone unavailable"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай громче");
        fixture.State.Snapshot.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task FailureSignalThrows_StillPublishesSafeProviderError()
    {
        var fixture = PipelineFixture.Create();
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IAudioCaptureSession>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<WakeWordDetectionResult>>(_ => throw new InvalidOperationException("provider failed"));
        fixture.Signals.PlayAsync(VoiceSignal.Failure, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("tone unavailable"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("voice-pipeline-failed");
    }

    private static Task WaitForStateAsync(VoicePipelineStateStore state, VoiceAssistantState expected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<VoicePipelineSnapshot>? handler = null;
        handler = (_, snapshot) =>
        {
            if (snapshot.State != expected)
            {
                return;
            }

            state.SnapshotChanged -= handler;
            completion.TrySetResult();
        };
        state.SnapshotChanged += handler;
        return completion.Task;
    }

    private static async IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> DeviceSnapshots(
        params IReadOnlyList<AudioInputDevice>[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            yield return snapshot;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> DeviceSnapshots(
        TaskCompletionSource started,
        params IReadOnlyList<AudioInputDevice>[] snapshots)
    {
        started.TrySetResult();
        foreach (var snapshot in snapshots)
        {
            yield return snapshot;
            await Task.Yield();
        }
    }

    private sealed class PipelineFixture
    {
        private PipelineFixture()
        {
        }

        public required VoicePipelineCoordinator Coordinator { get; init; }

        public required VoicePipelineStateStore State { get; init; }

        public required IVoiceSettingsRepository Settings { get; init; }

        public required IVoiceModelStore Models { get; init; }

        public required IVoiceRuntimeProviderFactory RuntimeProviders { get; init; }

        public required IAudioInputDeviceService Devices { get; init; }

        public required IAudioCaptureSessionFactory Captures { get; init; }

        public required IWakeWordProvider Wake { get; init; }

        public required ISpeechToTextProvider Speech { get; init; }

        public required IVoiceActivityDetector VoiceActivity { get; init; }

        public required IVoiceSignalService Signals { get; init; }

        public required IVoiceCommandExecutionService Commands { get; init; }

        public required TestCaptureSession WakeCapture { get; init; }

        public required TestCaptureSession CommandCapture { get; init; }

        public static PipelineFixture Create(bool blockWakeUntilCancellation = false)
        {
            var settings = Substitute.For<IVoiceSettingsRepository>();
            settings.GetAsync(Arg.Any<CancellationToken>()).Returns(
                VoiceSettings.Default with
                {
                    IsEnabled = true,
                    MicrophoneEndpointId = "bt-mic",
                    MicrophoneFriendlyName = "Bluetooth microphone",
                    Cooldown = TimeSpan.FromMilliseconds(1),
                });

            var wakeModel = Model(VoiceModelProvider.WakeVosk, "wake-ru", "0.22", "WakeVosk/wake-ru/0.22");
            var commandModel = Model(VoiceModelProvider.CommandWhisper, "whisper-base", "openai-base", "CommandWhisper/whisper-base/openai-base/ggml-base.bin");
            var models = Substitute.For<IVoiceModelStore>();
            models.GetActiveAsync(VoiceModelProvider.WakeVosk, Arg.Any<CancellationToken>()).Returns(wakeModel);
            models.GetActiveAsync(VoiceModelProvider.CommandWhisper, Arg.Any<CancellationToken>()).Returns(commandModel);

            var devices = Substitute.For<IAudioInputDeviceService>();
            devices.ResolveAsync("bt-mic", Arg.Any<CancellationToken>()).Returns(
                new AudioInputResolution(
                    AudioInputResultCode.Success,
                    new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)));

            var wakeCapture = new TestCaptureSession("bt-mic");
            var commandCapture = new TestCaptureSession("bt-mic");
            var captureQueue = new Queue<IAudioCaptureSession>([wakeCapture, commandCapture]);
            var captures = Substitute.For<IAudioCaptureSessionFactory>();
            captures.OpenAsync("bt-mic", Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(captureQueue.Dequeue()));

            var wake = Substitute.For<IWakeWordProvider>();
            wake.ProviderId.Returns("vosk");
            if (blockWakeUntilCancellation)
            {
                wake.WaitForDetectionAsync(
                        Arg.Any<IAudioCaptureSession>(),
                        Arg.Any<WakeWordOptions>(),
                        Arg.Any<CancellationToken>())
                    .Returns(async call =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                        return new WakeWordDetectionResult("альфа", 0.93);
                    });
            }
            else
            {
                wake.WaitForDetectionAsync(
                        Arg.Any<IAudioCaptureSession>(),
                        Arg.Any<WakeWordOptions>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new WakeWordDetectionResult("альфа", 0.93));
            }

            var speech = Substitute.For<ISpeechToTextProvider>();
            speech.ProviderId.Returns("whisper");
            speech.RecognizeAsync(
                    Arg.Any<CapturedCommandAudio>(),
                    Arg.Any<SpeechRecognitionOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(new SpeechRecognitionResult("сделай громче", 0.91, true));

            var runtimeProviders = Substitute.For<IVoiceRuntimeProviderFactory>();
            runtimeProviders.Create(wakeModel, commandModel).Returns(new VoiceRuntimeProviders(wake, speech));

            var audio = new CapturedCommandAudio(
                new byte[32_000],
                AudioFormat.Pcm16KhzMono,
                TimeSpan.FromSeconds(1));
            var vad = Substitute.For<IVoiceActivityDetector>();
            vad.CaptureAsync(
                    Arg.Any<IAudioCaptureSession>(),
                    Arg.Any<VoiceActivityOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(new VoiceActivityResult(true, TimeSpan.FromSeconds(1), audio));
            var signals = Substitute.For<IVoiceSignalService>();
            var commands = Substitute.For<IVoiceCommandExecutionService>();
            commands.ExecuteAsync(
                    Arg.Any<string>(),
                    Arg.Any<Action<VoiceCommandExecutionProgress>>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = new CommandRequest(CommandId.From("audio.change-volume"));
                    call.Arg<Action<VoiceCommandExecutionProgress>>()(
                        new VoiceCommandExecutionProgress(
                            request.CommandId,
                            IntentResolutionStatus.Resolved,
                            1));
                    return new VoiceCommandExecutionResult(
                        new IntentResolutionResult(
                            IntentResolutionStatus.Resolved,
                            request,
                            1),
                        CommandExecutionResult.Succeeded(request.CommandId));
                });
            var state = new VoicePipelineStateStore();
            var coordinator = new VoicePipelineCoordinator(
                settings,
                models,
                runtimeProviders,
                devices,
                captures,
                vad,
                signals,
                commands,
                state,
                TimeProvider.System);

            return new PipelineFixture
            {
                Coordinator = coordinator,
                State = state,
                Settings = settings,
                Models = models,
                RuntimeProviders = runtimeProviders,
                Devices = devices,
                Captures = captures,
                Wake = wake,
                Speech = speech,
                VoiceActivity = vad,
                Signals = signals,
                Commands = commands,
                WakeCapture = wakeCapture,
                CommandCapture = commandCapture,
            };
        }

        private static InstalledVoiceModel Model(
            VoiceModelProvider provider,
            string modelId,
            string version,
            string relativePath) => new(
                provider,
                modelId,
                version,
                relativePath,
                new string('a', 64),
                VoiceModelSource.Seed,
                true,
                false,
                DateTimeOffset.Parse("2026-07-18T00:00:00+03:00"));
    }

    private sealed class TestCaptureSession(string endpointId) : IAudioCaptureSession
    {
        public int DisposeCount { get; private set; }

        public string EndpointId { get; } = endpointId;

        public AudioFormat Format => AudioFormat.Pcm16KhzMono;

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
