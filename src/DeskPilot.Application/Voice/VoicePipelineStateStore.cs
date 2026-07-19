using DeskPilot.Core.Commands;
using DeskPilot.Core.Voice;

namespace DeskPilot.Application.Voice;

/// <summary>Contains the latest safe, displayable voice-pipeline state.</summary>
public sealed record VoicePipelineSnapshot(
    VoiceAssistantState State,
    string? MicrophoneEndpointId,
    string? LastWakePhrase,
    double? LastWakeConfidence,
    string? LastRecognizedText,
    double? LastRecognitionConfidence,
    string? ErrorCode,
    string? SafeMessage)
{
    /// <summary>Gets the active wake model version.</summary>
    public string? ActiveWakeModelVersion { get; init; }

    /// <summary>Gets the active command model version.</summary>
    public string? ActiveCommandModelVersion { get; init; }

    /// <summary>Gets the last resolved trusted command identifier.</summary>
    public string? LastResolvedCommandId { get; init; }

    /// <summary>Gets the last intent resolution status.</summary>
    public IntentResolutionStatus? LastIntentStatus { get; init; }

    /// <summary>Gets the last intent confidence.</summary>
    public double? LastIntentConfidence { get; init; }

    /// <summary>Gets the last command execution status.</summary>
    public CommandExecutionStatus? LastExecutionStatus { get; init; }

    /// <summary>Gets whether the exact selected microphone session is active.</summary>
    public bool IsCaptureActive { get; init; }

    /// <summary>Gets the bounded command duration captured in the current cycle.</summary>
    public TimeSpan? LastCapturedCommandDuration { get; init; }

    /// <summary>Gets the current cycle's in-memory ambient noise measurement.</summary>
    public double? LastNoiseFloorRms { get; init; }

    /// <summary>Gets the current cycle's in-memory peak measurement.</summary>
    public double? LastPeakRms { get; init; }

    /// <summary>Gets the initial disabled snapshot.</summary>
    public static VoicePipelineSnapshot Disabled { get; } = new(
        VoiceAssistantState.Disabled,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}

/// <summary>Exposes immutable voice-pipeline snapshots to presentation consumers.</summary>
public interface IVoicePipelineStateSource
{
    /// <summary>Raised after a new immutable snapshot becomes current.</summary>
    event EventHandler<VoicePipelineSnapshot>? SnapshotChanged;

    /// <summary>Gets the current voice-pipeline snapshot.</summary>
    VoicePipelineSnapshot Snapshot { get; }
}

/// <summary>Publishes immutable voice-pipeline snapshots to desktop consumers.</summary>
public sealed class VoicePipelineStateStore : IVoicePipelineStateSource
{
    private readonly object _sync = new();
    private readonly object _publishSync = new();
    private VoicePipelineSnapshot _snapshot = VoicePipelineSnapshot.Disabled;

    /// <summary>Raised after a new immutable snapshot becomes current.</summary>
    public event EventHandler<VoicePipelineSnapshot>? SnapshotChanged;

    /// <summary>Gets the current voice-pipeline snapshot.</summary>
    public VoicePipelineSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    internal void Publish(VoicePipelineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_publishSync)
        {
            lock (_sync)
            {
                _snapshot = snapshot;
            }

            SnapshotChanged?.Invoke(this, snapshot);
        }
    }
}
