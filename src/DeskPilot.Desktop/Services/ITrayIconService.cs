namespace DeskPilot.Desktop.Services;

/// <summary>Controls the DeskPilot system tray icon.</summary>
public interface ITrayIconService : IDisposable
{
    /// <summary>Shows the tray icon and its command menu.</summary>
    void Start();
}
