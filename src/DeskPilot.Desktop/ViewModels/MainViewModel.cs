namespace DeskPilot.Desktop.ViewModels;

/// <summary>Supplies state for the DeskPilot desktop shell.</summary>
public sealed class MainViewModel
{
    /// <summary>Creates the desktop shell view model.</summary>
    public MainViewModel(AudioControlViewModel audio, VoiceControlViewModel voice)
    {
        Audio = audio;
        Voice = voice;
    }

    /// <summary>Gets the manual audio control state.</summary>
    public AudioControlViewModel Audio { get; }

    /// <summary>Gets the local voice recognition state.</summary>
    public VoiceControlViewModel Voice { get; }
}
