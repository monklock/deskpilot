using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPilot.Voice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPilot.Desktop.ViewModels;

/// <summary>Exposes voice model state without starting implicit downloads.</summary>
public sealed partial class VoiceModelManagerViewModel : ObservableObject
{
    private readonly IVoiceModelManager _manager;
    private readonly ILogger<VoiceModelManagerViewModel> _logger;
    private CancellationTokenSource? _downloadCancellation;

    [ObservableProperty]
    private string _statusMessage = "Загрузка состояния моделей...";

    [ObservableProperty]
    private VoiceModelDescriptor? _selectedModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _progressPercentage;

    /// <summary>Creates the voice model management view model.</summary>
    public VoiceModelManagerViewModel(
        IVoiceModelManager manager,
        ILogger<VoiceModelManagerViewModel>? logger = null)
    {
        _manager = manager;
        _logger = logger ?? NullLogger<VoiceModelManagerViewModel>.Instance;
    }

    /// <summary>Gets models visible in the current catalog.</summary>
    public ObservableCollection<VoiceModelDescriptor> Models { get; } = [];

    /// <summary>Loads local model state without downloading content.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            ApplyState(await _manager.GetStateAsync(CancellationToken.None).ConfigureAwait(true));
            StatusMessage = "Модели готовы к проверке.";
        }
        catch (Exception exception)
        {
            LogSafeFailure("initialize", exception);
            StatusMessage = "Не удалось загрузить состояние моделей.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ApplyState(await _manager.CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(true));
            StatusMessage = "Обновления моделей проверены.";
        }
        catch (Exception exception)
        {
            LogSafeFailure("check-updates", exception);
            StatusMessage = "Не удалось проверить обновления моделей.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCheckForUpdates() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownloadOrUpdate))]
    private async Task DownloadOrUpdateAsync()
    {
        if (IsBusy || SelectedModel is null)
        {
            StatusMessage = SelectedModel is null ? "Сначала выберите модель." : StatusMessage;
            return;
        }

        IsBusy = true;
        ProgressPercentage = 0;
        _downloadCancellation = new CancellationTokenSource();
        CancelDownloadCommand.NotifyCanExecuteChanged();
        var selected = SelectedModel;
        var progress = new Progress<VoiceModelProgress>(item =>
        {
            ProgressPercentage = item.TotalBytes <= 0
                ? 0
                : (int)Math.Clamp(item.BytesReceived * 100 / item.TotalBytes, 0, 100);
        });

        try
        {
            var result = await _manager
                .InstallAsync(selected, progress, _downloadCancellation.Token)
                .ConfigureAwait(true);
            StatusMessage = ToSafeMessage(result, "Модель установлена.");
        }
        catch (Exception exception)
        {
            LogSafeFailure("install", exception, selected.Id);
            StatusMessage = "Не удалось установить модель.";
        }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            IsBusy = false;
        }
    }

    private bool CanDownloadOrUpdate() => !IsBusy && SelectedModel?.DownloadUri is not null;

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        _downloadCancellation?.Cancel();
    }

    private bool CanCancelDownload() => IsBusy && _downloadCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanUseModel))]
    private async Task UseModelAsync()
    {
        if (IsBusy || SelectedModel is null)
        {
            StatusMessage = SelectedModel is null ? "Сначала выберите модель." : StatusMessage;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _manager
                .ActivateAsync(SelectedModel.ProviderId, SelectedModel.Version, CancellationToken.None)
                .ConfigureAwait(true);
            StatusMessage = ToSafeMessage(result, "Модель активирована.");
        }
        catch (Exception exception)
        {
            LogSafeFailure("activate", exception, SelectedModel.Id);
            StatusMessage = "Не удалось активировать модель.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUseModel() => !IsBusy && SelectedModel is not null;

    [RelayCommand(CanExecute = nameof(CanRestoreBuiltIn))]
    private async Task RestoreBuiltInAsync()
    {
        if (IsBusy || SelectedModel is null)
        {
            StatusMessage = SelectedModel is null ? "Сначала выберите модель провайдера." : StatusMessage;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _manager
                .RestoreBuiltInAsync(SelectedModel.ProviderId, CancellationToken.None)
                .ConfigureAwait(true);
            StatusMessage = ToSafeMessage(result, "Встроенная модель восстановлена.");
        }
        catch (Exception exception)
        {
            LogSafeFailure("restore-built-in", exception, SelectedModel.Id);
            StatusMessage = "Не удалось восстановить встроенную модель.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRestoreBuiltIn() => !IsBusy && SelectedModel is not null;

    private void ApplyState(VoiceModelState state)
    {
        Models.Clear();
        foreach (var model in state.Catalog)
        {
            Models.Add(model);
        }

        var catalogIdentities = Models
            .Select(model => $"{model.ProviderId}:{model.Id}:{model.Version}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var installed in state.InstalledModels.Where(model =>
                     !catalogIdentities.Contains($"{model.ProviderId}:{model.ModelId}:{model.Version}")))
        {
            Models.Add(new VoiceModelDescriptor(
                installed.ModelId,
                installed.ProviderId,
                $"{installed.ModelId} {installed.Version}",
                installed.Version,
                installed.IsLastKnownGood ? "Rollback" : "Installed",
                null,
                installed.Sha256,
                1,
                1,
                string.Empty,
                string.Empty,
                installed.Source == VoiceModelSource.Seed,
                VoiceModelArchiveFormat.None,
                "0.0.0",
                "99.0.0"));
        }

        SelectedModel = Models.FirstOrDefault(model =>
                state.ActiveVersions.TryGetValue(model.ProviderId, out var activeVersion)
                && string.Equals(activeVersion, model.Version, StringComparison.Ordinal))
            ?? Models.FirstOrDefault(model => state.InstalledModels.Any(installed =>
                installed.ProviderId == model.ProviderId
                && string.Equals(installed.Version, model.Version, StringComparison.Ordinal)
                && installed.IsLastKnownGood))
            ?? Models.FirstOrDefault();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();

    partial void OnSelectedModelChanged(VoiceModelDescriptor? value) => NotifyCommandStates();

    private void NotifyCommandStates()
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DownloadOrUpdateCommand.NotifyCanExecuteChanged();
        CancelDownloadCommand.NotifyCanExecuteChanged();
        UseModelCommand.NotifyCanExecuteChanged();
        RestoreBuiltInCommand.NotifyCanExecuteChanged();
    }

    private static string ToSafeMessage(VoiceModelOperationResult result, string successMessage) => result.Code switch
    {
        VoiceModelResultCode.Success => successMessage,
        VoiceModelResultCode.Cancelled => "Операция отменена.",
        VoiceModelResultCode.InvalidSignature => "Подпись каталога моделей недействительна.",
        VoiceModelResultCode.HashMismatch => "Проверка целостности модели не пройдена.",
        VoiceModelResultCode.InsufficientSpace => "Недостаточно места для модели.",
        VoiceModelResultCode.UnsafeArchive => "Архив модели отклонён как небезопасный.",
        _ => result.Message ?? "Операция с моделью не выполнена.",
    };

    private void LogSafeFailure(string operation, Exception exception, string? modelId = null) =>
        _logger.LogWarning(
            "Voice model UI operation {Operation} for {ModelId} failed with {ExceptionType}.",
            operation,
            modelId ?? "none",
            exception.GetType().Name);
}
