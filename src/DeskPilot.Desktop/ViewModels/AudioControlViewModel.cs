using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPilot.Application.Commands;
using DeskPilot.Core.Commands;
using DeskPilot.Modules.AudioControl;
using Microsoft.Extensions.Logging;

namespace DeskPilot.Desktop.ViewModels;

/// <summary>Coordinates the manual Windows audio control surface.</summary>
public sealed partial class AudioControlViewModel : ObservableObject
{
    private readonly IAudioOutputDeviceService _outputService;
    private readonly IAudioVolumeService _volumeService;
    private readonly IAudioPreferredDeviceService _preferences;
    private readonly ISystemSoundSettingsLauncher _settingsLauncher;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ILogger<AudioControlViewModel> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _hasLoaded;

    [ObservableProperty]
    private AudioOutputDevice? _selectedDevice;

    [ObservableProperty]
    private int _volumePercentage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteStatus))]
    private bool _isMuted;

    [ObservableProperty]
    private string _defaultDeviceName = "Не найдено";

    [ObservableProperty]
    private string _statusMessage = "Загрузка аудиоустройств...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseSpeakers))]
    private AudioOutputDevice? _speakersDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseHeadphones))]
    private AudioOutputDevice? _headphonesDevice;

    /// <summary>Creates the manual audio control view model.</summary>
    public AudioControlViewModel(
        IAudioOutputDeviceService outputService,
        IAudioVolumeService volumeService,
        IAudioPreferredDeviceService preferences,
        ISystemSoundSettingsLauncher settingsLauncher,
        ICommandDispatcher dispatcher,
        ILogger<AudioControlViewModel> logger)
    {
        _outputService = outputService;
        _volumeService = volumeService;
        _preferences = preferences;
        _settingsLauncher = settingsLauncher;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Gets the active Windows render endpoints.</summary>
    public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = [];

    /// <summary>Gets whether the saved speakers endpoint is currently active.</summary>
    public bool CanUseSpeakers => SpeakersDevice?.IsAvailable == true;

    /// <summary>Gets whether the saved headphones endpoint is currently active.</summary>
    public bool CanUseHeadphones => HeadphonesDevice?.IsAvailable == true;

    /// <summary>Gets the localized mute state.</summary>
    public string MuteStatus => IsMuted ? "Звук выключен" : "Звук включён";

    /// <summary>Refreshes device, volume, mute, and preferred-endpoint state.</summary>
    public async Task<bool> RefreshAsync()
    {
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(true))
        {
            return false;
        }

        try
        {
            var selectedEndpointId = SelectedDevice?.EndpointId;
            var devices = await _outputService.GetDevicesAsync(CancellationToken.None);
            var defaultDevice = await _outputService.GetDefaultDeviceAsync(AudioDeviceRole.Multimedia, CancellationToken.None);
            var volumeState = await _volumeService.GetStateAsync(CancellationToken.None);
            var savedSpeakers = await _preferences.GetAsync(AudioDeviceSlot.Speakers, CancellationToken.None);
            var savedHeadphones = await _preferences.GetAsync(AudioDeviceSlot.Headphones, CancellationToken.None);

            OutputDevices.Clear();
            foreach (var device in devices.OrderBy(static device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                OutputDevices.Add(device);
            }

            SelectedDevice = FindDevice(devices, selectedEndpointId)
                ?? FindDevice(devices, defaultDevice?.EndpointId)
                ?? devices.FirstOrDefault();
            DefaultDeviceName = defaultDevice?.FriendlyName ?? "Не найдено";
            VolumePercentage = volumeState.Percentage;
            IsMuted = volumeState.IsMuted;
            SpeakersDevice = ReconcilePreferredDevice(savedSpeakers, devices);
            HeadphonesDevice = ReconcilePreferredDevice(savedHeadphones, devices);

            if (!_hasLoaded)
            {
                StatusMessage = "Ручное управление звуком готово.";
                _hasLoaded = true;
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to refresh the manual audio state.");
            StatusMessage = "Аудиоустройства временно недоступны. Проверьте подключение и повторите обновление.";
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    [RelayCommand]
    private async Task RefreshFromUiAsync()
    {
        if (await RefreshAsync())
        {
            StatusMessage = "Аудиосостояние обновлено.";
        }
    }

    [RelayCommand]
    private Task ApplyVolumeAsync() => DispatchVolumeAsync("audio.set-volume", "percentage", Math.Clamp(VolumePercentage, 0, 100), "Громкость изменена.");

    [RelayCommand]
    private Task IncreaseVolumeAsync() => DispatchVolumeAsync("audio.change-volume", "delta", 5, "Громкость увеличена.");

    [RelayCommand]
    private Task DecreaseVolumeAsync() => DispatchVolumeAsync("audio.change-volume", "delta", -5, "Громкость уменьшена.");

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        var result = await DispatchAsync("audio.toggle-mute");
        if (result.Status == CommandExecutionStatus.Succeeded)
        {
            if (await RefreshAsync())
            {
                StatusMessage = IsMuted ? "Звук выключен." : "Звук включён.";
            }

            return;
        }

        StatusMessage = result.Message ?? "Не удалось изменить состояние звука.";
    }

    [RelayCommand]
    private Task SaveSpeakersAsync() => SavePreferredAsync(AudioDeviceSlot.Speakers);

    [RelayCommand]
    private Task SaveHeadphonesAsync() => SavePreferredAsync(AudioDeviceSlot.Headphones);

    [RelayCommand]
    private Task UseSpeakersAsync() => UsePreferredAsync(AudioDeviceSlot.Speakers, SpeakersDevice);

    [RelayCommand]
    private Task UseHeadphonesAsync() => UsePreferredAsync(AudioDeviceSlot.Headphones, HeadphonesDevice);

    private async Task DispatchVolumeAsync(string commandId, string argumentName, int value, string successMessage)
    {
        var result = await DispatchAsync(
            commandId,
            new Dictionary<string, string>
            {
                [argumentName] = value.ToString(CultureInfo.InvariantCulture),
            });

        if (result.Status == CommandExecutionStatus.Succeeded)
        {
            if (await RefreshAsync())
            {
                StatusMessage = successMessage;
            }

            return;
        }

        StatusMessage = result.Message ?? "Не удалось изменить громкость.";
    }

    private async Task SavePreferredAsync(AudioDeviceSlot slot)
    {
        if (SelectedDevice is null)
        {
            StatusMessage = "Сначала выберите доступное аудиоустройство.";
            return;
        }

        var result = await DispatchAsync(
            "audio.save-preferred-device",
            new Dictionary<string, string>
            {
                ["endpointId"] = SelectedDevice.EndpointId,
                ["slot"] = slot.ToString(),
            });

        if (result.Status == CommandExecutionStatus.Succeeded)
        {
            if (await RefreshAsync())
            {
                StatusMessage = slot == AudioDeviceSlot.Speakers
                    ? "Устройство сохранено как колонки."
                    : "Устройство сохранено как наушники.";
            }

            return;
        }

        StatusMessage = result.Message ?? "Не удалось сохранить аудиоустройство.";
    }

    private async Task UsePreferredAsync(AudioDeviceSlot slot, AudioOutputDevice? device)
    {
        if (device?.IsAvailable != true)
        {
            StatusMessage = slot == AudioDeviceSlot.Headphones
                ? "Bluetooth-наушники недоступны. Подключите их и обновите список."
                : "Сохранённые колонки сейчас недоступны.";
            return;
        }

        var result = await DispatchAsync(
            "audio.set-default-device",
            new Dictionary<string, string> { ["endpointId"] = device.EndpointId });

        if (result.Status == CommandExecutionStatus.Succeeded)
        {
            if (await RefreshAsync())
            {
                StatusMessage = slot == AudioDeviceSlot.Speakers
                    ? "Колонки выбраны устройством вывода."
                    : "Наушники выбраны устройством вывода.";
            }

            return;
        }

        var settingsResult = _settingsLauncher.Open();
        StatusMessage = settingsResult.IsSuccess
            ? "Автопереключение не выполнено; открыты параметры звука Windows."
            : result.Message ?? settingsResult.Message ?? "Не удалось переключить аудиоустройство.";
    }

    private async Task<CommandExecutionResult> DispatchAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        var request = new CommandRequest(CommandId.From(commandId), arguments);
        try
        {
            return await _dispatcher.DispatchAsync(request, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Audio command {CommandId} failed unexpectedly.", request.CommandId);
            return new CommandExecutionResult(request.CommandId, CommandExecutionStatus.Failed, "Не удалось выполнить аудиооперацию.");
        }
    }

    private static AudioOutputDevice? FindDevice(IEnumerable<AudioOutputDevice> devices, string? endpointId) =>
        endpointId is null
            ? null
            : devices.FirstOrDefault(device => string.Equals(device.EndpointId, endpointId, StringComparison.Ordinal));

    private static AudioOutputDevice? ReconcilePreferredDevice(
        AudioOutputDevice? savedDevice,
        IReadOnlyCollection<AudioOutputDevice> activeDevices) =>
        savedDevice is null
            ? null
            : FindDevice(activeDevices, savedDevice.EndpointId)
                ?? savedDevice with { IsAvailable = false, IsDefault = false };
}
