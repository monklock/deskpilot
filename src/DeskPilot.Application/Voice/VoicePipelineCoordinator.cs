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

/// <summary>Owns the cancellable Milestone 2 voice state machine.</summary>
public sealed class VoicePipelineCoordinator : IVoicePipelineController
{
    private readonly IVoiceSettingsRepository _settings;
    private readonly IVoiceModelStore _models;
    private readonly IVoiceRuntimeProviderFactory _runtimeProviders;
    private readonly IAudioInputDeviceService _devices;
    private readonly IAudioCaptureSessionFactory _captures;
    private readonly IVoiceActivityDetector _voiceActivity;
    private readonly IVoiceSignalService _signals;
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
        IAudioCaptureSessionFactory captures,
        IVoiceActivityDetector voiceActivity,
        IVoiceSignalService signals,
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
            _runTask = RunLoopAsync(_runCancellation.Token);
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
                Publish(VoiceAssistantState.Disabled);
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

    /// <summary>Runs exactly one wake-to-text cycle without resolving or dispatching a command.</summary>
    public async Task RunSingleCycleAsync(CancellationToken cancellationToken)
    {
        await _pipelineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await RunSingleCycleCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (VoicePipelineResourceException exception)
            {
                _logger.LogWarning("Voice pipeline resource failure {ErrorCode}.", exception.Code);
                PublishError(exception.Code, exception.SafeMessage);
            }
            catch (AudioCaptureException exception)
            {
                _logger.LogWarning("Voice capture failure {ResultCode}.", exception.Code);
                PublishError(ToCaptureErrorCode(exception.Code), ToCaptureSafeMessage(exception.Code));
            }
            catch (SpeechRecognitionException exception)
            {
                _logger.LogWarning("Voice recognition failure {FailureCode}.", exception.Code);
                await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken).ConfigureAwait(false);
                PublishError(ToSpeechErrorCode(exception.Code), ToSpeechSafeMessage(exception.Code));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError("Voice pipeline failed with {ExceptionType}.", exception.GetType().Name);
                await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken).ConfigureAwait(false);
                PublishError("voice-pipeline-failed", "Голосовой контур временно недоступен. Повторите попытку.");
            }
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

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
            Publish(VoiceAssistantState.Disabled);
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
                await RunSingleCycleAsync(cancellationToken).ConfigureAwait(false);
                if (_state.Snapshot.State == VoiceAssistantState.Error)
                {
                    var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
                    if (string.Equals(_state.Snapshot.ErrorCode, "microphone-unavailable", StringComparison.Ordinal)
                        || string.Equals(_state.Snapshot.ErrorCode, "microphone-disconnected", StringComparison.Ordinal))
                    {
                        await WaitForMicrophoneAsync(settings.MicrophoneEndpointId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(settings.Cooldown, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSingleCycleCoreAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var wakeModel = await _models
            .GetActiveAsync(VoiceModelProvider.WakeVosk, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new VoicePipelineResourceException(
                "model-unavailable",
                "Встроенная модель распознавания недоступна. Восстановите модели.");
        var commandModel = await _models
            .GetActiveAsync(VoiceModelProvider.CommandWhisper, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new VoicePipelineResourceException(
                "model-unavailable",
                "Встроенная модель распознавания недоступна. Восстановите модели.");
        var resolution = await _devices
            .ResolveAsync(settings.MicrophoneEndpointId, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Code != AudioInputResultCode.Success || resolution.Device is null)
        {
            throw new VoicePipelineResourceException(
                "microphone-unavailable",
                settings.MicrophoneEndpointId is null
                    ? "Микрофон недоступен. Подключите устройство ввода и обновите список."
                    : "Сохранённый Bluetooth- или USB-микрофон недоступен. Подключите это же устройство.");
        }

        VoiceRuntimeProviders providers;
        try
        {
            providers = _runtimeProviders.Create(wakeModel, commandModel);
        }
        catch (InvalidOperationException)
        {
            throw new VoicePipelineResourceException(
                "model-unavailable",
                "Активная модель распознавания недоступна. Восстановите встроенную модель.");
        }
        var current = _state.Snapshot with
        {
            MicrophoneEndpointId = resolution.Device.EndpointId,
            ErrorCode = null,
            SafeMessage = null,
            ActiveWakeModelVersion = wakeModel.Version,
            ActiveCommandModelVersion = commandModel.Version,
        };
        Publish(current with { State = VoiceAssistantState.WaitingForWakeWord });

        WakeWordDetectionResult detection;
        await using (var wakeCapture = await _captures
                         .OpenAsync(resolution.Device.EndpointId, cancellationToken)
                         .ConfigureAwait(false))
        {
            detection = await providers.WakeWord
                .WaitForDetectionAsync(
                    wakeCapture,
                    new WakeWordOptions(settings.WakePhrase, settings.WakeConfidence),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Publish(_state.Snapshot with
        {
            State = VoiceAssistantState.WakeWordDetected,
            LastWakePhrase = detection.Phrase,
            LastWakeConfidence = detection.Confidence,
            ErrorCode = null,
            SafeMessage = null,
        });
        await PlaySignalSafelyAsync(VoiceSignal.Ready, cancellationToken).ConfigureAwait(false);

        VoiceActivityResult activity;
        await using (var commandCapture = await _captures
                         .OpenAsync(resolution.Device.EndpointId, cancellationToken)
                         .ConfigureAwait(false))
        {
            Publish(_state.Snapshot with { State = VoiceAssistantState.ListeningForCommand });
            Publish(_state.Snapshot with { State = VoiceAssistantState.DetectingSpeechEnd });
            activity = await _voiceActivity
                .CaptureAsync(
                    commandCapture,
                    VoiceActivityOptions.Default with { Sensitivity = settings.VoiceActivitySensitivity },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!activity.SpeechDetected || activity.Audio is null)
        {
            await PlaySignalSafelyAsync(VoiceSignal.Failure, cancellationToken).ConfigureAwait(false);
            await PublishCooldownAsync(settings.Cooldown, cancellationToken).ConfigureAwait(false);
            return;
        }

        Publish(_state.Snapshot with { State = VoiceAssistantState.RecognizingCommand });
        var recognition = await providers.SpeechToText
            .RecognizeAsync(
                activity.Audio,
                new SpeechRecognitionOptions(settings.RecognitionLanguage),
                cancellationToken)
            .ConfigureAwait(false);
        Publish(_state.Snapshot with
        {
            LastRecognizedText = recognition.Text,
            LastRecognitionConfidence = recognition.Confidence,
            ErrorCode = null,
            SafeMessage = null,
        });
        await PlaySignalSafelyAsync(VoiceSignal.Success, cancellationToken).ConfigureAwait(false);
        activity = activity with { Audio = null };
        await PublishCooldownAsync(settings.Cooldown, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCooldownAsync(TimeSpan cooldown, CancellationToken cancellationToken)
    {
        Publish(_state.Snapshot with { State = VoiceAssistantState.Cooldown });
        await Task.Delay(cooldown, _timeProvider, cancellationToken).ConfigureAwait(false);
        Publish(_state.Snapshot with { State = VoiceAssistantState.WaitingForWakeWord });
    }

    private async Task WaitForMicrophoneAsync(string? endpointId, CancellationToken cancellationToken)
    {
        await foreach (var snapshot in _devices.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            if (endpointId is null
                ? snapshot.Any(device => device.IsAvailable)
                : snapshot.Any(device => device.IsAvailable
                    && string.Equals(device.EndpointId, endpointId, StringComparison.Ordinal)))
            {
                return;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask ExitIdleAsync(bool shouldResume)
    {
        try
        {
            if (shouldResume)
            {
                _runCancellation = new CancellationTokenSource();
                _runTask = RunLoopAsync(_runCancellation.Token);
            }
        }
        finally
        {
            _pipelineGate.Release();
            _lifecycleGate.Release();
        }

        return ValueTask.CompletedTask;
    }

    private async Task PlaySignalSafelyAsync(VoiceSignal signal, CancellationToken cancellationToken)
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

    private void Publish(VoiceAssistantState state) => Publish(_state.Snapshot with { State = state });

    private void Publish(VoicePipelineSnapshot snapshot) => _state.Publish(snapshot);

    private void PublishError(string code, string safeMessage) => Publish(_state.Snapshot with
    {
        State = VoiceAssistantState.Error,
        ErrorCode = code,
        SafeMessage = safeMessage,
    });

    private static string ToCaptureErrorCode(AudioInputResultCode code) => code switch
    {
        AudioInputResultCode.Disconnected => "microphone-disconnected",
        AudioInputResultCode.UnsupportedFormat => "microphone-format-unsupported",
        _ => "microphone-capture-failed",
    };

    private static string ToCaptureSafeMessage(AudioInputResultCode code) => code switch
    {
        AudioInputResultCode.Disconnected => "Микрофон отключён. Подключите это же устройство.",
        AudioInputResultCode.UnsupportedFormat => "Формат микрофона не поддерживается.",
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
        SpeechRecognitionFailureCode.ModelUnavailable => "Модель распознавания недоступна. Восстановите встроенную модель.",
        SpeechRecognitionFailureCode.NoText => "Речь не распознана. Повторите команду.",
        SpeechRecognitionFailureCode.ConfidenceBelowThreshold => "Команда распознана неуверенно. Повторите её.",
        SpeechRecognitionFailureCode.UnsupportedFormat => "Формат записанного звука не поддерживается.",
        _ => "Локальное распознавание не выполнено. Повторите команду.",
    };

    private sealed class VoicePipelineResourceException(string code, string safeMessage) : Exception
    {
        public string Code { get; } = code;

        public string SafeMessage { get; } = safeMessage;
    }

    private sealed class IdleLease(VoicePipelineCoordinator owner, bool shouldResume) : IAsyncDisposable
    {
        private VoicePipelineCoordinator? _owner = owner;

        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            return current is null ? ValueTask.CompletedTask : current.ExitIdleAsync(shouldResume);
        }
    }
}
