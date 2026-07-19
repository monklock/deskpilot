using DeskPilot.Application.Voice;
using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;
using DeskPilot.Voice.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class VoicePipelineCoordinatorTests
{
    [Fact]
    public async Task RunSingleCycleAsync_UsesWakeEndCursorWithoutReopeningMicrophone()
    {
        var fixture = PipelineFixture.Create();

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.BufferedCaptures.OpenedEndpoints.Should().Equal("bt-mic");
        fixture.Buffered.OpenedOffsets.Should().Equal(0, 24_000);
        await fixture.Signals.DidNotReceive()
            .PlayAsync(VoiceSignal.Ready, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleCycleAsync_PublishesListeningBeforeCaptureAndEndpointingFromProgressOnce()
    {
        var fixture = PipelineFixture.Create();
        var history = new List<VoiceAssistantState>();
        fixture.State.SnapshotChanged += (_, snapshot) => history.Add(snapshot.State);
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.ListeningForCommand);
                var progress = call.ArgAt<Action<VoiceActivityProgress>>(3);
                progress(new VoiceActivityProgress(24_320, 0.01, 0.25));
                progress(new VoiceActivityProgress(24_320, 0.01, 0.25));
                fixture.Buffered.LatestSampleOffset = 40_000;
                return SuccessfulActivity();
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

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
        history.Count(state => state == VoiceAssistantState.DetectingSpeechEnd).Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_PassesExactAmbientVadAndWhisperDefaults()
    {
        var fixture = PipelineFixture.Create();
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cursor = (TestVoiceAudioCursor)call.ArgAt<IVoiceAudioCursor>(0);
                call.ArgAt<AmbientNoiseSnapshot>(1).Should().Be(
                    new AmbientNoiseSnapshot(0.01, TimeSpan.FromSeconds(3), 150));
                call.ArgAt<VoiceActivityOptions>(2).Should().Be(new VoiceActivityOptions(
                    TimeSpan.FromMilliseconds(300),
                    TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromMilliseconds(4_000),
                    TimeSpan.FromMilliseconds(1_200),
                    TimeSpan.FromMilliseconds(10_000),
                    fixture.SettingsValue.VoiceActivitySensitivity));
                call.ArgAt<Action<VoiceActivityProgress>>(3)(
                    new VoiceActivityProgress(cursor.StartSampleOffset + 320, 0.01, 0.25));
                cursor.Owner.LatestSampleOffset = cursor.StartSampleOffset + 16_000;
                return SuccessfulActivity(cursor.StartSampleOffset);
            });
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<SpeechRecognitionOptions>(1).Should().Be(
                    new SpeechRecognitionOptions("ru", 0.70));
                return new SpeechRecognitionResult("сделай громче", 0.91, true);
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        await fixture.Commands.Received(1)
            .ExecuteAsync("сделай громче", Arg.Any<Action<VoiceCommandExecutionProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleCycleAsync_DisposesCommandCursorBeforeWhisperAndSessionAfterCooldown()
    {
        var fixture = PipelineFixture.Create();
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fixture.Buffered.CursorAt(24_000).DisposeCount.Should().Be(1);
                fixture.Buffered.DisposeCount.Should().Be(0);
                return new SpeechRecognitionResult("команда", 0.90, true);
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.Buffered.CursorAt(0).DisposeCount.Should().Be(1);
        fixture.Buffered.CursorAt(24_000).DisposeCount.Should().Be(1);
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RunSingleCycleAsync_NoSpeechNeverEndpointsRecognizesOrDispatches()
    {
        var fixture = PipelineFixture.Create();
        var history = new List<VoiceAssistantState>();
        fixture.State.SnapshotChanged += (_, snapshot) => history.Add(snapshot.State);
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(NoSpeechActivity());

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        history.Should().NotContain(VoiceAssistantState.DetectingSpeechEnd);
        fixture.State.Snapshot.ErrorCode.Should().Be("speech-not-detected");
        fixture.State.Snapshot.SafeMessage.Should().Be("Команда не распознана");
        await fixture.Speech.DidNotReceiveWithAnyArgs()
            .RecognizeAsync(default!, default!, default);
        await fixture.Commands.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunSingleCycleAsync_BufferOverrunPublishesExactSafeFailureAndDispatchesNothing()
    {
        var fixture = PipelineFixture.Create();
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns<VoiceActivityResult>(_ => throw new AudioCaptureException(
                AudioInputResultCode.BufferOverrun,
                "private offset 123456"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.ErrorCode.Should().Be("voice-audio-overrun");
        fixture.State.Snapshot.SafeMessage.Should().Be(
            "Не удалось сохранить начало команды. Повторите команду.");
        await fixture.Speech.DidNotReceiveWithAnyArgs()
            .RecognizeAsync(default!, default!, default);
        await fixture.Commands.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_DetectionBeyondLastProviderFrameIsRejectedAndNextCycleUsesSameLiveSession()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        fixture.BufferedCaptures.Enqueue(new TestBufferedCaptureSession("bt-mic"));
        fixture.Buffered.QueueCursorReadEnd(25_600);
        var wakeCalls = 0;
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cursor = call.ArgAt<IVoiceAudioCursor>(0);
                var cancellationToken = call.ArgAt<CancellationToken>(2);
                switch (Interlocked.Increment(ref wakeCalls))
                {
                    case 1:
                        fixture.Buffered.LatestSampleOffset = 40_000;
                        await ConsumeAvailableFramesAsync(cursor, cancellationToken);
                        return new WakeWordDetectionResult("альфа", 0.93, 16_000, 24_000, 32_000);
                    case 2:
                        await ConsumeAvailableFramesAsync(cursor, cancellationToken);
                        return ValidDetection(cursor.StartSampleOffset);
                    default:
                        fixture.WakeBlocked.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return ValidDetection(cursor.StartSampleOffset);
                }
            });

        var errors = new List<string?>();
        fixture.State.SnapshotChanged += (_, snapshot) => errors.Add(snapshot.ErrorCode);
        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        errors.Should().Contain("voice-wake-timing-invalid");
        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.OpenedOffsets.Should().StartWith([0L, 40_000L, 64_000L, 80_000L]);
        await fixture.Commands.Received(1)
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<Action<VoiceCommandExecutionProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSingleCycleAsync_WakeBeforeCursorStartMapsSafeAndDispatchesNothing()
    {
        var fixture = PipelineFixture.Create(initialLiveEdge: 10_000);
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fixture.Buffered.LatestSampleOffset = 25_600;
                return new WakeWordDetectionResult("альфа", 0.93, 9_999, 24_000, 25_600);
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("voice-wake-timing-invalid");
        await fixture.Commands.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnabledPipeline_TwoCyclesReuseSessionAndSecondWakeStartsAtLiveEdge()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 2);

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.OpenedOffsets.Should().StartWith([0L, 24_000L, 40_000L, 64_000L]);
        fixture.Buffered.CursorAt(40_000).DisposeCount.Should().Be(1);
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task EnabledPipeline_HealthyCooldownDoesNotReopenMicrophone()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 1);

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_WhisperFailureKeepsSessionAndNextCycleStartsAtLiveEdge()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 2);
        fixture.BufferedCaptures.Enqueue(new TestBufferedCaptureSession("bt-mic"));
        var recognitionCalls = 0;
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref recognitionCalls) == 1)
                {
                    throw new SpeechRecognitionException(
                        SpeechRecognitionFailureCode.ProviderFailure,
                        "private whisper failure");
                }

                return new SpeechRecognitionResult("сделай громче", 0.91, true);
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.OpenedOffsets.Should().StartWith([0L, 24_000L, 40_000L, 64_000L, 80_000L]);
        await fixture.Commands.Received(1)
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<Action<VoiceCommandExecutionProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_BufferOverrunKeepsSessionAndNextCycleStartsAtLiveEdge()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 2);
        fixture.BufferedCaptures.Enqueue(new TestBufferedCaptureSession("bt-mic"));
        var activityCalls = 0;
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cursor = (TestVoiceAudioCursor)call.ArgAt<IVoiceAudioCursor>(0);
                cursor.Owner.LatestSampleOffset = cursor.StartSampleOffset + 16_000;
                if (Interlocked.Increment(ref activityCalls) == 1)
                {
                    throw new AudioCaptureException(
                        AudioInputResultCode.BufferOverrun,
                        "private offset 123456");
                }

                call.ArgAt<Action<VoiceActivityProgress>>(3)(
                    new VoiceActivityProgress(cursor.StartSampleOffset + 320, 0.01, 0.25));
                return SuccessfulActivity(cursor.StartSampleOffset);
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.OpenedOffsets.Should().StartWith([0L, 24_000L, 40_000L, 64_000L, 80_000L]);
        await fixture.Commands.Received(1)
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<Action<VoiceCommandExecutionProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_ProviderFailureKeepsHealthySession()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        fixture.BufferedCaptures.Enqueue(new TestBufferedCaptureSession("bt-mic"));
        var wakeCalls = 0;
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cursor = call.ArgAt<IVoiceAudioCursor>(0);
                var cancellationToken = call.ArgAt<CancellationToken>(2);
                switch (Interlocked.Increment(ref wakeCalls))
                {
                    case 1:
                        throw new InvalidOperationException("private provider detail");
                    case 2:
                        await ConsumeAvailableFramesAsync(cursor, cancellationToken);
                        return ValidDetection(cursor.StartSampleOffset);
                    default:
                        fixture.WakeBlocked.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return ValidDetection(cursor.StartSampleOffset);
                }
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.OpenedOffsets.Should().StartWith([0L, 0L, 24_000L, 40_000L]);
        await fixture.Commands.Received(1)
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<Action<VoiceCommandExecutionProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_ResolverFailureKeepsHealthySession()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 2);
        fixture.BufferedCaptures.Enqueue(new TestBufferedCaptureSession("bt-mic"));
        var commandCalls = 0;
        fixture.Commands.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<Action<VoiceCommandExecutionProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (Interlocked.Increment(ref commandCalls) == 1)
                {
                    throw new InvalidOperationException("private resolver detail");
                }

                var request = new CommandRequest(CommandId.From("audio.change-volume"));
                call.ArgAt<Action<VoiceCommandExecutionProgress>>(1)(
                    new VoiceCommandExecutionProgress(
                        request.CommandId,
                        IntentResolutionStatus.Resolved,
                        1));
                return new VoiceCommandExecutionResult(
                    new IntentResolutionResult(IntentResolutionStatus.Resolved, request, 1),
                    CommandExecutionResult.Succeeded(request.CommandId));
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.Buffered.OpenedOffsets.Should().StartWith([0L, 24_000L, 40_000L, 64_000L, 80_000L]);
        commandCalls.Should().Be(2);
    }

    [Fact]
    public async Task EnabledPipeline_DisconnectDisposesWaitsExactEndpointAndOpensOneReplacement()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 1);
        var replacement = new TestBufferedCaptureSession("bt-mic");
        fixture.BufferedCaptures.Enqueue(replacement);
        fixture.Buffered.TerminalFailure = new AudioCaptureException(
            AudioInputResultCode.Disconnected,
            "private endpoint detail");
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(
            DeviceSnapshots([new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)]));

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.Buffered.DisposeCount.Should().Be(1);
        fixture.Buffered.CursorAt(0).DisposeCount.Should().Be(1);
        replacement.DisposeCount.Should().Be(1);
        fixture.BufferedCaptures.OpenCount.Should().Be(2);
        fixture.BufferedCaptures.OpenedEndpoints.Should().OnlyContain(id => id == "bt-mic");
        fixture.Devices.Received(1).WatchAsync(Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_SelectedDeviceUnavailableDuringOpenWaitsExactEndpointBeforeReplacement()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var replacement = new TestBufferedCaptureSession("bt-mic");
        fixture.BufferedCaptures.FailNext(new AudioCaptureException(
            AudioInputResultCode.SelectedDeviceUnavailable,
            "private endpoint detail"));
        fixture.BufferedCaptures.Enqueue(replacement);
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(
            DeviceSnapshots([new AudioInputDevice("bt-mic", "Bluetooth microphone", false, true)]));

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(2);
        fixture.BufferedCaptures.OpenedEndpoints.Should().OnlyContain(id => id == "bt-mic");
        fixture.Devices.Received(1).WatchAsync(Arg.Any<CancellationToken>());
        fixture.Buffered.DisposeCount.Should().Be(0);
        replacement.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_RecoverySettingsFailureIsContainedAndPipelineRetries()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var replacement = new TestBufferedCaptureSession("bt-mic");
        fixture.BufferedCaptures.FailNext(new AudioCaptureException(
            AudioInputResultCode.InitializationFailed,
            "private initialization detail"));
        fixture.BufferedCaptures.Enqueue(replacement);
        var settingsCalls = 0;
        fixture.Settings.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref settingsCalls) == 2)
            {
                throw new InvalidOperationException("private settings detail");
            }

            return fixture.SettingsValue;
        });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(2);
        replacement.DisposeCount.Should().Be(1);
        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
    }

    [Fact(Timeout = 5_000)]
    public async Task EnabledPipeline_RecoveryWatchFailureIsContainedAndPipelineRetries()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var replacement = new TestBufferedCaptureSession("bt-mic");
        fixture.BufferedCaptures.FailNext(new AudioCaptureException(
            AudioInputResultCode.SelectedDeviceUnavailable,
            "private endpoint detail"));
        fixture.BufferedCaptures.Enqueue(replacement);
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(
            FailingDeviceSnapshots(new InvalidOperationException("private watch detail")));

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(2);
        fixture.Devices.Received(1).WatchAsync(Arg.Any<CancellationToken>());
        replacement.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task DisableAsync_WhileRecoveryWatchWaitsCancelsWatchAndPublishesDisabled()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        fixture.BufferedCaptures.FailNext(new AudioCaptureException(
            AudioInputResultCode.SelectedDeviceUnavailable,
            "private endpoint detail"));
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Devices.WatchAsync(Arg.Any<CancellationToken>()).Returns(call =>
            WaitingDeviceSnapshots(watchStarted, call.ArgAt<CancellationToken>(0)));

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await watchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(1);
        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
        fixture.State.Snapshot.IsCaptureActive.Should().BeFalse();
    }

    [Fact]
    public async Task DisableAsync_WhileWakeWaitsDisposesCursorAndSessionOnceAndClearsDiagnostics()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.Buffered.CursorAt(0).DisposeCount.Should().Be(1);
        fixture.Buffered.DisposeCount.Should().Be(1);
        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
        fixture.State.Snapshot.IsCaptureActive.Should().BeFalse();
        fixture.State.Snapshot.LastCapturedCommandDuration.Should().BeNull();
        fixture.State.Snapshot.LastNoiseFloorRms.Should().BeNull();
        fixture.State.Snapshot.LastPeakRms.Should().BeNull();
    }

    [Fact]
    public async Task EnableAsync_ReturnsBeforeSynchronousWakeProviderBlocksWorker()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entered.TrySetResult();
                release.Wait(call.ArgAt<CancellationToken>(2));
                return ValidDetection();
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disable = fixture.Coordinator.DisableAsync(CancellationToken.None);
        release.Set();
        await disable;
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task EnableAsync_WhileDisableWaitsForCursorExitCannotOverlapOwnership()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var replacement = new TestBufferedCaptureSession("bt-mic");
        fixture.BufferedCaptures.Enqueue(replacement);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wakeCalls = 0;
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.ArgAt<CancellationToken>(2);
                if (Interlocked.Increment(ref wakeCalls) == 1)
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        await releaseFirst.Task;
                        throw;
                    }
                }

                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ValidDetection();
            });

        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disable = fixture.Coordinator.DisableAsync(CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var enable = fixture.Coordinator.EnableAsync(CancellationToken.None);
        enable.IsCompleted.Should().BeFalse();
        releaseFirst.TrySetResult();
        await disable;
        await enable;
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);

        fixture.BufferedCaptures.OpenCount.Should().Be(2);
        fixture.Buffered.DisposeCount.Should().Be(1);
        replacement.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task DisableAsync_CallerCancellationAfterStopOwnershipDoesNotInterruptCleanup()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var pipelineToken = call.ArgAt<CancellationToken>(2);
                fixture.WakeBlocked.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, pipelineToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await releaseProvider.Task;
                    throw;
                }

                return ValidDetection();
            });
        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var callerCancellation = new CancellationTokenSource();

        var disable = fixture.Coordinator.DisableAsync(callerCancellation.Token);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        await Task.Delay(50);
        var completedBeforeRelease = disable.IsCompleted;
        releaseProvider.TrySetResult();

        await disable;
        completedBeforeRelease.Should().BeFalse();
        fixture.Buffered.DisposeCount.Should().Be(1);
        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
        fixture.State.Snapshot.IsCaptureActive.Should().BeFalse();
    }

    [Fact]
    public async Task ActivationGate_WhileWakeWaitsDisposesOnceAndResumesWithReplacement()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        var replacement = new TestBufferedCaptureSession("bt-mic");
        fixture.BufferedCaptures.Enqueue(replacement);
        await fixture.Coordinator.EnableAsync(CancellationToken.None);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var gate = new VoiceModelActivationGate(fixture.Coordinator);

        await using (await gate.EnterIdleAsync(CancellationToken.None))
        {
            fixture.Buffered.CursorAt(0).DisposeCount.Should().Be(1);
            fixture.Buffered.DisposeCount.Should().Be(1);
            fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Disabled);
        }

        await fixture.BufferedCaptures.WaitForOpenCountAsync(2).WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.DisableAsync(CancellationToken.None);
        replacement.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RunSingleCycleAsync_CancellationAtWakeDisposesCursorAndSessionOnce()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);
        await fixture.WakeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.Buffered.CursorAt(0).DisposeCount.Should().Be(1);
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_CancellationDuringDeviceResolutionStopsBeforeOpen()
    {
        var fixture = PipelineFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Devices.ResolveAsync("bt-mic", Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
            return new AudioInputResolution(AudioInputResultCode.Success, null);
        });
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.BufferedCaptures.OpenCount.Should().Be(0);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_CancellationDuringOpenStopsWithoutSessionOwnership()
    {
        var fixture = PipelineFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BufferedCaptures.FailNext(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return fixture.Buffered;
        });
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.Buffered.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task RunSingleCycleAsync_CancellationAtVadDisposesBothCursorsAndSessionOnce()
    {
        var fixture = PipelineFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(4));
                return SuccessfulActivity();
            });
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.Buffered.CursorAt(0).DisposeCount.Should().Be(1);
        fixture.Buffered.CursorAt(24_000).DisposeCount.Should().Be(1);
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RunSingleCycleAsync_CancellationAtWhisperKeepsSessionUntilRecognitionExits()
    {
        var fixture = PipelineFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                fixture.Buffered.CursorAt(24_000).DisposeCount.Should().Be(1);
                fixture.Buffered.DisposeCount.Should().Be(0);
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                return new SpeechRecognitionResult("команда", 0.9, true);
            });
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_CancellationDuringResolverDisposesSessionOnce()
    {
        var fixture = PipelineFixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Commands.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<Action<VoiceCommandExecutionProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                return default!;
            });
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RunSingleCycleAsync_CancellationAtCooldownDisposesSessionOnce()
    {
        var fixture = PipelineFixture.Create(cooldown: TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource();
        fixture.State.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.State == VoiceAssistantState.Cooldown)
            {
                cancellation.Cancel();
            }
        };

        var run = fixture.Coordinator.RunSingleCycleAsync(cancellation.Token);

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        fixture.Buffered.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RunSingleCycleAsync_OpenFailurePublishesSafeErrorWithoutDisposal()
    {
        var fixture = PipelineFixture.Create();
        fixture.BufferedCaptures.FailNext(new AudioCaptureException(
            AudioInputResultCode.InitializationFailed,
            "private native failure"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-capture-failed");
        fixture.Buffered.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task RunSingleCycleAsync_SessionDisposeFailurePublishesSafeGenericErrorAndClearsActiveFlag()
    {
        var fixture = PipelineFixture.Create();
        fixture.Buffered.DisposeFailure = new InvalidOperationException("private dispose detail");

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.Buffered.DisposeCount.Should().Be(1);
        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("voice-pipeline-failed");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("private");
        fixture.State.Snapshot.IsCaptureActive.Should().BeFalse();
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_DisconnectAndDisposeFailurePreservesDisconnectRecoveryCode()
    {
        var fixture = PipelineFixture.Create(successfulWakeCyclesBeforeBlock: 0);
        fixture.Buffered.DisposeFailure = new InvalidOperationException("private dispose detail");
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<WakeWordDetectionResult>(_ => throw new AudioCaptureException(
                AudioInputResultCode.Disconnected,
                "private endpoint detail"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.Buffered.DisposeCount.Should().Be(1);
        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-disconnected");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("private");
        fixture.State.Snapshot.IsCaptureActive.Should().BeFalse();
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_BufferOverrunAndDisposeFailurePreservesOverrunCode()
    {
        var fixture = PipelineFixture.Create();
        fixture.Buffered.DisposeFailure = new InvalidOperationException("private dispose detail");
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns<VoiceActivityResult>(_ => throw new AudioCaptureException(
                AudioInputResultCode.BufferOverrun,
                "private offset 123456"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.Buffered.DisposeCount.Should().Be(1);
        fixture.State.Snapshot.ErrorCode.Should().Be("voice-audio-overrun");
        fixture.State.Snapshot.SafeMessage.Should().Be(
            "Не удалось сохранить начало команды. Повторите команду.");
        fixture.State.Snapshot.IsCaptureActive.Should().BeFalse();
    }

    [Fact]
    public async Task RunSingleCycleAsync_BufferedFormatMismatchRejectsBeforeOpeningCursor()
    {
        var fixture = PipelineFixture.Create();
        fixture.Buffered.Format = new AudioFormat(48_000, 2, 32, true);

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-format-unsupported");
        fixture.Buffered.OpenedOffsets.Should().BeEmpty();
        await fixture.Commands.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunSingleCycleAsync_LateProgressAfterCaptureFailureIsIgnored()
    {
        var fixture = PipelineFixture.Create();
        Action<VoiceActivityProgress>? lateProgress = null;
        var history = new List<VoiceAssistantState>();
        fixture.State.SnapshotChanged += (_, snapshot) => history.Add(snapshot.State);
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns<VoiceActivityResult>(call =>
            {
                lateProgress = call.ArgAt<Action<VoiceActivityProgress>>(3);
                throw new AudioCaptureException(AudioInputResultCode.Disconnected, "disconnected");
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);
        lateProgress.Should().NotBeNull();
        lateProgress!(new VoiceActivityProgress(24_320, 0.75, 0.99));

        history.Should().NotContain(VoiceAssistantState.DetectingSpeechEnd);
        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.LastNoiseFloorRms.Should().BeNull();
        fixture.State.Snapshot.LastPeakRms.Should().BeNull();
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_LateProgressAfterSuccessfulCaptureIsIgnored()
    {
        var fixture = PipelineFixture.Create();
        Action<VoiceActivityProgress>? lateProgress = null;
        var history = new List<VoiceAssistantState>();
        fixture.State.SnapshotChanged += (_, snapshot) => history.Add(snapshot.State);
        fixture.VoiceActivity.CaptureAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<AmbientNoiseSnapshot>(),
                Arg.Any<VoiceActivityOptions>(),
                Arg.Any<Action<VoiceActivityProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cursor = (TestVoiceAudioCursor)call.ArgAt<IVoiceAudioCursor>(0);
                lateProgress = call.ArgAt<Action<VoiceActivityProgress>>(3);
                lateProgress(new VoiceActivityProgress(cursor.StartSampleOffset + 320, 0.01, 0.25));
                return SuccessfulActivity(cursor.StartSampleOffset);
            });

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);
        var stateAfterReturn = fixture.State.Snapshot;
        lateProgress.Should().NotBeNull();
        lateProgress!(new VoiceActivityProgress(99_999, 0.75, 0.99));

        fixture.State.Snapshot.Should().BeSameAs(stateAfterReturn);
        history.Count(state => state == VoiceAssistantState.DetectingSpeechEnd).Should().Be(1);
    }

    [Fact]
    public async Task RunSingleCycleAsync_PublishesOnlySafeInMemoryDiagnostics()
    {
        var fixture = PipelineFixture.Create();

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.LastCapturedCommandDuration.Should().Be(TimeSpan.FromSeconds(1));
        fixture.State.Snapshot.LastNoiseFloorRms.Should().Be(0.01);
        fixture.State.Snapshot.LastPeakRms.Should().Be(0.25);
        typeof(VoicePipelineSnapshot).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("SampleOffset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunSingleCycleAsync_SpeechRecognitionFailurePreservesDiagnosticTextWithoutDispatch()
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

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.ErrorCode.Should().Be("speech-confidence-low");
        fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай тише");
        fixture.State.Snapshot.LastRecognitionConfidence.Should().Be(0.42);
        await fixture.Commands.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_TypedWakeTimingFailurePublishesSafeCodeWithoutDispatch()
    {
        var fixture = PipelineFixture.Create();
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<WakeWordDetectionResult>(_ => throw new WakeWordDetectionException(
                WakeWordDetectionFailureCode.InvalidTiming,
                "private offset 123456"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.WaitingForWakeWord);
        fixture.State.Snapshot.ErrorCode.Should().Be("voice-wake-timing-invalid");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("123456");
        await fixture.Commands.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunSingleCycleAsync_SelectedMicrophoneUnavailableNeverOpensCapture()
    {
        var fixture = PipelineFixture.Create();
        fixture.Devices.ResolveAsync("bt-mic", Arg.Any<CancellationToken>()).Returns(
            new AudioInputResolution(
                AudioInputResultCode.SelectedDeviceUnavailable,
                null,
                "private device detail"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-unavailable");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("private");
        fixture.BufferedCaptures.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task RunSingleCycleAsync_MissingExactEndpointNeverFallsBackToDefault()
    {
        var fixture = PipelineFixture.Create(endpointId: null);

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-unavailable");
        await fixture.Devices.DidNotReceiveWithAnyArgs().ResolveAsync(default, default);
        fixture.BufferedCaptures.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task RunSingleCycleAsync_ResolvedDifferentEndpointNeverFallsBack()
    {
        var fixture = PipelineFixture.Create();
        fixture.Devices.ResolveAsync("bt-mic", Arg.Any<CancellationToken>()).Returns(
            new AudioInputResolution(
                AudioInputResultCode.Success,
                new AudioInputDevice("default-mic", "Default microphone", true, true)));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("microphone-unavailable");
        fixture.BufferedCaptures.OpenCount.Should().Be(0);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_MissingCommandModelPublishesSafeModelFailure()
    {
        var fixture = PipelineFixture.Create();
        fixture.Models.GetActiveAsync(
                VoiceModelProvider.CommandWhisper,
                Arg.Any<CancellationToken>())
            .Returns((InstalledVoiceModel?)null);

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("model-unavailable");
        fixture.BufferedCaptures.OpenCount.Should().Be(0);
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_RuntimeProviderFailurePublishesSafeModelFailure()
    {
        var fixture = PipelineFixture.Create();
        fixture.RuntimeProviders.Create(
                Arg.Any<InstalledVoiceModel>(),
                Arg.Any<InstalledVoiceModel>())
            .Returns<VoiceRuntimeProviders>(_ => throw new InvalidOperationException(
                "C:\\Users\\Person\\private-model"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.State.Should().Be(VoiceAssistantState.Error);
        fixture.State.Snapshot.ErrorCode.Should().Be("model-unavailable");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("Person");
        fixture.BufferedCaptures.OpenCount.Should().Be(0);
    }

    [Fact]
    public async Task RunSingleCycleAsync_ResolvesDispatchesSignalsAndPublishesSafeOutcome()
    {
        var fixture = PipelineFixture.Create();

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай громче");
        fixture.State.Snapshot.LastResolvedCommandId.Should().Be("audio.change-volume");
        fixture.State.Snapshot.LastIntentStatus.Should().Be(IntentResolutionStatus.Resolved);
        fixture.State.Snapshot.LastExecutionStatus.Should().Be(CommandExecutionStatus.Succeeded);
        fixture.State.Snapshot.SafeMessage.Should().Be("Команда выполнена.");
        await fixture.Signals.Received(1)
            .PlayAsync(VoiceSignal.Success, Arg.Any<CancellationToken>());
    }

    [Theory(Timeout = 5_000)]
    [InlineData(IntentResolutionStatus.NotFound, "voice-command-not-found")]
    [InlineData(IntentResolutionStatus.Ambiguous, "voice-command-ambiguous")]
    public async Task RunSingleCycleAsync_UnresolvedCommandSignalsFailureWithoutDispatchState(
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

        fixture.State.Snapshot.LastIntentStatus.Should().Be(status);
        fixture.State.Snapshot.LastExecutionStatus.Should().BeNull();
        fixture.State.Snapshot.ErrorCode.Should().Be(expectedCode);
        history.Should().Contain(VoiceAssistantState.ResolvingCommand);
        history.Should().NotContain(VoiceAssistantState.ExecutingCommand);
        await fixture.Signals.Received(1)
            .PlayAsync(VoiceSignal.Failure, Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 5_000)]
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
                    new IntentResolutionResult(IntentResolutionStatus.Resolved, request, 0.93),
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
        await fixture.Signals.Received(1)
            .PlayAsync(VoiceSignal.Failure, Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_SuccessSignalFailureDoesNotDiscardOutcome()
    {
        var fixture = PipelineFixture.Create();
        fixture.Signals.PlayAsync(VoiceSignal.Success, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("private signal detail"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.LastRecognizedText.Should().Be("сделай громче");
        fixture.State.Snapshot.LastExecutionStatus.Should().Be(CommandExecutionStatus.Succeeded);
        fixture.State.Snapshot.ErrorCode.Should().BeNull();
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_FailureSignalFailureDoesNotReplaceProviderCode()
    {
        var fixture = PipelineFixture.Create();
        fixture.Wake.WaitForDetectionAsync(
                Arg.Any<IVoiceAudioCursor>(),
                Arg.Any<WakeWordOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<WakeWordDetectionResult>(_ => throw new InvalidOperationException(
                "private provider detail"));
        fixture.Signals.PlayAsync(VoiceSignal.Failure, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("private signal detail"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        fixture.State.Snapshot.ErrorCode.Should().Be("voice-pipeline-failed");
        fixture.State.Snapshot.SafeMessage.Should().NotContain("private");
    }

    [Fact(Timeout = 5_000)]
    public async Task RunSingleCycleAsync_LogsExcludeEndpointTextOffsetsAndRmsValues()
    {
        var fixture = PipelineFixture.Create();
        fixture.Speech.RecognizeAsync(
                Arg.Any<CapturedCommandAudio>(),
                Arg.Any<SpeechRecognitionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SpeechRecognitionResult("секретная команда", 0.987654, true));
        fixture.Commands.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<Action<VoiceCommandExecutionProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns<VoiceCommandExecutionResult>(_ => throw new InvalidOperationException(
                "bt-private offset=123456 rms=0.987654 секретная команда"));

        await fixture.Coordinator.RunSingleCycleAsync(CancellationToken.None);

        var loggedData = fixture.Logger.Entries
            .SelectMany(entry => entry.Arguments.Prepend(entry.Template))
            .Select(value => value?.ToString() ?? string.Empty)
            .ToArray();
        loggedData.Should().NotContain(value => value.Contains("bt-private", StringComparison.Ordinal));
        loggedData.Should().NotContain(value => value.Contains("секретная команда", StringComparison.Ordinal));
        loggedData.Should().NotContain(value => value.Contains("123456", StringComparison.Ordinal));
        loggedData.Should().NotContain(value => value.Contains("0.987654", StringComparison.Ordinal));
    }

    private static VoiceActivityResult SuccessfulActivity(long commandStartSampleOffset = 24_000)
    {
        var audio = new CapturedCommandAudio(
            new byte[32_000],
            AudioFormat.Pcm16KhzMono,
            TimeSpan.FromSeconds(1));
        return new VoiceActivityResult(
            true,
            TimeSpan.FromSeconds(1),
            audio,
            new VoiceActivityDiagnostics(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                commandStartSampleOffset + 320,
                commandStartSampleOffset + 15_680,
                0.01,
                0.25));
    }

    private static VoiceActivityResult NoSpeechActivity() => new(
        false,
        TimeSpan.FromSeconds(4),
        null,
        new VoiceActivityDiagnostics(
            TimeSpan.FromSeconds(4),
            TimeSpan.Zero,
            null,
            null,
            0.01,
            0.02));

    private static WakeWordDetectionResult ValidDetection(long cursorStart = 0) => new(
        "альфа",
        0.93,
        cursorStart + 16_000,
        cursorStart + 24_000,
        cursorStart + 25_600);

    private static async Task ConsumeAvailableFramesAsync(
        IVoiceAudioCursor cursor,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in cursor.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
        }
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

    private static async IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> FailingDeviceSnapshots(
        Exception failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IReadOnlyList<AudioInputDevice>> WaitingDeviceSnapshots(
        TaskCompletionSource started,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private sealed class PipelineFixture
    {
        private PipelineFixture()
        {
        }

        public required VoicePipelineCoordinator Coordinator { get; init; }

        public required VoicePipelineStateStore State { get; init; }

        public required IVoiceSettingsRepository Settings { get; init; }

        public required VoiceSettings SettingsValue { get; init; }

        public required IVoiceModelStore Models { get; init; }

        public required IVoiceRuntimeProviderFactory RuntimeProviders { get; init; }

        public required IAudioInputDeviceService Devices { get; init; }

        public required TestBufferedCaptureFactory BufferedCaptures { get; init; }

        public required TestBufferedCaptureSession Buffered { get; init; }

        public required IWakeWordProvider Wake { get; init; }

        public required ISpeechToTextProvider Speech { get; init; }

        public required IVoiceActivityDetector VoiceActivity { get; init; }

        public required IVoiceSignalService Signals { get; init; }

        public required IVoiceCommandExecutionService Commands { get; init; }

        public required TestLogger<VoicePipelineCoordinator> Logger { get; init; }

        public required TaskCompletionSource WakeBlocked { get; init; }

        public static PipelineFixture Create(
            int successfulWakeCyclesBeforeBlock = 1,
            TimeSpan? cooldown = null,
            long initialLiveEdge = 0,
            string? endpointId = "bt-mic")
        {
            var settingsValue = VoiceSettings.Default with
            {
                IsEnabled = true,
                MicrophoneEndpointId = endpointId,
                MicrophoneFriendlyName = "Bluetooth microphone",
                Cooldown = cooldown ?? TimeSpan.Zero,
            };
            var settings = Substitute.For<IVoiceSettingsRepository>();
            settings.GetAsync(Arg.Any<CancellationToken>()).Returns(settingsValue);

            var wakeModel = Model(
                VoiceModelProvider.WakeVosk,
                "wake-ru",
                "0.22",
                "WakeVosk/wake-ru/0.22");
            var commandModel = Model(
                VoiceModelProvider.CommandWhisper,
                "whisper-base",
                "openai-base",
                "CommandWhisper/whisper-base/openai-base/ggml-base.bin");
            var models = Substitute.For<IVoiceModelStore>();
            models.GetActiveAsync(VoiceModelProvider.WakeVosk, Arg.Any<CancellationToken>())
                .Returns(wakeModel);
            models.GetActiveAsync(VoiceModelProvider.CommandWhisper, Arg.Any<CancellationToken>())
                .Returns(commandModel);

            var devices = Substitute.For<IAudioInputDeviceService>();
            if (endpointId is not null)
            {
                devices.ResolveAsync(endpointId, Arg.Any<CancellationToken>()).Returns(
                    new AudioInputResolution(
                        AudioInputResultCode.Success,
                        new AudioInputDevice(endpointId, "Bluetooth microphone", false, true)));
            }

            var buffered = new TestBufferedCaptureSession(endpointId ?? "bt-mic")
            {
                LatestSampleOffset = initialLiveEdge,
                NoiseSnapshot = new AmbientNoiseSnapshot(0.01, TimeSpan.FromSeconds(3), 150),
            };
            var captures = new TestBufferedCaptureFactory(buffered);
            var wakeBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wake = Substitute.For<IWakeWordProvider>();
            wake.ProviderId.Returns("vosk");
            var wakeCount = 0;
            wake.WaitForDetectionAsync(
                    Arg.Any<IVoiceAudioCursor>(),
                    Arg.Any<WakeWordOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var cursor = call.ArgAt<IVoiceAudioCursor>(0);
                    if (Interlocked.Increment(ref wakeCount) > successfulWakeCyclesBeforeBlock)
                    {
                        wakeBlocked.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                    }

                    await ConsumeAvailableFramesAsync(cursor, call.ArgAt<CancellationToken>(2));
                    return ValidDetection(cursor.StartSampleOffset);
                });

            var speech = Substitute.For<ISpeechToTextProvider>();
            speech.ProviderId.Returns("whisper");
            speech.RecognizeAsync(
                    Arg.Any<CapturedCommandAudio>(),
                    Arg.Any<SpeechRecognitionOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(new SpeechRecognitionResult("сделай громче", 0.91, true));
            var runtimeProviders = Substitute.For<IVoiceRuntimeProviderFactory>();
            runtimeProviders.Create(wakeModel, commandModel)
                .Returns(new VoiceRuntimeProviders(wake, speech));

            var vad = Substitute.For<IVoiceActivityDetector>();
            vad.CaptureAsync(
                    Arg.Any<IVoiceAudioCursor>(),
                    Arg.Any<AmbientNoiseSnapshot>(),
                    Arg.Any<VoiceActivityOptions>(),
                    Arg.Any<Action<VoiceActivityProgress>>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var cursor = (TestVoiceAudioCursor)call.ArgAt<IVoiceAudioCursor>(0);
                    call.ArgAt<Action<VoiceActivityProgress>>(3)(
                        new VoiceActivityProgress(cursor.StartSampleOffset + 320, 0.01, 0.25));
                    cursor.Owner.LatestSampleOffset = cursor.StartSampleOffset + 16_000;
                    return SuccessfulActivity(cursor.StartSampleOffset);
                });
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
            var logger = new TestLogger<VoicePipelineCoordinator>();
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
                TimeProvider.System,
                logger);

            return new PipelineFixture
            {
                Coordinator = coordinator,
                State = state,
                Settings = settings,
                SettingsValue = settingsValue,
                Models = models,
                RuntimeProviders = runtimeProviders,
                Devices = devices,
                BufferedCaptures = captures,
                Buffered = buffered,
                Wake = wake,
                Speech = speech,
                VoiceActivity = vad,
                Signals = signals,
                Commands = commands,
                Logger = logger,
                WakeBlocked = wakeBlocked,
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

    private sealed class TestBufferedCaptureFactory : IBufferedVoiceCaptureSessionFactory
    {
        private readonly object _sync = new();
        private readonly Queue<object> _results = [];
        private readonly Dictionary<int, TaskCompletionSource> _openWaiters = [];

        public TestBufferedCaptureFactory(TestBufferedCaptureSession initial) => Enqueue(initial);

        public int OpenCount { get; private set; }

        public List<string> OpenedEndpoints { get; } = [];

        public void Enqueue(TestBufferedCaptureSession session)
        {
            lock (_sync)
            {
                _results.Enqueue(session);
            }
        }

        public void FailNext(Exception exception)
        {
            lock (_sync)
            {
                _results.Clear();
                _results.Enqueue(exception);
            }
        }

        public void FailNext(
            Func<string, CancellationToken, Task<IBufferedVoiceCaptureSession>> open)
        {
            lock (_sync)
            {
                _results.Clear();
                _results.Enqueue(open);
            }
        }

        public Task WaitForOpenCountAsync(int count)
        {
            lock (_sync)
            {
                if (OpenCount >= count)
                {
                    return Task.CompletedTask;
                }

                if (!_openWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _openWaiters.Add(count, waiter);
                }

                return waiter.Task;
            }
        }

        public Task<IBufferedVoiceCaptureSession> OpenAsync(
            string endpointId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                OpenCount++;
                OpenedEndpoints.Add(endpointId);
                foreach (var waiter in _openWaiters.Where(pair => pair.Key <= OpenCount).ToArray())
                {
                    waiter.Value.TrySetResult();
                    _openWaiters.Remove(waiter.Key);
                }

                if (_results.Count == 0)
                {
                    throw new InvalidOperationException("No buffered capture result was configured.");
                }

                var result = _results.Dequeue();
                if (result is Exception exception)
                {
                    return Task.FromException<IBufferedVoiceCaptureSession>(exception);
                }

                if (result is Func<string, CancellationToken, Task<IBufferedVoiceCaptureSession>> open)
                {
                    return open(endpointId, cancellationToken);
                }

                return Task.FromResult<IBufferedVoiceCaptureSession>(
                    (TestBufferedCaptureSession)result);
            }
        }
    }

    private sealed class TestBufferedCaptureSession(string endpointId) : IBufferedVoiceCaptureSession
    {
        private readonly List<TestVoiceAudioCursor> _cursors = [];
        private readonly Queue<long> _cursorReadEnds = [];
        private bool _disposed;

        public string EndpointId { get; } = endpointId;

        public AudioFormat Format { get; set; } = AudioFormat.Pcm16KhzMono;

        public long EarliestSampleOffset { get; set; }

        public long LatestSampleOffset { get; set; }

        public AmbientNoiseSnapshot NoiseSnapshot { get; set; } = AmbientNoiseSnapshot.Empty;

        public List<long> OpenedOffsets { get; } = [];

        public int DisposeCount { get; private set; }

        public Exception? DisposeFailure { get; set; }

        public Exception? TerminalFailure { get; set; }

        public void QueueCursorReadEnd(long endSampleOffset) => _cursorReadEnds.Enqueue(endSampleOffset);

        public IVoiceAudioCursor OpenCursor(long startSampleOffset)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (startSampleOffset < EarliestSampleOffset || startSampleOffset > LatestSampleOffset)
            {
                throw new AudioCaptureException(
                    AudioInputResultCode.BufferOverrun,
                    "The requested test cursor range is unavailable.");
            }

            OpenedOffsets.Add(startSampleOffset);
            var cursor = new TestVoiceAudioCursor(
                this,
                startSampleOffset,
                _cursorReadEnds.Count > 0 ? _cursorReadEnds.Dequeue() : null);
            _cursors.Add(cursor);
            return cursor;
        }

        public TestVoiceAudioCursor CursorAt(long startSampleOffset) =>
            _cursors.Single(cursor => cursor.StartSampleOffset == startSampleOffset);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposed = true;
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class TestVoiceAudioCursor(
        TestBufferedCaptureSession owner,
        long startSampleOffset,
        long? configuredReadEndSampleOffset) : IVoiceAudioCursor
    {
        public TestBufferedCaptureSession Owner { get; } = owner;

        public AudioFormat Format => Owner.Format;

        public long StartSampleOffset { get; } = startSampleOffset;

        public int DisposeCount { get; private set; }

        public async IAsyncEnumerable<SequencedAudioFrame> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            if (Owner.TerminalFailure is not null)
            {
                throw Owner.TerminalFailure;
            }

            var endSampleOffset = configuredReadEndSampleOffset
                ?? checked(StartSampleOffset + 25_600);
            if (endSampleOffset < StartSampleOffset)
            {
                throw new AudioCaptureException(
                    AudioInputResultCode.BufferOverrun,
                    "The configured test cursor range is invalid.");
            }

            Owner.LatestSampleOffset = Math.Max(Owner.LatestSampleOffset, endSampleOffset);
            var sampleCount = checked((int)(endSampleOffset - StartSampleOffset));
            await Task.Yield();
            yield return new SequencedAudioFrame(
                new byte[checked(sampleCount * sizeof(short))],
                TimeSpan.FromSeconds(sampleCount / 16_000d),
                StartSampleOffset,
                endSampleOffset);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly object _sync = new();

        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var template = values?
                .FirstOrDefault(value => string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                .Value as string
                ?? state?.ToString()
                ?? string.Empty;
            var arguments = values?
                .Where(value => !string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                .Select(value => value.Value)
                .ToArray()
                ?? [];
            lock (_sync)
            {
                Entries.Add(new LogEntry(logLevel, template, arguments));
            }
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Template,
        IReadOnlyList<object?> Arguments);
}
