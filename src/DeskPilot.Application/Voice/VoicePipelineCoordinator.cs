using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;
using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPilot.Application.Voice;

/// <summary>Creates provider adapters for the currently active immutable models.</summary>
public interface IVoiceRuntimeProviderFactory
{
    /// <summary>Creates local providers without exposing native projects or absolute paths to Application.</summary>
    VoiceRuntimeProviders Create(InstalledVoiceModel wakeModel, InstalledVoiceModel commandModel);
}

/// <summary>Contains local providers bound to one pair of active model versions.</summary>
public sealed record VoiceRuntimeProviders(
    IWakeWordProvider WakeWord,
    ISpeechToTextProvider SpeechToText);

/// <summary>Controls the lifetime of the application-owned voice pipeline.</summary>
public interface IVoicePipelineController
{
    /// <summary>Starts the pipeline when it is not already running.</summary>
    Task EnableAsync(CancellationToken cancellationToken);

    /// <summary>Stops the pipeline and releases active native resources.</summary>
    Task DisableAsync(CancellationToken cancellationToken);

    /// <summary>Rebuilds the pipeline after an explicit settings change.</summary>
    Task RestartAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one continuous capture session across wake and command cycles.</summary>
public sealed class VoicePipelineCoordinator : IVoicePipelineController
{
    private const string InvalidWakeTimingCode = "voice-wake-timing-invalid";
    private const string InvalidWakeTimingMessage =
        "Не удалось определить границы ключевой фразы. Повторите попытку.";
    private readonly IVoiceSettingsRepository _settings;
    private readonly IVoiceModelStore _models;
    private readonly IVoiceRuntimeProviderFactory _runtimeProviders;
    private readonly IAudioInputDeviceService _devices;
    private readonly IBufferedVoiceCaptureSessionFactory _captures;
    private readonly IVoiceActivityDetector _voiceActivity;
    private readonly IVoiceSignalService _signals;
    private readonly IVoiceCommandExecutionService _commands;
    private readonly VoicePipelineStateStore _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VoicePipelineCoordinator> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    /// <summary>Creates the single-owner voice pipeline.</summary>
    public VoicePipelineCoordinator(
        IVoiceSettingsRepository settings,
        IVoiceModelStore models,
        IVoiceRuntimeProviderFactory runtimeProviders,
        IAudioInputDeviceService devices,
        IBufferedVoiceCaptureSessionFactory captures,
        IVoiceActivityDetector voiceActivity,
        IVoiceSignalService signals,
        IVoiceCommandExecutionService commands,
        VoicePipelineStateStore state,
        TimeProvider timeProvider,
        ILogger<VoicePipelineCoordinator>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _runtimeProviders = runtimeProviders ?? throw new ArgumentNullException(nameof(runtimeProviders));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _captures = captures ?? throw new ArgumentNullException(nameof(captures));
        _voiceActivity = voiceActivity ?? throw new ArgumentNullException(nameof(voiceActivity));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<VoicePipelineCoordinator>.Instance;
    }

    /// <summary>Starts the owned pipeline loop when it is not already running.</summary>
    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runTask is { IsCompleted: false })
            {
                return;
            }

            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            _runTask = StartRunLoop(_runCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Stops the owned pipeline loop and disposes its active capture session.</summary>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runTask = _runTask;
            var runCancellation = _runCancellation;
            runCancellation?.Cancel();
            if (runTask is not null)
            {
                await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (ReferenceEquals(_runTask, runTask))
            {
                _runTask = null;
                _runCancellation = null;
                runCancellation?.Dispose();
                PublishDisabled();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await DisableAsync(cancellationToken).ConfigureAwait(false);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs exactly one wake-to-command cycle in one owned buffered session.</summary>
    public Task RunSingleCycleAsync(CancellationToken cancellationToken) =>
        ExecuteOwnedSessionSafelyAsync(cycleLimit: 1, cancellationToken);

    internal async Task<IAsyncDisposable> EnterIdleAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleHeld = true;
        var pipelineHeld = false;
        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var runTask = _runTask;
            var runCancellation = _runCancellation;
            var shouldResume = settings.IsEnabled && runTask is { IsCompleted: false };
            _runTask = null;
            _runCancellation = null;
            runCancellation?.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runCancellation?.IsCancellationRequested == true)
                {
                }
            }

            runCancellation?.Dispose();
            await _pipelineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            pipelineHeld = true;
            PublishDisabled();
            return new IdleLease(this, shouldResume);
        }
        catch
        {
            if (pipelineHeld)
            {
                _pipelineGate.Release();
            }

            if (lifecycleHeld)
            {
                _lifecycleGate.Release();
                lifecycleHeld = false;
            }

            throw;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ExecuteOwnedSessionSafelyAsync(cycleLimit: null, cancellationToken)
                    .ConfigureAwait(false);

                var snapshot = _state.Snapshot;
                if (snapshot.State != VoiceAssistantState.Error)
                {
                    continue;
                }

                var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
                var recoveryEndpoint = snapshot.ErrorCode == "microphone-disconnected"
                    ? snapshot.MicrophoneEndpointId
                    : settings.MicrophoneEndpointId;
                if (snapshot.ErrorCode is "microphone-unavailable" or "microphone-disconnected"
                    && !string.IsNullOrWhiteSpace(recoveryEndpoint))
                {
                    await WaitForMicrophoneAsync(recoveryEndpoint, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(settings.Cooldown, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteOwnedSessionSafelyAsync(
        int? cycleLimit,
        CancellationToken cancellationToken)
    {
        await _pipelineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await RunOwnedSessionCoreAsync(cycleLimit, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (VoicePipelineResourceException exception)
            {
                _logger.LogWarning(
                    "Voice pipeline resource failure {ErrorCode}.",
                    exception.Code);
                PublishError(exception.Code, exception.SafeMessage);
            }
            catch (AudioCaptureException exception)
            {
                _logger.LogWarning(
                    "Voice capture failure {ResultCode}.",
                    exception.Code);
                PublishError(
                    ToCaptureErrorCode(exception.Code),
                    ToCaptureSafeMessage(exception.Code));
            }
            catch (SpeechRecognitionException exception)
            {
                _logger.LogWarning(
                    "Voice recognition failure {FailureCode}.",
                    exception.Code);
                await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken)
                    .ConfigureAwait(false);
                Publish(_state.Snapshot with
                {
                    State = VoiceAssistantState.Error,
                    LastRecognizedText = exception.RecognizedText,
                    LastRecognitionConfidence = exception.RecognitionConfidence,
                    LastResolvedCommandId = null,
                    LastIntentStatus = null,
                    LastIntentConfidence = null,
                    LastExecutionStatus = null,
                    ErrorCode = ToSpeechErrorCode(exception.Code),
                    SafeMessage = ToSpeechSafeMessage(exception.Code),
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Voice pipeline failed with {ExceptionType}.",
                    exception.GetType().Name);
                await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken)
                    .ConfigureAwait(false);
                PublishError(
                    "voice-pipeline-failed",
                    "Голосовой контур временно недоступен. Повторите попытку.");
            }
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private async Task RunOwnedSessionCoreAsync(
        int? cycleLimit,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.MicrophoneEndpointId))
        {
            throw MicrophoneUnavailable();
        }

        var wakeModel = await _models
            .GetActiveAsync(VoiceModelProvider.WakeVosk, cancellationToken)
            .ConfigureAwait(false)
            ?? throw ModelUnavailable();
        var commandModel = await _models
            .GetActiveAsync(VoiceModelProvider.CommandWhisper, cancellationToken)
            .ConfigureAwait(false)
            ?? throw ModelUnavailable();
        var resolution = await _devices
            .ResolveAsync(settings.MicrophoneEndpointId, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Code != AudioInputResultCode.Success
            || resolution.Device is null
            || !string.Equals(
                resolution.Device.EndpointId,
                settings.MicrophoneEndpointId,
                StringComparison.Ordinal))
        {
            throw MicrophoneUnavailable();
        }

        VoiceRuntimeProviders providers;
        try
        {
            providers = _runtimeProviders.Create(wakeModel, commandModel);
        }
        catch (InvalidOperationException)
        {
            throw ModelUnavailable();
        }

        var capture = await _captures
            .OpenAsync(settings.MicrophoneEndpointId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (capture.ConfigureAwait(false))
            {
                ValidateCapture(capture, settings.MicrophoneEndpointId);
                var completedCycles = 0;
                while (!cancellationToken.IsCancellationRequested
                    && (!cycleLimit.HasValue || completedCycles < cycleLimit.Value))
                {
                    Publish(NewCycleSnapshot(capture.EndpointId, wakeModel, commandModel));
                    await RunCycleAsync(
                            capture,
                            settings,
                            providers,
                            cancellationToken)
                        .ConfigureAwait(false);
                    completedCycles++;
                }
            }
        }
        finally
        {
            Publish(_state.Snapshot with { IsCaptureActive = false });
        }
    }

    private async Task RunCycleAsync(
        IBufferedVoiceCaptureSession capture,
        VoiceSettings settings,
        VoiceRuntimeProviders providers,
        CancellationToken cancellationToken)
    {
        var wakeStart = capture.LatestSampleOffset;
        WakeWordDetectionResult detection;
        AmbientNoiseSnapshot ambientNoise;
        await using (var wakeCursor = capture.OpenCursor(wakeStart))
        {
            detection = await providers.WakeWord
                .WaitForDetectionAsync(
                    wakeCursor,
                    new WakeWordOptions(settings.WakePhrase, settings.WakeConfidence),
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateWakeDetection(
                detection,
                settings,
                wakeCursor.StartSampleOffset,
                capture.LatestSampleOffset);
            ambientNoise = capture.NoiseSnapshot;
        }

        Publish(_state.Snapshot with
        {
            State = VoiceAssistantState.WakeWordDetected,
            LastWakePhrase = detection.Phrase,
            LastWakeConfidence = detection.Confidence,
            ErrorCode = null,
            SafeMessage = null,
        });
        Publish(_state.Snapshot with { State = VoiceAssistantState.ListeningForCommand });

        VoiceActivityResult activity;
        var progressSync = new object();
        var acceptsProgress = true;
        var progressPublished = false;
        await using (var commandCursor = capture.OpenCursor(detection.WakeEndSampleOffset))
        {
            void OnSpeechConfirmed(VoiceActivityProgress progress)
            {
                lock (progressSync)
                {
                    if (!acceptsProgress || progressPublished)
                    {
                        return;
                    }

                    ValidateProgress(progress, commandCursor.StartSampleOffset);
                    progressPublished = true;
                    Publish(_state.Snapshot with
                    {
                        State = VoiceAssistantState.DetectingSpeechEnd,
                        LastNoiseFloorRms = progress.NoiseFloorRms,
                        LastPeakRms = progress.PeakRms,
                    });
                }
            }

            try
            {
                activity = await _voiceActivity
                    .CaptureAsync(
                        commandCursor,
                        ambientNoise,
                        VoiceActivityOptions.Default with
                        {
                            Sensitivity = settings.VoiceActivitySensitivity,
                        },
                        OnSpeechConfirmed,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (progressSync)
                {
                    acceptsProgress = false;
                }
            }
        }

        ValidateActivity(activity, progressPublished);
        if (!activity.SpeechDetected || activity.Audio is null)
        {
            Publish(_state.Snapshot with
            {
                LastRecognizedText = null,
                LastRecognitionConfidence = null,
                LastResolvedCommandId = null,
                LastIntentStatus = null,
                LastIntentConfidence = null,
                LastExecutionStatus = null,
                LastCapturedCommandDuration = activity.Diagnostics.CapturedDuration,
                LastNoiseFloorRms = activity.Diagnostics.NoiseFloorRms,
                LastPeakRms = activity.Diagnostics.PeakRms,
                ErrorCode = "speech-not-detected",
                SafeMessage = "Команда не распознана",
            });
            await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken)
                .ConfigureAwait(false);
            await PublishCooldownAsync(settings.Cooldown, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Publish(_state.Snapshot with
        {
            State = VoiceAssistantState.RecognizingCommand,
            LastCapturedCommandDuration = activity.Diagnostics.CapturedDuration,
            LastNoiseFloorRms = activity.Diagnostics.NoiseFloorRms,
            LastPeakRms = activity.Diagnostics.PeakRms,
        });
        var recognition = await providers.SpeechToText
            .RecognizeAsync(
                activity.Audio,
                new SpeechRecognitionOptions(settings.RecognitionLanguage),
                cancellationToken)
            .ConfigureAwait(false);
        Publish(_state.Snapshot with
        {
            State = VoiceAssistantState.ResolvingCommand,
            LastRecognizedText = recognition.Text,
            LastRecognitionConfidence = recognition.Confidence,
            ErrorCode = null,
            SafeMessage = null,
        });
        var commandResult = await _commands.ExecuteAsync(
            recognition.Text,
            progress => Publish(_state.Snapshot with
            {
                State = VoiceAssistantState.ExecutingCommand,
                LastResolvedCommandId = progress.CommandId.Value,
                LastIntentStatus = progress.IntentStatus,
                LastIntentConfidence = progress.Confidence,
                LastExecutionStatus = null,
                ErrorCode = null,
                SafeMessage = null,
            }),
            cancellationToken).ConfigureAwait(false);
        var outcome = ToCommandOutcome(commandResult);
        Publish(_state.Snapshot with
        {
            LastResolvedCommandId = commandResult.Resolution.Request?.CommandId.Value,
            LastIntentStatus = commandResult.Resolution.Status,
            LastIntentConfidence = commandResult.Resolution.Confidence,
            LastExecutionStatus = commandResult.Execution?.Status,
            ErrorCode = outcome.ErrorCode,
            SafeMessage = outcome.SafeMessage,
        });
        await PlaySignalSafelyAsync(outcome.Signal, cancellationToken).ConfigureAwait(false);
        activity = activity with { Audio = null };
        await PublishCooldownAsync(settings.Cooldown, cancellationToken).ConfigureAwait(false);
    }

    private VoicePipelineSnapshot NewCycleSnapshot(
        string endpointId,
        InstalledVoiceModel wakeModel,
        InstalledVoiceModel commandModel) => _state.Snapshot with
        {
            State = VoiceAssistantState.WaitingForWakeWord,
            MicrophoneEndpointId = endpointId,
            LastWakePhrase = null,
            LastWakeConfidence = null,
            LastRecognizedText = null,
            LastRecognitionConfidence = null,
            ErrorCode = null,
            SafeMessage = null,
            ActiveWakeModelVersion = wakeModel.Version,
            ActiveCommandModelVersion = commandModel.Version,
            LastResolvedCommandId = null,
            LastIntentStatus = null,
            LastIntentConfidence = null,
            LastExecutionStatus = null,
            IsCaptureActive = true,
            LastCapturedCommandDuration = null,
            LastNoiseFloorRms = null,
            LastPeakRms = null,
        };

    private async Task PublishCooldownAsync(TimeSpan cooldown, CancellationToken cancellationToken)
    {
        Publish(_state.Snapshot with { State = VoiceAssistantState.Cooldown });
        await Task.Delay(cooldown, _timeProvider, cancellationToken).ConfigureAwait(false);
        Publish(_state.Snapshot with { State = VoiceAssistantState.WaitingForWakeWord });
    }

    private async Task WaitForMicrophoneAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        await foreach (var snapshot in _devices.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            if (snapshot.Any(device => device.IsAvailable
                && string.Equals(device.EndpointId, endpointId, StringComparison.Ordinal)))
            {
                return;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask ExitIdleAsync(bool shouldResume)
    {
        try
        {
            if (shouldResume)
            {
                _runCancellation = new CancellationTokenSource();
                _runTask = StartRunLoop(_runCancellation.Token);
            }
        }
        finally
        {
            _pipelineGate.Release();
            _lifecycleGate.Release();
        }

        return ValueTask.CompletedTask;
    }

    private Task StartRunLoop(CancellationToken cancellationToken) =>
        Task.Run(() => RunLoopAsync(cancellationToken), CancellationToken.None);

    private async Task PlaySignalSafelyAsync(
        VoiceSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            await _signals.PlayAsync(signal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Voice signal {Signal} failed with {ExceptionType}.",
                signal,
                exception.GetType().Name);
        }
    }

    private void PublishDisabled() => Publish(_state.Snapshot with
    {
        State = VoiceAssistantState.Disabled,
        IsCaptureActive = false,
        LastCapturedCommandDuration = null,
        LastNoiseFloorRms = null,
        LastPeakRms = null,
    });

    private void Publish(VoicePipelineSnapshot snapshot) => _state.Publish(snapshot);

    private void PublishError(string code, string safeMessage) => Publish(_state.Snapshot with
    {
        State = VoiceAssistantState.Error,
        ErrorCode = code,
        SafeMessage = safeMessage,
    });

    private static void ValidateCapture(
        IBufferedVoiceCaptureSession capture,
        string endpointId)
    {
        if (capture.Format != AudioFormat.Pcm16KhzMono)
        {
            throw new AudioCaptureException(
                AudioInputResultCode.UnsupportedFormat,
                "Buffered voice capture requires mono 16 kHz PCM16 audio.");
        }

        if (!string.Equals(capture.EndpointId, endpointId, StringComparison.Ordinal))
        {
            throw MicrophoneUnavailable();
        }

        if (capture.EarliestSampleOffset < 0
            || capture.LatestSampleOffset < capture.EarliestSampleOffset)
        {
            throw new VoicePipelineResourceException(
                InvalidWakeTimingCode,
                InvalidWakeTimingMessage);
        }
    }

    private static void ValidateWakeDetection(
        WakeWordDetectionResult detection,
        VoiceSettings settings,
        long cursorStartSampleOffset,
        long latestSampleOffset)
    {
        if (detection is null
            || string.IsNullOrWhiteSpace(detection.Phrase)
            || !string.Equals(
                detection.Phrase.Trim(),
                settings.WakePhrase.Trim(),
                StringComparison.OrdinalIgnoreCase)
            || !double.IsFinite(detection.Confidence)
            || detection.Confidence < settings.WakeConfidence
            || detection.Confidence > 1
            || cursorStartSampleOffset < 0
            || detection.WakeStartSampleOffset < cursorStartSampleOffset
            || detection.WakeEndSampleOffset < detection.WakeStartSampleOffset
            || detection.DetectionSampleOffset < detection.WakeEndSampleOffset
            || detection.DetectionSampleOffset > latestSampleOffset)
        {
            throw new VoicePipelineResourceException(
                InvalidWakeTimingCode,
                InvalidWakeTimingMessage);
        }
    }

    private static void ValidateProgress(
        VoiceActivityProgress progress,
        long commandStartSampleOffset)
    {
        if (progress is null
            || progress.SpeechStartSampleOffset < commandStartSampleOffset
            || !IsNormalizedRms(progress.NoiseFloorRms)
            || !IsNormalizedRms(progress.PeakRms))
        {
            throw new VoicePipelineResourceException(
                "voice-activity-invalid",
                "Не удалось определить границы команды. Повторите команду.");
        }
    }

    private static void ValidateActivity(VoiceActivityResult activity, bool progressPublished)
    {
        if (activity is null
            || activity.Diagnostics is null
            || activity.Duration < TimeSpan.Zero
            || activity.Diagnostics.ObservedDuration < TimeSpan.Zero
            || activity.Diagnostics.CapturedDuration < TimeSpan.Zero
            || !IsNormalizedRms(activity.Diagnostics.NoiseFloorRms)
            || !IsNormalizedRms(activity.Diagnostics.PeakRms)
            || activity.SpeechDetected != (activity.Audio is not null)
            || activity.SpeechDetected != progressPublished)
        {
            throw new VoicePipelineResourceException(
                "voice-activity-invalid",
                "Не удалось определить границы команды. Повторите команду.");
        }
    }

    private static bool IsNormalizedRms(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static VoicePipelineResourceException MicrophoneUnavailable() => new(
        "microphone-unavailable",
        "Сохранённый Bluetooth- или USB-микрофон недоступен. Подключите это же устройство.");

    private static VoicePipelineResourceException ModelUnavailable() => new(
        "model-unavailable",
        "Встроенная модель распознавания недоступна. Восстановите модели.");

    private static string ToCaptureErrorCode(AudioInputResultCode code) => code switch
    {
        AudioInputResultCode.Disconnected => "microphone-disconnected",
        AudioInputResultCode.UnsupportedFormat => "microphone-format-unsupported",
        AudioInputResultCode.BufferOverrun => "voice-audio-overrun",
        _ => "microphone-capture-failed",
    };

    private static string ToCaptureSafeMessage(AudioInputResultCode code) => code switch
    {
        AudioInputResultCode.Disconnected =>
            "Микрофон отключён. Подключите это же устройство.",
        AudioInputResultCode.UnsupportedFormat =>
            "Формат микрофона не поддерживается.",
        AudioInputResultCode.BufferOverrun =>
            "Не удалось сохранить начало команды. Повторите команду.",
        _ => "Не удалось начать запись с выбранного микрофона.",
    };

    private static string ToSpeechErrorCode(SpeechRecognitionFailureCode code) => code switch
    {
        SpeechRecognitionFailureCode.ModelUnavailable => "model-unavailable",
        SpeechRecognitionFailureCode.NoText => "speech-not-recognized",
        SpeechRecognitionFailureCode.ConfidenceBelowThreshold => "speech-confidence-low",
        SpeechRecognitionFailureCode.UnsupportedFormat => "speech-format-unsupported",
        _ => "speech-provider-failed",
    };

    private static string ToSpeechSafeMessage(SpeechRecognitionFailureCode code) => code switch
    {
        SpeechRecognitionFailureCode.ModelUnavailable =>
            "Модель распознавания недоступна. Восстановите встроенную модель.",
        SpeechRecognitionFailureCode.NoText =>
            "Речь не распознана. Повторите команду.",
        SpeechRecognitionFailureCode.ConfidenceBelowThreshold =>
            "Команда распознана неуверенно. Повторите её.",
        SpeechRecognitionFailureCode.UnsupportedFormat =>
            "Формат записанного звука не поддерживается.",
        _ => "Локальное распознавание не выполнено. Повторите команду.",
    };

    private static CommandOutcome ToCommandOutcome(VoiceCommandExecutionResult result)
    {
        if (result.Resolution.Status == IntentResolutionStatus.NotFound)
        {
            return new CommandOutcome(
                VoiceSignal.Failure,
                "voice-command-not-found",
                "Команда не найдена. Повторите команду.");
        }

        if (result.Resolution.Status == IntentResolutionStatus.Ambiguous)
        {
            return new CommandOutcome(
                VoiceSignal.Failure,
                "voice-command-ambiguous",
                "Команда распознана неоднозначно. Сформулируйте её точнее.");
        }

        return result.Execution?.Status switch
        {
            CommandExecutionStatus.Succeeded => new CommandOutcome(
                VoiceSignal.Success,
                null,
                "Команда выполнена."),
            CommandExecutionStatus.NotFound => new CommandOutcome(
                VoiceSignal.Failure,
                result.Execution.ErrorCode ?? "command-not-found",
                "Команда временно недоступна."),
            CommandExecutionStatus.Rejected => new CommandOutcome(
                VoiceSignal.Failure,
                result.Execution.ErrorCode ?? "voice-command-rejected",
                "Команда отклонена текущим состоянием системы."),
            CommandExecutionStatus.Failed => new CommandOutcome(
                VoiceSignal.Failure,
                result.Execution.ErrorCode ?? "voice-command-failed",
                "Не удалось выполнить команду."),
            _ => new CommandOutcome(
                VoiceSignal.Failure,
                "voice-command-failed",
                "Не удалось выполнить команду."),
        };
    }

    private sealed class VoicePipelineResourceException(string code, string safeMessage) : Exception
    {
        public string Code { get; } = code;

        public string SafeMessage { get; } = safeMessage;
    }

    private sealed record CommandOutcome(
        VoiceSignal Signal,
        string? ErrorCode,
        string SafeMessage);

    private sealed class IdleLease(VoicePipelineCoordinator owner, bool shouldResume) : IAsyncDisposable
    {
        private VoicePipelineCoordinator? _owner = owner;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            return current is null
                ? ValueTask.CompletedTask
                : current.ExitIdleAsync(shouldResume);
        }
    }
}
