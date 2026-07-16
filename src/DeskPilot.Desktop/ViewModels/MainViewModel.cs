using CommunityToolkit.Mvvm.ComponentModel;
using DeskPilot.Core.Voice;

namespace DeskPilot.Desktop.ViewModels;

/// <summary>Supplies state for the DeskPilot desktop shell.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private VoiceAssistantState _state = VoiceAssistantState.Disabled;

    /// <summary>Gets a displayable assistant status.</summary>
    public string StatusText => State == VoiceAssistantState.Disabled ? "Voice assistant is disabled" : State.ToString();

    partial void OnStateChanged(VoiceAssistantState value) => OnPropertyChanged(nameof(StatusText));
}
