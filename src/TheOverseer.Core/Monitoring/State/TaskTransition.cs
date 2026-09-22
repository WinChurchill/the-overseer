using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.State;

public enum TaskTransitionKind
{
    None,
    StateChanged,
    AttentionRaised,
    AttentionCleared,
    NonBlockingInputRaised,
    NonBlockingInputResolved,
}

/// <summary>
/// A meaningful state change produced by the reducer. Hydration transitions are
/// baseline only: the attention layer must not announce history.
/// </summary>
public sealed record TaskTransition(
    TaskTransitionKind Kind,
    TaskKey Key,
    int Number,
    AgentTaskState From,
    AgentTaskState To,
    string CauseCode,
    string TurnId,
    bool IsHydration,
    DateTimeOffset ObservedAt);
