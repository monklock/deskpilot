using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DeskPilot.Desktop.ViewModels;

namespace DeskPilot.Desktop;

/// <summary>Provides the minimal DeskPilot desktop shell.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _audioRefreshTimer;
    private bool _exitRequested;

    /// <summary>Creates the window with its view model.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        _audioRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _audioRefreshTimer.Tick += OnAudioRefreshTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Allows the tray service to close the desktop shell.</summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.Audio.RefreshAsync();
        await _viewModel.Voice.InitializeAsync();
        _audioRefreshTimer.Start();
    }

    private async void OnAudioRefreshTimerTick(object? sender, EventArgs e) =>
        await _viewModel.Audio.RefreshAsync();

    private void OnClosed(object? sender, EventArgs e) => _audioRefreshTimer.Stop();
}
