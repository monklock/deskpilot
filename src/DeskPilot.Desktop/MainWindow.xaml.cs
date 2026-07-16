using System.ComponentModel;
using System.Windows;
using DeskPilot.Desktop.ViewModels;

namespace DeskPilot.Desktop;

/// <summary>Provides the minimal DeskPilot desktop shell.</summary>
public partial class MainWindow : Window
{
    private bool _exitRequested;

    /// <summary>Creates the window with its view model.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
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
}
