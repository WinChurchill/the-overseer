namespace TheOverseer.Core.Models;

/// <summary>
/// Canonical current-action vocabulary from the frozen V1 specification.
/// </summary>
public enum AgentActionKind
{
    Thinking,
    ReadingFiles,
    EditingFiles,
    RunningCommand,
    RunningTests,
    Searching,
    WaitingForApproval,
    WaitingForInput,
    Completed,
    Idle,
    Error,
}

public static class AgentActionLabels
{
    public static string ToLabel(this AgentActionKind kind) => kind switch
    {
        AgentActionKind.Thinking => "Thinking",
        AgentActionKind.ReadingFiles => "Reading files",
        AgentActionKind.EditingFiles => "Editing files",
        AgentActionKind.RunningCommand => "Running command",
        AgentActionKind.RunningTests => "Running tests",
        AgentActionKind.Searching => "Searching",
        AgentActionKind.WaitingForApproval => "Waiting for approval",
        AgentActionKind.WaitingForInput => "Waiting for input",
        AgentActionKind.Completed => "Completed",
        AgentActionKind.Idle => "Idle",
        AgentActionKind.Error => "Error",
        _ => "Idle",
    };
}

/// <summary>
/// A derived action label plus the strength of the evidence behind it.
/// <paramref name="DetailCode"/> is always a safe structural code
/// (for example <c>test-runner</c>), never recorded text.
/// </summary>
public sealed record AgentAction(AgentActionKind Kind, EvidenceKind Evidence, string? DetailCode = null)
{
    public static AgentAction Idle { get; } = new(AgentActionKind.Idle, EvidenceKind.None);

    public string Label => Kind.ToLabel();
}
