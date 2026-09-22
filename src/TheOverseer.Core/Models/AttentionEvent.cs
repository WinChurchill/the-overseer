namespace TheOverseer.Core.Models;

/// <summary>
/// Semantic attention transitions. Voice policy and playback are Plan 3; this
/// layer only decides that a meaningful transition happened.
/// </summary>
public enum AttentionEventKind
{
    PermissionRequired,
    InputRequired,
    TaskError,
    TaskCompleted,
    AllTasksCompleted,
}

public sealed record AttentionEvent(
    AttentionEventKind Kind,
    TaskKey Key,
    int Number,
    string Label,
    AgentTaskState State,
    AgentActionKind Action,
    string TurnId,
    DateTimeOffset ObservedAt,
    string Code);
