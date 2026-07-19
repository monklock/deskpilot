namespace DeskPilot.Core.Commands;

/// <summary>Identifies a registered DeskPilot command.</summary>
public readonly record struct CommandId
{
    private CommandId(string value) => Value = value;

    /// <summary>Gets the stable command identifier.</summary>
    public string Value { get; }

    /// <summary>Creates a command identifier from a non-empty value.</summary>
    public static CommandId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new CommandId(value.Trim());
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Contains a request to invoke one registered command.</summary>
public sealed record CommandRequest(CommandId CommandId, IReadOnlyDictionary<string, string>? Arguments = null);

/// <summary>Describes the outcome of command dispatch.</summary>
public enum CommandExecutionStatus
{
    /// <summary>The command completed successfully.</summary>
    Succeeded,

    /// <summary>No handler is registered for the requested command.</summary>
    NotFound,

    /// <summary>The command was rejected before execution.</summary>
    Rejected,

    /// <summary>The command failed during execution.</summary>
    Failed,
}

/// <summary>Contains the result of one command execution attempt.</summary>
public sealed record CommandExecutionResult(CommandId CommandId, CommandExecutionStatus Status, string? Message = null)
{
    /// <summary>Gets a stable machine-readable failure code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Creates a successful command result.</summary>
    public static CommandExecutionResult Succeeded(CommandId commandId) => new(commandId, CommandExecutionStatus.Succeeded);

    /// <summary>Creates a result for an unknown command.</summary>
    public static CommandExecutionResult NotFound(CommandId commandId) =>
        new(commandId, CommandExecutionStatus.NotFound, "The command is not registered.")
        {
            ErrorCode = "command-not-found",
        };
}

/// <summary>Describes one finite phrase pattern and its trusted static arguments.</summary>
public sealed record CommandPhrasePattern(
    string Pattern,
    IReadOnlyDictionary<string, string>? Arguments = null)
{
    /// <summary>Gets the only dynamic placeholder supported by the local resolver.</summary>
    public const string PercentagePlaceholder = "{percentage}";
}

/// <summary>Describes a command available to an intent resolver.</summary>
public sealed record AvailableCommand(
    CommandId CommandId,
    string DisplayName,
    IReadOnlyCollection<CommandPhrasePattern> Phrases);

/// <summary>Describes intent resolution status.</summary>
public enum IntentResolutionStatus
{
    /// <summary>The input matched exactly one registered command.</summary>
    Resolved,

    /// <summary>The input did not match any registered command.</summary>
    NotFound,

    /// <summary>The input matched more than one registered command.</summary>
    Ambiguous,
}

/// <summary>Contains an intent resolution result and a trusted request on success.</summary>
public sealed record IntentResolutionResult(
    IntentResolutionStatus Status,
    CommandRequest? Request,
    double Confidence)
{
    /// <summary>Gets the canonical no-match result.</summary>
    public static IntentResolutionResult NotFound { get; } =
        new(IntentResolutionStatus.NotFound, null, 0);
}
