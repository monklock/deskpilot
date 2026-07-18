using System.Diagnostics;
using DeskPilot.Modules.AudioControl;

namespace DeskPilot.Infrastructure.WindowsAudio;

/// <summary>Opens the Windows sound settings without a command shell or helper executable.</summary>
public sealed class SystemSoundSettingsLauncher : ISystemSoundSettingsLauncher
{
    /// <inheritdoc />
    public AudioOperationResult Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
            return new AudioOperationResult(true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AudioOperationResult(false, "sound-settings-unavailable", "Windows sound settings could not be opened.");
        }
    }
}
