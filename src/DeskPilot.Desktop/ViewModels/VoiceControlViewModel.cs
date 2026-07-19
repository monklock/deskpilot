using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPilot.Application.Voice;
using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;
using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPilot.Desktop.ViewModels;

/// <summary>Exposes the local voice pipeline without performing native work on the WPF thread.</summary>
public sealed partial class VoiceControlViewModel : ObservableObject, IDisposable
{
    private readonly IVoiceSettingsRepository _settings;
    private readonly IAudioInputDeviceService _devices;
    private readonly IVoicePipelineController _controller;
    private readonly IVoicePipelineStateSource _state;
    private readonly ILogger<VoiceControlViewModel> _logger;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private int _isDisposed;

    [ObservableProperty]
    private AudioInputDevice? _selectedMicrophone;

    [ObservableProperty]
    private bool _isVoiceEnabled;

    [ObservableProperty]
    private double _sensitivity = VoiceSettings.Default.VoiceActivitySensitivity;

    [ObservableProperty]
    private VoiceAssistantState _currentState = VoiceAssistantState.Disabled;

    [ObservableProperty]
    private string _lastRecognizedText = "—";

    [ObservableProperty]
    private string _lastCommandOutcome = "—";

    [ObservableProperty]
    private string _captureStatus = "Микрофон не активен";

    [ObservableProperty]
    private string _capturedDuration = "—";

    [ObservableProperty]
    private string _audioLevelDiagnostics = "—";

    [ObservableProperty]
    private string _activeWakeModelVersion = "Не выбрана";

    [ObservableProperty]
    private string _activeCommandModelVersion = "Не выбрана";

    [ObservableProperty]
    private string _statusMessage = "Голосовое управление выключено.";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Creates the voice control surface.</summary>
    public VoiceControlViewModel(
        IVoiceSettingsRepository settings,
        IAudioInputDeviceService devices,
        IVoicePipelineController controller,
        IVoicePipelineStateSource state,
        VoiceModelManagerViewModel models,
        ILogger<VoiceControlViewModel>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        Models = models ?? throw new ArgumentNullException(nameof(models));
        _logger = logger ?? NullLogger<VoiceControlViewModel>.Instance;
        _uiSynchronizationContext = SynchronizationContext.Current;
        _state.SnapshotChanged += OnSnapshotChanged;
        ApplySnapshot(_state.Snapshot);
    }

    /// <summary>Gets the active and remembered capture endpoints.</summary>
    public ObservableCollection<AudioInputDevice> Microphones { get; } = [];

    /// <summary>Gets the nested secure voice model manager.</summary>
    public VoiceModelManagerViewModel Models { get; }

    /// <summary>Gets the fixed approved wake phrase.</summary>
    public string WakePhrase => VoiceSettings.Default.WakePhrase;

    /// <summary>Gets a localized state label.</summary>
    public string CurrentStateText => ToStateText(CurrentState);

    /// <summary>Gets the voice mode action label.</summary>
    public string ToggleVoiceButtonText => IsVoiceEnabled ? "Выключить" : "Включить";

    /// <summary>Loads settings, endpoints, models, and the current pipeline snapshot.</summary>
    public async Task InitializeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var settings = await _settings.GetAsync(CancellationToken.None).ConfigureAwait(true);
            IsVoiceEnabled = settings.IsEnabled;
            Sensitivity = settings.VoiceActivitySensitivity;
            await RefreshMicrophonesCoreAsync(settings, CancellationToken.None).ConfigureAwait(true);
            await Models.InitializeAsync().ConfigureAwait(true);
            ApplySnapshot(_state.Snapshot);
        }
        catch (Exception exception)
        {
            LogSafeFailure("initialize", exception);
            StatusMessage = "Не удалось загрузить настройки голосового управления.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refreshes active capture endpoints while preserving a disconnected saved endpoint.</summary>
    public async Task RefreshMicrophonesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var settings = await _settings.GetAsync(CancellationToken.None).ConfigureAwait(true);
            await RefreshMicrophonesCoreAsync(settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LogSafeFailure("refresh-microphones", exception);
            StatusMessage = "Не удалось обновить список микрофонов.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLongOperation), AllowConcurrentExecutions = false)]
    private async Task ToggleVoiceAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(true);
            var enable = !IsVoiceEnabled;
            await _settings.SaveAsync(current with { IsEnabled = enable }, cancellationToken).ConfigureAwait(true);
            if (enable)
            {
                await _controller.EnableAsync(cancellationToken).ConfigureAwait(true);
                StatusMessage = "Голосовое управление включено.";
            }
            else
            {
                await _controller.DisableAsync(cancellationToken).ConfigureAwait(true);
                StatusMessage = "Голосовое управление выключено.";
            }

            IsVoiceEnabled = enable;
        }
        catch (Exception exception)
        {
            LogSafeFailure("toggle", exception);
            StatusMessage = "Не удалось изменить состояние голосового управления.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLongOperation), AllowConcurrentExecutions = false)]
    private Task RefreshMicrophonesFromUiAsync(CancellationToken cancellationToken) => RefreshMicrophonesAsync();

    [RelayCommand(CanExecute = nameof(CanUseSelectedMicrophone), AllowConcurrentExecutions = false)]
    private async Task UseSelectedMicrophoneAsync(CancellationToken cancellationToken)
    {
        if (SelectedMicrophone is null)
        {
            StatusMessage = "Сначала выберите микрофон.";
            return;
        }

        IsBusy = true;
        try
        {
            var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(true);
            await _settings.SaveAsync(
                current with
                {
                    MicrophoneEndpointId = SelectedMicrophone.EndpointId,
                    MicrophoneFriendlyName = SelectedMicrophone.FriendlyName,
                },
                cancellationToken).ConfigureAwait(true);
            if (IsVoiceEnabled)
            {
                await _controller.RestartAsync(cancellationToken).ConfigureAwait(true);
            }

            StatusMessage = SelectedMicrophone.IsAvailable
                ? "Выбранный микрофон сохранён."
                : "Bluetooth-микрофон сохранён, но сейчас не подключён.";
        }
        catch (Exception exception)
        {
            LogSafeFailure("select-microphone", exception);
            StatusMessage = "Не удалось сохранить выбранный микрофон.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLongOperation), AllowConcurrentExecutions = false)]
    private async Task SaveSensitivityAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            Sensitivity = Math.Clamp(Sensitivity, 0.65, 0.90);
            var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(true);
            await _settings.SaveAsync(
                current with { VoiceActivitySensitivity = Sensitivity },
                cancellationToken).ConfigureAwait(true);
            if (IsVoiceEnabled)
            {
                await _controller.RestartAsync(cancellationToken).ConfigureAwait(true);
            }

            StatusMessage = "Чувствительность сохранена.";
        }
        catch (Exception exception)
        {
            LogSafeFailure("save-sensitivity", exception);
            StatusMessage = "Не удалось сохранить чувствительность.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunLongOperation() => !IsBusy;

    private bool CanUseSelectedMicrophone() => !IsBusy && SelectedMicrophone is not null;

    private async Task RefreshMicrophonesCoreAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        var active = await _devices.GetActiveAsync(cancellationToken).ConfigureAwait(true);
        var selectedId = SelectedMicrophone?.EndpointId ?? settings.MicrophoneEndpointId;
        Microphones.Clear();
        foreach (var microphone in active.OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            Microphones.Add(microphone);
        }

        SelectedMicrophone = selectedId is null
            ? active.FirstOrDefault(device => device.IsDefault) ?? active.FirstOrDefault()
            : active.FirstOrDefault(device => string.Equals(device.EndpointId, selectedId, StringComparison.Ordinal));
        if (SelectedMicrophone is null && settings.MicrophoneEndpointId is not null)
        {
            SelectedMicrophone = new AudioInputDevice(
                settings.MicrophoneEndpointId,
                settings.MicrophoneFriendlyName ?? "Сохранённый микрофон",
                false,
                false);
            Microphones.Add(SelectedMicrophone);
            StatusMessage = IsBluetooth(settings.MicrophoneFriendlyName)
                ? "Bluetooth-микрофон отключён. Подключите его в Windows и обновите список."
                : "Сохранённый микрофон отключён. Подключите это же устройство.";
        }
        else
        {
            StatusMessage = active.Count == 0
                ? "Активные микрофоны не найдены."
                : "Список микрофонов обновлён.";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _state.SnapshotChanged -= OnSnapshotChanged;
        }
    }

    private void OnSnapshotChanged(object? sender, VoicePipelineSnapshot snapshot)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (_uiSynchronizationContext is null
            || ReferenceEquals(SynchronizationContext.Current, _uiSynchronizationContext))
        {
            ApplySnapshot(snapshot);
            return;
        }

        _uiSynchronizationContext.Post(
            static state =>
            {
                var update = ((VoiceControlViewModel ViewModel, VoicePipelineSnapshot Snapshot))state!;
                if (Volatile.Read(ref update.ViewModel._isDisposed) == 0)
                {
                    update.ViewModel.ApplySnapshot(update.Snapshot);
                }
            },
            (this, snapshot));
    }

    private void ApplySnapshot(VoicePipelineSnapshot snapshot)
    {
        CurrentState = snapshot.State;
        LastRecognizedText = snapshot.LastRecognizedText ?? "—";
        LastCommandOutcome = ToCommandOutcome(snapshot);
        CaptureStatus = snapshot.IsCaptureActive ? "Микрофон активен" : "Микрофон не активен";
        CapturedDuration = ToCapturedDuration(snapshot);
        AudioLevelDiagnostics = ToAudioLevelDiagnostics(snapshot);
        ActiveWakeModelVersion = snapshot.ActiveWakeModelVersion ?? "Не выбрана";
        ActiveCommandModelVersion = snapshot.ActiveCommandModelVersion ?? "Не выбрана";
        if (!string.IsNullOrWhiteSpace(snapshot.SafeMessage))
        {
            StatusMessage = snapshot.SafeMessage;
        }
    }

    private void LogSafeFailure(string operation, Exception exception) =>
        _logger.LogWarning(
            "Voice UI operation {Operation} failed with {ExceptionType}.",
            operation,
            exception.GetType().Name);

    private static bool IsBluetooth(string? friendlyName) =>
        friendlyName?.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) == true;

    private static string ToStateText(VoiceAssistantState state) => state switch
    {
        VoiceAssistantState.Disabled => "Выключено",
        VoiceAssistantState.WaitingForWakeWord => "Ожидание слова «альфа»",
        VoiceAssistantState.WakeWordDetected => "Слово активации распознано",
        VoiceAssistantState.ListeningForCommand => "Слушаю команду",
        VoiceAssistantState.DetectingSpeechEnd => "Определяю окончание речи",
        VoiceAssistantState.RecognizingCommand => "Распознаю локально",
        VoiceAssistantState.ResolvingCommand => "Определяю команду",
        VoiceAssistantState.ExecutingCommand => "Выполняю команду",
        VoiceAssistantState.Cooldown => "Пауза",
        VoiceAssistantState.Error => "Требуется внимание",
        _ => state.ToString(),
    };

    private static string ToCommandOutcome(VoicePipelineSnapshot snapshot)
    {
        if (snapshot.ErrorCode is "speech-not-detected" or "speech-not-recognized")
        {
            return "Команда не распознана";
        }

        if (snapshot.ErrorCode == "speech-confidence-low"
            && !string.IsNullOrWhiteSpace(snapshot.LastRecognizedText)
            && snapshot.LastRecognitionConfidence is double confidence)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Распознано, confidence: {confidence:F2}");
        }

        return snapshot.LastResolvedCommandId is null
            ? snapshot.LastIntentStatus switch
            {
                IntentResolutionStatus.NotFound => "Команда не найдена",
                IntentResolutionStatus.Ambiguous => "Команда неоднозначна",
                _ => "—",
            }
            : snapshot.LastExecutionStatus switch
            {
                CommandExecutionStatus.Succeeded =>
                    $"{snapshot.LastResolvedCommandId} — выполнено",
                CommandExecutionStatus.Rejected =>
                    $"{snapshot.LastResolvedCommandId} — отклонено",
                CommandExecutionStatus.Failed =>
                    $"{snapshot.LastResolvedCommandId} — ошибка",
                CommandExecutionStatus.NotFound =>
                    $"{snapshot.LastResolvedCommandId} — недоступно",
                _ => snapshot.LastResolvedCommandId,
            };
    }

    private static string ToCapturedDuration(VoicePipelineSnapshot snapshot) =>
        snapshot.IsCaptureActive
        && snapshot.LastCapturedCommandDuration is TimeSpan duration
        && duration >= TimeSpan.Zero
            ? string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"{duration.TotalSeconds:F2} с")
            : "—";

    private static string ToAudioLevelDiagnostics(VoicePipelineSnapshot snapshot) =>
        snapshot.IsCaptureActive
        && snapshot.LastNoiseFloorRms is double noise
        && snapshot.LastPeakRms is double peak
        && noise >= 0
        && peak >= 0
        && double.IsFinite(noise)
        && double.IsFinite(peak)
            ? string.Create(
                CultureInfo.GetCultureInfo("ru-RU"),
                $"Шум: {noise:F4}; пик: {peak:F4}")
            : "—";

    partial void OnCurrentStateChanged(VoiceAssistantState value) => OnPropertyChanged(nameof(CurrentStateText));

    partial void OnIsVoiceEnabledChanged(bool value) => OnPropertyChanged(nameof(ToggleVoiceButtonText));

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    partial void OnSelectedMicrophoneChanged(AudioInputDevice? value) => NotifyCommands();

    private void NotifyCommands()
    {
        ToggleVoiceCommand.NotifyCanExecuteChanged();
        RefreshMicrophonesFromUiCommand.NotifyCanExecuteChanged();
        UseSelectedMicrophoneCommand.NotifyCanExecuteChanged();
        SaveSensitivityCommand.NotifyCanExecuteChanged();
    }
}
