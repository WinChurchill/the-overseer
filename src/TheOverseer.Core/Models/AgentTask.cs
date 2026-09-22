namespace TheOverseer.Core.Models;

/// <summary>
/// Canonical task states. Monitoring health is deliberately not represented here.
/// </summary>
public enum AgentTaskState
{
    Working,
    NeedsInput,
    NeedsApproval,
    Error,
    Done,
    Idle,
}

public enum ClientSurface
{
    Unknown,
    Desktop,
    Cli,
    VsCode,
}

/// <summary>
/// Strength of the evidence behind a task state, action or count.
/// </summary>
public enum EvidenceKind
{
    None,
    Timing,
    Inferred,
    Structured,
    Explicit,
}

/// <summary>
/// Identity of one user conversation inside one Codex profile.
/// Paths, titles and client surfaces deliberately do not participate.
/// </summary>
public readonly record struct TaskKey(string ProfileId, string ConversationId)
{
    public bool IsEmpty => string.IsNullOrEmpty(ProfileId) || string.IsNullOrEmpty(ConversationId);

    public override string ToString() => ProfileId + "/" + ConversationId;
}

/// <summary>
/// Immutable normalized task row. Negative facts are explicit:
/// <see cref="IsStale"/> and <see cref="IsProvisional"/> mark evidence limits
/// instead of silently reusing a last-known value as current truth.
/// </summary>
public sealed record AgentTask(
    TaskKey Key,
    int Number,
    string ConversationId,
    string ProfileId,
    ClientSurface Surface,
    string Label,
    string TurnId,
    AgentTaskState State,
    AgentAction Action,
    EvidenceKind Evidence,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset StateSince,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsStale,
    bool IsProvisional,
    bool NeedsHuman,
    bool CancellationObserved,
    bool TerminalFailureObserved,
    string? AttentionCode,
    int SourceFileCount)
{
    public bool IsTerminal => State is AgentTaskState.Done or AgentTaskState.Error;

    /// <summary>True when this task counts as work in progress.</summary>
    public bool IsActive => State is AgentTaskState.Working;

    /// <summary>True when the task demands a human.</summary>
    public bool IsWaitingOnHuman => State is AgentTaskState.NeedsInput or AgentTaskState.NeedsApproval;

    /// <summary>Safe short display label; never conversation text.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? string.Empty : Label;
}
