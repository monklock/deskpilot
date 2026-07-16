using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DeskPilot.Desktop.Services;

/// <summary>Exposes the WPF shell through the Windows notification area.</summary>
public sealed class TrayIconService : ITrayIconService
{
    private readonly MainWindow _mainWindow;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _started;

    /// <summary>Creates the tray icon service.</summary>
    public TrayIconService(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "DeskPilot",
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    /// <inheritdoc />
    public void Start()
    {
        _notifyIcon.Visible = true;
        _started = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _started = false;
    }

    private void ShowWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    private void ExitApplication()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _mainWindow.RequestExit();
            System.Windows.Application.Current.Shutdown();
        });
    }
}
