namespace DeskPilot.Core.Voice;

/// <summary>Represents the lifecycle state of the voice assistant.</summary>
public enum VoiceAssistantState
{
    /// <summary>Voice processing is disabled.</summary>
    Disabled,
    /// <summary>The assistant is waiting for the wake phrase.</summary>
    WaitingForWakeWord,
    /// <summary>The wake phrase was detected.</summary>
    WakeWordDetected,
    /// <summary>The assistant is listening for a command.</summary>
    ListeningForCommand,
    /// <summary>The assistant is detecting the end of speech.</summary>
    DetectingSpeechEnd,
    /// <summary>The assistant is recognizing a command.</summary>
    RecognizingCommand,
    /// <summary>The assistant is resolving a command.</summary>
    ResolvingCommand,
    /// <summary>The assistant is waiting for confirmation.</summary>
    WaitingForConfirmation,
    /// <summary>The assistant is executing a command.</summary>
    ExecutingCommand,
    /// <summary>The assistant is producing a notification.</summary>
    Speaking,
    /// <summary>The assistant is waiting before accepting another wake phrase.</summary>
    Cooldown,
    /// <summary>The assistant encountered an error.</summary>
    Error,
}
